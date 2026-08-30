using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using QuestCommandCenter.VehicleCore;
using UnityEngine;

namespace QuestCommandCenter.XRInput
{
    /// <summary>
    /// Local-first session recorder. Unity callbacks only serialize and enqueue;
    /// directory creation and file writes run on a dedicated background thread.
    /// </summary>
    public sealed class QuestFlightSessionLogger : MonoBehaviour
    {
        [SerializeField] private MonoBehaviour telemetrySourceBehaviour;
        [SerializeField] private MonoBehaviour inputSourceBehaviour;
        [SerializeField] private MonoBehaviour flightActionSourceBehaviour;
        [SerializeField] private MonoBehaviour statusSourceBehaviour;

        private readonly ConcurrentQueue<PendingLine> pendingLines =
            new ConcurrentQueue<PendingLine>();
        private IVehicleTelemetrySource telemetrySource;
        private IManualControlIntentSource inputSource;
        private IFlightActionControl flightActionSource;
        private IStatusSource statusSource;
        private AutoResetEvent writerSignal;
        private Thread writerThread;
        private string sessionDirectory;
        private string metadataJson;
        private long sessionStartTicks;
        private long sequence;
        private volatile bool stopping;
        private bool lastManualAuthorized;
        private bool sessionStarted;
        private string writerFailure;

        public string SessionDirectory => sessionDirectory;

        public void Initialize(
            MonoBehaviour telemetry,
            IManualControlIntentSource input,
            MonoBehaviour flightActions,
            MonoBehaviour status)
        {
            telemetrySourceBehaviour = telemetry;
            inputSourceBehaviour = input as MonoBehaviour;
            flightActionSourceBehaviour = flightActions;
            statusSourceBehaviour = status;
            BindReferences();
        }

        private void Start()
        {
            BindReferences();
            StartSession();
            Subscribe();
            EnqueueEvent("application", "session_started", sessionDirectory);
            UnityEngine.Debug.Log("Flight session logging started: " + sessionDirectory);
        }

        private void OnApplicationPause(bool paused)
        {
            if (sessionStarted)
            {
                EnqueueEvent("application", paused ? "paused" : "resumed", string.Empty);
            }
        }

        private void Update()
        {
            var failure = Interlocked.Exchange(ref writerFailure, null);
            if (!string.IsNullOrEmpty(failure))
            {
                UnityEngine.Debug.LogError(failure);
            }
        }

        private void OnDestroy()
        {
            Unsubscribe();
            if (!sessionStarted)
            {
                return;
            }

            EnqueueEvent("application", "session_stopping", string.Empty);
            stopping = true;
            writerSignal?.Set();
            sessionStarted = false;
        }

        private void StartSession()
        {
            var startedUtc = DateTime.UtcNow;
            var sessionId = startedUtc.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
            sessionDirectory = Path.Combine(
                Application.persistentDataPath,
                "FlightSession",
                sessionId);
            sessionStartTicks = Stopwatch.GetTimestamp();
            metadataJson = JsonUtility.ToJson(new MetadataRecord
            {
                schemaVersion = 1,
                sessionId = sessionId,
                startedUtc = startedUtc.ToString("O", CultureInfo.InvariantCulture),
                applicationVersion = Application.version,
                packageName = Application.identifier,
                platform = Application.platform.ToString(),
                localOnly = true
            }, true);

            stopping = false;
            writerSignal = new AutoResetEvent(false);
            writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "QCC Flight Session Writer"
            };
            sessionStarted = true;
            writerThread.Start();
        }

        private void Subscribe()
        {
            if (telemetrySource != null)
            {
                telemetrySource.TelemetryUpdated -= OnTelemetryUpdated;
                telemetrySource.TelemetryUpdated += OnTelemetryUpdated;
            }
            if (inputSource != null)
            {
                inputSource.ManualIntentUpdated -= OnManualIntentUpdated;
                inputSource.ManualIntentUpdated += OnManualIntentUpdated;
                inputSource.OperatorIntentRaised -= OnOperatorIntent;
                inputSource.OperatorIntentRaised += OnOperatorIntent;
            }
            if (flightActionSource != null)
            {
                flightActionSource.FlightActionStateChanged -= OnFlightActionChanged;
                flightActionSource.FlightActionStateChanged += OnFlightActionChanged;
            }
            if (statusSource != null)
            {
                statusSource.StatusChanged -= OnControlStatusChanged;
                statusSource.StatusChanged += OnControlStatusChanged;
            }
        }

