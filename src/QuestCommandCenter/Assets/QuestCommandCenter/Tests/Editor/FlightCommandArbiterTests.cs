using NUnit.Framework;

namespace QuestCommandCenter.VehicleCore.Tests
{
    public sealed class FlightCommandArbiterTests
    {
        [Test]
        public void ManualTakeover_InvalidatesAutopilotAndImmediatelySelectsNeutral()
        {
            var machine = ReadyMachine();
            machine.TryTransition(ControlMode.Autopilot, 30, "Mission started", "mission-1", out _);
            var arbiter = new FlightCommandArbiter(machine);
            Assert.That(arbiter.TrySubmitAutopilot(
                new NormalizedFlightCommand(0f, 0.5f, 0f, 0f),
                40,
                out var autonomous), Is.True);

            Assert.That(arbiter.TryManualTakeover(50, "takeover-1", out var transition), Is.True);
            var selected = arbiter.Select(50);

            Assert.That(transition.Previous, Is.EqualTo(ControlMode.Autopilot));
            Assert.That(transition.Next, Is.EqualTo(ControlMode.Manual));
            Assert.That(arbiter.AuthorityEpoch, Is.EqualTo(1));
            Assert.That(autonomous.AuthorityEpoch, Is.EqualTo(0));
            Assert.That(selected.Source, Is.EqualTo(FlightCommandSource.Neutral));
            Assert.That(selected.Command.IsNeutral(), Is.True);
        }

        [Test]
        public void ExpiredManualCommand_IsReplacedByFreshNeutral()
        {
            var machine = ReadyMachine();
            var arbiter = new FlightCommandArbiter(machine, commandTimeoutMilliseconds: 250);
            arbiter.TryManualTakeover(50, "takeover-1", out _);
            var submitted = arbiter.SubmitManual(
                new ManualControlIntent(
                    new NormalizedFlightCommand(0.2f, 0.3f, 0f, 0f),
                    true,
                    false,
                    false,
                    false),
                100);

            var selected = arbiter.Select(351);

            Assert.That(selected.Sequence, Is.GreaterThan(submitted.Sequence));
            Assert.That(selected.CreatedAtMilliseconds, Is.EqualTo(351));
            Assert.That(selected.Source, Is.EqualTo(FlightCommandSource.Neutral));
            Assert.That(selected.Command.IsNeutral(), Is.True);
        }

        [Test]
        public void Takeover_IsRejectedBeforeReady()
        {
            var machine = new ControlModeStateMachine();
            machine.TryTransition(ControlMode.Preflight, 10, "Connected", "preflight-1", out _);
            var arbiter = new FlightCommandArbiter(machine);

            Assert.That(arbiter.TryManualTakeover(20, "takeover-1", out _), Is.False);
            Assert.That(arbiter.AuthorityEpoch, Is.Zero);
            Assert.That(machine.Current, Is.EqualTo(ControlMode.Preflight));
        }

        private static ControlModeStateMachine ReadyMachine()
        {
            var machine = new ControlModeStateMachine();
            machine.TryTransition(ControlMode.Preflight, 10, "Connected", "ready-1", out _);
            machine.TryTransition(ControlMode.Ready, 20, "Checks passed", "ready-2", out _);
            return machine;
        }
    }
}
