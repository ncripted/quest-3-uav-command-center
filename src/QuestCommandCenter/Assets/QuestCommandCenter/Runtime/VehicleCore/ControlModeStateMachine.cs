using System;

namespace QuestCommandCenter.VehicleCore
{
    public sealed class ControlModeTransition
    {
        public ControlModeTransition(
            ControlMode previous,
            ControlMode next,
            long monotonicMilliseconds,
            string reason,
            string correlationId)
        {
            Previous = previous;
            Next = next;
            MonotonicMilliseconds = monotonicMilliseconds;
            Reason = reason;
            CorrelationId = correlationId;
        }

        public ControlMode Previous { get; }
        public ControlMode Next { get; }
        public long MonotonicMilliseconds { get; }
        public string Reason { get; }
        public string CorrelationId { get; }
    }

    /// <summary>
    /// Owns the application control mode. It records explicit transitions but
    /// deliberately has no knowledge of DJI SDK objects or vehicle commands.
    /// </summary>
    public sealed class ControlModeStateMachine
    {
        public ControlModeStateMachine(ControlMode initialMode = ControlMode.Disconnected)
        {
            Current = initialMode;
        }

        public ControlMode Current { get; private set; }

        public event Action<ControlModeTransition> Transitioned;

        public bool CanTransitionTo(ControlMode next)
        {
            if (next == Current)
            {
                return false;
            }

            if (next == ControlMode.Disconnected)
            {
                return true;
            }

            if (next == ControlMode.Failsafe)
            {
                return Current != ControlMode.Disconnected;
            }

            switch (Current)
            {
                case ControlMode.Disconnected:
                    return next == ControlMode.Preflight;
                case ControlMode.Preflight:
                    return next == ControlMode.Ready;
                case ControlMode.Ready:
                    return next == ControlMode.Manual
                        || next == ControlMode.Assisted
                        || next == ControlMode.Autopilot;
                case ControlMode.Manual:
                    return next == ControlMode.Ready
                        || next == ControlMode.Assisted
                        || next == ControlMode.Autopilot;
                case ControlMode.Assisted:
                    return next == ControlMode.Ready
                        || next == ControlMode.Manual
                        || next == ControlMode.Autopilot;
                case ControlMode.Autopilot:
                    return next == ControlMode.Ready
                        || next == ControlMode.Manual
                        || next == ControlMode.Assisted;
                case ControlMode.Failsafe:
                    return next == ControlMode.Preflight;
                default:
                    return false;
            }
        }

        public bool TryTransition(
            ControlMode next,
            long monotonicMilliseconds,
            string reason,
            string correlationId,
            out ControlModeTransition transition)
        {
            if (monotonicMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(monotonicMilliseconds));
            }
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("A control-mode transition requires a reason.", nameof(reason));
            }
            if (string.IsNullOrWhiteSpace(correlationId))
            {
                throw new ArgumentException("A control-mode transition requires a correlation ID.", nameof(correlationId));
            }
            if (!CanTransitionTo(next))
            {
                transition = null;
                return false;
            }

            transition = new ControlModeTransition(
                Current,
                next,
                monotonicMilliseconds,
                reason,
                correlationId);
            Current = next;
            Transitioned?.Invoke(transition);
            return true;
        }
    }
}
