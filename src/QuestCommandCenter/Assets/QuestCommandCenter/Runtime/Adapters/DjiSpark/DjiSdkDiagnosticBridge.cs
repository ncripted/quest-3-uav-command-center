using System;
using System.Collections;
using System.Globalization;
using QuestCommandCenter.VehicleCore;
using QuestCommandCenter.Video;
using UnityEngine;

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace QuestCommandCenter.Adapters.DjiSpark
{
    /// <summary>
    /// DJI MSDK compatibility adapter. It normalizes read-only product state and
    /// exposes guarded onboard camera commands, bounded stationary head-gimbal pitch,
    /// and deliberately confirmed high-level flight actions.
    /// </summary>
    public sealed class DjiSdkDiagnosticBridge : MonoBehaviour, IStatusSource, IVehicleTelemetrySource, IVideoTextureSource, ICameraControl, IGimbalControl, IFlightActionControl, IManualFlightControl
    {
        private const string JavaBridgeClass = "com.dronecommand.quest.dji.DjiSdkBridge";
        private const int PreviewWidth = 640;
        private const int PreviewHeight = 360;
        private const double VideoPollIntervalSeconds = 0.1;
        private const double GimbalTelemetryMaximumAgeSeconds = 0.5;
        private const double VirtualStickRequestTimeoutSeconds = 10.0;
        private const double VirtualStickStatusProbeRetrySeconds = 2.0;
#if UNITY_ANDROID && !UNITY_EDITOR
        private const string GroundedNeutralTestIntentExtra =
            "com.dronecommand.quest.DJI_GROUNDED_NEUTRAL_TEST";
#endif

        private string sdkVersion = "4.18";
        private string sdkState = "STARTING";
        private string productName = "";
        private string diagnosticMessage = "Starting DJI SDK.";
        private bool productConnected;
        private double telemetrySegmentStartedAt;
        private double lastTelemetryReceivedAt;
        private double nextTelemetryHealthLogAt;
        private double maximumTelemetryGapSeconds;
        private long telemetryReceiveCount;
        private bool telemetryStale;
        private Texture2D videoTexture;
        private double nextVideoPollAt;
        private double videoSegmentStartedAt;
        private double lastVideoFrameAt;
        private double nextVideoHealthLogAt;
        private double maximumVideoGapSeconds;
        private long videoFrameCount;
        private readonly CameraCommandSession cameraCommandSession = new CameraCommandSession();
        private CameraCommandResult lastCameraCommand;
        private readonly GimbalCommandSession gimbalCommandSession = new GimbalCommandSession();
        private GimbalCommandResult lastGimbalCommand;
        private readonly FlightActionCommandSession flightActionSession = new FlightActionCommandSession();
        private readonly DjiVirtualStickCommandMapper virtualStickCommandMapper =
            new DjiVirtualStickCommandMapper(new DjiVirtualStickDevelopmentProfile());
        private FlightActionResult lastFlightAction;
        private long virtualStickRequestSequence;
        private bool virtualStickStatusRequestPending;
        private bool virtualStickStatusKnown;
        private bool virtualStickAvailable;
        private bool virtualStickEnabled;
        private bool virtualStickHasError;
        private string virtualStickMessage = "Read-only compatibility probe pending.";
        private string virtualStickPendingOperation = string.Empty;
        private long virtualStickActiveRequestId;
        private long virtualStickLastCompletedRequestId;
        private double virtualStickRequestStartedAt;
        private double virtualStickRequestDeadline;
        private double nextVirtualStickStatusProbeAt;
#if UNITY_ANDROID && !UNITY_EDITOR
        private bool groundedNeutralTestRequested;
        private bool groundedNeutralTestStarted;
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaClass androidBridge;
#endif

        public string CurrentStatus { get; private set; } =
            "QUEST COMMAND CENTER — DJI HIGH-LEVEL FLIGHT ACTIONS\n"
            + "DJI MSDK 4.18 | SDK: STARTING\n"
            + "Product: DISCONNECTED\n\n"
            + "MANUAL VIRTUAL STICK: DISABLED";

        public VehicleTelemetry LatestTelemetry { get; private set; }
        public Texture CurrentVideoTexture => videoTexture;
        public bool HasVideoFrame => videoTexture != null && videoFrameCount > 0;
        public bool IsCameraAvailable => productConnected && LatestTelemetry?.HasCameraState == true;
        public bool IsRecording => LatestTelemetry?.CameraRecording == true;
        public bool IsCameraCommandPending => cameraCommandSession.IsPending;
        public CameraCommandResult LastCameraCommand => lastCameraCommand ?? cameraCommandSession.Current;
        public bool IsGimbalAvailable => productConnected
            && LatestTelemetry?.HasGimbalState == true
            && telemetryReceiveCount > 0
            && !telemetryStale
            && Time.realtimeSinceStartupAsDouble - lastTelemetryReceivedAt <= GimbalTelemetryMaximumAgeSeconds;
        public bool IsGimbalCommandPending => gimbalCommandSession.IsPending;
        public GimbalCommandResult LastGimbalCommand => lastGimbalCommand ?? gimbalCommandSession.Current;
        public bool IsFlightActionPending => flightActionSession.IsPending;
        public FlightActionResult LastFlightAction => lastFlightAction ?? flightActionSession.Current;
        // DJI documents isVirtualStickControlModeAvailable() as false until the
        // mode has already been enabled. A successful status getter therefore
        // proves that the API is reachable; actual command readiness requires
        // both the enabled state and DJI's post-enable availability result.
        public bool IsManualControlInterfaceReady => virtualStickStatusKnown;
        public bool IsManualControlEnabled => virtualStickEnabled && virtualStickAvailable;
        public string ManualControlStatusMessage => virtualStickMessage;
        public bool HasManualControlError => virtualStickHasError;

        public event Action<string> StatusChanged;
        public event Action<Texture> VideoFrameUpdated;
        public event Action<CameraCommandResult> CameraCommandStateChanged;
        public event Action<GimbalCommandResult> GimbalCommandStateChanged;
        public event Action<FlightActionResult> FlightActionStateChanged;
        public event Action<VehicleTelemetry> TelemetryUpdated;
        public event Action<bool> ManualControlModeChanged;

        private void Start()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            groundedNeutralTestRequested = ConsumeGroundedNeutralTestIntentExtra();
            if (groundedNeutralTestRequested)
            {
                Debug.LogWarning(
                    "DJI GROUNDED NEUTRAL TEST armed by one-shot launch intent; "
                    + "waiting for fresh motors-off telemetry.");
            }
            StartCoroutine(RegisterAfterPermissions());
#else
            SetSimpleStatus("ANDROID BUILD REQUIRED", "DJI telemetry requires an Android player build.");
#endif
        }

        private void Update()
        {
            if (flightActionSession.TryExpire(Time.realtimeSinceStartupAsDouble, out var expiredAction))
            {
                PublishFlightAction(expiredAction);
                Debug.LogWarning(expiredAction.Message);
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            PollVideoFrame();
#endif

            var now = Time.realtimeSinceStartupAsDouble;
            UpdateVirtualStickRequestHealth(now);

            if (!productConnected || telemetryReceiveCount == 0)
            {
                return;
            }

            var nowMilliseconds = SecondsToMilliseconds(now);
            var receivedAtMilliseconds = SecondsToMilliseconds(lastTelemetryReceivedAt);
            if (!FlightTelemetryFreshnessPolicy.IsFresh(
                    nowMilliseconds,
                    receivedAtMilliseconds)
                && !telemetryStale)
            {
                telemetryStale = true;
                var age = FlightTelemetryFreshnessPolicy.TryGetAgeMilliseconds(
                    nowMilliseconds,
                    receivedAtMilliseconds,
                    out var ageMilliseconds)
                    ? ageMilliseconds.ToString(CultureInfo.InvariantCulture)
                    : "invalid";
                Debug.LogWarning(
                    "DJI telemetry stream stale; ageMs="
                    + age);
                sdkState = "TELEMETRY STALE";
                diagnosticMessage =
                    "No telemetry received within the 1500 ms flight-safety window.";
                RenderStatus();
            }

            if (now >= nextTelemetryHealthLogAt)
            {
                LogTelemetryHealth(now, "periodic");
                nextTelemetryHealthLogAt = now + 30.0;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!virtualStickStatusKnown
                && !virtualStickStatusRequestPending
                && IsFlightTelemetryFresh()
                && now >= nextVirtualStickStatusProbeAt)
            {
                RequestVirtualStickStatusProbe();
            }
#endif
        }

        private void OnDestroy()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            DisableVirtualStickBestEffort("application destroyed");
            androidBridge?.Dispose();
            androidBridge = null;
#endif
            if (videoTexture != null)
            {
                Destroy(videoTexture);
                videoTexture = null;
            }
        }

        private void OnApplicationPause(bool paused)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (paused)
            {
                DisableVirtualStickBestEffort("application paused");
            }
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private IEnumerator RegisterAfterPermissions()
        {
            var requiredPermissions = new[] { Permission.FineLocation };

            foreach (var permission in requiredPermissions)
            {
                if (Permission.HasUserAuthorizedPermission(permission))
                {
                    continue;
                }

                SetSimpleStatus("WAITING FOR PERMISSION", "Required: " + permission);

                var completed = false;
                var granted = false;
                var callbacks = new PermissionCallbacks();
                callbacks.PermissionGranted += _ => { granted = true; completed = true; };
                callbacks.PermissionDenied += _ => { completed = true; };
                callbacks.PermissionDeniedAndDontAskAgain += _ => { completed = true; };
                Permission.RequestUserPermission(permission, callbacks);

                var deadline = Time.realtimeSinceStartup + 60f;
                while (!completed && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                if (!granted && !Permission.HasUserAuthorizedPermission(permission))
                {
                    SetSimpleStatus("PERMISSION DENIED", "Missing: " + permission);
                    yield break;
                }
            }

            if (!Permission.HasUserAuthorizedPermission("android.permission.READ_PHONE_STATE"))
            {
                Debug.LogWarning(
                    "Quest did not grant DJI's legacy READ_PHONE_STATE permission; "
                    + "continuing the compatibility probe without it.");
            }

            SetSimpleStatus("REGISTERING", "Registering DJI MSDK.");

            try
            {
                if (androidBridge == null)
                {
                    androidBridge = new AndroidJavaClass(JavaBridgeClass);
                }
                androidBridge.CallStatic("register", gameObject.name);
            }
            catch (Exception exception)
            {
                Debug.LogError("DJI bridge startup failed: " + exception);
                SetSimpleStatus("BRIDGE STARTUP FAILED", exception.GetType().Name);
            }
        }
#endif

        // Called by the Android bridge through UnityPlayer.UnitySendMessage.
        public void OnDjiSdkEvent(string json)
        {
            var sdkEvent = JsonUtility.FromJson<DjiSdkEvent>(json);
            if (sdkEvent == null)
            {
                Debug.LogError("DJI bridge returned an invalid diagnostic event.");
                return;
            }

            if (!string.IsNullOrEmpty(sdkEvent.sdkVersion))
            {
                sdkVersion = sdkEvent.sdkVersion;
            }

            if (!string.IsNullOrEmpty(sdkEvent.productModel))
            {
                productName = sdkEvent.productModel;
            }

            sdkState = string.IsNullOrEmpty(sdkEvent.type)
                ? "UNKNOWN"
                : sdkEvent.type.ToUpperInvariant();
            diagnosticMessage = string.IsNullOrEmpty(sdkEvent.message) ? sdkEvent.type : sdkEvent.message;

            switch (sdkEvent.type)
            {
                case "product_connected":
                case "product_changed":
                case "component_changed":
                case "telemetry_bound":
                    productConnected = true;
                    break;
                case "product_disconnected":
                    LogTelemetryHealth(Time.realtimeSinceStartupAsDouble, "disconnected");
                    productConnected = false;
                    productName = "";
                    LatestTelemetry = null;
                    TelemetryUpdated?.Invoke(null);
                    ResetTelemetryHealth();
                    ResetVideoPreview();
                    FailPendingCameraCommand("Camera disconnected while the command was pending.");
                    FailPendingGimbalCommand("Gimbal disconnected while the command was pending.");
                    FailOrCancelFlightAction("Aircraft disconnected while the flight action was pending.");
                    ResetVirtualStickStatus("Aircraft disconnected.");
                    break;
                case "virtual_stick_safety_disabled":
                    {
                        var wasManualControlEnabled = virtualStickEnabled;
                        virtualStickLastCompletedRequestId = Math.Max(
                            virtualStickLastCompletedRequestId,
                            virtualStickActiveRequestId);
                        virtualStickStatusRequestPending = false;
                        virtualStickActiveRequestId = 0;
                        virtualStickPendingOperation = string.Empty;
                        virtualStickEnabled = false;
                        virtualStickAvailable = false;
                        // A safety disable result is not a read-only API probe.
                        // Preserve previously established API reachability so the
                        // operator can correct the cause and deliberately retry.
                        virtualStickStatusKnown = virtualStickStatusKnown || sdkEvent.ok;
                        virtualStickHasError = true;
                        virtualStickMessage = diagnosticMessage;
                        if (wasManualControlEnabled)
                        {
                            ManualControlModeChanged?.Invoke(false);
                        }
                        break;
                    }
            }

            RenderStatus();

#if UNITY_ANDROID && !UNITY_EDITOR
            if (sdkEvent.type == "telemetry_bound")
            {
                RequestVirtualStickStatusProbe();
            }
#endif

            if (sdkEvent.ok)
            {
                Debug.Log("DJI diagnostic event: " + sdkEvent.type);
            }
            else
            {
                Debug.LogWarning("DJI diagnostic event: " + sdkEvent.type + " - " + diagnosticMessage);
            }
        }

        public bool RequestStartRecording()
        {
            return RequestCameraCommand(CameraCommandType.StartRecording);
        }

        public bool RequestStopRecording()
        {
            return RequestCameraCommand(CameraCommandType.StopRecording);
        }

        public bool RequestTakePhoto()
        {
            return RequestCameraCommand(CameraCommandType.TakePhoto);
        }

        public bool RequestFlightActionConfirmation(FlightActionType action)
        {
            if (!flightActionSession.TryRequestConfirmation(
                    action,
                    LatestTelemetry,
                    IsFlightTelemetryFresh(),
                    Time.realtimeSinceStartupAsDouble,
                    out var result))
            {
                PublishFlightAction(result);
                Debug.LogWarning("Flight action rejected: " + result.Message);
                return false;
            }

            PublishFlightAction(result);
            Debug.LogWarning("Flight action awaiting deliberate confirmation: " + action);
            return true;
        }

        public bool ConfirmFlightAction(FlightActionType action)
        {
            if (!flightActionSession.TryConfirm(
                    action,
                    LatestTelemetry,
                    IsFlightTelemetryFresh(),
                    Time.realtimeSinceStartupAsDouble,
                    out var pending))
            {
                PublishFlightAction(pending);
                Debug.LogWarning("Flight action confirmation rejected: " + pending.Message);
                return false;
            }

            PublishFlightAction(pending);
            Debug.LogWarning(
                "Dispatching operator-confirmed aircraft action: " + action
                + ", requestId=" + pending.RequestId);

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                var accepted = androidBridge != null
                    && androidBridge.CallStatic<bool>(
                        "requestFlightAction",
                        FlightActionWireName(action),
                        pending.RequestId);
                if (accepted)
                {
                    return true;
                }

                CompleteFlightAction(
                    pending.RequestId,
                    false,
                    "Android flight backend rejected the request because aircraft state changed.");
                return false;
            }
            catch (Exception exception)
            {
                Debug.LogError("DJI flight action dispatch failed: " + exception);
                CompleteFlightAction(
                    pending.RequestId,
                    false,
                    "Flight action dispatch failed: " + exception.GetType().Name);
                return false;
            }
#else
            CompleteFlightAction(
                pending.RequestId,
                false,
                "DJI flight actions require an Android device build.");
            return false;
#endif
        }

        public void CancelFlightActionConfirmation()
        {
            if (flightActionSession.CancelConfirmation(out var result))
            {
                PublishFlightAction(result);
                Debug.LogWarning(result.Message);
            }
        }

        /// <summary>
        /// Performs only DJI status getters. It cannot enable Virtual Stick or
        /// send movement data.
        /// </summary>
        public bool RequestVirtualStickStatusProbe()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (androidBridge == null || !productConnected || virtualStickStatusRequestPending)
            {
                return false;
            }

            var requestId = ++virtualStickRequestSequence;
            BeginVirtualStickRequest(requestId, "query");
            nextVirtualStickStatusProbeAt = Time.realtimeSinceStartupAsDouble
                + VirtualStickStatusProbeRetrySeconds;
            try
            {
                var accepted = androidBridge.CallStatic<bool>("queryVirtualStickStatus", requestId);
                if (accepted)
                {
                    return true;
                }
                FailVirtualStickRequestLocally(
                    requestId,
                    "query",
                    "Flight controller unavailable for read-only Virtual Stick probe.");
                return false;
            }
            catch (Exception exception)
            {
                FailVirtualStickRequestLocally(
                    requestId,
                    "query",
                    "Virtual Stick probe failed: " + exception.GetType().Name);
                Debug.LogError("DJI Virtual Stick status probe failed: " + exception);
                return false;
            }
#else
            return false;
#endif
        }

        /// <summary>
        /// Guarded Virtual Stick lifecycle entry point. Enabling requires fresh,
        /// failsafe-clear flight telemetry; disabling remains available during a
        /// telemetry fault so recovery never depends on a healthy data stream.
        /// </summary>
        public bool RequestVirtualStickModeForHardwareTest(bool enabled)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (androidBridge == null || virtualStickStatusRequestPending)
            {
                return false;
            }
            if (enabled
                && (!IsFlightTelemetryFresh() || LatestTelemetry.FailsafeEnabled))
            {
                virtualStickHasError = true;
                virtualStickMessage =
                    "Virtual Stick enable rejected: flight telemetry is older than 1500 ms, "
                    + "missing, clock-invalid, or DJI failsafe is active.";
                Debug.LogError(virtualStickMessage);
                RenderStatus();
                return false;
            }

            var requestId = ++virtualStickRequestSequence;
            var operation = enabled ? "enable" : "disable";
            BeginVirtualStickRequest(requestId, operation);
            try
            {
                var accepted = androidBridge.CallStatic<bool>(
                    "requestVirtualStickMode",
                    enabled,
                    requestId);
                if (accepted)
                {
                    return true;
                }
                FailVirtualStickRequestLocally(
                    requestId,
                    operation,
                    "DJI flight controller rejected the Virtual Stick " + operation
                        + " request before dispatch.");
                return false;
            }
            catch (Exception exception)
            {
                FailVirtualStickRequestLocally(
                    requestId,
                    operation,
                    "Virtual Stick " + operation + " failed: " + exception.GetType().Name);
                Debug.LogError("DJI Virtual Stick mode request failed: " + exception);
                return false;
            }