        private void Unsubscribe()
        {
            if (telemetrySource != null)
            {
                telemetrySource.TelemetryUpdated -= OnTelemetryUpdated;
            }
            if (inputSource != null)
            {
                inputSource.ManualIntentUpdated -= OnManualIntentUpdated;
                inputSource.OperatorIntentRaised -= OnOperatorIntent;
            }
            if (flightActionSource != null)
            {
                flightActionSource.FlightActionStateChanged -= OnFlightActionChanged;
            }
            if (statusSource != null)
            {
                statusSource.StatusChanged -= OnControlStatusChanged;
            }
        }

        private void BindReferences()
        {
            telemetrySource = telemetrySourceBehaviour as IVehicleTelemetrySource;
            inputSource = inputSourceBehaviour as IManualControlIntentSource;
            flightActionSource = flightActionSourceBehaviour as IFlightActionControl;
            statusSource = statusSourceBehaviour as IStatusSource;
        }

        private void OnTelemetryUpdated(VehicleTelemetry telemetry)
        {
            if (!sessionStarted || telemetry == null)
            {
                return;
            }

            var record = new TelemetryRecord
            {
                schemaVersion = 1,
                sessionTimeNs = SessionTimeNanoseconds(),
                sequence = NextSequence(),
                vehicleId = string.IsNullOrEmpty(telemetry.ProductName) ? "spark" : telemetry.ProductName,
                sourceSequence = telemetry.Sequence,
                sourceMonotonicMilliseconds = telemetry.MonotonicMilliseconds,
                connected = telemetry.IsConnected,
                hasBattery = telemetry.HasBatteryState,
                batteryPercent = telemetry.BatteryPercent,
                batteryVoltageMillivolts = telemetry.BatteryVoltageMillivolts,
                batteryTemperatureCelsius = telemetry.BatteryTemperatureCelsius,
                hasFlightState = telemetry.HasFlightState,
                positionValid = telemetry.PositionValid,
                latitudeDegrees = telemetry.LatitudeDegrees,
                longitudeDegrees = telemetry.LongitudeDegrees,
                altitudeMeters = telemetry.AltitudeMeters,
                headingDegrees = telemetry.HeadingDegrees,
                pitchDegrees = telemetry.PitchDegrees,
                rollDegrees = telemetry.RollDegrees,
                yawDegrees = telemetry.YawDegrees,
                velocityNorthMetersPerSecond = telemetry.VelocityNorthMetersPerSecond,
                velocityEastMetersPerSecond = telemetry.VelocityEastMetersPerSecond,
                velocityDownMetersPerSecond = telemetry.VelocityDownMetersPerSecond,
                satelliteCount = telemetry.SatelliteCount,
                gpsSignalLevel = telemetry.GpsSignalLevel,
                flightMode = telemetry.FlightMode,
                motorsOn = telemetry.MotorsOn,
                isFlying = telemetry.IsFlying,
                isGoingHome = telemetry.IsGoingHome,
                homePositionSet = telemetry.HomePositionSet,
                failsafeEnabled = telemetry.FailsafeEnabled,
                cameraRecording = telemetry.CameraRecording,
                cameraRecordingSeconds = telemetry.CameraRecordingSeconds,
                gimbalPitchDegrees = telemetry.GimbalPitchDegrees
            };
            Enqueue("telemetry", JsonUtility.ToJson(record));
        }

        private void OnManualIntentUpdated(ManualControlIntent intent)
        {
            if (intent.ManualFlightAuthorized == lastManualAuthorized)
            {
                return;
            }
            lastManualAuthorized = intent.ManualFlightAuthorized;
            EnqueueEvent(
                "control",
                intent.ManualFlightAuthorized ? "deadman_engaged" : "deadman_released",
                intent.ManualFlightAuthorized ? "Manual movement authorized." : "Command neutralized.");
        }

        private void OnOperatorIntent(OperatorIntentType intent)
        {
            EnqueueEvent("operator_intent", intent.ToString(), string.Empty);
        }

        private void OnFlightActionChanged(FlightActionResult result)
        {
            if (result == null)
            {
                return;
            }
            EnqueueEvent(
                "flight_action",
                result.Action + "_" + result.Status,
                result.Message);
        }

