using NUnit.Framework;

namespace QuestCommandCenter.VehicleCore.Tests
{
    public sealed class VehicleTelemetryTests
    {
        [Test]
        public void HorizontalSpeed_UsesNorthAndEastVelocity()
        {
            var telemetry = new VehicleTelemetry
            {
                VelocityNorthMetersPerSecond = 3f,
                VelocityEastMetersPerSecond = 4f,
                VelocityDownMetersPerSecond = 12f
            };

            Assert.That(telemetry.HorizontalSpeedMetersPerSecond, Is.EqualTo(5f).Within(0.0001f));
        }
    }
}
