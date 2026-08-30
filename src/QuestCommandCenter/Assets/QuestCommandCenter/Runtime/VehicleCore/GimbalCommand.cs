using System;

namespace QuestCommandCenter.VehicleCore
{
    public enum GimbalCommandStatus
    {
        Idle,
        Pending,
        Succeeded,
        Failed,
        Rejected
    }

    public sealed class GimbalCommandResult
    {
        public GimbalCommandResult(
            long requestId,
            float pitchDegrees,
            GimbalCommandStatus status,
            string message)
        {
            RequestId = requestId;
            PitchDegrees = pitchDegrees;
            Status = status;
            Message = message ?? string.Empty;
        }

        public long RequestId { get; }
        public float PitchDegrees { get; }
        public GimbalCommandStatus Status { get; }
        public string Message { get; }
    }

    public interface IGimbalControl
    {
        bool IsGimbalAvailable { get; }
        bool IsGimbalCommandPending { get; }
        GimbalCommandResult LastGimbalCommand { get; }

        event Action<GimbalCommandResult> GimbalCommandStateChanged;

        bool RequestGimbalPitch(float absolutePitchDegrees);
    }
}
