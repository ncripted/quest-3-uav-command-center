using System.Globalization;
using QuestCommandCenter.VehicleCore;
using QuestCommandCenter.Video;
using UnityEngine;
using UnityEngine.UI;

namespace QuestCommandCenter.UI
{
    /// <summary>
    /// Renders generic command-center state. It does not know about DJI SDK types
    /// and remains usable with a MockDrone or remote backend.
    /// </summary>
    public sealed class QuestDashboardPresenter : MonoBehaviour
    {
        [SerializeField] private Text vehicleText;
        [SerializeField] private Text topStatusText;
        [SerializeField] private Text bottomStatusText;
        [SerializeField] private Text missionText;
        [SerializeField] private Text alertText;
        [SerializeField] private Text videoLabelText;
        [SerializeField] private Text mapText;
        [SerializeField] private Text telemetryDetailText;
        [SerializeField] private MonoBehaviour stateSourceBehaviour;
        [SerializeField] private MonoBehaviour flightActionSourceBehaviour;
        [SerializeField] private MonoBehaviour controlStateSourceBehaviour;

        private IStatusSource statusSource;
        private IVehicleTelemetrySource telemetrySource;
        private IVideoTextureSource videoSource;
        private ICameraControl cameraControl;
        private CameraCommandResult cameraCommand;
        private IFlightActionControl flightActionControl;
        private FlightActionResult flightAction;
        private IManualFlightControlState controlStateSource;

        public void Initialize(
            Text vehicle,
            Text topStatus,
            Text bottomStatus,
            Text mission,
            Text alert,
            Text videoLabel,
            Text map,
            Text telemetryDetail,
            MonoBehaviour source)
        {
            vehicleText = vehicle;
            topStatusText = topStatus;
            bottomStatusText = bottomStatus;
            missionText = mission;
            alertText = alert;
            videoLabelText = videoLabel;
            mapText = map;
            telemetryDetailText = telemetryDetail;
            stateSourceBehaviour = source;
            BindSource();
            Render();
        }

        public void InitializeFlightActionSource(MonoBehaviour source)
        {
            UnbindSource();
            flightActionSourceBehaviour = source;
            BindSource();
            Render();
        }

        public void InitializeControlStateSource(MonoBehaviour source)
        {
            UnbindSource();
            controlStateSourceBehaviour = source;
            BindSource();
            Render();
        }

        private void OnEnable()
        {
            BindSource();
            Render();
        }

        private void OnDisable()
        {
            UnbindSource();
        }

        private void BindSource()
        {
            UnbindSource();
            statusSource = stateSourceBehaviour as IStatusSource;
            telemetrySource = stateSourceBehaviour as IVehicleTelemetrySource;
            videoSource = stateSourceBehaviour as IVideoTextureSource;
            cameraControl = stateSourceBehaviour as ICameraControl;
            flightActionControl = (flightActionSourceBehaviour != null
                ? flightActionSourceBehaviour
                : stateSourceBehaviour) as IFlightActionControl;
            controlStateSource = controlStateSourceBehaviour as IManualFlightControlState;

            if (statusSource != null)
            {
                statusSource.StatusChanged += OnStatusChanged;
            }
            if (telemetrySource != null)
            {
                telemetrySource.TelemetryUpdated += OnTelemetryUpdated;
            }
            if (videoSource != null)
            {
                videoSource.VideoFrameUpdated += OnVideoFrameUpdated;
            }
            if (cameraControl != null)
            {
                cameraControl.CameraCommandStateChanged += OnCameraCommandStateChanged;
                cameraCommand = cameraControl.LastCameraCommand;
            }
            if (flightActionControl != null)
            {
                flightActionControl.FlightActionStateChanged += OnFlightActionStateChanged;
                flightAction = flightActionControl.LastFlightAction;
            }
            if (controlStateSource != null)
            {
                controlStateSource.StatusChanged += OnStatusChanged;
            }
        }

        private void UnbindSource()
        {
            if (statusSource != null)
            {
                statusSource.StatusChanged -= OnStatusChanged;
            }
            if (telemetrySource != null)
            {
                telemetrySource.TelemetryUpdated -= OnTelemetryUpdated;
            }
            if (videoSource != null)
            {
                videoSource.VideoFrameUpdated -= OnVideoFrameUpdated;
            }
            if (cameraControl != null)
            {
                cameraControl.CameraCommandStateChanged -= OnCameraCommandStateChanged;
            }
            if (flightActionControl != null)
            {
                flightActionControl.FlightActionStateChanged -= OnFlightActionStateChanged;
            }
            if (controlStateSource != null)
            {
                controlStateSource.StatusChanged -= OnStatusChanged;
            }
        }

