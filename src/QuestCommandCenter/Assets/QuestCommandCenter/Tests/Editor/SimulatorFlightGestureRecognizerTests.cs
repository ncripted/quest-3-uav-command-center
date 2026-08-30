using NUnit.Framework;

namespace QuestCommandCenter.VehicleCore.Tests
{
    public sealed class SimulatorFlightGestureRecognizerTests
    {
        [Test]
        public void DownAndInwardSticks_TriggerTakeoffOnlyAfterHold()
        {
            var gestures = new SimulatorFlightGestureRecognizer();

            Assert.That(Update(gestures, 10.0, grounded: true), Is.Null);
            Assert.That(Update(gestures, 10.99, grounded: true), Is.Null);
            Assert.That(
                Update(gestures, 11.0, grounded: true),
                Is.EqualTo(FlightActionType.Takeoff));
        }

        [Test]
        public void TakeoffCombination_DoesNotRepeatUntilSticksAreReleased()
        {
            var gestures = new SimulatorFlightGestureRecognizer();
            Update(gestures, 10.0, grounded: true);
            Assert.That(Update(gestures, 11.0, grounded: true), Is.EqualTo(FlightActionType.Takeoff));
            Assert.That(Update(gestures, 12.0, grounded: true), Is.Null);

            gestures.Update(12.1, 0f, 0f, 0f, 0f, false, false, true, false, false);
            Assert.That(Update(gestures, 13.0, grounded: true), Is.Null);
            Assert.That(Update(gestures, 14.0, grounded: true), Is.EqualTo(FlightActionType.Takeoff));
        }

        [Test]
        public void ReturnHomeButtonHold_TriggersOnlyFromAirborneState()
        {
            var gestures = new SimulatorFlightGestureRecognizer();

            gestures.Update(1.0, 0f, 0f, 0f, 0f, true, false, false, false, true);
            Assert.That(
                gestures.Update(2.0, 0f, 0f, 0f, 0f, true, false, false, false, true),
                Is.Null);

            gestures.Update(3.0, 0f, 0f, 0f, 0f, false, false, false, true, true);
            gestures.Update(4.0, 0f, 0f, 0f, 0f, true, false, false, true, true);
            Assert.That(
                gestures.Update(5.0, 0f, 0f, 0f, 0f, true, false, false, true, true),
                Is.EqualTo(FlightActionType.ReturnHome));
        }

        [Test]
        public void AHold_TriggersLandingOnlyWhileFlying()
        {
            var gestures = new SimulatorFlightGestureRecognizer();

            gestures.Update(1.0, 0f, 0f, 0f, 0f, false, true, true, false, false);
            Assert.That(
                gestures.Update(2.0, 0f, 0f, 0f, 0f, false, true, true, false, false),
                Is.Null);

            gestures.Update(3.0, 0f, 0f, 0f, 0f, false, false, false, true, true);
            gestures.Update(4.0, 0f, 0f, 0f, 0f, false, true, false, true, true);
            Assert.That(
                gestures.Update(5.0, 0f, 0f, 0f, 0f, false, true, false, true, true),
                Is.EqualTo(FlightActionType.Land));
        }

        [Test]
        public void WrongStickDirections_DoNotTriggerTakeoff()
        {
            var gestures = new SimulatorFlightGestureRecognizer();
            gestures.Update(1.0, -1f, -1f, 1f, -1f, false, false, true, false, false);

            var result = gestures.Update(
                4.0,
                -1f,
                -1f,
                1f,
                -1f,
                false,
                false,
                true,
                false,
                false);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void MeasuredQuestInwardValues_TriggerTakeoff()
        {
            var gestures = new SimulatorFlightGestureRecognizer();
            gestures.Update(
                1.0,
                0.90f,
                -0.37f,
                -0.93f,
                -0.38f,
                false,
                false,
                true,
                false,
                false);

            var result = gestures.Update(
                2.0,
                0.90f,
                -0.37f,
                -0.93f,
                -0.38f,
                false,
                false,
                true,
                false,
                false);

            Assert.That(result, Is.EqualTo(FlightActionType.Takeoff));
        }

        [Test]
        public void HorizontalOnlyInwardSticks_DoNotTriggerTakeoff()
        {
            var gestures = new SimulatorFlightGestureRecognizer();
            gestures.Update(1.0, 1f, -0.1f, -1f, -0.1f, false, false, true, false, false);

            var result = gestures.Update(
                3.0,
                1f,
                -0.1f,
                -1f,
                -0.1f,
                false,
                false,
                true,
                false,
                false);

            Assert.That(result, Is.Null);
        }

        private static FlightActionType? Update(
            SimulatorFlightGestureRecognizer gestures,
            double now,
            bool grounded)
        {
            return gestures.Update(
                now,
                1f,
                -1f,
                -1f,
                -1f,
                false,
                false,
                grounded,
                false,
                false);
        }
    }
}
