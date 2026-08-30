using NUnit.Framework;

namespace QuestCommandCenter.VehicleCore.Tests
{
    public sealed class FlightActionCommandSessionTests
    {
        [Test]
        public void Takeoff_RequiresTwoStagesAndValidLandedTelemetry()
        {
            var session = new FlightActionCommandSession();
            var telemetry = LandedTelemetry();

            Assert.That(
                session.TryRequestConfirmation(FlightActionType.Takeoff, telemetry, true, 10.0, out var requested),
                Is.True);
            Assert.That(requested.Status, Is.EqualTo(FlightActionStatus.AwaitingConfirmation));

            Assert.That(
                session.TryConfirm(FlightActionType.Takeoff, telemetry, true, 12.0, out var confirmed),
                Is.True);
            Assert.That(confirmed.Status, Is.EqualTo(FlightActionStatus.Pending));
        }

        [Test]
        public void Takeoff_IsRejectedBelowDevelopmentBatteryMinimum()
        {
            var session = new FlightActionCommandSession();
            var telemetry = LandedTelemetry();
            telemetry.BatteryPercent = 29;

            var accepted = session.TryRequestConfirmation(
                FlightActionType.Takeoff,
                telemetry,
                true,
                10.0,
                out var result);

            Assert.That(accepted, Is.False);
            Assert.That(result.Status, Is.EqualTo(FlightActionStatus.Rejected));
            StringAssert.Contains("30%", result.Message);
        }

        [Test]
        public void Confirmation_RevalidatesAircraftState()
        {
            var session = new FlightActionCommandSession();
            var telemetry = LandedTelemetry();
            session.TryRequestConfirmation(FlightActionType.Takeoff, telemetry, true, 10.0, out _);
            telemetry.MotorsOn = true;

            var accepted = session.TryConfirm(
                FlightActionType.Takeoff,
                telemetry,
                true,
                11.0,
                out var result);

            Assert.That(accepted, Is.False);
            Assert.That(result.Status, Is.EqualTo(FlightActionStatus.Rejected));
        }

        [Test]
        public void Confirmation_ExpiresWithoutProducingPendingCommand()
        {
            var session = new FlightActionCommandSession();
            var telemetry = LandedTelemetry();
            session.TryRequestConfirmation(FlightActionType.Takeoff, telemetry, true, 10.0, out _);

            var accepted = session.TryConfirm(
                FlightActionType.Takeoff,
                telemetry,
                true,
                15.01,
                out var result);

            Assert.That(accepted, Is.False);
            Assert.That(result.Status, Is.EqualTo(FlightActionStatus.Expired));
            Assert.That(session.IsPending, Is.False);
        }

        [Test]
        public void ReturnHome_RequiresFlyingStateAndDjiHomePoint()
        {
            var session = new FlightActionCommandSession();
            var telemetry = LandedTelemetry();
            telemetry.IsFlying = true;
            telemetry.MotorsOn = true;
            telemetry.HomePositionSet = false;

            Assert.That(
                session.TryRequestConfirmation(FlightActionType.ReturnHome, telemetry, true, 10.0, out _),
                Is.False);

            telemetry.HomePositionSet = true;
            Assert.That(
                session.TryRequestConfirmation(FlightActionType.ReturnHome, telemetry, true, 11.0, out var result),
                Is.True);
            Assert.That(result.Status, Is.EqualTo(FlightActionStatus.AwaitingConfirmation));
        }

        [Test]
        public void Land_RequiresFlyingStateAndFreshTelemetry()
        {
            var session = new FlightActionCommandSession();
            var telemetry = LandedTelemetry();
            telemetry.IsFlying = true;
            telemetry.MotorsOn = true;

            Assert.That(
                session.TryRequestConfirmation(FlightActionType.Land, telemetry, false, 10.0, out _),
                Is.False);
            Assert.That(
                session.TryRequestConfirmation(FlightActionType.Land, telemetry, true, 11.0, out var result),
                Is.True);
            Assert.That(result.Action, Is.EqualTo(FlightActionType.Land));
        }

        private static VehicleTelemetry LandedTelemetry()
        {
            return new VehicleTelemetry
            {
                IsConnected = true,
                HasFlightState = true,
                HasBatteryState = true,
                BatteryPercent = 80,
                MotorsOn = false,
                IsFlying = false,
                FailsafeEnabled = false
            };
        }
    }
}
