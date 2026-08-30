namespace QuestCommandCenter.VehicleCore
{
    /// <summary>
    /// Owns one in-flight camera command and rejects invalid or conflicting
    /// requests before an adapter is invoked.
    /// </summary>
    public sealed class CameraCommandSession
    {
        private long nextRequestId;

        public CameraCommandResult Current { get; private set; } =
            new CameraCommandResult(0, CameraCommandType.TakePhoto, CameraCommandStatus.Idle, "Camera idle.");

        public bool IsPending => Current.Status == CameraCommandStatus.Pending;

        public bool TryBegin(
            CameraCommandType command,
            bool cameraAvailable,
            bool storageReady,
            bool isRecording,
            out CameraCommandResult result)
        {
            if (IsPending)
            {
                result = new CameraCommandResult(
                    ++nextRequestId,
                    command,
                    CameraCommandStatus.Rejected,
                    "Another camera command is still pending.");
                return false;
            }

            if (!cameraAvailable)
            {
                result = Reject(command, "Camera is unavailable.");
                return false;
            }

            if (command == CameraCommandType.StartRecording && isRecording)
            {
                result = Reject(command, "Camera is already recording.");
                return false;
            }

            if (command == CameraCommandType.StopRecording && !isRecording)
            {
                result = Reject(command, "Camera is not recording.");
                return false;
            }

            if (command == CameraCommandType.TakePhoto && isRecording)
            {
                result = Reject(command, "Stop Spark recording before taking a photo.");
                return false;
            }

            if (command != CameraCommandType.StopRecording && !storageReady)
            {
                result = Reject(command, "Camera storage is unavailable, full, read-only, or reporting an error.");
                return false;
            }

            Current = new CameraCommandResult(
                ++nextRequestId,
                command,
                CameraCommandStatus.Pending,
                PendingMessage(command));
            result = Current;
            return true;
        }

        public bool TryComplete(long requestId, bool succeeded, string message, out CameraCommandResult result)
        {
            if (!IsPending || requestId != Current.RequestId)
            {
                result = Current;
                return false;
            }

            Current = new CameraCommandResult(
                requestId,
                Current.Command,
                succeeded ? CameraCommandStatus.Succeeded : CameraCommandStatus.Failed,
                message);
            result = Current;
            return true;
        }

        public bool FailPending(string message, out CameraCommandResult result)
        {
            if (!IsPending)
            {
                result = Current;
                return false;
            }

            return TryComplete(Current.RequestId, false, message, out result);
        }

        private CameraCommandResult Reject(CameraCommandType command, string message)
        {
            Current = new CameraCommandResult(++nextRequestId, command, CameraCommandStatus.Rejected, message);
            return Current;
        }

        private static string PendingMessage(CameraCommandType command)
        {
            switch (command)
            {
                case CameraCommandType.StartRecording:
                    return "Starting onboard recording.";
                case CameraCommandType.StopRecording:
                    return "Stopping onboard recording.";
                default:
                    return "Taking one onboard photo.";
            }
        }
    }
}