        private void OnStatusChanged(string _)
        {
            Render();
        }

        private void OnTelemetryUpdated(VehicleTelemetry _)
        {
            Render();
        }

        private void OnVideoFrameUpdated(Texture _)
        {
            Render();
        }

        private void OnCameraCommandStateChanged(CameraCommandResult result)
        {
            cameraCommand = result;
            Render();
        }

        private void OnFlightActionStateChanged(FlightActionResult result)
        {
            flightAction = result;
            Render();
        }

        private void Render()
        {
            var telemetry = telemetrySource?.LatestTelemetry;
            var connected = telemetry?.IsConnected == true;
            var product = connected && !string.IsNullOrEmpty(telemetry.ProductName)
                ? telemetry.ProductName
                : "NO VEHICLE";

            if (vehicleText != null)
            {
                var mode = telemetry?.HasFlightState == true && !string.IsNullOrEmpty(telemetry.FlightMode)
                    ? telemetry.FlightMode.ToUpperInvariant()
                    : connected ? "PREFLIGHT" : "OFFLINE";
                vehicleText.text = "QCC   |   " + product + "   |   " + mode;
                vehicleText.color = connected
                    ? new Color(0.55f, 1f, 0.72f, 1f)
                    : new Color(0.65f, 0.72f, 0.82f, 1f);
            }

            if (topStatusText != null)
            {
                var battery = telemetry?.HasBatteryState == true
                    ? telemetry.BatteryPercent + "%"
                    : "--";
                var gps = telemetry?.HasFlightState == true
                    ? telemetry.SatelliteCount + " SAT / SIGNAL " + telemetry.GpsSignalLevel
                    : "UNAVAILABLE";
                var recording = telemetry?.HasCameraState == true && telemetry.CameraRecording
                    ? "● REC"
                    : "○ REC";
                topStatusText.text = "GPS  " + gps
                    + "     RC  " + (connected ? "CONNECTED" : "OFFLINE")
                    + "     BAT  " + battery + "\n"
                    + "VIDEO  " + (videoSource?.HasVideoFrame == true ? "LIVE" : "WAITING")
                    + "     " + recording;
                topStatusText.color = telemetry?.HasBatteryState == true && telemetry.BatteryPercent <= 10
                    ? new Color(1f, 0.3f, 0.25f, 1f)
                    : Color.white;
            }

            if (bottomStatusText != null)
            {
                var hasFlight = telemetry?.HasFlightState == true;
                bottomStatusText.text = "ALT  "
                    + Format(hasFlight ? telemetry.AltitudeMeters : (float?)null, "0.0") + " m"
                    + "     SPEED  " + Format(hasFlight ? telemetry.HorizontalSpeedMetersPerSecond : (float?)null, "0.0") + " m/s"
                    + "     V/S  " + Format(hasFlight ? -telemetry.VelocityDownMetersPerSecond : (float?)null, "0.0") + " m/s\n"
                    + "HDG  " + (hasFlight ? telemetry.HeadingDegrees.ToString() + "°" : "---")
                    + "     CONTROL  " + ControlStatus(connected);
            }

            if (missionText != null)
            {
                missionText.text = "MISSION\nQUEUE  0\n\nAUTOPILOT  LOCKED";
            }

            if (videoLabelText != null)
            {
                var live = videoSource?.HasVideoFrame == true;
                videoLabelText.text = live ? "LIVE  •  SPARK FPV" : "FPV  •  WAITING FOR VIDEO";
                videoLabelText.color = live
                    ? new Color(0.55f, 1f, 0.72f, 1f)
                    : new Color(0.65f, 0.72f, 0.82f, 1f);
            }

            if (mapText != null)
            {
                var hasPosition = telemetry?.HasFlightState == true && telemetry.PositionValid;
                mapText.text = "N  ↑\n\n        △\n\nGPS  "
                    + (hasPosition ? "POSITION FIX" : "NO POSITION")
                    + "     HOME  --     200 m";
                mapText.color = hasPosition
                    ? new Color(0.75f, 1f, 0.78f, 1f)
                    : new Color(0.78f, 0.84f, 0.9f, 1f);
            }

            RenderTelemetryDetails(telemetry, connected);

            RenderAlert(telemetry, connected);
        }

