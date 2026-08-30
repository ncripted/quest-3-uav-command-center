using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using QuestCommandCenter.VehicleCore;
using UnityEngine;

namespace QuestCommandCenter.XRInput
{
    /// <summary>
    /// Simulation-only controller relay. Safety-approved normalized commands are
    /// sent to a localhost TCP endpoint exposed through ADB. This component has
    /// no DJI adapter dependency and cannot produce aircraft commands.
    /// </summary>
    public sealed class DesktopSimulatorRelay : MonoBehaviour, IVehicleTelemetrySource, IFlightActionControl
    {
        private const string SimulatorHost = "127.0.0.1";
        private const long TelemetryFailsafeMilliseconds = 1000;
        private const int ReconnectDelayMilliseconds = 500;
        private const long FlightActionAcknowledgementTimeoutMilliseconds = 2000;

        private readonly object transportGate = new object();
        private readonly AutoResetEvent transportWake = new AutoResetEvent(false);
        private readonly ControlModeStateMachine stateMachine = new ControlModeStateMachine();
        private FlightCommandArbiter arbiter;
        private FlightSafetyManager safetyManager;
        private IManualControlIntentSource inputSource;
        private ManualControlIntent latestIntent;
        private bool hasInput;
        private long latestInputAtMilliseconds;
        private long inputVersion;
        private long submittedInputVersion = -1;
        private bool pendingTakeover;
        private long correlationSequence;

        private Thread transportThread;
        private TcpClient activeClient;
        private volatile bool stopTransport;
        private volatile bool transportConnected;
        private bool observedTransportConnected;
        private string latestOutbound;
        private long outboundVersion;
        private readonly Queue<string> discreteOutbound = new Queue<string>();
        private string latestTelemetryJson;
        private long telemetryVersion;
        private long observedTelemetryVersion;
        private long lastTelemetryAtMilliseconds = -1;
        private SimulatorTelemetryDto latestTelemetry;
        private readonly FlightActionCommandSession flightActionSession = new FlightActionCommandSession();
        private FlightActionResult lastFlightAction;
        private long pendingFlightActionAtMilliseconds = -1;

        public ControlMode ControlMode => stateMachine.Current;
        public VehicleTelemetry LatestTelemetry { get; private set; }
        public bool IsFlightActionPending => flightActionSession.IsPending;
        public FlightActionResult LastFlightAction => lastFlightAction ?? flightActionSession.Current;

        public event Action<VehicleTelemetry> TelemetryUpdated;
        public event Action<FlightActionResult> FlightActionStateChanged;

        private void Awake()
        {
            arbiter = new FlightCommandArbiter(stateMachine, commandTimeoutMilliseconds: 250);
            safetyManager = new FlightSafetyManager(new IndoorDevelopmentSafetyProfile());
            stateMachine.Transitioned += OnControlModeTransition;
        }

        public void Initialize(IManualControlIntentSource source)
        {
            if (isActiveAndEnabled)
            {
                UnsubscribeInput();
            }
            inputSource = source;
            if (isActiveAndEnabled)
            {
                SubscribeInput();
            }
        }

        private void OnEnable()
        {
            if (inputSource == null)
            {
                inputSource = GetComponent<QuestControllerInput>();
            }
            SubscribeInput();
            StartTransport();
        }

        private void OnDisable()
        {
            UnsubscribeInput();
            StopTransport();
            hasInput = false;
            pendingTakeover = false;
        }

        private void OnDestroy()
        {
            stateMachine.Transitioned -= OnControlModeTransition;
            transportWake.Dispose();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                StopTransport();
            }
            else if (isActiveAndEnabled)
            {
                StartTransport();
            }
        }

