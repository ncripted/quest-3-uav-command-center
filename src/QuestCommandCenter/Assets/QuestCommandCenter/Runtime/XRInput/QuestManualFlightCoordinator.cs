using System;
using System.Text;
using QuestCommandCenter.VehicleCore;
using UnityEngine;

namespace QuestCommandCenter.XRInput
{
    /// <summary>
    /// Integrated production control boundary. It selects simulator or live
    /// vehicle context, owns manual authority, applies preflight/arbitration/
    /// safety, and is the only Quest input path allowed to reach a real adapter.
    /// </summary>
    public sealed class QuestManualFlightCoordinator : MonoBehaviour,
        IManualFlightControlState,
        IFlightActionControl
    {
        private const long CommandTimeoutMilliseconds = 250;
        private const long TelemetryTimeoutMilliseconds = 1000;
        private const double DispatchIntervalSeconds = 0.05;
        private const double EnableRequestTimeoutSeconds = 5.0;

        [SerializeField] private MonoBehaviour inputSourceBehaviour;
        [SerializeField] private MonoBehaviour liveTelemetrySourceBehaviour;
        [SerializeField] private MonoBehaviour manualControlBehaviour;
        [SerializeField] private MonoBehaviour liveFlightActionBehaviour;
        [SerializeField] private MonoBehaviour simulatorRelayBehaviour;
        [SerializeField] private MonoBehaviour simulatorFlightActionCoordinatorBehaviour;

        private readonly ControlModeStateMachine stateMachine = new ControlModeStateMachine();
        private readonly FlightPreflightGate preflightGate = new FlightPreflightGate();
        private FlightCommandArbiter arbiter;
        private FlightSafetyManager safetyManager;
        private IManualControlIntentSource inputSource;
        private IVehicleTelemetrySource liveTelemetrySource;
        private IManualFlightControl manualControl;
        private IFlightActionControl liveFlightActions;
        private IFlightActionControl simulatorFlightActions;
        private VehicleTelemetry liveTelemetry;
        private ManualControlIntent latestIntent = new ManualControlIntent(
            NormalizedFlightCommand.Neutral,
            false,
            false,
            false,
            false);
        private FlightPreflightResult preflightResult;
        private FlightActionResult lastFlightAction;
        private long latestTelemetryAtMilliseconds = -1;
        private long latestInputAtMilliseconds;
        private long correlationSequence;
        private long dispatchSequence;
        private long localFlightActionSequence;
        private double nextDispatchAt;
        private double enableRequestDeadline;
        private bool unlockHoldCompleted;
        private bool awaitingControlRelease;
        private bool enableRequestPending;
        private string currentStatus = "REAL FLIGHT LOCKED\nWaiting for a live Spark connection.";
        private string statusBadge = "FLIGHT LOCKED";

        public ControlMode ControlMode => stateMachine.Current;
        public bool IsLiveVehicleSelected { get; private set; }
        public bool IsFlightControlReady => IsLiveVehicleSelected
            && manualControl?.IsManualControlEnabled == true
            && (stateMachine.Current == ControlMode.Ready
                || stateMachine.Current == ControlMode.Manual
                || stateMachine.Current == ControlMode.Assisted);
        public string StatusBadge => statusBadge;
        public string CurrentStatus => currentStatus;
        public bool IsFlightActionPending => SelectedFlightActions?.IsFlightActionPending == true;
        public FlightActionResult LastFlightAction => lastFlightAction
            ?? SelectedFlightActions?.LastFlightAction;

        public event Action<string> StatusChanged;
        public event Action<FlightActionResult> FlightActionStateChanged;

        private IFlightActionControl SelectedFlightActions => IsLiveVehicleSelected
            ? liveFlightActions
            : simulatorFlightActions;

        public void Initialize(
            IManualControlIntentSource source,
            MonoBehaviour liveTelemetry,
            MonoBehaviour manualBackend,
            MonoBehaviour liveActions,
            MonoBehaviour simulatorRelay,
            MonoBehaviour simulatorFlightActionCoordinator)
        {
            Unsubscribe();
            inputSourceBehaviour = source as MonoBehaviour;
            liveTelemetrySourceBehaviour = liveTelemetry;
            manualControlBehaviour = manualBackend;
            liveFlightActionBehaviour = liveActions;
            simulatorRelayBehaviour = simulatorRelay;
            simulatorFlightActionCoordinatorBehaviour = simulatorFlightActionCoordinator;
            BindReferences();
            Subscribe();
            RenderStatus();
        }

        private void Awake()
        {
            arbiter = new FlightCommandArbiter(
                stateMachine,
                commandTimeoutMilliseconds: CommandTimeoutMilliseconds);
            safetyManager = new FlightSafetyManager(new IndoorDevelopmentSafetyProfile());
            stateMachine.Transitioned += OnControlModeTransition;
        }

        private void OnEnable()
        {
            BindReferences();
            Subscribe();
        }

        private void Start()
        {
            OnLiveTelemetryUpdated(liveTelemetrySource?.LatestTelemetry);
            RenderStatus();
        }

        private void OnDisable()
        {
            if (Application.isPlaying)
            {
                DisableLiveControl("coordinator disabled");
            }
            Unsubscribe();
        }

        private void OnDestroy()
        {
            Unsubscribe();
            stateMachine.Transitioned -= OnControlModeTransition;
        }

        private void OnApplicationPause(bool paused)
        {
            if (!paused)
            {
                return;
            }

            SendNeutral();
            DisableLiveControl("application paused");
            ResetUnlock();
            RenderStatus();
        }

        private void Update()
        {
            if (!IsLiveVehicleSelected || manualControl == null)
            {
                return;
            }

            var now = MonotonicMilliseconds();
            var telemetryFresh = IsTelemetryFresh(now);
            if (!telemetryFresh || liveTelemetry?.FailsafeEnabled == true)
            {
                EnterFailsafe(
                    liveTelemetry?.FailsafeEnabled == true
                        ? "DJI FAILSAFE ACTIVE"
                        : "TELEMETRY TIMEOUT");
                RenderStatus();
                return;
            }

            if (stateMachine.Current == ControlMode.Failsafe)
            {
                if (manualControl.IsManualControlEnabled)
                {
                    manualControl.RequestManualControlMode(false);
                    return;
                }

                stateMachine.TryTransition(
                    ControlMode.Preflight,
                    now,
                    "Fresh safe telemetry recovered; new preflight required",
                    NextCorrelationId("recover"),
                    out _);
                ResetUnlock();
            }

            if (stateMachine.Current == ControlMode.Preflight)
            {
                UpdatePreflight(telemetryFresh);
                TryAdvancePreflight(now);
            }

            if (!IsFlightControlReady)
            {
                RenderStatus();
                return;
            }

            if (stateMachine.Current == ControlMode.Ready
                && latestIntent.ManualFlightAuthorized)
            {
                arbiter.TryManualTakeover(
                    now,
                    NextCorrelationId("manual"),
                    out _);
            }

            if (stateMachine.Current == ControlMode.Manual
                || stateMachine.Current == ControlMode.Assisted)
            {
                arbiter.SubmitManual(latestIntent, latestInputAtMilliseconds);
            }

            if (Time.realtimeSinceStartupAsDouble >= nextDispatchAt)
            {
                nextDispatchAt = Time.realtimeSinceStartupAsDouble + DispatchIntervalSeconds;
                DispatchSelectedCommand(now);
            }

            RenderStatus();
        }

        private void UpdatePreflight(bool telemetryFresh)
        {
            // The deliberate unlock chord includes the right-grip dead-man. Only
            // the stick axes belong in this preflight check; TryAdvancePreflight
            // separately requires the operator to release the chord before the
            // adapter is allowed to enable manual control.
            var controllerAxesNeutral = CommandIsNeutral(latestIntent.Command);
            preflightResult = preflightGate.Evaluate(new FlightPreflightInput
            {
                Telemetry = liveTelemetry,
                TelemetryFresh = telemetryFresh,
                ManualControlInterfaceReady = manualControl?.IsManualControlInterfaceReady == true,
                ControllerAxesNeutral = controllerAxesNeutral
            });

            if (!preflightResult.Passed)
            {
                unlockHoldCompleted = false;
                awaitingControlRelease = false;
            }
        }

        private void TryAdvancePreflight(long now)
        {
            if (preflightResult?.Passed != true)
            {
                return;
            }

            if (unlockHoldCompleted)
            {
                awaitingControlRelease = true;
                unlockHoldCompleted = false;
            }

            if (awaitingControlRelease)
            {
                if (latestIntent.ManualFlightAuthorized)
                {
                    return;
                }

                awaitingControlRelease = false;
                enableRequestPending = manualControl.RequestManualControlMode(true);
                enableRequestDeadline = Time.realtimeSinceStartupAsDouble
                    + EnableRequestTimeoutSeconds;
                if (!enableRequestPending)
                {
                    Debug.LogError("Real-flight preflight passed, but manual-control enable was rejected.");
                }
            }

            if (manualControl.IsManualControlEnabled)
            {
                enableRequestPending = false;
                stateMachine.TryTransition(
                    ControlMode.Ready,
                    now,
                    "Operator preflight acknowledged and adapter enabled",
                    NextCorrelationId("ready"),
                    out _);
                SendNeutral();
            }
            else if (enableRequestPending
                && Time.realtimeSinceStartupAsDouble > enableRequestDeadline)
            {
                enableRequestPending = false;
                Debug.LogError("Manual-control enable timed out; preflight remains locked.");
            }
        }

        private void DispatchSelectedCommand(long now)
        {
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

            if (!manualControl.SubmitManualFlightCommand(
                    ++dispatchSequence,
                    decision.Command,
                    authorized))
            {
                EnterFailsafe("ADAPTER REJECTED CONTROL COMMAND");
                return;
            }

            if (decision.Reason != FlightSafetyReason.None
                && decision.Reason != FlightSafetyReason.ManualAuthorizationMissing)
            {
                Debug.LogWarning(
                    "Manual flight safety decision: " + decision.Disposition
                    + " / " + decision.Reason);
            }
        }

        private void OnManualIntentUpdated(ManualControlIntent intent)
        {
            latestIntent = intent;
            latestInputAtMilliseconds = MonotonicMilliseconds();
            if (intent.BrakeHoverRequested)
            {
                SendNeutral();
            }
            if (intent.ManualTakeoverRequested && IsFlightControlReady)
            {
                arbiter.TryManualTakeover(
                    latestInputAtMilliseconds,
                    NextCorrelationId("takeover"),
                    out _);
            }
        }

        private void OnOperatorIntent(OperatorIntentType intent)
        {
            if (intent == OperatorIntentType.FlightControlUnlockHoldCompleted)
            {
                if (IsLiveVehicleSelected && preflightResult?.Passed == true)
                {
                    unlockHoldCompleted = true;
                    Debug.LogWarning(
                        "Outdoor preflight acknowledgement received; waiting for control release.");
                }
                else
                {
                    Debug.LogWarning(
                        "Flight-control unlock hold ignored because live preflight is not complete.");
                }
                return;
            }

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
                    SelectedFlightActions?.CancelFlightActionConfirmation();
                    SendNeutral();
                    break;
            }
        }

