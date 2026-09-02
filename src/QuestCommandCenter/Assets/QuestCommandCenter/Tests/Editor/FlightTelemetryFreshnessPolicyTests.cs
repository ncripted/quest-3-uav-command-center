using NUnit.Framework;

namespace QuestCommandCenter.VehicleCore.Tests
{
    public sealed class FlightTelemetryFreshnessPolicyTests
    {
        [Test]
        public void ExactFreshnessCeiling_IsFresh()
        {
            Assert.That(
                FlightTelemetryFreshnessPolicy.IsFresh(2500, 1000),
                Is.True);
        }

        [Test]
        public void OneMillisecondPastCeiling_IsStale()
        {
            Assert.That(
                FlightTelemetryFreshnessPolicy.IsFresh(2501, 1000),
                Is.False);
        }

        [TestCase(-1, 0)]
        [TestCase(0, -1)]
        [TestCase(999, 1000)]
        public void MissingOrClockInvalidTimestamp_FailsClosed(
            long nowMilliseconds,
            long receivedAtMilliseconds)
        {
            Assert.That(
                FlightTelemetryFreshnessPolicy.IsFresh(
                    nowMilliseconds,
                    receivedAtMilliseconds),
                Is.False);
            Assert.That(
                FlightTelemetryFreshnessPolicy.TryGetAgeMilliseconds(
                    nowMilliseconds,
                    receivedAtMilliseconds,
                    out var ageMilliseconds),
                Is.False);
            Assert.That(ageMilliseconds, Is.EqualTo(long.MaxValue));
        }

        [Test]
        public void ValidTimestamps_ReturnExactAge()
        {
            Assert.That(
                FlightTelemetryFreshnessPolicy.TryGetAgeMilliseconds(
                    2400,
                    1000,
                    out var ageMilliseconds),
                Is.True);
            Assert.That(ageMilliseconds, Is.EqualTo(1400));
        }
    }
}
