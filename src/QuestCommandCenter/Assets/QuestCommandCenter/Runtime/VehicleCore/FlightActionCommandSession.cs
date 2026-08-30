namespace QuestCommandCenter.VehicleCore
{
    /// <summary>
    /// Owns two-stage authorization for aircraft-moving high-level commands.
    /// Every confirmation revalidates current aircraft state before dispatch.
    /// </summary>
    public sealed class FlightActionCommandSession
    {
        public const double ConfirmationWindowSeconds = 5.0;
        public const int MinimumTakeoffBatteryPercent = 30;

        private long nextRequestId;
        private double confirmationExpiresAt;

        public FlightActionResult Current { get; private set; } =
            new FlightActionResult(0, FlightActionType.Takeoff, FlightActionStatus.Idle, "Flight actions idle.");

        public bool IsPending => Current.Status == FlightActionStatus.Pending;
        public bool IsAwaitingConfirmation => Current.Status == FlightActionStatus.AwaitingConfirmation;

        public bool TryRequestConfirmation(
            FlightActionType action,
            VehicleTelemetry telemetry,
            bool telemetryFresh,
            double nowSeconds,
            out FlightActionResult result)
        {
            if (IsPending)
            {
                result = Current;
                return false;
            }

            if (IsAwaitingConfirmation)
            {
                result = Reject(action, "Another flight action is already awaiting confirmation.");
                return false;
            }

            if (!Validate(action, telemetry, telemetryFresh, out var validationMessage))
            {
                result = Reject(action, validationMessage);
                return false;
            }

            Current = new FlightActionResult(
                ++nextRequestId,
                action,
                FlightActionStatus.AwaitingConfirmation,
                ConfirmationMessage(action));
            confirmationExpiresAt = nowSeconds + ConfirmationWindowSeconds;
            result = Current;
            return true;
        }

        public bool TryConfirm(
            FlightActionType action,
            VehicleTelemetry telemetry,
            bool telemetryFresh,
            double nowSeconds,
            out FlightActionResult result)
        {
            if (!IsAwaitingConfirmation || Current.Action != action)
            {
                result = Reject(action, "No matching flight action is awaiting confirmation.");
                return false;
            }

            if (nowSeconds > confirmationExpiresAt)
            {
                Current = new FlightActionResult(
                    Current.RequestId,
                    Current.Action,
                    FlightActionStatus.Expired,
                    "Confirmation expired; no aircraft command was sent.");
                result = Current;
                return false;
            }

            if (!Validate(action, telemetry, telemetryFresh, out var validationMessage))
            {
                Current = new FlightActionResult(
                    Current.RequestId,
                    Current.Action,
                    FlightActionStatus.Rejected,
                    "State changed before confirmation: " + validationMessage);
                result = Current;
                return false;
            }

            Current = new FlightActionResult(
                Current.RequestId,
                Current.Action,
                FlightActionStatus.Pending,
                "Sending confirmed " + DisplayName(Current.Action) + " request to DJI.");
            result = Current;
            return true;
        }

        public bool TryComplete(long requestId, bool succeeded, string message, out FlightActionResult result)
        {
            if (!IsPending || Current.RequestId != requestId)
            {
                result = Current;
                return false;
            }

            Current = new FlightActionResult(
                requestId,
                Current.Action,
                succeeded ? FlightActionStatus.Succeeded : FlightActionStatus.Failed,
                message);
            result = Current;
            return true;
        }

        public bool TryExpire(double nowSeconds, out FlightActionResult result)
        {
            if (!IsAwaitingConfirmation || nowSeconds <= confirmationExpiresAt)
            {
                result = Current;
                return false;
            }

            Current = new FlightActionResult(
                Current.RequestId,
                Current.Action,
                FlightActionStatus.Expired,
                "Confirmation expired; no aircraft command was sent.");
            result = Current;
            return true;
        }

        public bool CancelConfirmation(out FlightActionResult result)
        {
            if (!IsAwaitingConfirmation)
            {
                result = Current;
                return false;
            }

            Current = new FlightActionResult(
                Current.RequestId,
                Current.Action,
                FlightActionStatus.Cancelled,
                "Confirmation cancelled; no aircraft command was sent.");
            result = Current;
            return true;
        }

        public bool FailPending(string message, out FlightActionResult result)
        {
            return TryComplete(Current.RequestId, false, message, out result);
        }

        private FlightActionResult Reject(FlightActionType action, string message)
        {
            Current = new FlightActionResult(++nextRequestId, action, FlightActionStatus.Rejected, message);
            return Current;
        }

        private static bool Validate(
            FlightActionType action,
            VehicleTelemetry telemetry,
            bool telemetryFresh,
            out string message)
        {
            if (telemetry == null || !telemetry.IsConnected || !telemetry.HasFlightState)
            {
                message = "Connected aircraft flight telemetry is required.";
                return false;
            }
            if (!telemetryFresh)
            {
                message = "Flight telemetry is stale.";
                return false;
            }
            if (telemetry.FailsafeEnabled)
            {
                message = "DJI failsafe is active.";
                return false;
            }

            switch (action)
            {
                case FlightActionType.Takeoff:
                    if (telemetry.IsFlying || telemetry.MotorsOn)
                    {
                        message = "Takeoff requires landed state with motors off.";
                        return false;
                    }
                    if (!telemetry.HasBatteryState
                        || telemetry.BatteryPercent < MinimumTakeoffBatteryPercent)
                    {
                        message = "Takeoff requires battery telemetry at or above 30%.";
                        return false;
                    }
                    break;
                case FlightActionType.Land:
                    if (!telemetry.IsFlying)
                    {
                        message = "Landing requires confirmed flying state.";
                        return false;
                    }
                    break;
                case FlightActionType.ReturnHome:
                    if (!telemetry.IsFlying)
                    {
                        message = "Return Home requires confirmed flying state.";
                        return false;
                    }
                    if (!telemetry.HomePositionSet)
                    {
                        message = "DJI has not confirmed a home point.";
                        return false;
                    }
                    break;
            }

            message = string.Empty;
            return true;
        }

        private static string ConfirmationMessage(FlightActionType action)
        {
            return "PRESS A within 5 seconds to confirm " + DisplayName(action) + ".";
        }

        private static string DisplayName(FlightActionType action)
        {
            return action == FlightActionType.ReturnHome ? "RETURN HOME" : action.ToString().ToUpperInvariant();
        }
    }
}
