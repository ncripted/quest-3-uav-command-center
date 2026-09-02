namespace QuestCommandCenter.VehicleCore
{
    /// <summary>
    /// Shared fail-closed freshness boundary for flight-state telemetry.
    /// The 1.5 second ceiling allows one observed one-second DJI callback
    /// interval plus bounded scheduling jitter without masking a link loss.
    /// </summary>
    public static class FlightTelemetryFreshnessPolicy
    {
        public const long MaximumAgeMilliseconds = 1500;

        public static bool IsFresh(long nowMilliseconds, long receivedAtMilliseconds)
        {
            return TryGetAgeMilliseconds(
                    nowMilliseconds,
                    receivedAtMilliseconds,
                    out var ageMilliseconds)
                && ageMilliseconds <= MaximumAgeMilliseconds;
        }

        public static bool TryGetAgeMilliseconds(
            long nowMilliseconds,
            long receivedAtMilliseconds,
            out long ageMilliseconds)
        {
            ageMilliseconds = long.MaxValue;
            if (nowMilliseconds < 0
                || receivedAtMilliseconds < 0
                || nowMilliseconds < receivedAtMilliseconds)
            {
                return false;
            }

            ageMilliseconds = nowMilliseconds - receivedAtMilliseconds;
            return true;
        }
    }
}
