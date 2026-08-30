using System;

namespace QuestCommandCenter.VehicleCore
{
    public enum OperatorIntentType
    {
        Confirm,
        BrakeHover,
        ManualTakeover,
        TakeoffConfirmationRequested,
        TakeoffConfirmed,
        LandConfirmationRequested,
        LandConfirmed,
        ReturnHomeConfirmationRequested,
        ReturnHomeConfirmed,
        UiSelect,
        FlightControlUnlockHoldCompleted
    }

    /// <summary>
    /// Vehicle-independent result of one controller sample. This is pilot intent,
    /// not permission to call an aircraft adapter.
    /// </summary>
    public struct ManualControlIntent
    {
        public ManualControlIntent(
            NormalizedFlightCommand command,
            bool manualFlightAuthorized,
            bool brakeHoverRequested,
            bool manualTakeoverRequested,
            bool cameraModifierActive)
        {
            Command = command;
            ManualFlightAuthorized = manualFlightAuthorized;
            BrakeHoverRequested = brakeHoverRequested;
            ManualTakeoverRequested = manualTakeoverRequested;
            CameraModifierActive = cameraModifierActive;
        }

        public NormalizedFlightCommand Command { get; }
        public bool ManualFlightAuthorized { get; }
        public bool BrakeHoverRequested { get; }
        public bool ManualTakeoverRequested { get; }
        public bool CameraModifierActive { get; }
    }

    public interface IManualControlIntentSource
    {
        ManualControlIntent LatestManualIntent { get; }
        event Action<ManualControlIntent> ManualIntentUpdated;
        event Action<OperatorIntentType> OperatorIntentRaised;
    }

    public static class Mode2InputMapper
    {
        public const float DefaultDeadZone = 0.12f;

        public static ManualControlIntent Map(
            float leftX,
            float leftY,
            float rightX,
            float rightY,
            bool rightGripHeld,
            bool brakeHoverRequested,
            bool manualTakeoverRequested,
            bool cameraModifierActive,
            float deadZone = DefaultDeadZone)
        {
            ValidateAxis(leftX, nameof(leftX));
            ValidateAxis(leftY, nameof(leftY));
            ValidateAxis(rightX, nameof(rightX));
            ValidateAxis(rightY, nameof(rightY));
            if (deadZone < 0f || deadZone >= 1f || float.IsNaN(deadZone))
            {
                throw new ArgumentOutOfRangeException(nameof(deadZone));
            }

            var authorized = rightGripHeld && !brakeHoverRequested;
            var command = authorized
                ? new NormalizedFlightCommand(
                    ApplyDeadZone(rightX, deadZone),
                    ApplyDeadZone(rightY, deadZone),
                    ApplyDeadZone(leftX, deadZone),
                    ApplyDeadZone(leftY, deadZone))
                : NormalizedFlightCommand.Neutral;

            return new ManualControlIntent(
                command,
                authorized,
                brakeHoverRequested,
                manualTakeoverRequested,
                cameraModifierActive);
        }

        private static float ApplyDeadZone(float value, float deadZone)
        {
            var magnitude = Math.Abs(value);
            if (magnitude <= deadZone)
            {
                return 0f;
            }

            var scaled = (magnitude - deadZone) / (1f - deadZone);
            return Math.Sign(value) * Math.Min(1f, scaled);
        }

        private static void ValidateAxis(float value, string parameterName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }
}