        public bool RequestFlightActionConfirmation(FlightActionType action)
        {
            if (!IsLiveVehicleSelected)
            {
                return simulatorFlightActions?.RequestFlightActionConfirmation(action) == true;
            }
            if (!IsFlightControlReady)
            {
                return RejectFlightAction(action, "Complete outdoor preflight before aircraft actions.");
            }
            return liveFlightActions?.RequestFlightActionConfirmation(action) == true;
        }

        public bool ConfirmFlightAction(FlightActionType action)
        {
            if (!IsLiveVehicleSelected)
            {
                return simulatorFlightActions?.ConfirmFlightAction(action) == true;
            }
            if (!IsFlightControlReady)
            {
                return RejectFlightAction(action, "Real flight control is locked.");
            }
            return liveFlightActions?.ConfirmFlightAction(action) == true;
        }

        public void CancelFlightActionConfirmation()
        {
            SelectedFlightActions?.CancelFlightActionConfirmation();
        }

        private bool RejectFlightAction(FlightActionType action, string message)
        {
            lastFlightAction = new FlightActionResult(
                ++localFlightActionSequence,
                action,
                FlightActionStatus.Rejected,
                message);
            FlightActionStateChanged?.Invoke(lastFlightAction);
            Debug.LogWarning("Flight action blocked by integrated preflight: " + action);
            return false;
        }

