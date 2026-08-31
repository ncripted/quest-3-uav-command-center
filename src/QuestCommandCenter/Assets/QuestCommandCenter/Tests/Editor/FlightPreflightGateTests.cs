using NUnit.Framework;

namespace QuestCommandCenter.VehicleCore.Tests
{
    public sealed class FlightPreflightGateTests
    {
        [Test]
        public void CompleteOutdoorPreflight_Passes()
        {
            var result = new FlightPreflightGate().Evaluate(ReadyInput());

            Assert.That(result.Passed, Is.True);
            Assert.That(result.PassedCount, Is.EqualTo(result.Checks.Count));
        }

        [Test]
        public void MissingHomePoint_BlocksPreflight()
        {
            var input = ReadyInput();
            input.Telemetry.HomePositionSet = false;

            var result = new FlightPreflightGate().Evaluate(input);

            Assert.That(result.Passed, Is.False);
            Assert.That(result.Checks, Has.Some.Matches<FlightPreflightCheck>(
                check => check.Label == "HOME POINT" && !check.Passed));
        }

        [Test]
        public void LowBattery_BlocksPreflightAtConservativeThreshold()
        {
            var input = ReadyInput();
            input.Telemetry.BatteryPercent = FlightPreflightGate.MinimumBatteryPercent - 1;

            var result = new FlightPreflightGate().Evaluate(input);

            Assert.That(result.Passed, Is.False);
            Assert.That(result.Checks, Has.Some.Matches<FlightPreflightCheck>(
                check => check.Label == "BATTERY" && !check.Passed));
        }

        [Test]
        public void NonNeutralStickInput_BlocksPreflight()
        {
            var input = ReadyInput();
            input.ControllerAxesNeutral = false;

            var result = new FlightPreflightGate().Evaluate(input);

            Assert.That(result.Passed, Is.False);
            Assert.That(result.Checks, Has.Some.Matches<FlightPreflightCheck>(
                check => check.Label == "CONTROLLER STICKS" && !check.Passed));
        }

        [Test]
        public void FlyingAircraft_CannotEnterInitialPreflight()
        {
            var input = ReadyInput();
            input.Telemetry.MotorsOn = true;
            input.Telemetry.IsFlying = true;

            var result = new FlightPreflightGate().Evaluate(input);

            Assert.That(result.Passed, Is.False);
            Assert.That(result.Checks, Has.Some.Matches<FlightPreflightCheck>(
                check => check.Label == "AIRCRAFT" && !check.Passed));
        }

        [Test]
        public void ReachableVirtualStickApi_PassesBeforeModeIsEnabled()
        {
            var input = ReadyInput();
            input.ManualControlInterfaceReady = true;

            var result = new FlightPreflightGate().Evaluate(input);

            Assert.That(result.Checks, Has.Some.Matches<FlightPreflightCheck>(
                check => check.Label == "VIRTUAL STICK API"
                    && check.Passed
                    && check.Detail.Contains("ENABLES AFTER CONFIRM")));
        }

        [Test]
        public void FailedVirtualStickStatusQuery_BlocksPreflight()
        {
            var input = ReadyInput();
            input.ManualControlInterfaceReady = false;

            var result = new FlightPreflightGate().Evaluate(input);

            Assert.That(result.Passed, Is.False);
            Assert.That(result.Checks, Has.Some.Matches<FlightPreflightCheck>(
                check => check.Label == "VIRTUAL STICK API"
                    && !check.Passed
                    && check.Detail == "STATUS QUERY FAILED"));
        }

        private static FlightPreflightInput ReadyInput()
        {
            return new FlightPreflightInput
            {
                TelemetryFresh = true,
                ManualControlInterfaceReady = true,
                ControllerAxesNeutral = true,
                Telemetry = new VehicleTelemetry
                {
                    IsConnected = true,
                    HasFlightState = true,
                    HasBatteryState = true,
                    BatteryPercent = FlightPreflightGate.MinimumBatteryPercent,
                    PositionValid = true,
                    SatelliteCount = 10,
                    HomePositionSet = true,
                    FailsafeEnabled = false,
                    MotorsOn = false,
                    IsFlying = false
                }
            };
        }
    }
}
