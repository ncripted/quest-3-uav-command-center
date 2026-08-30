using NUnit.Framework;

namespace QuestCommandCenter.VehicleCore.Tests
{
    public sealed class ShadowVehicleTelemetryTests
    {
        [Test]
        public void Project_GroundedLiveStateRemainsGrounded()
        {
            var projected = ShadowVehicleTelemetry.Project(
                LiveTelemetry(motorsOn: false, isFlying: false),
                simulatedAirborne: false,
                monotonicMilliseconds: 42);

            Assert.That(projected.MotorsOn, Is.False);
            Assert.That(projected.IsFlying, Is.False);
            Assert.That(projected.FlightMode, Is.EqualTo("Grounded"));
        }

        [Test]
        public void Project_SyntheticTakeoffCreatesAirborneShadowState()
        {
            var projected = ShadowVehicleTelemetry.Project(
                LiveTelemetry(motorsOn: false, isFlying: false),
                simulatedAirborne: true,
                monotonicMilliseconds: 43);

            Assert.That(projected.MotorsOn, Is.True);
            Assert.That(projected.IsFlying, Is.True);
            Assert.That(projected.FlightMode, Is.EqualTo("Airborne"));
            Assert.That(projected.AltitudeMeters, Is.GreaterThanOrEqualTo(1f));
        }

        [Test]
        public void Project_RealFlyingStateCannotBeMaskedAsGrounded()
        {
            var projected = ShadowVehicleTelemetry.Project(
                LiveTelemetry(motorsOn: true, isFlying: true),
                simulatedAirborne: false,
                monotonicMilliseconds: 44);

            Assert.That(projected.MotorsOn, Is.True);
            Assert.That(projected.IsFlying, Is.True);
            Assert.That(projected.FlightMode, Is.EqualTo("Airborne"));
        }

        private static VehicleTelemetry LiveTelemetry(bool motorsOn, bool isFlying)
        {
            return new VehicleTelemetry
            {
                IsConnected = true,
                ProductName = "Spark",
                HasBatteryState = true,
                BatteryPercent = 80,
                HasFlightState = true,
                MotorsOn = motorsOn,
                IsFlying = isFlying
            };
        }
    }
}