        private void OnLiveTelemetryUpdated(VehicleTelemetry telemetry)
        {
            liveTelemetry = telemetry;
            latestTelemetryAtMilliseconds = telemetry?.IsConnected == true
                ? MonotonicMilliseconds()
                : -1;
            var selectLive = telemetry?.IsConnected == true;
            if (selectLive != IsLiveVehicleSelected)
            {
                SelectLiveVehicle(selectLive);
            }
            RenderStatus();
        }

        private void SelectLiveVehicle(bool selected)
        {
            IsLiveVehicleSelected = selected;
            ResetUnlock();
            if (simulatorRelayBehaviour != null)
            {
                simulatorRelayBehaviour.enabled = !selected;
            }
            if (simulatorFlightActionCoordinatorBehaviour != null)
            {
                simulatorFlightActionCoordinatorBehaviour.enabled = !selected;
            }
            if (inputSourceBehaviour is QuestControllerInput controllerInput)
            {
                controllerInput.Initialize(
                    selected ? liveTelemetrySourceBehaviour : simulatorRelayBehaviour);
            }

            var now = MonotonicMilliseconds();
            if (selected)
            {
                if (stateMachine.Current == ControlMode.Disconnected)
                {
                    stateMachine.TryTransition(
                        ControlMode.Preflight,
                        now,
                        "Live Spark selected",
                        NextCorrelationId("connect"),
                        out _);
                }
                Debug.LogWarning(
                    "Live Spark selected; real flight remains locked pending outdoor preflight.");
            }
            else
            {
                DisableLiveControl("live Spark disconnected");
                if (stateMachine.Current != ControlMode.Disconnected)
                {
                    stateMachine.TryTransition(
                        ControlMode.Disconnected,
                        now,
                        "Live Spark disconnected; simulator restored",
                        NextCorrelationId("disconnect"),
                        out _);
                }
            }
        }

