using System;
using NUnit.Framework;

namespace QuestCommandCenter.VehicleCore.Tests
{
    public sealed class FlightSafetyManagerTests
    {
        [Test]
        public void FreshAuthorizedManualCommand_IsLimitedByIndoorProfile()
        {
            var manager = new FlightSafetyManager(new IndoorDevelopmentSafetyProfile());
            var decision = manager.Evaluate(
                Envelope(new NormalizedFlightCommand(1f, 1f, 1f, 1f)),
                Context());

            var horizontalMagnitude = Math.Sqrt(
                (decision.Command.Roll * decision.Command.Roll)
                + (decision.Command.Pitch * decision.Command.Pitch));
            Assert.That(decision.Disposition, Is.EqualTo(FlightSafetyDisposition.Limited));
            Assert.That(decision.Reason, Is.EqualTo(FlightSafetyReason.IndoorProfileLimit));
            Assert.That(horizontalMagnitude, Is.EqualTo(0.35d).Within(0.0001d));
            Assert.That(decision.Command.Vertical, Is.EqualTo(0.25f));
            Assert.That(decision.Command.Yaw, Is.EqualTo(0.30f));
        }

        [Test]
        public void ReleasedDeadman_ProducesNeutral()
        {
            var manager = new FlightSafetyManager(new IndoorDevelopmentSafetyProfile());
            var envelope = new FlightCommandEnvelope(
                1,
                0,
                100,
                350,
                FlightCommandSource.Manual,
                NormalizedFlightCommand.Neutral,
                false);

            var decision = manager.Evaluate(envelope, Context());

            Assert.That(decision.Disposition, Is.EqualTo(FlightSafetyDisposition.Neutralized));
            Assert.That(decision.Reason, Is.EqualTo(FlightSafetyReason.ManualAuthorizationMissing));
            Assert.That(decision.Command.IsNeutral(), Is.True);
        }

        [Test]
        public void StaleControllerInput_IsNeverRepeated()
        {
            var manager = new FlightSafetyManager(new IndoorDevelopmentSafetyProfile());
            var context = Context();
            context.MonotonicMilliseconds = 251;

            var decision = manager.Evaluate(
                Envelope(new NormalizedFlightCommand(0.1f, 0.1f, 0f, 0f)),
                context);

            Assert.That(decision.Reason, Is.EqualTo(FlightSafetyReason.ControllerInputStale));
            Assert.That(decision.Command.IsNeutral(), Is.True);
        }

        [Test]
        public void StaleTelemetry_ProducesNeutral()
        {
            var manager = new FlightSafetyManager(new IndoorDevelopmentSafetyProfile());
            var context = Context();
            context.TelemetryAgeMilliseconds =
                FlightTelemetryFreshnessPolicy.MaximumAgeMilliseconds + 1;

            var decision = manager.Evaluate(
                Envelope(new NormalizedFlightCommand(0.1f, 0f, 0f, 0f)),
                context);

            Assert.That(decision.Reason, Is.EqualTo(FlightSafetyReason.TelemetryStale));
            Assert.That(decision.Command.IsNeutral(), Is.True);
        }

        [Test]
        public void DevelopmentAltitudeLimit_BlocksOnlyFurtherAscent()
        {
            var manager = new FlightSafetyManager(new IndoorDevelopmentSafetyProfile());
            var context = Context();
            context.RelativeAltitudeMeters = 2f;

            var ascent = manager.Evaluate(
                Envelope(new NormalizedFlightCommand(0f, 0f, 0f, 0.2f)),
                context);
            var descent = manager.Evaluate(
                Envelope(new NormalizedFlightCommand(0f, 0f, 0f, -0.2f)),
                context);

            Assert.That(ascent.Reason, Is.EqualTo(FlightSafetyReason.DevelopmentAltitudeLimit));
            Assert.That(ascent.Command.Vertical, Is.Zero);
            Assert.That(descent.Disposition, Is.EqualTo(FlightSafetyDisposition.Accepted));
            Assert.That(descent.Command.Vertical, Is.EqualTo(-0.2f));
        }

        [Test]
        public void OldAuthorityEpoch_ProducesNeutral()
        {
            var manager = new FlightSafetyManager(new IndoorDevelopmentSafetyProfile());
            var context = Context();
            context.AuthorityEpoch = 2;

            var decision = manager.Evaluate(
                Envelope(new NormalizedFlightCommand(0.1f, 0f, 0f, 0f)),
                context);

            Assert.That(decision.Reason, Is.EqualTo(FlightSafetyReason.AuthorityMismatch));
            Assert.That(decision.Command.IsNeutral(), Is.True);
        }

        private static FlightCommandEnvelope Envelope(NormalizedFlightCommand command)
        {
            return new FlightCommandEnvelope(
                1,
                0,
                100,
                350,
                FlightCommandSource.Manual,
                command,
                true);
        }

        private static FlightSafetyContext Context()
        {
            return new FlightSafetyContext
            {
                MonotonicMilliseconds = 110,
                AuthorityEpoch = 0,
                ControlMode = ControlMode.Manual,
                IsConnected = true,
                HasTelemetry = true,
                TelemetryAgeMilliseconds = 20,
                HasFlightState = true,
                HasBatteryState = true,
                BatteryPercent = 80,
                RelativeAltitudeMeters = 0f
            };
        }
    }
}
