using NUnit.Framework;
using QuestCommandCenter.Adapters.DjiSpark;

namespace QuestCommandCenter.VehicleCore.Tests
{
    public sealed class DjiVirtualStickCommandMapperTests
    {
        [Test]
        public void Map_UsesBodyVelocityAndYawRateDevelopmentLimits()
        {
            var mapper = new DjiVirtualStickCommandMapper(
                new DjiVirtualStickDevelopmentProfile());

            var result = mapper.Map(
                new NormalizedFlightCommand(0.5f, -0.25f, 0.2f, -0.5f));

            Assert.That(result.RollMetersPerSecond, Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(result.PitchMetersPerSecond, Is.EqualTo(-0.25f).Within(0.0001f));
            Assert.That(result.YawRateDegreesPerSecond, Is.EqualTo(6f).Within(0.0001f));
            Assert.That(result.VerticalMetersPerSecond, Is.EqualTo(-0.4f).Within(0.0001f));
        }

        [Test]
        public void Map_NeutralRemainsNeutral()
        {
            var mapper = new DjiVirtualStickCommandMapper(
                new DjiVirtualStickDevelopmentProfile());

            var result = mapper.Map(NormalizedFlightCommand.Neutral);

            Assert.That(result.RollMetersPerSecond, Is.Zero);
            Assert.That(result.PitchMetersPerSecond, Is.Zero);
            Assert.That(result.YawRateDegreesPerSecond, Is.Zero);
            Assert.That(result.VerticalMetersPerSecond, Is.Zero);
        }

        [Test]
        public void Profile_RejectsValuesAboveDjiDocumentedLimits()
        {
            var profile = new DjiVirtualStickDevelopmentProfile
            {
                MaximumHorizontalVelocityMetersPerSecond = 15.01f
            };

            Assert.Throws<System.InvalidOperationException>(() => profile.Validate());
        }
    }
}