        private void EnterFailsafe(string reason)
        {
            SendNeutral();
            if (manualControl?.IsManualControlEnabled == true)
            {
                manualControl.RequestManualControlMode(false);
            }
            enableRequestPending = false;
            ResetUnlock();
            if (stateMachine.Current != ControlMode.Disconnected
                && stateMachine.Current != ControlMode.Failsafe)
            {
                stateMachine.TryTransition(
                    ControlMode.Failsafe,
                    MonotonicMilliseconds(),
                    reason,
                    NextCorrelationId("failsafe"),
                    out _);
            }
        }

        private void DisableLiveControl(string reason)
        {
            SendNeutral();
            if (manualControl?.IsManualControlEnabled == true)
            {
                manualControl.RequestManualControlMode(false);
                Debug.LogWarning("Manual flight disabled: " + reason);
            }
            enableRequestPending = false;
        }

        private void SendNeutral()
        {
            if (manualControl?.IsManualControlEnabled != true)
            {
                return;
            }
            manualControl.SubmitManualFlightCommand(
                ++dispatchSequence,
                NormalizedFlightCommand.Neutral,
                false);
        }

        private bool IsTelemetryFresh(long now)
        {
            return liveTelemetry?.IsConnected == true
                && liveTelemetry.HasFlightState
                && latestTelemetryAtMilliseconds >= 0
                && now - latestTelemetryAtMilliseconds <= TelemetryTimeoutMilliseconds;
        }

