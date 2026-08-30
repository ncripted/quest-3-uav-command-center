using NUnit.Framework;

namespace QuestCommandCenter.VehicleCore.Tests
{
    public sealed class GimbalCommandSessionTests
    {
        [Test]
        public void TryBegin_RejectsTargetOutsideBenchRange()
        {
            var session = new GimbalCommandSession();

            var accepted = session.TryBegin(-21f, true, true, true, out var result);

            Assert.That(accepted, Is.False);
            Assert.That(result.Status, Is.EqualTo(GimbalCommandStatus.Rejected));
        }

        [Test]
        public void TryBegin_RejectsWhenAircraftIsNotStationary()
        {
            var session = new GimbalCommandSession();

            var accepted = session.TryBegin(-20f, true, false, true, out var result);

            Assert.That(accepted, Is.False);
            Assert.That(result.Status, Is.EqualTo(GimbalCommandStatus.Rejected));
        }

        [Test]
        public void TryBegin_RejectsWhenFreshGimbalTelemetryIsUnavailable()
        {
            var session = new GimbalCommandSession();

            var accepted = session.TryBegin(-10f, false, true, true, out var result);

            Assert.That(accepted, Is.False);
            Assert.That(result.Status, Is.EqualTo(GimbalCommandStatus.Rejected));
        }

        [Test]
        public void TryBegin_AllowsOneHealthyStationaryCommand()
        {
            var session = new GimbalCommandSession();

            Assert.That(session.TryBegin(-20f, true, true, true, out var pending), Is.True);
            Assert.That(pending.Status, Is.EqualTo(GimbalCommandStatus.Pending));
            Assert.That(session.TryBegin(0f, true, true, true, out var rejected), Is.False);
            Assert.That(rejected.Status, Is.EqualTo(GimbalCommandStatus.Rejected));
        }

        [Test]
        public void TryComplete_CompletesOnlyMatchingRequest()
        {
            var session = new GimbalCommandSession();
            session.TryBegin(-20f, true, true, true, out var pending);

            Assert.That(session.TryComplete(pending.RequestId + 1, true, "wrong", out _), Is.False);
            Assert.That(session.TryComplete(pending.RequestId, true, "complete", out var result), Is.True);
            Assert.That(result.Status, Is.EqualTo(GimbalCommandStatus.Succeeded));
        }
    }
}
