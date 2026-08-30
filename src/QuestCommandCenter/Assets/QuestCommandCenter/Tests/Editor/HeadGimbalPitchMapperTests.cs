using NUnit.Framework;

namespace QuestCommandCenter.VehicleCore.Tests
{
    public sealed class HeadGimbalPitchMapperTests
    {
        [Test]
        public void Map_LookingDownProducesNegativeGimbalPitch()
        {
            var target = HeadGimbalPitchMapper.Map(0f, 15f, 0f, -20f, 0f);

            Assert.That(target, Is.EqualTo(-15f));
        }

        [Test]
        public void Map_IsRelativeToPitchAtModifierPress()
        {
            var target = HeadGimbalPitchMapper.Map(10f, 15f, -8f, -20f, 0f);

            Assert.That(target, Is.EqualTo(-13f));
        }

        [Test]
        public void Map_ClampsToDevelopmentRange()
        {
            Assert.That(HeadGimbalPitchMapper.Map(0f, 90f, 0f, -20f, 0f), Is.EqualTo(-20f));
            Assert.That(HeadGimbalPitchMapper.Map(0f, -90f, -5f, -20f, 0f), Is.EqualTo(0f));
        }

        [Test]
        public void Map_HandlesEulerWraparound()
        {
            var target = HeadGimbalPitchMapper.Map(355f, 5f, 0f, -20f, 0f);

            Assert.That(target, Is.EqualTo(-10f));
        }
    }
}