        private void OnControlStatusChanged(string status)
        {
            EnqueueEvent("control_state", "status_changed", status ?? string.Empty);
        }

        private void EnqueueEvent(string category, string name, string message)
        {
            if (!sessionStarted)
            {
                return;
            }
            var record = new EventRecord
            {
                schemaVersion = 1,
                sessionTimeNs = SessionTimeNanoseconds(),
                sequence = NextSequence(),
                vehicleId = "spark",
                category = category,
                name = name,
                message = message
            };
            Enqueue("events", JsonUtility.ToJson(record));
        }

        private void Enqueue(string stream, string json)
        {
            pendingLines.Enqueue(new PendingLine(stream, json));
            writerSignal?.Set();
        }

        private void WriterLoop()
        {
            try
            {
                Directory.CreateDirectory(sessionDirectory);
                Directory.CreateDirectory(Path.Combine(sessionDirectory, "video"));
                File.WriteAllText(Path.Combine(sessionDirectory, "metadata.json"), metadataJson);
                File.WriteAllText(
                    Path.Combine(sessionDirectory, "mission.json"),
                    "{\"schemaVersion\":1,\"mission\":null}\n");

                using (var telemetryWriter = new StreamWriter(
                           Path.Combine(sessionDirectory, "telemetry.jsonl"), true))
                using (var eventWriter = new StreamWriter(
                           Path.Combine(sessionDirectory, "events.jsonl"), true))
                {
                    while (!stopping || !pendingLines.IsEmpty)
                    {
                        var wrote = false;
                        while (pendingLines.TryDequeue(out var line))
                        {
                            (line.Stream == "telemetry" ? telemetryWriter : eventWriter)
                                .WriteLine(line.Json);
                            wrote = true;
                        }
                        if (wrote)
                        {
                            telemetryWriter.Flush();
                            eventWriter.Flush();
                        }
                        if (!stopping)
                        {
                            writerSignal.WaitOne(250);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Interlocked.Exchange(
                    ref writerFailure,
                    "Flight session writer stopped after an I/O error: " + exception.Message);
            }
            finally
            {
                writerSignal?.Dispose();
            }
        }

        private long NextSequence()
        {
            return Interlocked.Increment(ref sequence);
        }

        private long SessionTimeNanoseconds()
        {
            var elapsedTicks = Stopwatch.GetTimestamp() - sessionStartTicks;
            return (long)(elapsedTicks * (1000000000.0 / Stopwatch.Frequency));
        }

        private readonly struct PendingLine
        {
            public PendingLine(string stream, string json)
            {
                Stream = stream;
                Json = json;
            }

            public string Stream { get; }
            public string Json { get; }
        }

        [Serializable]
        private sealed class MetadataRecord
        {
            public int schemaVersion;
            public string sessionId;
            public string startedUtc;
            public string applicationVersion;
            public string packageName;
            public string platform;
            public bool localOnly;
        }

        [Serializable]
        private sealed class EventRecord
        {
            public int schemaVersion;
            public long sessionTimeNs;
            public long sequence;
            public string vehicleId;
            public string category;
            public string name;
            public string message;
        }

        [Serializable]
        private sealed class TelemetryRecord
        {
            public int schemaVersion;
            public long sessionTimeNs;
            public long sequence;
            public string vehicleId;
            public long sourceSequence;
            public long sourceMonotonicMilliseconds;
            public bool connected;
            public bool hasBattery;
            public int batteryPercent;
            public int batteryVoltageMillivolts;
            public float batteryTemperatureCelsius;
            public bool hasFlightState;
            public bool positionValid;
            public double latitudeDegrees;
            public double longitudeDegrees;
            public float altitudeMeters;
            public int headingDegrees;
            public float pitchDegrees;
            public float rollDegrees;
            public float yawDegrees;
            public float velocityNorthMetersPerSecond;
            public float velocityEastMetersPerSecond;
            public float velocityDownMetersPerSecond;
            public int satelliteCount;
            public int gpsSignalLevel;
            public string flightMode;
            public bool motorsOn;
            public bool isFlying;
            public bool isGoingHome;
            public bool homePositionSet;
            public bool failsafeEnabled;
            public bool cameraRecording;
            public int cameraRecordingSeconds;
            public float gimbalPitchDegrees;
        }
    }
}
