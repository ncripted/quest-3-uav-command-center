using System;

namespace QuestCommandCenter.VehicleCore
{
    public enum FlightCommandSource
    {
        Neutral,
        Manual,
        Assisted,
        Autopilot,
        Failsafe
    }

    /// <summary>
    /// Adds freshness and authority metadata to a normalized command. Adapters
    /// must never accept an un-enveloped command from UI or input code.
    /// </summary>
    public sealed class FlightCommandEnvelope
    {
        public FlightCommandEnvelope(
            long sequence,
            long authorityEpoch,
            long createdAtMilliseconds,
            long expiresAtMilliseconds,
            FlightCommandSource source,
            NormalizedFlightCommand command,
            bool authorized)
        {
            if (sequence <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sequence));
            }
            if (authorityEpoch < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(authorityEpoch));
            }
            if (createdAtMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(createdAtMilliseconds));
            }
            if (expiresAtMilliseconds < createdAtMilliseconds)
            {
                throw new ArgumentOutOfRangeException(nameof(expiresAtMilliseconds));
            }

            Sequence = sequence;
            AuthorityEpoch = authorityEpoch;
            CreatedAtMilliseconds = createdAtMilliseconds;
            ExpiresAtMilliseconds = expiresAtMilliseconds;
            Source = source;
            Command = command;
            Authorized = authorized;
        }

        public long Sequence { get; }
        public long AuthorityEpoch { get; }
        public long CreatedAtMilliseconds { get; }
        public long ExpiresAtMilliseconds { get; }
        public FlightCommandSource Source { get; }
        public NormalizedFlightCommand Command { get; }
        public bool Authorized { get; }

        public bool IsExpired(long monotonicMilliseconds)
        {
            return monotonicMilliseconds > ExpiresAtMilliseconds;
        }
    }
}
