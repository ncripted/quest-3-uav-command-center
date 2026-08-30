using System;

namespace QuestCommandCenter.VehicleCore
{
    /// <summary>
    /// The sole selector of flight-command authority. This class only chooses a
    /// normalized command; it cannot dispatch to a vehicle adapter.
    /// </summary>
    public sealed class FlightCommandArbiter
    {
        private readonly ControlModeStateMachine stateMachine;
        private readonly long commandTimeoutMilliseconds;
        private long nextSequence;
        private FlightCommandEnvelope manualCommand;
        private FlightCommandEnvelope autopilotCommand;

        public FlightCommandArbiter(
            ControlModeStateMachine stateMachine,
            long commandTimeoutMilliseconds = 250)
        {
            this.stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
            if (commandTimeoutMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(commandTimeoutMilliseconds));
            }

            this.commandTimeoutMilliseconds = commandTimeoutMilliseconds;
        }

        public long AuthorityEpoch { get; private set; }

        public bool TryManualTakeover(
            long monotonicMilliseconds,
            string correlationId,
            out ControlModeTransition transition)
        {
            transition = null;
            if (stateMachine.Current != ControlMode.Ready
                && stateMachine.Current != ControlMode.Manual
                && stateMachine.Current != ControlMode.Assisted
                && stateMachine.Current != ControlMode.Autopilot)
            {
                return false;
            }

            var alreadyManual = stateMachine.Current == ControlMode.Manual;
            if (!alreadyManual
                && !stateMachine.TryTransition(
                    ControlMode.Manual,
                    monotonicMilliseconds,
                    "Operator manual takeover",
                    correlationId,
                    out transition))
            {
                return false;
            }
            if (alreadyManual)
            {
                transition = null;
            }

            AuthorityEpoch++;
            manualCommand = null;
            autopilotCommand = null;
            return true;
        }

        public FlightCommandEnvelope SubmitManual(ManualControlIntent intent, long sampledAtMilliseconds)
        {
            var source = stateMachine.Current == ControlMode.Assisted
                ? FlightCommandSource.Assisted
                : FlightCommandSource.Manual;
            manualCommand = Create(
                source,
                intent.Command,
                intent.ManualFlightAuthorized,
                sampledAtMilliseconds);
            return manualCommand;
        }

        public bool TrySubmitAutopilot(
            NormalizedFlightCommand command,
            long createdAtMilliseconds,
            out FlightCommandEnvelope envelope)
        {
            if (stateMachine.Current != ControlMode.Autopilot)
            {
                envelope = null;
                return false;
            }

            autopilotCommand = Create(
                FlightCommandSource.Autopilot,
                command,
                true,
                createdAtMilliseconds);
            envelope = autopilotCommand;
            return true;
        }

        public FlightCommandEnvelope Select(long monotonicMilliseconds)
        {
            FlightCommandEnvelope selected = null;
            switch (stateMachine.Current)
            {
                case ControlMode.Manual:
                case ControlMode.Assisted:
                    selected = manualCommand;
                    break;
                case ControlMode.Autopilot:
                    selected = autopilotCommand;
                    break;
            }

            if (selected == null
                || selected.AuthorityEpoch != AuthorityEpoch
                || selected.IsExpired(monotonicMilliseconds))
            {
                return Create(
                    FlightCommandSource.Neutral,
                    NormalizedFlightCommand.Neutral,
                    false,
                    monotonicMilliseconds);
            }

            return selected;
        }

        private FlightCommandEnvelope Create(
            FlightCommandSource source,
            NormalizedFlightCommand command,
            bool authorized,
            long createdAtMilliseconds)
        {
            if (createdAtMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(createdAtMilliseconds));
            }

            return new FlightCommandEnvelope(
                ++nextSequence,
                AuthorityEpoch,
                createdAtMilliseconds,
                checked(createdAtMilliseconds + commandTimeoutMilliseconds),
                source,
                command,
                authorized);
        }
    }
}
