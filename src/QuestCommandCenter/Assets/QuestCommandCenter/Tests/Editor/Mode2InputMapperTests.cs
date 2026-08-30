using NUnit.Framework;

namespace QuestCommandCenter.VehicleCore.Tests
{
    public sealed class Mode2InputMapperTests
    {
        [Test]
        public void Map_UsesRequestedMode2Axes()
        {
            var intent = Mode2InputMapper.Map(
                leftX: 0.5f,
                leftY: -0.6f,
                rightX: -0.7f,
                rightY: 0.8f,
                rightGripHeld: true,
                brakeHoverRequested: false,
                manualTakeoverRequested: false,
                cameraModifierActive: false,
                deadZone: 0f);

            Assert.That(intent.Command.Roll, Is.EqualTo(-0.7f));
            Assert.That(intent.Command.Pitch, Is.EqualTo(0.8f));
            Assert.That(intent.Command.Yaw, Is.EqualTo(0.5f));
            Assert.That(intent.Command.Vertical, Is.EqualTo(-0.6f));
        }

        [Test]
        public void Map_ReturnsNeutralWithoutRightGripAuthorization()
        {
            var intent = Mode2InputMapper.Map(1f, 1f, 1f, 1f, false, false, false, false);

            Assert.That(intent.ManualFlightAuthorized, Is.False);
            Assert.That(intent.Command.IsNeutral(), Is.True);
        }

        [Test]
        public void Map_BrakeOverridesAuthorizedStickInput()
        {
            var intent = Mode2InputMapper.Map(1f, 1f, 1f, 1f, true, true, false, false);

            Assert.That(intent.BrakeHoverRequested, Is.True);
            Assert.That(intent.ManualFlightAuthorized, Is.False);
            Assert.That(intent.Command.IsNeutral(), Is.True);
        }

        [Test]
        public void Map_RemovesDriftInsideDeadZone()
        {
            var intent = Mode2InputMapper.Map(0.1f, -0.1f, 0.1f, -0.1f, true, false, false, false);

            Assert.That(intent.Command.IsNeutral(), Is.True);
        }

        [Test]
        public void Map_PreservesTakeoverAndCameraModifierIntent()
        {
            var intent = Mode2InputMapper.Map(0f, 0f, 0f, 0f, false, false, true, true);

            Assert.That(intent.ManualTakeoverRequested, Is.True);
            Assert.That(intent.CameraModifierActive, Is.True);
        }
    }
}
