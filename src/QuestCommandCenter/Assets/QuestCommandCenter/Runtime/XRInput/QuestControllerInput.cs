using System;
using QuestCommandCenter.VehicleCore;
using UnityEngine;
using UnityEngine.InputSystem;

namespace QuestCommandCenter.XRInput
{
    /// <summary>
    /// Samples the authoritative Quest Mode 2 control map and emits generic
    /// operator intent only. Nothing in this component references a DJI adapter.
    /// </summary>
    public sealed class QuestControllerInput : MonoBehaviour, IManualControlIntentSource
    {
        private InputAction leftStick;
        private InputAction rightStick;
        private InputAction returnHomeButton;
        private InputAction confirmButton;
        private InputAction brakeButton;
        private InputAction cameraModifier;
        private InputAction manualTakeover;
        private InputAction uiSelect;
        private InputAction manualFlightEnable;
        [SerializeField] private MonoBehaviour telemetrySourceBehaviour;
        private IVehicleTelemetrySource telemetrySource;
        private readonly SimulatorFlightGestureRecognizer simulatorGestures =
            new SimulatorFlightGestureRecognizer();
        private double nextTakeoffGestureDiagnosticAt;
        private double? flightUnlockHoldStartedAt;
        private bool flightUnlockHoldTriggered;

        private const double FlightUnlockHoldSeconds = 3.0;
        private const float FlightUnlockNeutralThreshold = 0.15f;

        public ManualControlIntent LatestManualIntent { get; private set; } =
            new ManualControlIntent(NormalizedFlightCommand.Neutral, false, false, false, false);

        public event Action<ManualControlIntent> ManualIntentUpdated;
        public event Action<OperatorIntentType> OperatorIntentRaised;

        public void Initialize(MonoBehaviour source)
        {
            telemetrySourceBehaviour = source;
            telemetrySource = source as IVehicleTelemetrySource;
        }

        private void Awake()
        {
            leftStick = CreateValueAction("Left Mode 2 Stick", "Vector2", "<XRController>{LeftHand}/thumbstick");
            rightStick = CreateValueAction("Right Mode 2 Stick", "Vector2", "<XRController>{RightHand}/thumbstick");
            returnHomeButton = CreateButtonAction("Return Home Hold", "<XRController>{LeftHand}/primaryButton");
            confirmButton = CreateButtonAction("Select Confirm", "<XRController>{RightHand}/primaryButton");
            brakeButton = CreateButtonAction("Brake Hover", "<XRController>{RightHand}/secondaryButton");
            cameraModifier = CreateButtonAction("Camera Modifier", "<XRController>{LeftHand}/triggerPressed");
            manualTakeover = CreateButtonAction("Manual Takeover", "<XRController>{LeftHand}/gripPressed");
            uiSelect = CreateButtonAction("UI Select", "<XRController>{RightHand}/triggerPressed");
            manualFlightEnable = CreateButtonAction("Manual Flight Enable", "<XRController>{RightHand}/gripPressed");
        }

        private void OnEnable()
        {
            if (telemetrySource == null)
            {
                telemetrySource = telemetrySourceBehaviour as IVehicleTelemetrySource;
            }
            SetActionsEnabled(true);
        }

        private void OnDisable()
        {
            SetActionsEnabled(false);
            simulatorGestures.Reset();
            flightUnlockHoldStartedAt = null;
            flightUnlockHoldTriggered = false;
            LatestManualIntent = new ManualControlIntent(
                NormalizedFlightCommand.Neutral,
                false,
                false,
                false,
                false);
            ManualIntentUpdated?.Invoke(LatestManualIntent);
        }

        private void OnDestroy()
        {
            DisposeAction(leftStick);
            DisposeAction(rightStick);
            DisposeAction(returnHomeButton);
            DisposeAction(confirmButton);
            DisposeAction(brakeButton);
            DisposeAction(cameraModifier);
            DisposeAction(manualTakeover);
            DisposeAction(uiSelect);
            DisposeAction(manualFlightEnable);
        }

