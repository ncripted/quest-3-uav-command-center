using NUnit.Framework;

namespace QuestCommandCenter.VehicleCore.Tests
{
    public sealed class ControlModeStateMachineTests
    {
        [Test]
        public void NormalStartup_RequiresExplicitPreflightAndReadyTransitions()
        {
            var machine = new ControlModeStateMachine();

            Assert.That(machine.TryTransition(
                ControlMode.Preflight,
                10,
                "Vehicle telemetry received",
                "startup-1",
                out var preflight), Is.True);
            Assert.That(machine.TryTransition(
                ControlMode.Ready,
                20,
                "Preflight checks passed",
                "startup-2",
                out var ready), Is.True);

            Assert.That(preflight.Previous, Is.EqualTo(ControlMode.Disconnected));
            Assert.That(ready.Next, Is.EqualTo(ControlMode.Ready));
            Assert.That(ready.Reason, Is.EqualTo("Preflight checks passed"));
            Assert.That(machine.Current, Is.EqualTo(ControlMode.Ready));
        }

        [Test]
        public void InvalidTransition_IsRejectedWithoutChangingMode()
        {
            var machine = new ControlModeStateMachine();

            var changed = machine.TryTransition(
                ControlMode.Manual,
                10,
                "Invalid shortcut",
                "invalid-1",
                out var transition);

            Assert.That(changed, Is.False);
            Assert.That(transition, Is.Null);
            Assert.That(machine.Current, Is.EqualTo(ControlMode.Disconnected));
        }

        [Test]
        public void Failsafe_CanOnlyRecoverThroughPreflight()
        {
            var machine = ReadyMachine();
            Assert.That(machine.TryTransition(
                ControlMode.Failsafe,
                30,
                "Telemetry timeout",
                "failsafe-1",
                out _), Is.True);

            Assert.That(machine.TryTransition(
                ControlMode.Ready,
                40,
                "Unsafe direct recovery",
                "failsafe-2",
                out _), Is.False);
            Assert.That(machine.TryTransition(
                ControlMode.Preflight,
                50,
                "Critical condition cleared",
                "failsafe-3",
                out _), Is.True);
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