        private void RenderTelemetryDetails(VehicleTelemetry telemetry, bool connected)
        {
            if (telemetryDetailText == null)
            {
                return;
            }

            var hasFlight = telemetry?.HasFlightState == true;
            var flightState = !hasFlight
                ? "UNAVAILABLE"
                : telemetry.IsFlying
                    ? "FLYING"
                    : telemetry.MotorsOn ? "GROUND / MOTORS ON" : "LANDED / MOTORS OFF";
            var flightMode = hasFlight && !string.IsNullOrEmpty(telemetry.FlightMode)
                ? telemetry.FlightMode
                : "Unavailable";
            var gpsState = !hasFlight
                ? "Unavailable"
                : telemetry.PositionValid ? "VALID FIX" : "INVALID / NO FIX";
            var position = hasFlight && telemetry.PositionValid
                ? telemetry.LatitudeDegrees.ToString("0.000000", CultureInfo.InvariantCulture)
                    + ", " + telemetry.LongitudeDegrees.ToString("0.000000", CultureInfo.InvariantCulture)
                : "Unavailable";
            var attitude = hasFlight
                ? "P " + telemetry.PitchDegrees.ToString("0.0", CultureInfo.InvariantCulture) + "°   "
                    + "R " + telemetry.RollDegrees.ToString("0.0", CultureInfo.InvariantCulture) + "°   "
                    + "Y " + telemetry.YawDegrees.ToString("0.0", CultureInfo.InvariantCulture) + "°"
                : "Unavailable";
            var velocity = hasFlight
                ? "N " + telemetry.VelocityNorthMetersPerSecond.ToString("0.0", CultureInfo.InvariantCulture)
                    + "   E " + telemetry.VelocityEastMetersPerSecond.ToString("0.0", CultureInfo.InvariantCulture)
                    + "   UP " + (-telemetry.VelocityDownMetersPerSecond).ToString("0.0", CultureInfo.InvariantCulture) + " m/s"
                : "Unavailable";
            var battery = telemetry?.HasBatteryState == true
                ? telemetry.BatteryPercent + "%   "
                    + (telemetry.BatteryVoltageMillivolts / 1000f).ToString("0.00", CultureInfo.InvariantCulture) + " V   "
                    + telemetry.BatteryTemperatureCelsius.ToString("0.0", CultureInfo.InvariantCulture) + " °C"
                : "Unavailable";
            var camera = telemetry?.HasCameraState == true
                ? (string.IsNullOrEmpty(telemetry.CameraMode) ? "Mode unavailable" : telemetry.CameraMode)
                    + "   " + (telemetry.CameraRecording ? "RECORDING" : "Idle")
                : "Unavailable";
            var storage = telemetry?.HasCameraStorageState == true
                ? telemetry.CameraStorageInserted
                    ? telemetry.CameraStorageRemainingMegabytes + " MB remaining"
                    : "NO SD CARD"
                : "Unavailable";
            var gimbal = telemetry?.HasGimbalState == true
                ? "Pitch " + telemetry.GimbalPitchDegrees.ToString("0.0", CultureInfo.InvariantCulture) + "°   "
                    + (string.IsNullOrEmpty(telemetry.GimbalMode) ? "Mode unavailable" : telemetry.GimbalMode)
                : "Unavailable";

            telemetryDetailText.text =
                "AIRCRAFT SYSTEMS\n"
                + "LINK     " + (connected ? "CONNECTED" : "OFFLINE") + "\n"
                + "STATE    " + flightState + "\n"
                + "MODE     " + flightMode + "\n"
                + "HOME     " + (hasFlight ? telemetry.HomePositionSet ? "SET" : "NOT SET" : "Unavailable") + "\n"
                + "FAILSAFE " + (hasFlight ? telemetry.FailsafeEnabled ? "ACTIVE" : "CLEAR" : "--") + "\n"
                + "GPS      " + gpsState + "\n"
                + "SAT      " + (hasFlight ? telemetry.SatelliteCount + "    SIGNAL  " + telemetry.GpsSignalLevel : "Unavailable") + "\n"
                + "BAT      " + battery + "\n"
                + "POSITION\n  " + position + "\n"
                + "VEL      " + velocity + "\n"
                + "ATT      " + attitude + "\n"
                + "CAM      " + camera + "\n"
                + "SD       " + storage + "\n"
                + "GIMBAL   " + gimbal;

            telemetryDetailText.color = connected
                ? new Color(0.9f, 0.96f, 1f, 1f)
                : new Color(0.67f, 0.74f, 0.82f, 1f);
        }

