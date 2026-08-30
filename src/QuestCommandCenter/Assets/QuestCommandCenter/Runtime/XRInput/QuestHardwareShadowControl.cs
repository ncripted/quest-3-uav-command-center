using System;
using QuestCommandCenter.VehicleCore;
using UnityEngine;

namespace QuestCommandCenter.XRInput
{
    /// <summary>
    /// Launch-only dry-run path for validating Quest controls against live vehicle
    /// telemetry. This component has no vehicle-command adapter reference and can
    /// only log the command that passed arbitration and safety evaluation.
    /// </summary>
    public sealed class QuestHardwareShadowControl : MonoBehaviour, IFlightActionControl, IVehicleTelemetrySource
    {
        private const long CommandTimeoutMilliseconds = 250;
        private const long TelemetryTimeoutMilliseconds = 1000;
        // Periodic output is diagnostic only. Safety and action changes still log
        // immediately, while the slower heartbeat avoids rotating useful ADB logs.
        private const double LogIntervalSeconds = 1.0;
#if UNITY_ANDROID && !UNITY_EDITOR
        private const string ShadowTestIntentExtra =
            "com.dronecommand.quest.DJI_HARDWARE_SHADOW_TEST";
#endif

        [SerializeField] private MonoBehaviour inputSourceBehaviour;
        [SerializeField] private MonoBehaviour telemetrySourceBehaviour;
        [SerializeField] private MonoBehaviour simulatorRelayBehaviour;
        [SerializeField] private MonoBehaviour simulatorFlightActionCoordinatorBehaviour;
        [SerializeField] private MonoBehaviour integratedManualFlightCoordinatorBehaviour;

        private readonly ControlModeStateMachine stateMachine = new ControlModeStateMachine();
        private readonly FlightActionCommandSession flightActionSession = new FlightActionCommandSession();
        private FlightCommandArbiter arbiter;
        private FlightSafetyManager safetyManager;
        private IManualControlIntentSource inputSource;
        private IVehicleTelemetrySource telemetrySource;
        private ManualControlIntent latestIntent;
        private VehicleTelemetry liveTelemetry;
        private bool shadowAirborne;
        private long latestTelemetryAtMilliseconds = -1;
        private long latestInputAtMilliseconds;
        private long inputVersion;
        private long submittedInputVersion = -1;
        private bool hasInput;
        private bool shadowActive;
        private long correlationSequence;
        private double nextLogAt;
        private bool lastLoggedAuthorized;
        private FlightSafetyDisposition lastLoggedDisposition;
        private FlightSafetyReason lastLoggedReason;
        private NormalizedFlightCommand lastLoggedCommand;
        private FlightActionResult lastFlightAction;

        public ControlMode ControlMode => stateMachine.Current;
        public VehicleTelemetry LatestTelemetry { get; private set; }
        public bool IsFlightActionPending => flightActionSession.IsPending;
        public FlightActionResult LastFlightAction => lastFlightAction ?? flightActionSession.Current;

        public event Action<VehicleTelemetry> TelemetryUpdated;
        public event Action<FlightActionResult> FlightActionStateChanged;

        public void Initialize(
            IManualControlIntentSource source,
            MonoBehaviour liveTelemetrySource,
            MonoBehaviour simulatorRelay,
            MonoBehaviour simulatorFlightActionCoordinator,
            MonoBehaviour integratedManualFlightCoordinator)
        {
            inputSourceBehaviour = source as MonoBehaviour;
            telemetrySourceBehaviour = liveTelemetrySource;
            simulatorRelayBehaviour = simulatorRelay;
            simulatorFlightActionCoordinatorBehaviour = simulatorFlightActionCoordinator;
            integratedManualFlightCoordinatorBehaviour = integratedManualFlightCoordinator;
        }

        private void Awake()
        {
            arbiter = new FlightCommandArbiter(
                stateMachine,
                commandTimeoutMilliseconds: CommandTimeoutMilliseconds);
            safetyManager = new FlightSafetyManager(new IndoorDevelopmentSafetyProfile());
            stateMachine.Transitioned += OnControlModeTransition;
        }

        private void Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            shadowActive = ConsumeShadowTestIntentExtra();
#else
            shadowActive = false;
#endif
            if (!shadowActive)
            {
                enabled = false;
                return;
            }

            if (simulatorRelayBehaviour != null)
            {
                simulatorRelayBehaviour.enabled = false;
            }
            if (simulatorFlightActionCoordinatorBehaviour != null)
            {
                simulatorFlightActionCoordinatorBehaviour.enabled = false;
            }
            if (integratedManualFlightCoordinatorBehaviour != null)
            {
                integratedManualFlightCoordinatorBehaviour.enabled = false;
            }

