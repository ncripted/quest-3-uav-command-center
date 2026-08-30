using System;

namespace QuestCommandCenter.VehicleCore
{
    /// <summary>
    /// Recognizes deliberate simulator-only high-level flight gestures. It does
    /// not dispatch commands and has no vehicle-adapter dependency.
    /// </summary>
    public sealed class SimulatorFlightGestureRecognizer
    {
        public const float InwardHorizontalThreshold = 0.65f;
        public const float DownwardThreshold = 0.25f;
        public const double TakeoffCombinationHoldSeconds = 1.0;
        public const double ActionButtonHoldSeconds = 1.0;

        private double? takeoffStartedAt;
        private double? returnHomeStartedAt;
        private double? landStartedAt;
        private bool takeoffTriggered;
        private bool returnHomeTriggered;
        private bool landTriggered;

        public bool IsTakeoffCombinationActive { get; private set; }

        public FlightActionType? Update(
            double nowSeconds,
            float leftX,
            float leftY,
            float rightX,
            float rightY,
            bool returnHomeHeld,
            bool landHeld,
            bool grounded,
            bool airborne,
            bool flying)
        {
            if (double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds) || nowSeconds < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(nowSeconds));
            }

            IsTakeoffCombinationActive = grounded
                && leftX >= InwardHorizontalThreshold
                && leftY <= -DownwardThreshold
                && rightX <= -InwardHorizontalThreshold
                && rightY <= -DownwardThreshold;

            if (HeldLongEnough(
                    IsTakeoffCombinationActive,
                    nowSeconds,
                    TakeoffCombinationHoldSeconds,
                    ref takeoffStartedAt,
                    ref takeoffTriggered))
            {
                return FlightActionType.Takeoff;
            }

            if (HeldLongEnough(
                    airborne && returnHomeHeld,
                    nowSeconds,
                    ActionButtonHoldSeconds,
                    ref returnHomeStartedAt,
                    ref returnHomeTriggered))
            {
                return FlightActionType.ReturnHome;
            }

            if (HeldLongEnough(
                    flying && landHeld,
                    nowSeconds,
                    ActionButtonHoldSeconds,
                    ref landStartedAt,
                    ref landTriggered))
            {
                return FlightActionType.Land;
            }

            return null;
        }

        public void Reset()
        {
            takeoffStartedAt = null;
            returnHomeStartedAt = null;
            landStartedAt = null;
            takeoffTriggered = false;
            returnHomeTriggered = false;
            landTriggered = false;
            IsTakeoffCombinationActive = false;
        }

        private static bool HeldLongEnough(
            bool held,
            double nowSeconds,
            double requiredSeconds,
            ref double? startedAt,
            ref bool triggered)
        {
            if (!held)
            {
                startedAt = null;
                triggered = false;
                return false;
            }

            if (!startedAt.HasValue)
            {
                startedAt = nowSeconds;
                return false;
            }

            if (triggered || nowSeconds - startedAt.Value < requiredSeconds)
            {
                return false;
            }

            triggered = true;
            return true;
        }
    }
}
