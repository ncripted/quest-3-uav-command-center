using System;

namespace QuestCommandCenter.VehicleCore
{
    public enum CameraCommandType
    {
        StartRecording,
        StopRecording,
        TakePhoto
    }

    public enum CameraCommandStatus
    {
        Idle,
        Pending,
        Succeeded,
        Failed,
        Rejected
    }

    public sealed class CameraCommandResult
    {
        public CameraCommandResult(
            long requestId,
            CameraCommandType command,
            CameraCommandStatus status,
            string message)
        {
            RequestId = requestId;
            Command = command;
            Status = status;
            Message = message ?? string.Empty;
        }

        public long RequestId { get; }
        public CameraCommandType Command { get; }
        public CameraCommandStatus Status { get; }
        public string Message { get; }
    }

    public interface ICameraControl
    {
        bool IsCameraAvailable { get; }
        bool IsRecording { get; }
        bool IsCameraCommandPending { get; }
        CameraCommandResult LastCameraCommand { get; }

        event Action<CameraCommandResult> CameraCommandStateChanged;

        bool RequestStartRecording();
        bool RequestStopRecording();
        bool RequestTakePhoto();
    }
}