        private void Update()
        {
            var now = MonotonicMilliseconds();
            if (flightActionSession.TryExpire(
                    Time.realtimeSinceStartupAsDouble,
                    out var expiredAction))
            {
                PublishFlightAction(expiredAction);
            }
            if (flightActionSession.IsPending
                && pendingFlightActionAtMilliseconds >= 0
                && now - pendingFlightActionAtMilliseconds
                    > FlightActionAcknowledgementTimeoutMilliseconds)
            {
                flightActionSession.FailPending(
                    "Desktop simulator did not acknowledge the action within two seconds.",
                    out var timedOutAction);
                PublishFlightAction(timedOutAction);
                pendingFlightActionAtMilliseconds = -1;
            }
            ProcessTransportState(now);
            ProcessTelemetry(now);

            if (stateMachine.Current == ControlMode.Ready
                && hasInput
                && latestIntent.ManualFlightAuthorized)
            {
                stateMachine.TryTransition(
                    ControlMode.Manual,
                    now,
                    "Desktop simulator manual authorization",
                    NextCorrelationId("sim-manual"),
                    out _);
            }

            if ((stateMachine.Current == ControlMode.Manual
                    || stateMachine.Current == ControlMode.Assisted)
                && submittedInputVersion != inputVersion)
            {
                arbiter.SubmitManual(latestIntent, latestInputAtMilliseconds);
                submittedInputVersion = inputVersion;
            }

            var selected = arbiter.Select(now);
            var telemetryAge = lastTelemetryAtMilliseconds < 0
                ? long.MaxValue
                : Math.Max(0, now - lastTelemetryAtMilliseconds);
            var decision = safetyManager.Evaluate(
                selected,
                new FlightSafetyContext
                {
                    MonotonicMilliseconds = now,
                    AuthorityEpoch = arbiter.AuthorityEpoch,
                    ControlMode = stateMachine.Current,
                    IsConnected = observedTransportConnected,
                    HasTelemetry = lastTelemetryAtMilliseconds >= 0,
                    TelemetryAgeMilliseconds = telemetryAge,
                    HasFlightState = latestTelemetry != null,
                    HasBatteryState = latestTelemetry != null,
                    BatteryPercent = latestTelemetry == null
                        ? 0
                        : Mathf.RoundToInt(latestTelemetry.battery_percent),
                    RelativeAltitudeMeters = latestTelemetry == null
                        ? 0f
                        : latestTelemetry.altitude_meters
                });

            var authorized = decision.Disposition != FlightSafetyDisposition.Neutralized
                && selected.Authorized
                && (selected.Source == FlightCommandSource.Manual
                    || selected.Source == FlightCommandSource.Assisted);
            var outbound = DesktopSimulatorProtocol.SerializeManualControl(
                selected.Sequence,
                decision.Command,
                authorized,
                latestIntent.BrakeHoverRequested,
                pendingTakeover);
            pendingTakeover = false;
            QueueOutbound(outbound);
        }

        private void OnManualIntentUpdated(ManualControlIntent intent)
        {
            var now = MonotonicMilliseconds();
            latestIntent = intent;
            latestInputAtMilliseconds = now;
            inputVersion++;
            hasInput = true;

            if (intent.ManualTakeoverRequested
                && arbiter.TryManualTakeover(
                    now,
                    NextCorrelationId("sim-takeover"),
                    out _))
            {
                pendingTakeover = true;
                submittedInputVersion = -1;
            }
        }

        private void ProcessTransportState(long now)
        {
            var connected = transportConnected;
            if (connected == observedTransportConnected)
            {
                return;
            }

            observedTransportConnected = connected;
            if (connected)
            {
                lastTelemetryAtMilliseconds = -1;
                latestTelemetry = null;
                stateMachine.TryTransition(
                    ControlMode.Preflight,
                    now,
                    "Desktop simulator TCP connected",
                    NextCorrelationId("sim-connect"),
                    out _);
                Debug.Log("Desktop simulator relay connected through localhost/ADB.");
            }
            else
            {
                lastTelemetryAtMilliseconds = -1;
                latestTelemetry = null;
                LatestTelemetry = null;
                TelemetryUpdated?.Invoke(null);
                lock (transportGate)
                {
                    discreteOutbound.Clear();
                }
                if (flightActionSession.FailPending(
                        "Desktop simulator disconnected before acknowledging the action.",
                        out var failedAction))
                {
                    PublishFlightAction(failedAction);
                }
                else
                {
                    CancelFlightActionConfirmation();
                }
                pendingFlightActionAtMilliseconds = -1;
                if (stateMachine.Current != ControlMode.Disconnected)
                {
                    stateMachine.TryTransition(
                        ControlMode.Disconnected,
                        now,
                        "Desktop simulator TCP disconnected",
                        NextCorrelationId("sim-disconnect"),
                        out _);
                }
                Debug.Log("Desktop simulator relay disconnected; output is neutral.");
            }
        }

