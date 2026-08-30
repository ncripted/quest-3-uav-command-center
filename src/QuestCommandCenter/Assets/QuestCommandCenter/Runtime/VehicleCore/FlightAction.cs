using System;

namespace QuestCommandCenter.VehicleCore
{
    public enum FlightActionType
    {
        Takeoff,
        Land,
        ReturnHome
    }

    public enum FlightActionStatus
    {
        Idle,
        AwaitingConfirmation,
        Pending,
        Succeeded,
        Failed,
        Rejected,
        Cancelled,
        Expired
    }

    public sealed class FlightActionResult
    {
        public FlightActionResult(
            long requestId,
            FlightActionType action,
            FlightActionStatus status,
            string message)
        {
            RequestId = requestId;
            Action = action;
            Status = status;
            Message = message ?? string.Empty;
        }

        public long RequestId { get; }
        public FlightActionType Action { get; }
        public FlightActionStatus Status { get; }
        public string Message { get; }
    }

    public interface IFlightActionControl
    {
        bool IsFlightActionPending { get; }
        FlightActionResult LastFlightAction { get; }

        event Action<FlightActionResult> FlightActionStateChanged;

        bool RequestFlightActionConfirmation(FlightActionType action);
        bool ConfirmFlightAction(FlightActionType action);
        void CancelFlightActionConfirmation();
    }
}
