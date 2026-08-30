using NUnit.Framework;

namespace QuestCommandCenter.VehicleCore.Tests
{
    public sealed class NormalizedFlightCommandTests
    {
        [Test]
        public void Constructor_ClampsFiniteAxes()
        {
            var command = new NormalizedFlightCommand(2f, -2f, 0.25f, -0.5f);

            Assert.That(command.Roll, Is.EqualTo(1f));
            Assert.That(command.Pitch, Is.EqualTo(-1f));
            Assert.That(command.Yaw, Is.EqualTo(0.25f));
            Assert.That(command.Vertical, Is.EqualTo(-0.5f));
        }

        [Test]
        public void Constructor_RejectsNonFiniteAxes()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => new NormalizedFlightCommand(0f, 0f, float.NaN, 0f));
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => new NormalizedFlightCommand(0f, 0f, 0f, float.PositiveInfinity));
        }

        [Test]
        public void Neutral_IsNeutral()
        {
            Assert.That(NormalizedFlightCommand.Neutral.IsNeutral(), Is.True);
        }
    }
}
