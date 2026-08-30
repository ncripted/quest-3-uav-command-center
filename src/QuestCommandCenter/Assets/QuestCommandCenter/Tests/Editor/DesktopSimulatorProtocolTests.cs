using NUnit.Framework;

namespace QuestCommandCenter.VehicleCore.Tests
{
    public sealed class DesktopSimulatorProtocolTests
    {
        [Test]
        public void SerializeManualControl_UsesInvariantBoundedJsonLine()
        {
            var line = DesktopSimulatorProtocol.SerializeManualControl(
                42,
                new NormalizedFlightCommand(0.25f, -0.5f, 0.1f, -0.2f),
                true,
                false,
                true);

            Assert.That(line, Does.StartWith("{\"schemaVersion\":1,\"type\":\"manual_control\""));
            Assert.That(line, Does.Contain("\"sequence\":42"));
            Assert.That(line, Does.Contain("\"roll\":0.25"));
            Assert.That(line, Does.Contain("\"pitch\":-0.5"));
            Assert.That(line, Does.Contain("\"authorized\":true"));
            Assert.That(line, Does.Contain("\"brake\":false"));
            Assert.That(line, Does.Contain("\"takeover\":true"));
            Assert.That(line, Does.EndWith("\n"));
        }

        [TestCase(FlightActionType.Takeoff, "takeoff")]
        [TestCase(FlightActionType.Land, "land")]
        [TestCase(FlightActionType.ReturnHome, "return_home")]
        public void SerializeFlightAction_ProducesDiscreteJsonLine(
            FlightActionType action,
            string expectedWireName)
        {
            var line = DesktopSimulatorProtocol.SerializeFlightAction(17, action);

            Assert.That(line, Does.StartWith("{\"schemaVersion\":1,\"type\":\"simulator_action\""));
            Assert.That(line, Does.Contain("\"sequence\":17"));
            Assert.That(line, Does.Contain("\"action\":\"" + expectedWireName + "\""));
            Assert.That(line, Does.EndWith("\n"));
        }
    }
}
