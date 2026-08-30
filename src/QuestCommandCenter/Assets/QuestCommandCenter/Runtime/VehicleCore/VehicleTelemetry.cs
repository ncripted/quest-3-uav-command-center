using System;

namespace QuestCommandCenter.VehicleCore
{
    /// <summary>
    /// Vehicle-independent snapshot of read-only state. Adapters translate their
    /// SDK-specific telemetry into this model before it reaches UI or logging.
    /// </summary>
    [Serializable]
    public sealed class VehicleTelemetry
    {
        public long Sequence { get; set; }
        public long MonotonicMilliseconds { get; set; }
        public bool IsConnected { get; set; }
        public string ProductName { get; set; }

        public bool HasBatteryState { get; set; }
        public int BatteryPercent { get; set; }
        public int BatteryVoltageMillivolts { get; set; }
        public int BatteryCurrentMilliamps { get; set; }
        public float BatteryTemperatureCelsius { get; set; }

        public bool HasFlightState { get; set; }
        public bool PositionValid { get; set; }
        public double LatitudeDegrees { get; set; }
        public double LongitudeDegrees { get; set; }
        public float AltitudeMeters { get; set; }
        public float PitchDegrees { get; set; }
        public float RollDegrees { get; set; }
        public float YawDegrees { get; set; }
        public int HeadingDegrees { get; set; }
        public float VelocityNorthMetersPerSecond { get; set; }
        public float VelocityEastMetersPerSecond { get; set; }
        public float VelocityDownMetersPerSecond { get; set; }
        public int SatelliteCount { get; set; }
        public int GpsSignalLevel { get; set; }
        public string FlightMode { get; set; }
        public bool MotorsOn { get; set; }
        public bool IsFlying { get; set; }
        public bool IsGoingHome { get; set; }
        public bool HomePositionSet { get; set; }
        public bool FailsafeEnabled { get; set; }

        public bool HasCameraState { get; set; }
        public string CameraMode { get; set; }
        public bool CameraRecording { get; set; }
        public bool CameraShootingPhoto { get; set; }
        public int CameraRecordingSeconds { get; set; }
        public bool CameraOverheating { get; set; }
        public bool CameraHasError { get; set; }
        public bool HasCameraStorageState { get; set; }
        public bool CameraStorageInserted { get; set; }
        public bool CameraStorageFull { get; set; }
        public bool CameraStorageReadOnly { get; set; }
        public bool CameraStorageHasError { get; set; }
        public int CameraStorageRemainingMegabytes { get; set; }
        public long CameraAvailablePhotoCount { get; set; }
        public int CameraAvailableRecordingSeconds { get; set; }

        public bool HasGimbalState { get; set; }
        public float GimbalPitchDegrees { get; set; }
        public float GimbalRollDegrees { get; set; }
        public float GimbalYawDegrees { get; set; }
        public string GimbalMode { get; set; }
        public bool GimbalCalibrating { get; set; }
        public bool GimbalCalibrationSuccessful { get; set; }
        public bool GimbalMotorOverloaded { get; set; }
        public bool GimbalPitchAtStop { get; set; }

        public float HorizontalSpeedMetersPerSecond
        {
            get
            {
                var north = VelocityNorthMetersPerSecond;
                var east = VelocityEastMetersPerSecond;
                return (float)Math.Sqrt((north * north) + (east * east));
            }
        }
    }
}
