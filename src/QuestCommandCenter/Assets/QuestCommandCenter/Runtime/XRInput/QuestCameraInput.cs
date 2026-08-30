using QuestCommandCenter.VehicleCore;
using UnityEngine;
using UnityEngine.InputSystem;

namespace QuestCommandCenter.XRInput
{
    /// <summary>
    /// Maps the left Touch controller Y button to generic camera intents. A tap
    /// requests one photo; a one-second hold toggles onboard recording.
    /// </summary>
    public sealed class QuestCameraInput : MonoBehaviour
    {
        private const double RecordingHoldSeconds = 1.0;
        private const double CameraActionCooldownSeconds = 1.5;

        [SerializeField] private MonoBehaviour cameraControlBehaviour;
        private ICameraControl cameraControl;
        private InputAction cameraAction;
        private bool pressActive;
        private bool holdHandled;
        private double pressedAt;
        private double nextAllowedActionAt;

        public void Initialize(MonoBehaviour source)
        {
            cameraControlBehaviour = source;
            cameraControl = source as ICameraControl;
        }

        private void Awake()
        {
            cameraAction = new InputAction(
                "Camera Action",
                InputActionType.Button,
                "<XRController>{LeftHand}/secondaryButton");
            cameraControl = cameraControlBehaviour as ICameraControl;
        }

        private void OnEnable()
        {
            cameraAction?.Enable();
        }

        private void OnDisable()
        {
            cameraAction?.Disable();
            pressActive = false;
            holdHandled = false;
        }

        private void OnDestroy()
        {
            cameraAction?.Dispose();
        }

        private void Update()
        {
            if (cameraAction == null || cameraControl == null)
            {
                return;
            }

            if (cameraAction.WasPressedThisFrame())
            {
                if (Time.realtimeSinceStartupAsDouble < nextAllowedActionAt)
                {
                    pressActive = false;
                    holdHandled = false;
                    return;
                }
                pressActive = true;
                holdHandled = false;
                pressedAt = Time.realtimeSinceStartupAsDouble;
            }

            if (pressActive
                && !holdHandled
                && cameraAction.IsPressed()
                && Time.realtimeSinceStartupAsDouble - pressedAt >= RecordingHoldSeconds)
            {
                holdHandled = true;
                nextAllowedActionAt = Time.realtimeSinceStartupAsDouble + CameraActionCooldownSeconds;
                if (cameraControl.IsRecording)
                {
                    cameraControl.RequestStopRecording();
                }
                else
                {
                    cameraControl.RequestStartRecording();
                }
            }

            if (!cameraAction.WasReleasedThisFrame())
            {
                return;
            }

            if (pressActive && !holdHandled)
            {
                nextAllowedActionAt = Time.realtimeSinceStartupAsDouble + CameraActionCooldownSeconds;
                cameraControl.RequestTakePhoto();
            }
            pressActive = false;
            holdHandled = false;
        }
    }
}