        private void Update()
        {
            if (leftStick == null || rightStick == null)
            {
                return;
            }

            var now = Time.realtimeSinceStartupAsDouble;
            var left = leftStick.ReadValue<Vector2>();
            var right = rightStick.ReadValue<Vector2>();
            var telemetry = telemetrySource?.LatestTelemetry;
            UpdateFlightUnlockHold(now, left, right);
            var hasFlightState = telemetry?.HasFlightState == true;
            var grounded = hasFlightState && !telemetry.IsFlying && !telemetry.MotorsOn;
            var airborne = hasFlightState
                && telemetry.IsFlying
                && string.Equals(telemetry.FlightMode, "Airborne", StringComparison.OrdinalIgnoreCase);
            var action = simulatorGestures.Update(
                now,
                left.x,
                left.y,
                right.x,
                right.y,
                returnHomeButton.IsPressed(),
                confirmButton.IsPressed(),
                grounded,
                airborne,
                hasFlightState && telemetry.IsFlying);
            if (grounded
                && left.y <= -0.3f
                && right.y <= -0.3f
                && now >= nextTakeoffGestureDiagnosticAt)
            {
                nextTakeoffGestureDiagnosticAt = now + 0.5;
                Debug.Log(
                    "Takeoff stick sample: left=("
                    + left.x.ToString("0.00") + "," + left.y.ToString("0.00")
                    + "), right=(" + right.x.ToString("0.00") + "," + right.y.ToString("0.00")
                    + "), inwardRecognized=" + simulatorGestures.IsTakeoffCombinationActive);
            }
            if (action.HasValue)
            {
                RaiseIntent(RequestIntent(action.Value));
                RaiseIntent(ConfirmedIntent(action.Value));
            }

            if (confirmButton.WasPressedThisFrame())
            {
                RaiseIntent(OperatorIntentType.Confirm);
            }

            if (brakeButton.WasPressedThisFrame())
            {
                RaiseIntent(OperatorIntentType.BrakeHover);
            }
            if (manualTakeover.WasPressedThisFrame())
            {
                RaiseIntent(OperatorIntentType.ManualTakeover);
            }
            if (uiSelect.WasPressedThisFrame())
            {
                RaiseIntent(OperatorIntentType.UiSelect);
            }

            LatestManualIntent = Mode2InputMapper.Map(
                left.x,
                left.y,
                right.x,
                right.y,
                manualFlightEnable.IsPressed() && !simulatorGestures.IsTakeoffCombinationActive,
                brakeButton.IsPressed(),
                manualTakeover.WasPressedThisFrame(),
                cameraModifier.IsPressed());
            ManualIntentUpdated?.Invoke(LatestManualIntent);
        }

        private static OperatorIntentType RequestIntent(FlightActionType action)
        {
            switch (action)
            {
                case FlightActionType.Takeoff:
                    return OperatorIntentType.TakeoffConfirmationRequested;
                case FlightActionType.Land:
                    return OperatorIntentType.LandConfirmationRequested;
                default:
                    return OperatorIntentType.ReturnHomeConfirmationRequested;
            }
        }

        private static OperatorIntentType ConfirmedIntent(FlightActionType action)
        {
            switch (action)
            {
                case FlightActionType.Takeoff:
                    return OperatorIntentType.TakeoffConfirmed;
                case FlightActionType.Land:
                    return OperatorIntentType.LandConfirmed;
                default:
                    return OperatorIntentType.ReturnHomeConfirmed;
            }
        }

        private void RaiseIntent(OperatorIntentType intent)
        {
            Debug.Log("Quest operator intent: " + intent);
            OperatorIntentRaised?.Invoke(intent);
        }

        private void UpdateFlightUnlockHold(double now, Vector2 left, Vector2 right)
        {
            var neutral = Mathf.Abs(left.x) <= FlightUnlockNeutralThreshold
                && Mathf.Abs(left.y) <= FlightUnlockNeutralThreshold
                && Mathf.Abs(right.x) <= FlightUnlockNeutralThreshold
                && Mathf.Abs(right.y) <= FlightUnlockNeutralThreshold;
            var held = neutral
                && confirmButton.IsPressed()
                && manualTakeover.IsPressed()
                && manualFlightEnable.IsPressed();
            if (!held)
            {
                flightUnlockHoldStartedAt = null;
                flightUnlockHoldTriggered = false;
                return;
            }

            if (!flightUnlockHoldStartedAt.HasValue)
            {
                flightUnlockHoldStartedAt = now;
                return;
            }

            if (flightUnlockHoldTriggered
                || now - flightUnlockHoldStartedAt.Value < FlightUnlockHoldSeconds)
            {
                return;
            }

            flightUnlockHoldTriggered = true;
            RaiseIntent(OperatorIntentType.FlightControlUnlockHoldCompleted);
        }

        private void SetActionsEnabled(bool enabled)
        {
            SetActionEnabled(leftStick, enabled);
            SetActionEnabled(rightStick, enabled);
            SetActionEnabled(returnHomeButton, enabled);
            SetActionEnabled(confirmButton, enabled);
            SetActionEnabled(brakeButton, enabled);
            SetActionEnabled(cameraModifier, enabled);
            SetActionEnabled(manualTakeover, enabled);
            SetActionEnabled(uiSelect, enabled);
            SetActionEnabled(manualFlightEnable, enabled);
        }

        private static InputAction CreateValueAction(string name, string controlType, string binding)
        {
            return new InputAction(name, InputActionType.Value, binding, expectedControlType: controlType);
        }

        private static InputAction CreateButtonAction(string name, string binding)
        {
            return new InputAction(name, InputActionType.Button, binding);
        }

        private static void SetActionEnabled(InputAction action, bool enabled)
        {
            if (action == null)
            {
                return;
            }
            if (enabled)
            {
                action.Enable();
            }
            else
            {
                action.Disable();
            }
        }

        private static void DisposeAction(InputAction action)
        {
            action?.Dispose();
        }
    }
}