        private void OnManualControlModeChanged(bool enabled)
        {
            enableRequestPending = false;
            if (!enabled
                && IsLiveVehicleSelected
                && (stateMachine.Current == ControlMode.Ready
                    || stateMachine.Current == ControlMode.Manual
                    || stateMachine.Current == ControlMode.Assisted))
            {
                EnterFailsafe("MANUAL CONTROL DISABLED");
            }
            RenderStatus();
        }

        private void OnLiveFlightActionChanged(FlightActionResult result)
        {
            if (!IsLiveVehicleSelected)
            {
                return;
            }
            lastFlightAction = result;
            FlightActionStateChanged?.Invoke(result);
        }

        private void OnSimulatorFlightActionChanged(FlightActionResult result)
        {
            if (IsLiveVehicleSelected)
            {
                return;
            }
            lastFlightAction = result;
            FlightActionStateChanged?.Invoke(result);
        }

        private void BindReferences()
        {
            inputSource = inputSourceBehaviour as IManualControlIntentSource;
            liveTelemetrySource = liveTelemetrySourceBehaviour as IVehicleTelemetrySource;
            manualControl = manualControlBehaviour as IManualFlightControl;
            liveFlightActions = liveFlightActionBehaviour as IFlightActionControl;
            simulatorFlightActions = simulatorRelayBehaviour as IFlightActionControl;
        }

        private void Subscribe()
        {
            if (inputSource != null)
            {
                inputSource.ManualIntentUpdated -= OnManualIntentUpdated;
                inputSource.ManualIntentUpdated += OnManualIntentUpdated;
                inputSource.OperatorIntentRaised -= OnOperatorIntent;
                inputSource.OperatorIntentRaised += OnOperatorIntent;
            }
            if (liveTelemetrySource != null)
            {
                liveTelemetrySource.TelemetryUpdated -= OnLiveTelemetryUpdated;
                liveTelemetrySource.TelemetryUpdated += OnLiveTelemetryUpdated;
            }
            if (manualControl != null)
            {
                manualControl.ManualControlModeChanged -= OnManualControlModeChanged;
                manualControl.ManualControlModeChanged += OnManualControlModeChanged;
            }
            if (liveFlightActions != null)
            {
                liveFlightActions.FlightActionStateChanged -= OnLiveFlightActionChanged;
                liveFlightActions.FlightActionStateChanged += OnLiveFlightActionChanged;
            }
            if (simulatorFlightActions != null)
            {
                simulatorFlightActions.FlightActionStateChanged -= OnSimulatorFlightActionChanged;
                simulatorFlightActions.FlightActionStateChanged += OnSimulatorFlightActionChanged;
            }
        }

        private void Unsubscribe()
        {
            if (inputSource != null)
            {
                inputSource.ManualIntentUpdated -= OnManualIntentUpdated;
                inputSource.OperatorIntentRaised -= OnOperatorIntent;
            }
            if (liveTelemetrySource != null)
            {
                liveTelemetrySource.TelemetryUpdated -= OnLiveTelemetryUpdated;
            }
            if (manualControl != null)
            {
                manualControl.ManualControlModeChanged -= OnManualControlModeChanged;
            }
            if (liveFlightActions != null)
            {
                liveFlightActions.FlightActionStateChanged -= OnLiveFlightActionChanged;
            }
            if (simulatorFlightActions != null)
            {
                simulatorFlightActions.FlightActionStateChanged -= OnSimulatorFlightActionChanged;
            }
        }