#else
            return false;
#endif
        }

        public bool RequestManualControlMode(bool enabled)
        {
            return RequestVirtualStickModeForHardwareTest(enabled);
        }

        /// <summary>
        /// Low-level adapter data entry point. The caller must provide an already
        /// arbitrated and safety-approved command.
        /// </summary>
        public bool SubmitVirtualStickCommandForHardwareTest(
            long sequence,
            DjiVirtualStickCommand command,
            bool authorized)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (androidBridge == null || !virtualStickEnabled)
            {
                return false;
            }
            try
            {
                return androidBridge.CallStatic<bool>(
                    "submitVirtualStickCommand",
                    sequence,
                    command.RollMetersPerSecond,
                    command.PitchMetersPerSecond,
                    command.YawRateDegreesPerSecond,
                    command.VerticalMetersPerSecond,
                    authorized);
            }
            catch (Exception exception)
            {
                Debug.LogError("DJI Virtual Stick command submission failed: " + exception);
                return false;
            }
#else
            return false;
#endif
        }

        public bool SubmitManualFlightCommand(
            long sequence,
            NormalizedFlightCommand command,
            bool authorized)
        {
            return SubmitVirtualStickCommandForHardwareTest(
                sequence,
                virtualStickCommandMapper.Map(command),
                authorized);
        }

        private void BeginVirtualStickRequest(long requestId, string operation)
        {
            virtualStickStatusRequestPending = true;
            virtualStickActiveRequestId = requestId;
            virtualStickPendingOperation = operation ?? string.Empty;
            virtualStickRequestStartedAt = Time.realtimeSinceStartupAsDouble;
            virtualStickRequestDeadline = virtualStickRequestStartedAt
                + VirtualStickRequestTimeoutSeconds;
        }

        private void FailVirtualStickRequestLocally(
            long requestId,
            string operation,
            string message)
        {
            if (virtualStickActiveRequestId != 0
                && requestId != virtualStickActiveRequestId)
            {
                return;
            }

            virtualStickStatusRequestPending = false;
            virtualStickLastCompletedRequestId = Math.Max(
                virtualStickLastCompletedRequestId,
                requestId);
            virtualStickActiveRequestId = 0;
            virtualStickPendingOperation = string.Empty;
            virtualStickRequestDeadline = 0.0;
            virtualStickHasError = true;
            virtualStickMessage = message ?? "Unknown Virtual Stick request error.";

            if (string.Equals(operation, "query", StringComparison.Ordinal))
            {
                virtualStickStatusKnown = false;
            }
            else
            {
                virtualStickAvailable = false;
                virtualStickEnabled = false;
                ManualControlModeChanged?.Invoke(false);
            }

            Debug.LogError(
                "DJI Virtual Stick " + operation + " request " + requestId
                + " failed locally: " + virtualStickMessage);
            RenderStatus();
        }

        private void UpdateVirtualStickRequestHealth(double now)
        {
            if (!virtualStickStatusRequestPending
                || virtualStickActiveRequestId <= 0
                || now <= virtualStickRequestDeadline)
            {
                return;
            }

            var requestId = virtualStickActiveRequestId;
            var operation = virtualStickPendingOperation;
            var elapsedMilliseconds = Math.Max(
                0.0,
                (now - virtualStickRequestStartedAt) * 1000.0);
            FailVirtualStickRequestLocally(
                requestId,
                operation,
                "Virtual Stick " + operation + " callback timed out after "
                    + elapsedMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms.");
        }

        // Called by the Android bridge through UnityPlayer.UnitySendMessage.
        public void OnDjiVirtualStickStatus(string json)
        {
            var dto = JsonUtility.FromJson<DjiVirtualStickStatusDto>(json);
            if (dto == null || dto.requestId <= 0)
            {
                Debug.LogError("DJI bridge returned an invalid Virtual Stick status.");
                return;
            }

            if ((virtualStickActiveRequestId > 0
                    && dto.requestId != virtualStickActiveRequestId)
                || (virtualStickActiveRequestId == 0
                    && dto.requestId <= virtualStickLastCompletedRequestId))
            {
                Debug.LogError(
                    "Ignoring stale DJI Virtual Stick callback for request "
                    + dto.requestId + ".");
#if UNITY_ANDROID && !UNITY_EDITOR
                if (dto.enabled)
                {
                    // A timed-out enable may still complete inside DJI after
                    // managed authority has been withdrawn. Always force it
                    // back off; never adopt late authority.
                    DisableVirtualStickBestEffort(
                        "a stale callback reported Virtual Stick enabled",
                        true);
                }
#endif
                return;
            }

            var operation = string.IsNullOrEmpty(dto.operation)
                ? virtualStickPendingOperation
                : dto.operation;
            var callbackLatencyMilliseconds = virtualStickRequestStartedAt <= 0.0
                ? 0.0
                : Math.Max(
                    0.0,
                    (Time.realtimeSinceStartupAsDouble - virtualStickRequestStartedAt) * 1000.0);
            virtualStickStatusRequestPending = false;
            virtualStickLastCompletedRequestId = Math.Max(
                virtualStickLastCompletedRequestId,
                dto.requestId);
            virtualStickActiveRequestId = 0;
            virtualStickPendingOperation = string.Empty;
            virtualStickRequestDeadline = 0.0;
            if (string.Equals(operation, "query", StringComparison.Ordinal))
            {
                virtualStickStatusKnown = dto.succeeded;
            }
            else if (string.Equals(operation, "enable", StringComparison.Ordinal)
                || string.Equals(operation, "disable", StringComparison.Ordinal))
            {
                // A mode-change callback proves the API is reachable even when
                // DJI rejects the requested state. Do not turn an enable failure
                // into a false read-only status-query failure.
                virtualStickStatusKnown = true;
            }
            else
            {
                virtualStickStatusKnown = dto.succeeded;
            }
            virtualStickAvailable = dto.available;
            virtualStickEnabled = dto.enabled;
            virtualStickHasError = !dto.succeeded;
            virtualStickMessage = dto.message ?? string.Empty;
            ManualControlModeChanged?.Invoke(IsManualControlEnabled);
            var statusLog =
                "DJI Virtual Stick result: requestId=" + dto.requestId
                + ", operation=" + operation
                + ", latencyMs="
                + callbackLatencyMilliseconds.ToString("0", CultureInfo.InvariantCulture)
                + ", available=" + dto.available
                + ", enabled=" + dto.enabled
                + ", succeeded=" + dto.succeeded
                + ", telemetryAgeMs="
                + (telemetryReceiveCount <= 0
                    ? "unknown"
                    : Math.Max(
                        0.0,
                        (Time.realtimeSinceStartupAsDouble - lastTelemetryReceivedAt) * 1000.0)
                        .ToString("0", CultureInfo.InvariantCulture))
                + ", message=" + virtualStickMessage;
            if (dto.succeeded)
            {
                Debug.Log(statusLog);
            }
            else
            {
                Debug.LogError(statusLog);
            }
            RenderStatus();

#if UNITY_ANDROID && !UNITY_EDITOR
            if (groundedNeutralTestRequested && !groundedNeutralTestStarted)
            {
                StartCoroutine(RunGroundedNeutralVirtualStickTest());
            }
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private bool ConsumeGroundedNeutralTestIntentExtra()
        {
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var intent = activity?.Call<AndroidJavaObject>("getIntent"))
                {
                    if (intent == null
                        || !intent.Call<bool>("getBooleanExtra", GroundedNeutralTestIntentExtra, false))
                    {
                        return false;
                    }

                    // Replace the Activity intent after consuming the flag. Calling
                    // Intent.removeExtra through AndroidJavaObject cannot express
                    // its concrete Intent return type on IL2CPP reliably.
                    using (var clearedIntent = new AndroidJavaObject("android.content.Intent"))
                    {
                        activity.Call("setIntent", clearedIntent);
                        return true;
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogError("Could not consume grounded test launch intent: " + exception);
                return false;
            }
        }

        private IEnumerator RunGroundedNeutralVirtualStickTest()
        {
            groundedNeutralTestStarted = true;
            groundedNeutralTestRequested = false;
            var telemetryDeadline = Time.realtimeSinceStartupAsDouble + 5.0;
            while ((LatestTelemetry == null
                    || !LatestTelemetry.HasFlightState
                    || !IsFlightTelemetryFresh())
                && Time.realtimeSinceStartupAsDouble < telemetryDeadline)
            {
                yield return null;
            }

            var telemetry = LatestTelemetry;
            if (telemetry == null || !telemetry.HasFlightState || !IsFlightTelemetryFresh())
            {
                Debug.LogError(
                    "DJI GROUNDED NEUTRAL TEST rejected: fresh flight-state telemetry "
                    + "did not arrive within five seconds.");
                yield break;
            }

            if (telemetry.FailsafeEnabled || telemetry.MotorsOn || telemetry.IsFlying)
            {
                Debug.LogError(
                    "DJI GROUNDED NEUTRAL TEST rejected by aircraft state: requires "
                    + "failsafe clear, motors off, and aircraft not flying.");
                yield break;
            }

            Debug.LogWarning(
                "DJI GROUNDED NEUTRAL TEST preconditions passed; enabling Virtual Stick "
                + "with neutral-only output and the 500 ms native command timeout.");
            if (!RequestVirtualStickModeForHardwareTest(true))
            {
                Debug.LogError("DJI GROUNDED NEUTRAL TEST enable request was rejected locally.");
                yield break;
            }

            var enableDeadline = Time.realtimeSinceStartupAsDouble
                + VirtualStickRequestTimeoutSeconds + 1.0;
            while (virtualStickStatusRequestPending
                && Time.realtimeSinceStartupAsDouble < enableDeadline)
            {
                yield return null;
            }

            if (virtualStickStatusRequestPending || !virtualStickEnabled)
            {
                Debug.LogError(
                    "DJI GROUNDED NEUTRAL TEST did not reach enabled state: "
                    + virtualStickMessage);
                DisableVirtualStickBestEffort("grounded test enable did not complete");
                yield break;
            }

            Debug.LogWarning(
                "DJI GROUNDED NEUTRAL TEST enabled; no authorized movement command submitted. "
                + "Waiting for automatic safety disable.");

            var disableDeadline = Time.realtimeSinceStartupAsDouble + 3.0;
            while (virtualStickEnabled && Time.realtimeSinceStartupAsDouble < disableDeadline)
            {
                yield return null;
            }

            if (virtualStickEnabled)
            {
                Debug.LogError(
                    "DJI GROUNDED NEUTRAL TEST timeout was not observed; requesting explicit disable.");
                DisableVirtualStickBestEffort("grounded test verification deadline");
                yield break;
            }

            Debug.LogWarning(
                "DJI GROUNDED NEUTRAL TEST passed: native timeout disabled Virtual Stick.");
            RequestVirtualStickStatusProbe();
        }

        private void DisableVirtualStickBestEffort(
            string reason,
            bool nativeReportedEnabled = false)
        {
            if ((!virtualStickEnabled && !nativeReportedEnabled) || androidBridge == null)
            {
                virtualStickEnabled = false;
                virtualStickAvailable = false;
                return;
            }
            try
            {
                var requestId = ++virtualStickRequestSequence;
                BeginVirtualStickRequest(requestId, "disable");
                var accepted = androidBridge.CallStatic<bool>(
                    "requestVirtualStickMode",
                    false,
                    requestId);
                virtualStickEnabled = false;
                virtualStickAvailable = false;
                if (!accepted)
                {
                    FailVirtualStickRequestLocally(
                        requestId,
                        "disable",
                        "DJI flight controller rejected the best-effort Virtual Stick disable request.");
                    return;
                }
                Debug.LogWarning("Requested Virtual Stick disable because " + reason + ".");
            }
            catch (Exception exception)
            {
                var requestId = virtualStickActiveRequestId;
                if (requestId > 0)
                {
                    FailVirtualStickRequestLocally(
                        requestId,
                        "disable",
                        "Best-effort Virtual Stick disable failed: " + exception.GetType().Name);
                }
                virtualStickEnabled = false;
                virtualStickAvailable = false;
                Debug.LogError("Could not request Virtual Stick disable: " + exception);
            }
        }
#endif

        private void ResetVirtualStickStatus(string message)
        {
            var wasEnabled = virtualStickEnabled;
            virtualStickLastCompletedRequestId = Math.Max(
                virtualStickLastCompletedRequestId,
                virtualStickActiveRequestId);
            virtualStickStatusRequestPending = false;
            virtualStickActiveRequestId = 0;
            virtualStickPendingOperation = string.Empty;
            virtualStickRequestDeadline = 0.0;
            virtualStickStatusKnown = false;
            virtualStickAvailable = false;
            virtualStickEnabled = false;
            virtualStickHasError = false;
            virtualStickMessage = message;
            if (wasEnabled)
            {
                ManualControlModeChanged?.Invoke(false);
            }
        }

        // Called by the Android bridge through UnityPlayer.UnitySendMessage.
        public void OnDjiFlightActionResult(string json)
        {
            var dto = JsonUtility.FromJson<DjiFlightActionDto>(json);
            if (dto == null || dto.requestId <= 0)
            {
                Debug.LogError("DJI bridge returned an invalid flight-action result.");
                return;
            }

            CompleteFlightAction(dto.requestId, dto.succeeded, dto.message);
        }

        private void CompleteFlightAction(long requestId, bool succeeded, string message)
        {
            if (!flightActionSession.TryComplete(requestId, succeeded, message, out var result))
            {
                Debug.LogWarning("Ignoring stale DJI flight-action result; requestId=" + requestId);
                return;
            }

            PublishFlightAction(result);
            if (succeeded)
            {
                Debug.LogWarning("DJI accepted flight action: " + result.Action + " - " + result.Message);
            }
            else
            {
                Debug.LogError("DJI flight action failed: " + result.Action + " - " + result.Message);
            }
        }

        private void FailOrCancelFlightAction(string message)
        {
            if (flightActionSession.FailPending(message, out var pendingResult))
            {
                PublishFlightAction(pendingResult);
                Debug.LogError(message);
                return;
            }

            if (flightActionSession.CancelConfirmation(out var cancelledResult))
            {
                PublishFlightAction(cancelledResult);
                Debug.LogWarning(cancelledResult.Message);
            }
        }

        private void PublishFlightAction(FlightActionResult result)
        {
            lastFlightAction = result;
            FlightActionStateChanged?.Invoke(result);
            RenderStatus();
        }

        private bool IsFlightTelemetryFresh()
        {
            return productConnected
                && LatestTelemetry?.HasFlightState == true
                && telemetryReceiveCount > 0
                && !telemetryStale
                && FlightTelemetryFreshnessPolicy.IsFresh(
                    SecondsToMilliseconds(Time.realtimeSinceStartupAsDouble),
                    SecondsToMilliseconds(lastTelemetryReceivedAt));
        }

        private static long SecondsToMilliseconds(double seconds)
        {
            if (double.IsNaN(seconds)
                || double.IsInfinity(seconds)
                || seconds < 0.0
                || seconds > long.MaxValue / 1000.0)
            {
                return -1;
            }

            return (long)(seconds * 1000.0);
        }

        private static string FlightActionWireName(FlightActionType action)
        {
            switch (action)
            {
                case FlightActionType.Takeoff:
                    return "takeoff";
                case FlightActionType.Land:
                    return "land";
                default:
                    return "return_home";
            }
        }

        public bool RequestGimbalPitch(float absolutePitchDegrees)
        {
            var telemetry = LatestTelemetry;
            var aircraftStationary = telemetry?.HasFlightState == true
                && !telemetry.IsFlying
                && !telemetry.MotorsOn;
            var gimbalHealthy = telemetry?.HasGimbalState == true
                && !telemetry.GimbalCalibrating
                && !telemetry.GimbalMotorOverloaded;

            if (!gimbalCommandSession.TryBegin(
                    absolutePitchDegrees,
                    IsGimbalAvailable,
                    aircraftStationary,
                    gimbalHealthy,
                    out var pending))
            {
                PublishGimbalCommand(pending);
                Debug.LogWarning("Gimbal command rejected: " + pending.Message);
                return false;
            }

            PublishGimbalCommand(pending);
            Debug.Log(
                "Gimbal pitch requested: " + absolutePitchDegrees.ToString("0.0", CultureInfo.InvariantCulture)
                + " degrees, requestId=" + pending.RequestId);

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                var accepted = androidBridge != null
                    && androidBridge.CallStatic<bool>(
                        "requestGimbalPitch",
                        absolutePitchDegrees,
                        pending.RequestId);
                if (accepted)
                {
                    return true;
                }

                CompleteGimbalCommand(
                    pending.RequestId,
                    false,
                    "Android gimbal backend rejected the request because its state changed.");
                return false;
            }
            catch (Exception exception)
            {
                Debug.LogError("DJI gimbal command dispatch failed: " + exception);
                CompleteGimbalCommand(
                    pending.RequestId,
                    false,
                    "Gimbal command dispatch failed: " + exception.GetType().Name);
                return false;
            }
#else
            CompleteGimbalCommand(
                pending.RequestId,
                false,
                "DJI gimbal commands require an Android device build.");
            return false;
#endif
        }

        // Called by the Android bridge through UnityPlayer.UnitySendMessage.
        public void OnDjiGimbalCommand(string json)
        {
            var dto = JsonUtility.FromJson<DjiGimbalCommandDto>(json);
            if (dto == null || dto.requestId <= 0)
            {
                Debug.LogError("DJI bridge returned an invalid gimbal command result.");
                return;
            }

            CompleteGimbalCommand(dto.requestId, dto.succeeded, dto.message);
        }

        private void CompleteGimbalCommand(long requestId, bool succeeded, string message)
        {
            if (!gimbalCommandSession.TryComplete(requestId, succeeded, message, out var result))
            {
                Debug.LogWarning("Ignoring stale DJI gimbal command result; requestId=" + requestId);
                return;
            }

            PublishGimbalCommand(result);
            if (succeeded)
            {
                Debug.Log("Gimbal command succeeded: " + result.Message);
            }
            else
            {
                Debug.LogError("Gimbal command failed: " + result.Message);
            }
        }

        private void FailPendingGimbalCommand(string message)
        {
            if (gimbalCommandSession.FailPending(message, out var result))
            {
                PublishGimbalCommand(result);
                Debug.LogError("Gimbal command failed: " + result.Message);
            }
        }

        private void PublishGimbalCommand(GimbalCommandResult result)
        {
            lastGimbalCommand = result;
            GimbalCommandStateChanged?.Invoke(result);
            RenderStatus();
        }

        private bool RequestCameraCommand(CameraCommandType command)
        {
            var telemetry = LatestTelemetry;
            var storageReady = telemetry != null
                && telemetry.HasCameraStorageState
                && telemetry.CameraStorageInserted
                && !telemetry.CameraStorageFull
                && !telemetry.CameraStorageReadOnly
                && !telemetry.CameraStorageHasError;

            if (!cameraCommandSession.TryBegin(
                    command,
                    IsCameraAvailable,
                    storageReady,
                    IsRecording,
                    out var pending))
            {
                PublishCameraCommand(pending);
                Debug.LogWarning("Camera command rejected: " + pending.Message);
                return false;
            }

            PublishCameraCommand(pending);
            Debug.Log(
                "Camera command requested: " + command
                + ", requestId=" + pending.RequestId);

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                var accepted = androidBridge != null
                    && androidBridge.CallStatic<bool>(
                        "requestCameraCommand",
                        CameraCommandWireName(command),
                        pending.RequestId);
                if (accepted)
                {
                    return true;
                }

                CompleteCameraCommand(
                    pending.RequestId,
                    false,
                    "Android camera backend rejected the request because its state changed.");
                return false;
            }
            catch (Exception exception)
            {
                Debug.LogError("DJI camera command dispatch failed: " + exception);
                CompleteCameraCommand(
                    pending.RequestId,
                    false,
                    "Camera command dispatch failed: " + exception.GetType().Name);
                return false;
            }
#else
            CompleteCameraCommand(
                pending.RequestId,
                false,
                "DJI camera commands require an Android device build.");
            return false;
#endif
        }

        // Called by the Android bridge through UnityPlayer.UnitySendMessage.
        public void OnDjiCameraCommand(string json)
        {
            var dto = JsonUtility.FromJson<DjiCameraCommandDto>(json);
            if (dto == null || dto.requestId <= 0)
            {
                Debug.LogError("DJI bridge returned an invalid camera command result.");
                return;
            }

            CompleteCameraCommand(dto.requestId, dto.succeeded, dto.message);
        }

        private void CompleteCameraCommand(long requestId, bool succeeded, string message)
        {
            if (!cameraCommandSession.TryComplete(requestId, succeeded, message, out var result))
            {
                Debug.LogWarning("Ignoring stale DJI camera command result; requestId=" + requestId);
                return;
            }

            PublishCameraCommand(result);
            if (succeeded)
            {
                Debug.Log("Camera command succeeded: " + result.Command + " - " + result.Message);
            }
            else
            {
                Debug.LogError("Camera command failed: " + result.Command + " - " + result.Message);
            }
        }

        private void FailPendingCameraCommand(string message)
        {
            if (cameraCommandSession.FailPending(message, out var result))
            {
                PublishCameraCommand(result);
                Debug.LogError("Camera command failed: " + result.Command + " - " + result.Message);
            }
        }

        private void PublishCameraCommand(CameraCommandResult result)
        {
            lastCameraCommand = result;
            CameraCommandStateChanged?.Invoke(result);
            RenderStatus();
        }

        private static string CameraCommandWireName(CameraCommandType command)
        {
            switch (command)
            {
                case CameraCommandType.StartRecording:
                    return "start_recording";
                case CameraCommandType.StopRecording:
                    return "stop_recording";
                default:
                    return "take_photo";
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private void PollVideoFrame()
        {
            if (!productConnected || androidBridge == null)
            {
                return;
            }

            var now = Time.realtimeSinceStartupAsDouble;
            if (now < nextVideoPollAt)
            {
                return;
            }
            nextVideoPollAt = now + VideoPollIntervalSeconds;

            try
            {
                var signedRgba = androidBridge.CallStatic<sbyte[]>("pollLatestVideoFrame");
                if (signedRgba == null || signedRgba.Length == 0)
                {
                    return;
                }

                var expectedLength = PreviewWidth * PreviewHeight * 4;
                if (signedRgba.Length != expectedLength)
                {
                    Debug.LogWarning(
                        "DJI preview frame size mismatch: expected=" + expectedLength
                        + ", actual=" + signedRgba.Length);
                    return;
                }

                var rgba = new byte[signedRgba.Length];
                Buffer.BlockCopy(signedRgba, 0, rgba, 0, signedRgba.Length);

                if (videoTexture == null)
                {
                    videoTexture = new Texture2D(
                        PreviewWidth,
                        PreviewHeight,
                        TextureFormat.RGBA32,
                        false,
                        false)
                    {
                        name = "DJI Spark FPV Diagnostic",
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp
                    };
                    videoSegmentStartedAt = now;
                    nextVideoHealthLogAt = now + 30.0;
                }
                else
                {
                    maximumVideoGapSeconds = Math.Max(maximumVideoGapSeconds, now - lastVideoFrameAt);
                }

                videoTexture.LoadRawTextureData(rgba);
                videoTexture.Apply(false, false);
                lastVideoFrameAt = now;
                videoFrameCount++;
                VideoFrameUpdated?.Invoke(videoTexture);

                if (videoFrameCount == 1)
                {
                    Debug.Log("DJI live preview displayed its first frame in Unity.");
                    RenderStatus();
                }

                if (now >= nextVideoHealthLogAt)
                {
                    var duration = Math.Max(0.001, now - videoSegmentStartedAt);
                    Debug.Log(
                        "DJI video health: durationSec=" + duration.ToString("0.0", CultureInfo.InvariantCulture)
                        + ", displayed=" + videoFrameCount
                        + ", displayRateHz=" + (videoFrameCount / duration).ToString("0.00", CultureInfo.InvariantCulture)
                        + ", maxGapMs=" + (maximumVideoGapSeconds * 1000.0).ToString("0", CultureInfo.InvariantCulture));
                    nextVideoHealthLogAt = now + 30.0;
                }
            }
            catch (Exception exception)
            {
                Debug.LogError("DJI preview polling failed: " + exception);
                nextVideoPollAt = now + 1.0;
            }
        }
#endif

        private void ResetVideoPreview()
        {
            nextVideoPollAt = 0.0;
            videoSegmentStartedAt = 0.0;
            lastVideoFrameAt = 0.0;
            nextVideoHealthLogAt = 0.0;
            maximumVideoGapSeconds = 0.0;
            videoFrameCount = 0;
            if (videoTexture != null)
            {
                Destroy(videoTexture);
                videoTexture = null;
            }
            VideoFrameUpdated?.Invoke(null);
        }

        // Called by the Android bridge through UnityPlayer.UnitySendMessage.
        public void OnDjiTelemetry(string json)
        {
            var dto = JsonUtility.FromJson<DjiTelemetryDto>(json);
            if (dto == null)
            {
                Debug.LogError("DJI bridge returned invalid telemetry.");
                return;
            }

            var now = Time.realtimeSinceStartupAsDouble;
            if (telemetryReceiveCount == 0)
            {
                telemetrySegmentStartedAt = now;
                nextTelemetryHealthLogAt = now + 30.0;
                Debug.Log("DJI telemetry health segment started.");
            }
            else
            {
                maximumTelemetryGapSeconds = Math.Max(
                    maximumTelemetryGapSeconds,
                    now - lastTelemetryReceivedAt);
            }

            lastTelemetryReceivedAt = now;
            telemetryReceiveCount++;
            if (telemetryStale)
            {
                telemetryStale = false;
                Debug.Log("DJI telemetry stream recovered after stale state.");
            }

            LatestTelemetry = new VehicleTelemetry
            {
                Sequence = dto.sequence,
                MonotonicMilliseconds = dto.monotonicMilliseconds,
                IsConnected = dto.isConnected,
                ProductName = dto.productModel,
                HasBatteryState = dto.hasBatteryState,
                BatteryPercent = dto.batteryPercent,
                BatteryVoltageMillivolts = dto.batteryVoltageMillivolts,
                BatteryCurrentMilliamps = dto.batteryCurrentMilliamps,
                BatteryTemperatureCelsius = dto.batteryTemperatureCelsius,
                HasFlightState = dto.hasFlightState,
                PositionValid = dto.positionValid,
                LatitudeDegrees = dto.latitudeDegrees,
                LongitudeDegrees = dto.longitudeDegrees,
                AltitudeMeters = dto.altitudeMeters,
                PitchDegrees = dto.pitchDegrees,
                RollDegrees = dto.rollDegrees,
                YawDegrees = dto.yawDegrees,
                HeadingDegrees = dto.headingDegrees,
                VelocityNorthMetersPerSecond = dto.velocityNorthMetersPerSecond,
                VelocityEastMetersPerSecond = dto.velocityEastMetersPerSecond,
                VelocityDownMetersPerSecond = dto.velocityDownMetersPerSecond,
                SatelliteCount = dto.satelliteCount,
                GpsSignalLevel = dto.gpsSignalLevel,
                FlightMode = dto.flightMode,
                MotorsOn = dto.motorsOn,
                IsFlying = dto.isFlying,
                IsGoingHome = dto.isGoingHome,
                HomePositionSet = dto.homePositionSet,
                FailsafeEnabled = dto.failsafeEnabled,
                HasCameraState = dto.hasCameraState,
                CameraMode = dto.cameraMode,
                CameraRecording = dto.cameraRecording,
                CameraShootingPhoto = dto.cameraShootingPhoto,
                CameraRecordingSeconds = dto.cameraRecordingSeconds,
                CameraOverheating = dto.cameraOverheating,
                CameraHasError = dto.cameraHasError,
                HasCameraStorageState = dto.hasCameraStorageState,
                CameraStorageInserted = dto.cameraStorageInserted,
                CameraStorageFull = dto.cameraStorageFull,
                CameraStorageReadOnly = dto.cameraStorageReadOnly,
                CameraStorageHasError = dto.cameraStorageHasError,
                CameraStorageRemainingMegabytes = dto.cameraStorageRemainingMegabytes,
                CameraAvailablePhotoCount = dto.cameraAvailablePhotoCount,
                CameraAvailableRecordingSeconds = dto.cameraAvailableRecordingSeconds,
                HasGimbalState = dto.hasGimbalState,
                GimbalPitchDegrees = dto.gimbalPitchDegrees,
                GimbalRollDegrees = dto.gimbalRollDegrees,
                GimbalYawDegrees = dto.gimbalYawDegrees,
                GimbalMode = dto.gimbalMode,
                GimbalCalibrating = dto.gimbalCalibrating,
                GimbalCalibrationSuccessful = dto.gimbalCalibrationSuccessful,
                GimbalMotorOverloaded = dto.gimbalMotorOverloaded,
                GimbalPitchAtStop = dto.gimbalPitchAtStop
            };
            TelemetryUpdated?.Invoke(LatestTelemetry);

            productConnected = dto.isConnected;
            if (!string.IsNullOrEmpty(dto.productModel))
            {
                productName = dto.productModel;
            }
            sdkState = "TELEMETRY";
            diagnosticMessage = "Live read-only aircraft telemetry.";
            RenderStatus();
        }

        private void LogTelemetryHealth(double now, string reason)
        {
            if (telemetryReceiveCount == 0)
            {
                return;
            }

            var durationSeconds = Math.Max(0.001, now - telemetrySegmentStartedAt);
            var receiveRate = telemetryReceiveCount / durationSeconds;
            var ageMilliseconds = Math.Max(0.0, now - lastTelemetryReceivedAt) * 1000.0;
            Debug.Log(
                "DJI telemetry health: reason=" + reason
                + ", durationSec=" + durationSeconds.ToString("0.0", CultureInfo.InvariantCulture)
                + ", received=" + telemetryReceiveCount
                + ", receiveRateHz=" + receiveRate.ToString("0.00", CultureInfo.InvariantCulture)
                + ", maxGapMs=" + (maximumTelemetryGapSeconds * 1000.0).ToString("0", CultureInfo.InvariantCulture)
                + ", ageMs=" + ageMilliseconds.ToString("0", CultureInfo.InvariantCulture));
        }

        private void ResetTelemetryHealth()
        {
            telemetrySegmentStartedAt = 0.0;
            lastTelemetryReceivedAt = 0.0;
            nextTelemetryHealthLogAt = 0.0;
            maximumTelemetryGapSeconds = 0.0;
            telemetryReceiveCount = 0;
            telemetryStale = false;
        }

        private void SetSimpleStatus(string state, string message)
        {
            sdkState = state;
            diagnosticMessage = message;
            RenderStatus();
        }

        private void RenderStatus()
        {
            var model = !string.IsNullOrEmpty(productName)
                ? productName
                : productConnected ? "CONNECTED (IDENTIFYING)" : "DISCONNECTED";
            var connection = productConnected ? "CONNECTED" : "DISCONNECTED";
            var telemetry = LatestTelemetry;

            if (telemetry == null
                || (!telemetry.HasBatteryState
                    && !telemetry.HasFlightState
                    && !telemetry.HasCameraState
                    && !telemetry.HasGimbalState))
            {
                SetStatus(
                    "QUEST COMMAND CENTER — DJI HIGH-LEVEL FLIGHT ACTIONS\n"
                    + "DJI MSDK " + sdkVersion + " | SDK: " + sdkState + "\n"
                    + "Product: " + model + " | " + connection + "\n"
                    + diagnosticMessage + "\n\n"
                    + VirtualStickStatusText());
                return;
            }

            var battery = telemetry.HasBatteryState
                ? telemetry.BatteryPercent + "% | "
                    + (telemetry.BatteryVoltageMillivolts / 1000f).ToString("0.00", CultureInfo.InvariantCulture) + " V | "
                    + telemetry.BatteryTemperatureCelsius.ToString("0.0", CultureInfo.InvariantCulture) + " C"
                : "waiting";
            var gps = telemetry.HasFlightState
                ? "level " + telemetry.GpsSignalLevel + " | " + telemetry.SatelliteCount + " sats | "
                    + (telemetry.PositionValid ? "valid" : "not valid")
                : "waiting";
            var flightMode = string.IsNullOrEmpty(telemetry.FlightMode) ? "UNKNOWN" : telemetry.FlightMode;
            var cameraMode = string.IsNullOrEmpty(telemetry.CameraMode) ? "UNKNOWN" : telemetry.CameraMode;
            var gimbalMode = string.IsNullOrEmpty(telemetry.GimbalMode) ? "UNKNOWN" : telemetry.GimbalMode;
            var cameraStatus = telemetry.HasCameraState
                ? cameraMode
                    + " | Recording " + (telemetry.CameraRecording ? "YES" : "NO")
                    + " | Photo " + (telemetry.CameraShootingPhoto ? "YES" : "NO")
                    + (telemetry.CameraHasError ? " | ERROR" : "")
                    + (telemetry.CameraOverheating ? " | HOT" : "")
                : "waiting";
            var storageStatus = telemetry.HasCameraStorageState
                ? (telemetry.CameraStorageInserted ? "inserted" : "missing")
                    + " | " + telemetry.CameraStorageRemainingMegabytes + " MB free"
                    + (telemetry.CameraStorageFull ? " | FULL" : "")
                    + (telemetry.CameraStorageReadOnly ? " | READ ONLY" : "")
                    + (telemetry.CameraStorageHasError ? " | ERROR" : "")
                : "waiting";
            var gimbalStatus = telemetry.HasGimbalState
                ? "Pitch/Roll/Yaw "
                    + telemetry.GimbalPitchDegrees.ToString("0.0", CultureInfo.InvariantCulture) + " / "
                    + telemetry.GimbalRollDegrees.ToString("0.0", CultureInfo.InvariantCulture) + " / "
                    + telemetry.GimbalYawDegrees.ToString("0.0", CultureInfo.InvariantCulture)
                    + " deg | " + gimbalMode
                    + (telemetry.GimbalMotorOverloaded ? " | OVERLOAD" : "")
                : "waiting";

            SetStatus(
                "QUEST COMMAND CENTER — DJI HIGH-LEVEL FLIGHT ACTIONS\n"
                + "DJI MSDK " + sdkVersion + " | " + model + " | " + connection + "\n"
                + "Battery: " + battery + "\n"
                + "GPS: " + gps + "\n"
                + "Altitude: " + telemetry.AltitudeMeters.ToString("0.0", CultureInfo.InvariantCulture)
                    + " m | Heading: " + telemetry.HeadingDegrees + " deg | Speed: "
                    + telemetry.HorizontalSpeedMetersPerSecond.ToString("0.0", CultureInfo.InvariantCulture) + " m/s\n"
                + "Velocity N/E/D: "
                    + telemetry.VelocityNorthMetersPerSecond.ToString("0.0", CultureInfo.InvariantCulture) + " / "
                    + telemetry.VelocityEastMetersPerSecond.ToString("0.0", CultureInfo.InvariantCulture) + " / "
                    + telemetry.VelocityDownMetersPerSecond.ToString("0.0", CultureInfo.InvariantCulture) + " m/s\n"
                + "Flight: " + flightMode
                    + " | Motors " + (telemetry.MotorsOn ? "ON" : "OFF")
                    + " | Flying " + (telemetry.IsFlying ? "YES" : "NO") + "\n\n"
                + "Camera: " + cameraStatus + "\n"
                + "Storage: " + storageStatus + "\n"
                + "Gimbal: " + gimbalStatus + "\n\n"
                + "Video: " + (HasVideoFrame ? "LIVE 640x360 diagnostic preview" : "waiting for frame") + "\n"
                + "Camera command: " + CameraCommandStatusText() + "\n"
                + "Flight action: " + FlightActionStatusText() + "\n"
                + VirtualStickStatusText());
        }

        private string VirtualStickStatusText()
        {
            if (virtualStickStatusRequestPending)
            {
                return "MANUAL VIRTUAL STICK: READ-ONLY PROBE PENDING";
            }
            if (!virtualStickStatusKnown)
            {
                return "MANUAL VIRTUAL STICK: LOCKED | " + virtualStickMessage;
            }
            if (!virtualStickEnabled)
            {
                return "MANUAL VIRTUAL STICK: API READY | MODE DISABLED | "
                    + virtualStickMessage;
            }
            return "MANUAL VIRTUAL STICK: MODE ENABLED | SEND READY "
                + (virtualStickAvailable ? "YES" : "NO")
                + " | " + virtualStickMessage;
        }

        private string CameraCommandStatusText()
        {
            var result = LastCameraCommand;
            return result == null
                ? "idle"
                : result.Status + " | " + result.Message;
        }

        private string FlightActionStatusText()
        {
            var result = LastFlightAction;
            return result == null
                ? "idle"
                : result.Status + " | " + result.Action + " | " + result.Message;
        }

        private void SetStatus(string value)
        {
            CurrentStatus = value;
            StatusChanged?.Invoke(value);
        }

        [Serializable]
        private sealed class DjiSdkEvent
        {
            public string type;
            public bool ok;
            public string message;
            public string sdkVersion;
            public string productModel;
        }

        [Serializable]
        private sealed class DjiTelemetryDto
        {
            public long sequence;
            public long monotonicMilliseconds;
            public bool isConnected;
            public string productModel;
            public bool hasBatteryState;
            public int batteryPercent;
            public int batteryVoltageMillivolts;
            public int batteryCurrentMilliamps;
            public float batteryTemperatureCelsius;
            public bool hasFlightState;
            public bool positionValid;
            public double latitudeDegrees;
            public double longitudeDegrees;
            public float altitudeMeters;
            public float pitchDegrees;
            public float rollDegrees;
            public float yawDegrees;
            public int headingDegrees;
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
            public bool hasCameraState;
            public string cameraMode;
            public bool cameraRecording;
            public bool cameraShootingPhoto;
            public int cameraRecordingSeconds;
            public bool cameraOverheating;
            public bool cameraHasError;
            public bool hasCameraStorageState;
            public bool cameraStorageInserted;
            public bool cameraStorageFull;
            public bool cameraStorageReadOnly;
            public bool cameraStorageHasError;
            public int cameraStorageRemainingMegabytes;
            public long cameraAvailablePhotoCount;
            public int cameraAvailableRecordingSeconds;
            public bool hasGimbalState;
            public float gimbalPitchDegrees;
            public float gimbalRollDegrees;
            public float gimbalYawDegrees;
            public string gimbalMode;
            public bool gimbalCalibrating;
            public bool gimbalCalibrationSuccessful;
            public bool gimbalMotorOverloaded;
            public bool gimbalPitchAtStop;
        }

        [Serializable]
        private sealed class DjiCameraCommandDto
        {
            public long requestId;
            public string operation;
            public bool succeeded;
            public string message;
            public long monotonicMilliseconds;
        }

        [Serializable]
        private sealed class DjiFlightActionDto
        {
            public long requestId;
            public string operation;
            public bool succeeded;
            public string message;
            public long monotonicMilliseconds;
        }

        [Serializable]
        private sealed class DjiGimbalCommandDto
        {
            public long requestId;
            public float pitchDegrees;
            public bool succeeded;
            public string message;
            public long monotonicMilliseconds;
        }

        [Serializable]
        private sealed class DjiVirtualStickStatusDto
        {
            public long requestId;
            public string operation;
            public bool succeeded;
            public bool available;
            public bool enabled;
            public string message;
            public long monotonicMilliseconds;
        }
    }
}
