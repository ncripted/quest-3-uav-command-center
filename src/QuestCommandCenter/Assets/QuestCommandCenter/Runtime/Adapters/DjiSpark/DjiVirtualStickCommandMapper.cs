using System;
using QuestCommandCenter.VehicleCore;

namespace QuestCommandCenter.Adapters.DjiSpark
{
    public sealed class DjiVirtualStickDevelopmentProfile
    {
        public float MaximumHorizontalVelocityMetersPerSecond { get; set; } = 1.0f;
        public float MaximumVerticalVelocityMetersPerSecond { get; set; } = 0.8f;
        public float MaximumYawRateDegreesPerSecond { get; set; } = 30f;

        public void Validate()
        {
            ValidateLimit(MaximumHorizontalVelocityMetersPerSecond, 15f, nameof(MaximumHorizontalVelocityMetersPerSecond));
            ValidateLimit(MaximumVerticalVelocityMetersPerSecond, 4f, nameof(MaximumVerticalVelocityMetersPerSecond));
            ValidateLimit(MaximumYawRateDegreesPerSecond, 100f, nameof(MaximumYawRateDegreesPerSecond));
        }

        private static void ValidateLimit(float value, float officialMaximum, string name)
        {
            if (float.IsNaN(value)
                || float.IsInfinity(value)
                || value <= 0f
                || value > officialMaximum)
            {
                throw new InvalidOperationException(
                    name + " must be finite, positive, and no greater than DJI's documented limit.");
            }
        }
    }

    public readonly struct DjiVirtualStickCommand
    {
        public DjiVirtualStickCommand(
            float pitchMetersPerSecond,
            float rollMetersPerSecond,
            float yawRateDegreesPerSecond,
            float verticalMetersPerSecond)
        {
            PitchMetersPerSecond = pitchMetersPerSecond;
            RollMetersPerSecond = rollMetersPerSecond;
            YawRateDegreesPerSecond = yawRateDegreesPerSecond;
            VerticalMetersPerSecond = verticalMetersPerSecond;
        }

        public float PitchMetersPerSecond { get; }
        public float RollMetersPerSecond { get; }
        public float YawRateDegreesPerSecond { get; }
        public float VerticalMetersPerSecond { get; }
    }

    /// <summary>
    /// Converts an already safety-approved normalized command into DJI's body-
    /// relative velocity/angular-velocity values. It cannot call the DJI SDK.
    /// </summary>
    public sealed class DjiVirtualStickCommandMapper
    {
        private readonly DjiVirtualStickDevelopmentProfile profile;

        public DjiVirtualStickCommandMapper(DjiVirtualStickDevelopmentProfile profile)
        {
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
            profile.Validate();
        }

        public DjiVirtualStickCommand Map(NormalizedFlightCommand command)
        {
            return new DjiVirtualStickCommand(
                command.Pitch * profile.MaximumHorizontalVelocityMetersPerSecond,
                command.Roll * profile.MaximumHorizontalVelocityMetersPerSecond,
                command.Yaw * profile.MaximumYawRateDegreesPerSecond,
                command.Vertical * profile.MaximumVerticalVelocityMetersPerSecond);
        }
    }
}