            inputSource = inputSourceBehaviour as IManualControlIntentSource;
            telemetrySource = telemetrySourceBehaviour as IVehicleTelemetrySource;
            if (inputSource == null || telemetrySource == null)
            {
                Debug.LogError(
                    "DJI SHADOW TEST could not start: input or telemetry source is missing. "
                    + "SDK_DISPATCH=BLOCKED");
                enabled = false;
                return;
            }

            if (inputSourceBehaviour is QuestControllerInput controllerInput)
            {
                controllerInput.Initialize(this);
            }

            inputSource.ManualIntentUpdated += OnManualIntentUpdated;
            inputSource.OperatorIntentRaised += OnOperatorIntent;
            telemetrySource.TelemetryUpdated += OnTelemetryUpdated;
            OnTelemetryUpdated(telemetrySource.LatestTelemetry);
            Debug.LogWarning(
                "DJI SHADOW TEST ACTIVE: live Spark telemetry + Quest input + arbiter + safety; "
                + "Virtual Stick and all flight-action SDK dispatch are blocked.");
        }

        private void OnDisable()
        {
            Unsubscribe();
            hasInput = false;
            latestIntent = new ManualControlIntent(
                NormalizedFlightCommand.Neutral,
                false,
                false,
                false,
                false);
        }

        private void OnDestroy()
        {
            Unsubscribe();
            stateMachine.Transitioned -= OnControlModeTransition;
        }

        private void OnApplicationPause(bool paused)
        {
            if (!paused || !shadowActive)
            {
                return;
            }

            hasInput = false;
            submittedInputVersion = -1;
            CancelFlightActionConfirmation();
            Debug.LogWarning("DJI SHADOW TEST paused; shadow output reset to neutral.");
        }

        private void Update()
        {
            if (!shadowActive || arbiter == null || safetyManager == null)
            {
                return;
            }

            var now = MonotonicMilliseconds();
            UpdateControlState(now);

            if (stateMachine.Current == ControlMode.Ready
                && hasInput
                && latestIntent.ManualFlightAuthorized)
            {
                arbiter.TryManualTakeover(
                    now,
                    NextCorrelationId("shadow-manual"),
                    out _);
                submittedInputVersion = -1;
            }

            if ((stateMachine.Current == ControlMode.Manual
                    || stateMachine.Current == ControlMode.Assisted)
                && submittedInputVersion != inputVersion)
            {
                arbiter.SubmitManual(latestIntent, latestInputAtMilliseconds);
                submittedInputVersion = inputVersion;
            }

            var selected = arbiter.Select(now);
            var telemetryAge = latestTelemetryAtMilliseconds < 0
                ? long.MaxValue
                : Math.Max(0, now - latestTelemetryAtMilliseconds);
            var decision = safetyManager.Evaluate(
                selected,
                new FlightSafetyContext
                {
                    MonotonicMilliseconds = now,
                    AuthorityEpoch = arbiter.AuthorityEpoch,
                    ControlMode = stateMachine.Current,
                    IsConnected = liveTelemetry?.IsConnected == true,
                    HasTelemetry = latestTelemetryAtMilliseconds >= 0,
                    TelemetryAgeMilliseconds = telemetryAge,
                    HasFlightState = liveTelemetry?.HasFlightState == true,
                    HasBatteryState = liveTelemetry?.HasBatteryState == true,
                    BatteryPercent = liveTelemetry?.BatteryPercent ?? 0,
                    RelativeAltitudeMeters = liveTelemetry?.AltitudeMeters ?? 0f
                });

            var authorized = decision.Disposition != FlightSafetyDisposition.Neutralized
                && selected.Authorized
                && (selected.Source == FlightCommandSource.Manual
                    || selected.Source == FlightCommandSource.Assisted);
            LogShadowOutput(now, selected, decision, authorized);
        }

