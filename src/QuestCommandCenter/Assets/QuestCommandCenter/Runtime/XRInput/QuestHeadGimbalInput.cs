using System;
using QuestCommandCenter.VehicleCore;
using UnityEngine;
using UnityEngine.InputSystem;

namespace QuestCommandCenter.XRInput
{
    /// <summary>
    /// Modifier-gated, pitch-only headset tracking. It emits bounded absolute
    /// gimbal targets through a generic contract and never controls yaw or flight.
    /// </summary>
    public sealed class QuestHeadGimbalInput : MonoBehaviour
    {
        private const float MinimumPitchDegrees = -20f;
        private const float MaximumPitchDegrees = 0f;
        private const float TargetDeadbandDegrees = 2f;
        private const float SmoothingResponse = 7f;
        private const double MinimumCommandIntervalSeconds = 0.25;

        [SerializeField] private Transform headTransform;
        [SerializeField] private MonoBehaviour controlBehaviour;
        private IGimbalControl gimbalControl;
        private IVehicleTelemetrySource telemetrySource;
        private InputAction cameraModifier;
        private bool trackingActive;
        private float referenceHeadPitch;
        private float referenceGimbalPitch;
        private float smoothedTargetPitch;
        private float lastCommandedPitch;
        private double nextCommandAt;

        public void Initialize(Transform headset, MonoBehaviour source)
        {
            headTransform = headset;
            controlBehaviour = source;
            BindSource();
        }

        private void Awake()
        {
            cameraModifier = new InputAction(
                "Head Gimbal Camera Modifier",
                InputActionType.Button,
                "<XRController>{LeftHand}/triggerPressed");
            BindSource();
        }

        private void OnEnable()
        {
            cameraModifier?.Enable();
        }

        private void OnDisable()
        {
            cameraModifier?.Disable();
            StopTracking("component disabled");
        }

        private void OnDestroy()
        {
            cameraModifier?.Dispose();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                StopTracking("application paused");
            }
        }

        private void Update()
        {
            if (cameraModifier == null || headTransform == null || gimbalControl == null)
            {
                return;
            }

            if (!cameraModifier.IsPressed())
            {
                if (trackingActive)
                {
                    StopTracking("left trigger released");
                }
                return;
            }

            if (!trackingActive && !TryStartTracking())
            {
                return;
            }

            var rawTarget = HeadGimbalPitchMapper.Map(
                referenceHeadPitch,
                headTransform.eulerAngles.x,
                referenceGimbalPitch,
                MinimumPitchDegrees,
                MaximumPitchDegrees);
            var smoothing = 1f - Mathf.Exp(-SmoothingResponse * Time.unscaledDeltaTime);
            smoothedTargetPitch = Mathf.Lerp(smoothedTargetPitch, rawTarget, smoothing);

            var now = Time.realtimeSinceStartupAsDouble;
            if (now < nextCommandAt
                || gimbalControl.IsGimbalCommandPending
                || Mathf.Abs(smoothedTargetPitch - lastCommandedPitch) < TargetDeadbandDegrees)
            {
                return;
            }

            nextCommandAt = now + MinimumCommandIntervalSeconds;
            var target = Mathf.Clamp(smoothedTargetPitch, MinimumPitchDegrees, MaximumPitchDegrees);
            if (gimbalControl.RequestGimbalPitch(target))
            {
                lastCommandedPitch = target;
                Debug.Log(
                    "Head-gimbal pitch target accepted: "
                    + target.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                    + " degrees.");
            }
        }

        private bool TryStartTracking()
        {
            var telemetry = telemetrySource?.LatestTelemetry;
            if (telemetry?.HasFlightState != true
                || telemetry.IsFlying
                || telemetry.MotorsOn
                || telemetry.HasGimbalState != true
                || telemetry.GimbalCalibrating
                || telemetry.GimbalMotorOverloaded)
            {
                return false;
            }

            trackingActive = true;
            referenceHeadPitch = headTransform.eulerAngles.x;
            referenceGimbalPitch = Mathf.Clamp(
                telemetry.GimbalPitchDegrees,
                MinimumPitchDegrees,
                MaximumPitchDegrees);
            smoothedTargetPitch = referenceGimbalPitch;
            lastCommandedPitch = referenceGimbalPitch;
            nextCommandAt = Time.realtimeSinceStartupAsDouble;
            Debug.Log("Head-gimbal tracking enabled: pitch only, range -20 to 0 degrees.");
            return true;
        }

        private void StopTracking(string reason)
        {
            if (trackingActive)
            {
                Debug.Log("Head-gimbal tracking stopped: " + reason + ". Holding last absolute pitch target.");
            }
            trackingActive = false;
        }

        private void BindSource()
        {
            gimbalControl = controlBehaviour as IGimbalControl;
            telemetrySource = controlBehaviour as IVehicleTelemetrySource;
        }
    }
}
