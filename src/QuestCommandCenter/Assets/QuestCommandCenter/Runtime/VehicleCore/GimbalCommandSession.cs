using System;

namespace QuestCommandCenter.VehicleCore
{
    /// <summary>
    /// Serializes the deliberately narrow Spark development gimbal command.
    /// This is not a general aircraft capability.
    /// </summary>
    public sealed class GimbalCommandSession
    {
        public const float MinimumPitchDegrees = -20f;
        public const float MaximumPitchDegrees = 0f;

        private long nextRequestId = 1;

        public GimbalCommandResult Current { get; private set; } =
            new GimbalCommandResult(0, 0f, GimbalCommandStatus.Idle, "Idle");

        public bool IsPending => Current.Status == GimbalCommandStatus.Pending;

        public bool TryBegin(
            float pitchDegrees,
            bool gimbalAvailable,
            bool aircraftStationary,
            bool gimbalHealthy,
            out GimbalCommandResult result)
        {
            if (IsPending)
            {
                result = Reject(pitchDegrees, "Another gimbal command is pending.");
                return false;
            }
            if (float.IsNaN(pitchDegrees) || float.IsInfinity(pitchDegrees)
                || pitchDegrees < MinimumPitchDegrees || pitchDegrees > MaximumPitchDegrees)
            {
                result = Reject(pitchDegrees, "Pitch target is outside the -20 to 0 degree development range.");
                return false;
            }
            if (!gimbalAvailable)
            {
                result = Reject(pitchDegrees, "Gimbal telemetry is unavailable.");
                return false;
            }
            if (!aircraftStationary)
            {
                result = Reject(pitchDegrees, "Aircraft must report landed with motors off.");
                return false;
            }
            if (!gimbalHealthy)
            {
                result = Reject(pitchDegrees, "Gimbal is calibrating or overloaded.");
                return false;
            }

            result = new GimbalCommandResult(
                nextRequestId++,
                pitchDegrees,
                GimbalCommandStatus.Pending,
                "Gimbal pitch command pending.");
            Current = result;
            return true;
        }

        public bool TryComplete(long requestId, bool succeeded, string message, out GimbalCommandResult result)
        {
            if (!IsPending || Current.RequestId != requestId)
            {
                result = Current;
                return false;
            }

            result = new GimbalCommandResult(
                requestId,
                Current.PitchDegrees,
                succeeded ? GimbalCommandStatus.Succeeded : GimbalCommandStatus.Failed,
                message);
            Current = result;
            return true;
        }

        public bool FailPending(string message, out GimbalCommandResult result)
        {
            return TryComplete(Current.RequestId, false, message, out result);
        }

        private GimbalCommandResult Reject(float pitchDegrees, string message)
        {
            var result = new GimbalCommandResult(0, pitchDegrees, GimbalCommandStatus.Rejected, message);
            if (!IsPending)
            {
                Current = result;
            }
            return result;
        }
    }
}
