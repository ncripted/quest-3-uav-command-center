using System;

namespace QuestCommandCenter.VehicleCore
{
    /// <summary>
    /// Vehicle-independent pilot or autopilot intent. Values are always in [-1, 1].
    /// No instance of this type talks to an aircraft SDK directly.
    /// </summary>
    [Serializable]
    public struct NormalizedFlightCommand
    {
        public static readonly NormalizedFlightCommand Neutral = new NormalizedFlightCommand(0f, 0f, 0f, 0f);

        public float Roll { get; }
        public float Pitch { get; }
        public float Yaw { get; }
        public float Vertical { get; }

        public NormalizedFlightCommand(float roll, float pitch, float yaw, float vertical)
        {
            EnsureFinite(roll, nameof(roll));
            EnsureFinite(pitch, nameof(pitch));
            EnsureFinite(yaw, nameof(yaw));
            EnsureFinite(vertical, nameof(vertical));
            Roll = ClampAxis(roll);
            Pitch = ClampAxis(pitch);
            Yaw = ClampAxis(yaw);
            Vertical = ClampAxis(vertical);
        }

        public bool IsNeutral(float tolerance = 0.0001f)
        {
            return Math.Abs(Roll) <= tolerance
                && Math.Abs(Pitch) <= tolerance
                && Math.Abs(Yaw) <= tolerance
                && Math.Abs(Vertical) <= tolerance;
        }

        private static float ClampAxis(float value)
        {
            return Math.Max(-1f, Math.Min(1f, value));
        }

        private static void EnsureFinite(float value, string parameterName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(parameterName, value, "Flight command axes must be finite.");
            }
        }
    }
}