        private void ResetUnlock()
        {
            unlockHoldCompleted = false;
            awaitingControlRelease = false;
            enableRequestPending = false;
            preflightResult = null;
        }

        private void RenderStatus()
        {
            var builder = new StringBuilder();
            string badge;
            if (!IsLiveVehicleSelected)
            {
                badge = "SIMULATOR / REAL FLIGHT LOCKED";
                builder.AppendLine("REAL AIRCRAFT CONTROL LOCKED");
                builder.Append("Connect Spark for the on-headset outdoor preflight checklist.");
            }
            else if (stateMachine.Current == ControlMode.Failsafe)
            {
                badge = "FAILSAFE — CONTROL DISABLED";
                builder.AppendLine("FAILSAFE — REAL CONTROL DISABLED");
                builder.Append("Release controls and restore link/telemetry. A complete new preflight is required.");
            }
            else if (IsFlightControlReady)
            {
                badge = stateMachine.Current == ControlMode.Manual
                    ? "MANUAL — RIGHT GRIP ENABLED"
                    : "FIELD READY — RIGHT GRIP DEAD-MAN";
                builder.AppendLine("FIELD CONTROL READY — OUTDOORS ONLY");
                builder.AppendLine("RIGHT GRIP: manual flight    B: brake/hover    LEFT GRIP: takeover");
                builder.AppendLine("STICKS DOWN/IN: takeoff    HOLD X: RTH    HOLD A: land");
                builder.Append("LIMITS: 0.35 m/s horizontal | 0.20 m/s vertical | 9 deg/s yaw | 2 m altitude");
            }
            else
            {
                badge = enableRequestPending
                    ? "PREFLIGHT — ENABLING CONTROL"
                    : awaitingControlRelease
                        ? "PREFLIGHT — RELEASE CONTROLS"
                        : "PREFLIGHT — FLIGHT LOCKED";
                builder.AppendLine("OUTDOOR FIELD PREFLIGHT — MOTORS LOCKED");
                if (preflightResult == null)
                {
                    builder.Append("Waiting for normalized Spark telemetry and Virtual Stick status.");
                }
                else
                {
                    builder.AppendLine(
                        "CHECKS  " + preflightResult.PassedCount + "/" + preflightResult.Checks.Count);
                    foreach (var check in preflightResult.Checks)
                    {
                        builder.Append(check.Passed ? "[OK] " : "[--] ")
                            .Append(check.Label).Append("  ").AppendLine(check.Detail);
                    }
                    if (preflightResult.Passed)
                    {
                        builder.AppendLine("Confirm clear outdoor area and safe launch direction.");
                        builder.Append("CENTER STICKS + HOLD BOTH GRIPS + A FOR 3 SEC, THEN RELEASE.");
                    }
                }
            }

            var nextStatus = builder.ToString().TrimEnd();
            if (nextStatus == currentStatus && badge == statusBadge)
            {
                return;
            }
            currentStatus = nextStatus;
            statusBadge = badge;
            StatusChanged?.Invoke(currentStatus);
        }

        private void OnControlModeTransition(ControlModeTransition transition)
        {
            Debug.LogWarning(
                "CONTROL MODE: " + transition.Previous + " -> " + transition.Next
                + " | " + transition.Reason + " | " + transition.CorrelationId);
            RenderStatus();
        }

        private string NextCorrelationId(string prefix)
        {
            return prefix + "-" + (++correlationSequence);
        }

        private static bool CommandIsNeutral(NormalizedFlightCommand command)
        {
            const float tolerance = 0.001f;
            return Math.Abs(command.Roll) <= tolerance
                && Math.Abs(command.Pitch) <= tolerance
                && Math.Abs(command.Yaw) <= tolerance
                && Math.Abs(command.Vertical) <= tolerance;
        }

        private static long MonotonicMilliseconds()
        {
            return (long)(Time.realtimeSinceStartupAsDouble * 1000.0);
        }
    }
}
