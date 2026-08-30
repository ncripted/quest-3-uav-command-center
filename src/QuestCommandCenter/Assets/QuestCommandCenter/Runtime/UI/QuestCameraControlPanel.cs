using QuestCommandCenter.VehicleCore;
using UnityEngine;
using UnityEngine.UI;

namespace QuestCommandCenter.UI
{
    public sealed class QuestCameraControlPanel : MonoBehaviour
    {
        [SerializeField] private Text controlText;
        [SerializeField] private MonoBehaviour cameraControlBehaviour;
        private ICameraControl cameraControl;
        private IStatusSource statusSource;
        private CameraCommandResult displayedResult;

        public void Initialize(Text target, MonoBehaviour source)
        {
            controlText = target;
            cameraControlBehaviour = source;
            BindCameraControl();
            Render();
        }

        private void OnEnable()
        {
            BindCameraControl();
            Render();
        }

        private void OnDisable()
        {
            if (cameraControl != null)
            {
                cameraControl.CameraCommandStateChanged -= OnCameraCommandStateChanged;
            }
            if (statusSource != null)
            {
                statusSource.StatusChanged -= OnStatusChanged;
            }
        }

        private void BindCameraControl()
        {
            if (cameraControl != null)
            {
                cameraControl.CameraCommandStateChanged -= OnCameraCommandStateChanged;
            }
            if (statusSource != null)
            {
                statusSource.StatusChanged -= OnStatusChanged;
            }

            cameraControl = cameraControlBehaviour as ICameraControl;
            statusSource = cameraControlBehaviour as IStatusSource;
            if (cameraControl != null)
            {
                cameraControl.CameraCommandStateChanged += OnCameraCommandStateChanged;
                displayedResult = cameraControl.LastCameraCommand;
            }
            if (statusSource != null)
            {
                statusSource.StatusChanged += OnStatusChanged;
            }
        }

        private void OnCameraCommandStateChanged(CameraCommandResult result)
        {
            displayedResult = result;
            Render();
        }

        private void OnStatusChanged(string _)
        {
            Render();
        }

        private void Render()
        {
            if (controlText == null)
            {
                return;
            }

            var recording = cameraControl != null && cameraControl.IsRecording;
            var available = cameraControl != null && cameraControl.IsCameraAvailable;
            var command = displayedResult == null
                ? "Idle"
                : displayedResult.Status + ": " + displayedResult.Message;

            var recordingSeconds = cameraControlBehaviour is IVehicleTelemetrySource telemetrySource
                && telemetrySource.LatestTelemetry?.HasCameraState == true
                ? telemetrySource.LatestTelemetry.CameraRecordingSeconds
                : 0;
            var minutes = recordingSeconds / 60;
            var seconds = recordingSeconds % 60;

            controlText.text =
                (recording ? "■" : "●") + "\n"
                + minutes.ToString("00") + ":" + seconds.ToString("00") + "\n\n"
                + "Y  PHOTO\n"
                + "HOLD Y  " + (recording ? "STOP REC" : "START REC") + "\n\n"
                + "HOLD LEFT\nTRIGGER\nHEAD PITCH\n\n"
                + "CAM  " + (available ? "READY" : "WAITING") + "\n"
                + command;
            controlText.color = recording
                ? new Color(1f, 0.3f, 0.25f, 1f)
                : available
                    ? new Color(0.55f, 1f, 0.72f, 1f)
                    : new Color(1f, 0.75f, 0.3f, 1f);
        }
    }
}