        private void ProcessTelemetry(long now)
        {
            string json = null;
            long version;
            lock (transportGate)
            {
                version = telemetryVersion;
                if (version != observedTelemetryVersion)
                {
                    json = latestTelemetryJson;
                }
            }

            if (json != null)
            {
                try
                {
                    var telemetry = JsonUtility.FromJson<SimulatorTelemetryDto>(json);
                    if (telemetry != null && telemetry.type == "simulator_telemetry")
                    {
                        latestTelemetry = telemetry;
                        lastTelemetryAtMilliseconds = now;
                        LatestTelemetry = NormalizeTelemetry(telemetry, now);
                        TelemetryUpdated?.Invoke(LatestTelemetry);
                        TryAcknowledgeFlightAction(telemetry.flight_phase);
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("Desktop simulator telemetry rejected: " + exception.Message);
                }
                observedTelemetryVersion = version;
            }

            if (stateMachine.Current == ControlMode.Preflight
                && latestTelemetry != null)
            {
                stateMachine.TryTransition(
                    ControlMode.Ready,
                    now,
                    "Desktop simulator telemetry received",
                    NextCorrelationId("sim-ready"),
                    out _);
            }
            else if (stateMachine.Current != ControlMode.Disconnected
                && stateMachine.Current != ControlMode.Preflight
                && stateMachine.Current != ControlMode.Failsafe
                && lastTelemetryAtMilliseconds >= 0
                && now - lastTelemetryAtMilliseconds > TelemetryFailsafeMilliseconds)
            {
                stateMachine.TryTransition(
                    ControlMode.Failsafe,
                    now,
                    "Desktop simulator telemetry timeout",
                    NextCorrelationId("sim-failsafe"),
                    out _);
            }
            else if (stateMachine.Current == ControlMode.Failsafe
                && latestTelemetry != null
                && now - lastTelemetryAtMilliseconds <= TelemetryFailsafeMilliseconds)
            {
                stateMachine.TryTransition(
                    ControlMode.Preflight,
                    now,
                    "Desktop simulator telemetry recovered",
                    NextCorrelationId("sim-recover"),
                    out _);
            }
        }

        private void SubscribeInput()
        {
            if (inputSource != null)
            {
                inputSource.ManualIntentUpdated -= OnManualIntentUpdated;
                inputSource.ManualIntentUpdated += OnManualIntentUpdated;
            }
        }

        public bool RequestFlightActionConfirmation(FlightActionType action)
        {
            if (!flightActionSession.TryRequestConfirmation(
                    action,
                    LatestTelemetry,
                    IsSimulatorTelemetryFresh(),
                    Time.realtimeSinceStartupAsDouble,
                    out var result))
            {
                PublishFlightAction(result);
                return false;
            }

            PublishFlightAction(result);
            return true;
        }

        public bool ConfirmFlightAction(FlightActionType action)
        {
            if (!flightActionSession.TryConfirm(
                    action,
                    LatestTelemetry,
                    IsSimulatorTelemetryFresh(),
                    Time.realtimeSinceStartupAsDouble,
                    out var pending))
            {
                PublishFlightAction(pending);
                return false;
            }

            PublishFlightAction(pending);
            pendingFlightActionAtMilliseconds = MonotonicMilliseconds();
            QueueDiscreteOutbound(
                DesktopSimulatorProtocol.SerializeFlightAction(pending.RequestId, action));
            return true;
        }

        public void CancelFlightActionConfirmation()
        {
            if (flightActionSession.CancelConfirmation(out var result))
            {
                PublishFlightAction(result);
            }
        }

        private void UnsubscribeInput()
        {
            if (inputSource != null)
            {
                inputSource.ManualIntentUpdated -= OnManualIntentUpdated;
            }
        }

        private void StartTransport()
        {
            if (transportThread != null && transportThread.IsAlive)
            {
                return;
            }
            stopTransport = false;
            transportThread = new Thread(TransportLoop)
            {
                IsBackground = true,
                Name = "QCC Desktop Simulator Relay"
            };
            transportThread.Start();
        }

        private void StopTransport()
        {
            stopTransport = true;
            transportWake.Set();
            lock (transportGate)
            {
                try
                {
                    activeClient?.Close();
                }
                catch (Exception)
                {
                    // Closing a failed socket is best effort; state is still neutralized.
                }
            }
            if (transportThread != null && transportThread.IsAlive)
            {
                transportThread.Join(1000);
            }
            transportThread = null;
            transportConnected = false;
        }

        private void TransportLoop()
        {
            while (!stopTransport)
            {
                try
                {
                    using (var client = new TcpClient())
                    {
                        lock (transportGate)
                        {
                            activeClient = client;
                        }
                        client.NoDelay = true;
                        client.Connect(SimulatorHost, DesktopSimulatorProtocol.DefaultPort);
                        transportConnected = true;
                        RunConnected(client);
                    }
                }
                catch (Exception)
                {
                    // The desktop server is optional; retry quietly while the app runs.
                }
                finally
                {
                    transportConnected = false;
                    lock (transportGate)
                    {
                        activeClient = null;
                    }
                }

                if (!stopTransport)
                {
                    transportWake.WaitOne(ReconnectDelayMilliseconds);
                }
            }
        }

        private void RunConnected(TcpClient client)
        {
            var stream = client.GetStream();
            stream.WriteTimeout = 250;
            var receive = new byte[4096];
            var receiveText = string.Empty;
            long sentVersion = -1;

            while (!stopTransport && client.Connected)
            {
                if (PeerClosed(client))
                {
                    return;
                }
                string discrete = null;
                string outbound = null;
                long version;
                lock (transportGate)
                {
                    if (discreteOutbound.Count > 0)
                    {
                        discrete = discreteOutbound.Dequeue();
                    }
                    version = outboundVersion;
                    if (version != sentVersion)
                    {
                        outbound = latestOutbound;
                    }
                }
                if (discrete != null)
                {
                    var discreteBytes = Encoding.UTF8.GetBytes(discrete);
                    stream.Write(discreteBytes, 0, discreteBytes.Length);
                }
                if (outbound != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(outbound);
                    stream.Write(bytes, 0, bytes.Length);
                    sentVersion = version;
                }

                while (stream.DataAvailable)
                {
                    var count = stream.Read(receive, 0, receive.Length);
                    if (count <= 0)
                    {
                        return;
                    }
                    receiveText += Encoding.UTF8.GetString(receive, 0, count);
                    var newline = receiveText.IndexOf('\n');
                    while (newline >= 0)
                    {
                        var line = receiveText.Substring(0, newline).Trim();
                        receiveText = receiveText.Substring(newline + 1);
                        if (line.Length > 0)
                        {
                            lock (transportGate)
                            {
                                latestTelemetryJson = line;
                                telemetryVersion++;
                            }
                        }
                        newline = receiveText.IndexOf('\n');
                    }
                }

                transportWake.WaitOne(10);
            }
        }

        private static bool PeerClosed(TcpClient connectedClient)
        {
            try
            {
                var socket = connectedClient.Client;
                return socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0;
            }
            catch (SocketException)
            {
                return true;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }

        private void QueueOutbound(string line)
        {
            lock (transportGate)
            {
                latestOutbound = line;
                outboundVersion++;
            }
            transportWake.Set();
        }

        private void QueueDiscreteOutbound(string line)
        {
            lock (transportGate)
            {
                if (discreteOutbound.Count < 16)
                {
                    discreteOutbound.Enqueue(line);
                }
            }
            transportWake.Set();
        }

        private void TryAcknowledgeFlightAction(string flightPhase)
        {
            if (!flightActionSession.IsPending || string.IsNullOrEmpty(flightPhase))
            {
                return;
            }

            var action = flightActionSession.Current.Action;
            var acknowledged = action == FlightActionType.Takeoff
                ? flightPhase == "TakingOff" || flightPhase == "Airborne"
                : action == FlightActionType.Land
                    ? flightPhase == "Landing" || flightPhase == "Grounded"
                    : flightPhase == "ReturningHome"
                        || flightPhase == "Landing"
                        || flightPhase == "Grounded";
            if (!acknowledged)
            {
                return;
            }

            if (flightActionSession.TryComplete(
                    flightActionSession.Current.RequestId,
                    true,
                    "Desktop simulator accepted " + action + ".",
                    out var result))
            {
                pendingFlightActionAtMilliseconds = -1;
                PublishFlightAction(result);
            }
        }

        private bool IsSimulatorTelemetryFresh()
        {
            return observedTransportConnected
                && LatestTelemetry?.HasFlightState == true
                && lastTelemetryAtMilliseconds >= 0
                && MonotonicMilliseconds() - lastTelemetryAtMilliseconds <= 500;
        }

        private static VehicleTelemetry NormalizeTelemetry(
            SimulatorTelemetryDto telemetry,
            long nowMilliseconds)
        {
            var grounded = telemetry.flight_phase == "Grounded";
            return new VehicleTelemetry
            {
                Sequence = telemetry.last_sequence,
                MonotonicMilliseconds = nowMilliseconds,
                IsConnected = telemetry.connected,
                ProductName = "Desktop Simulator",
                HasBatteryState = true,
                BatteryPercent = Mathf.RoundToInt(telemetry.battery_percent),
                HasFlightState = true,
                AltitudeMeters = telemetry.altitude_meters,
                FlightMode = telemetry.flight_phase,
                MotorsOn = !grounded,
                IsFlying = !grounded,
                HomePositionSet = true,
                FailsafeEnabled = false
            };
        }

        private void PublishFlightAction(FlightActionResult result)
        {
            lastFlightAction = result;
            FlightActionStateChanged?.Invoke(result);
            Debug.Log("Desktop simulator flight action: "
                + result.Status + " | " + result.Action + " | " + result.Message);
        }

        private void OnControlModeTransition(ControlModeTransition transition)
        {
            Debug.Log(
                "Desktop simulator control mode: "
                + transition.Previous
                + " -> "
                + transition.Next
                + " | "
                + transition.Reason
                + " | "
                + transition.CorrelationId);
        }

        private string NextCorrelationId(string prefix)
        {
            correlationSequence++;
            return prefix + "-" + correlationSequence;
        }

        private static long MonotonicMilliseconds()
        {
            return (long)(Time.realtimeSinceStartupAsDouble * 1000.0);
        }

        [Serializable]
        private sealed class SimulatorTelemetryDto
        {
            public string type;
            public float altitude_meters;
            public float battery_percent;
            public bool connected;
            public long last_sequence;
            public string flight_phase;
        }
    }
}
