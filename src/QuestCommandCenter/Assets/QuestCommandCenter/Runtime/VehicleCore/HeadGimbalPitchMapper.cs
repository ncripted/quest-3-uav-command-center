using System;

namespace QuestCommandCenter.VehicleCore
{
    /// <summary>
    /// Converts relative headset pitch into a bounded absolute gimbal target.
    /// Unity headset pitch increases while looking down; DJI gimbal pitch uses
    /// negative degrees for downward camera motion.
    /// </summary>
    public static class HeadGimbalPitchMapper
    {
        public static float Map(
            float referenceHeadPitchDegrees,
            float currentHeadPitchDegrees,
            float referenceGimbalPitchDegrees,
            float minimumGimbalPitchDegrees,
            float maximumGimbalPitchDegrees)
        {
            ValidateFinite(referenceHeadPitchDegrees, nameof(referenceHeadPitchDegrees));
            ValidateFinite(currentHeadPitchDegrees, nameof(currentHeadPitchDegrees));
            ValidateFinite(referenceGimbalPitchDegrees, nameof(referenceGimbalPitchDegrees));
            ValidateFinite(minimumGimbalPitchDegrees, nameof(minimumGimbalPitchDegrees));
            ValidateFinite(maximumGimbalPitchDegrees, nameof(maximumGimbalPitchDegrees));
            if (minimumGimbalPitchDegrees > maximumGimbalPitchDegrees)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumGimbalPitchDegrees));
            }

            var headDelta = ShortestSignedAngle(
                currentHeadPitchDegrees - referenceHeadPitchDegrees);
            var requested = referenceGimbalPitchDegrees - headDelta;
            return Math.Max(minimumGimbalPitchDegrees, Math.Min(maximumGimbalPitchDegrees, requested));
        }

        private static float ShortestSignedAngle(float degrees)
        {
            while (degrees > 180f)
            {
                degrees -= 360f;
            }
            while (degrees < -180f)
            {
                degrees += 360f;
            }
            return degrees;
        }

        private static void ValidateFinite(float value, string parameterName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }
    }
}