        private void UpdateControlState(long now)
        {
            var connected = liveTelemetry?.IsConnected == true;
            var telemetryFresh = connected
                && latestTelemetryAtMilliseconds >= 0
                && now - latestTelemetryAtMilliseconds <= TelemetryTimeoutMilliseconds;

            if (!connected)
            {
                if (stateMachine.Current != ControlMode.Disconnected)
                {
                    stateMachine.TryTransition(
                        ControlMode.Disconnected,
                        now,
                        "Live vehicle telemetry disconnected",
                        NextCorrelationId("shadow-disconnect"),
                        out _);
                }
                return;
            }

            if (stateMachine.Current == ControlMode.Disconnected)
            {
                stateMachine.TryTransition(
                    ControlMode.Preflight,
                    now,
                    "Live vehicle connected in shadow mode",
                    NextCorrelationId("shadow-connect"),
                    out _);
            }

            if (liveTelemetry.FailsafeEnabled || !telemetryFresh)
            {
                if (stateMachine.Current != ControlMode.Preflight
                    && stateMachine.Current != ControlMode.Failsafe)
                {
                    stateMachine.TryTransition(
                        ControlMode.Failsafe,
                        now,
                        liveTelemetry.FailsafeEnabled
                            ? "DJI telemetry reports failsafe"
                            : "Live vehicle telemetry timeout",
                        NextCorrelationId("shadow-failsafe"),
                        out _);
                }
                return;
            }

            if (stateMachine.Current == ControlMode.Failsafe)
            {
                stateMachine.TryTransition(
                    ControlMode.Preflight,
                    now,
                    "Live vehicle telemetry recovered",
                    NextCorrelationId("shadow-recover"),
                    out _);
            }

            if (stateMachine.Current == ControlMode.Preflight
                && liveTelemetry.HasFlightState)
            {
                stateMachine.TryTransition(
                    ControlMode.Ready,
                    now,
                    "Fresh flight telemetry received in shadow mode",
                    NextCorrelationId("shadow-ready"),
                    out _);
            }
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
                    NextCorrelationId("shadow-takeover"),
                    out _))
            {
                submittedInputVersion = -1;
            }
        }

        private void OnTelemetryUpdated(VehicleTelemetry telemetry)
        {
            liveTelemetry = telemetry;
            if (telemetry == null || !telemetry.IsConnected)
            {
                shadowAirborne = false;
            }
            else if (telemetry.IsFlying || telemetry.MotorsOn)
            {
                shadowAirborne = true;
            }
            latestTelemetryAtMilliseconds = telemetry == null
                ? -1
                : MonotonicMilliseconds();
            PublishShadowTelemetry();
        }

        private void OnOperatorIntent(OperatorIntentType intent)
        {
            switch (intent)
            {
                case OperatorIntentType.TakeoffConfirmationRequested:
                    RequestFlightActionConfirmation(FlightActionType.Takeoff);
                    break;
                case OperatorIntentType.LandConfirmationRequested:
                    RequestFlightActionConfirmation(FlightActionType.Land);
                    break;
                case OperatorIntentType.ReturnHomeConfirmationRequested:
                    RequestFlightActionConfirmation(FlightActionType.ReturnHome);
                    break;
                case OperatorIntentType.TakeoffConfirmed:
                    ConfirmFlightAction(FlightActionType.Takeoff);
                    break;
                case OperatorIntentType.LandConfirmed:
                    ConfirmFlightAction(FlightActionType.Land);
                    break;
                case OperatorIntentType.ReturnHomeConfirmed:
                    ConfirmFlightAction(FlightActionType.ReturnHome);
                    break;
                case OperatorIntentType.BrakeHover:
                case OperatorIntentType.ManualTakeover:
                    CancelFlightActionConfirmation();
                    break;
            }
        }

        public bool RequestFlightActionConfirmation(FlightActionType action)
        {
            var accepted = flightActionSession.TryRequestConfirmation(
                action,
                LatestTelemetry,
                IsTelemetryFresh(),
                Time.realtimeSinceStartupAsDouble,
                out var result);
            PublishFlightAction(result, "REQUEST");
            return accepted;
        }

        public bool ConfirmFlightAction(FlightActionType action)
        {
            if (!flightActionSession.TryConfirm(
                    action,
                    LatestTelemetry,
                    IsTelemetryFresh(),
                    Time.realtimeSinceStartupAsDouble,
                    out var pending))
            {
                PublishFlightAction(pending, "CONFIRM");
                return false;
            }

            PublishFlightAction(pending, "CONFIRM");
            flightActionSession.TryComplete(
                pending.RequestId,
                true,
                "SHADOW ONLY: validated; no DJI SDK action was called.",
                out var completed);
            if (action == FlightActionType.Takeoff)
            {
                shadowAirborne = true;
                PublishShadowTelemetry();
            }
            else if (action == FlightActionType.Land)
            {
                shadowAirborne = false;
                PublishShadowTelemetry();
            }
            PublishFlightAction(completed, "BLOCKED");
            return true;
        }

        public void CancelFlightActionConfirmation()
        {
            if (flightActionSession.CancelConfirmation(out var result))
            {
                PublishFlightAction(result, "CANCEL");
            }
        }

        private bool IsTelemetryFresh()
        {
            return liveTelemetry?.IsConnected == true
                && liveTelemetry.HasFlightState
                && latestTelemetryAtMilliseconds >= 0
                && MonotonicMilliseconds() - latestTelemetryAtMilliseconds <= 500;
        }

        private void PublishFlightAction(FlightActionResult result, string stage)
        {
            lastFlightAction = result;
            FlightActionStateChanged?.Invoke(result);
            Debug.LogWarning(
                "DJI SHADOW ACTION " + stage + ": "
                + result.Action + " | " + result.Status + " | " + result.Message
                + " | SDK_DISPATCH=BLOCKED");
        }

        private void LogShadowOutput(
            long now,
            FlightCommandEnvelope selected,
            FlightSafetyDecision decision,
            bool authorized)
        {
            var changed = authorized != lastLoggedAuthorized
                || decision.Disposition != lastLoggedDisposition
                || decision.Reason != lastLoggedReason
                || CommandChanged(decision.Command, lastLoggedCommand);
            var nowSeconds = now / 1000.0;
            if (!changed && nowSeconds < nextLogAt)
            {
                return;
            }

            nextLogAt = nowSeconds + LogIntervalSeconds;
            lastLoggedAuthorized = authorized;
            lastLoggedDisposition = decision.Disposition;
            lastLoggedReason = decision.Reason;
            lastLoggedCommand = decision.Command;
            Debug.Log(
                "DJI SHADOW OUTPUT: mode=" + stateMachine.Current
                + ", source=" + selected.Source
                + ", authorized=" + authorized
                + ", disposition=" + decision.Disposition
                + ", reason=" + decision.Reason
                + ", raw=" + FormatCommand(selected.Command)
                + ", safe=" + FormatCommand(decision.Command)
                + ", liveMotors=" + (liveTelemetry?.MotorsOn == true ? "ON" : "OFF")
                + ", liveFlying=" + (liveTelemetry?.IsFlying == true ? "YES" : "NO")
                + ", shadowPhase=" + (shadowAirborne ? "Airborne" : "Grounded")
                + ", SDK_DISPATCH=BLOCKED");
        }

        private void PublishShadowTelemetry()
        {
            if (liveTelemetry == null)
            {
                LatestTelemetry = null;
                TelemetryUpdated?.Invoke(null);
                return;
            }

            LatestTelemetry = ShadowVehicleTelemetry.Project(
                liveTelemetry,
                shadowAirborne,
                MonotonicMilliseconds());
            TelemetryUpdated?.Invoke(LatestTelemetry);
        }

        private void OnControlModeTransition(ControlModeTransition transition)
        {
            Debug.LogWarning(
                "DJI SHADOW MODE: " + transition.Previous + " -> " + transition.Next
                + " | " + transition.Reason
                + " | " + transition.CorrelationId
                + " | SDK_DISPATCH=BLOCKED");
        }

        private void Unsubscribe()
        {
            if (inputSource != null)
            {
                inputSource.ManualIntentUpdated -= OnManualIntentUpdated;
                inputSource.OperatorIntentRaised -= OnOperatorIntent;
            }
            if (telemetrySource != null)
            {
                telemetrySource.TelemetryUpdated -= OnTelemetryUpdated;
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static bool ConsumeShadowTestIntentExtra()
        {
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var intent = activity?.Call<AndroidJavaObject>("getIntent"))
                {
                    if (intent == null
                        || !intent.Call<bool>("getBooleanExtra", ShadowTestIntentExtra, false))
                    {
                        return false;
                    }

                    using (var clearedIntent = new AndroidJavaObject("android.content.Intent"))
                    {
                        activity.Call("setIntent", clearedIntent);
                    }
                    return true;
                }
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "DJI SHADOW TEST launch gate failed; SDK_DISPATCH=BLOCKED: " + exception);
                return false;
            }
        }
#endif

        private string NextCorrelationId(string prefix)
        {
            correlationSequence++;
            return prefix + "-" + correlationSequence;
        }

        private static bool CommandChanged(
            NormalizedFlightCommand left,
            NormalizedFlightCommand right)
        {
            const float tolerance = 0.02f;
            return Math.Abs(left.Roll - right.Roll) > tolerance
                || Math.Abs(left.Pitch - right.Pitch) > tolerance
                || Math.Abs(left.Yaw - right.Yaw) > tolerance
                || Math.Abs(left.Vertical - right.Vertical) > tolerance;
        }

        private static string FormatCommand(NormalizedFlightCommand command)
        {
            return "(" + command.Roll.ToString("0.00")
                + "," + command.Pitch.ToString("0.00")
                + "," + command.Yaw.ToString("0.00")
                + "," + command.Vertical.ToString("0.00") + ")";
        }

        private static long MonotonicMilliseconds()
        {
            return (long)(Time.realtimeSinceStartupAsDouble * 1000.0);
        }
    }
}