        private void RenderAlert(VehicleTelemetry telemetry, bool connected)
        {
            if (alertText == null)
            {
                return;
            }

            if (!connected)
            {
                SetAlert("OFFLINE UI MODE  •  Connect MockDrone or Spark when available", false);
                return;
            }
            if (telemetry.HasBatteryState && telemetry.BatteryPercent <= 5)
            {
                SetAlert("CRITICAL BATTERY  •  Aircraft commands remain locked", true);
                return;
            }
            if (flightAction != null)
            {
                if (flightAction.Status == FlightActionStatus.AwaitingConfirmation)
                {
                    SetAlert("FLIGHT ACTION  •  " + flightAction.Message, true);
                    return;
                }
                if (flightAction.Status == FlightActionStatus.Pending)
                {
                    SetAlert("FLIGHT ACTION PENDING  •  " + flightAction.Action, true);
                    return;
                }
                if (flightAction.Status == FlightActionStatus.Failed
                    || flightAction.Status == FlightActionStatus.Rejected)
                {
                    SetAlert("FLIGHT ACTION BLOCKED  •  " + flightAction.Message, true);
                    return;
                }
                if (flightAction.Status == FlightActionStatus.Succeeded)
                {
                    SetAlert("DJI COMMAND ACCEPTED  •  Monitor aircraft telemetry", false);
                    return;
                }
            }
            if (cameraCommand != null
                && (cameraCommand.Status == CameraCommandStatus.Failed
                    || cameraCommand.Status == CameraCommandStatus.Rejected))
            {
                SetAlert("CAMERA  •  " + cameraCommand.Message, true);
                return;
            }
            if (telemetry.HasCameraStorageState && !telemetry.CameraStorageInserted)
            {
                SetAlert("CAMERA STORAGE MISSING  •  FPV remains available", false);
                return;
            }
            if (telemetry.CameraHasError || telemetry.CameraStorageHasError)
            {
                SetAlert("CAMERA ERROR  •  Operator action required", true);
                return;
            }

            if (controlStateSource?.IsLiveVehicleSelected == true)
            {
                if (controlStateSource.ControlMode == ControlMode.Failsafe)
                {
                    SetAlert("FAILSAFE  •  Real flight control disabled  •  Release controls", true);
                    return;
                }
                if (!controlStateSource.IsFlightControlReady)
                {
                    SetAlert("REAL FLIGHT LOCKED  •  Complete the outdoor preflight panel", false);
                    return;
                }
                SetAlert("FIELD READY  •  Right grip dead-man  •  B brake/hover", false);
                return;
            }

            SetAlert("SIM CONTROLS  •  Sticks down/in: Takeoff  •  Hold X: RTH  •  Hold A: Land", false);
        }

        private string ControlStatus(bool connected)
        {
            if (!connected)
            {
                return "DISCONNECTED";
            }
            if (controlStateSource == null || !controlStateSource.IsLiveVehicleSelected)
            {
                return "SIMULATOR";
            }
            if (controlStateSource.ControlMode == ControlMode.Failsafe)
            {
                return "FAILSAFE";
            }
            if (!controlStateSource.IsFlightControlReady)
            {
                return "FLIGHT LOCKED";
            }
            return controlStateSource.ControlMode == ControlMode.Manual
                ? "MANUAL"
                : "READY";
        }

        private void SetAlert(string message, bool critical)
        {
            alertText.text = message;
            alertText.color = critical
                ? new Color(1f, 0.3f, 0.25f, 1f)
                : new Color(1f, 0.75f, 0.3f, 1f);
        }

        private static string Format(float? value, string format)
        {
            return value.HasValue
                ? value.Value.ToString(format, CultureInfo.InvariantCulture)
                : "--";
        }
    }
}
