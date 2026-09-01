package com.dronecommand.quest.dji;

import android.app.Activity;
import android.graphics.SurfaceTexture;
import android.media.MediaFormat;
import android.os.SystemClock;
import android.util.Log;

import com.unity3d.player.UnityPlayer;

import org.json.JSONObject;

import java.nio.ByteBuffer;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;

import dji.common.battery.BatteryState;
import dji.common.camera.SystemState;
import dji.common.camera.SettingsDefinitions;
import dji.common.camera.StorageState;
import dji.common.error.DJIError;
import dji.common.error.DJISDKError;
import dji.common.flightcontroller.Attitude;
import dji.common.flightcontroller.FlightControllerState;
import dji.common.flightcontroller.LocationCoordinate3D;
import dji.common.flightcontroller.virtualstick.FlightControlData;
import dji.common.flightcontroller.virtualstick.FlightCoordinateSystem;
import dji.common.flightcontroller.virtualstick.RollPitchControlMode;
import dji.common.flightcontroller.virtualstick.VerticalControlMode;
import dji.common.flightcontroller.virtualstick.YawControlMode;
import dji.common.gimbal.GimbalState;
import dji.common.gimbal.Rotation;
import dji.common.gimbal.RotationMode;
import dji.common.model.LocationCoordinate2D;
import dji.common.util.CommonCallbacks;
import dji.sdk.base.BaseComponent;
import dji.sdk.base.BaseProduct;
import dji.sdk.battery.Battery;
import dji.sdk.camera.Camera;
import dji.sdk.camera.VideoFeeder;
import dji.sdk.codec.DJICodecManager;
import dji.sdk.flightcontroller.FlightController;
import dji.sdk.gimbal.Gimbal;
import dji.sdk.products.Aircraft;
import dji.sdk.sdkmanager.DJISDKInitEvent;
import dji.sdk.sdkmanager.DJISDKManager;

public final class DjiSdkBridge {
    private static final String TAG = "QuestDjiBridge";
    private static final long TELEMETRY_EMIT_INTERVAL_MS = 200L;
    private static final long GIMBAL_TELEMETRY_MAXIMUM_AGE_MS = 500L;
    private static final long FLIGHT_ACTION_TELEMETRY_MAXIMUM_AGE_MS = 500L;
    private static final long VIRTUAL_STICK_COMMAND_STALE_MS = 150L;
    private static final long VIRTUAL_STICK_DISABLE_TIMEOUT_MS = 500L;
    private static final long VIRTUAL_STICK_PERIOD_MS = 50L;
    private static final float VIRTUAL_STICK_MAX_HORIZONTAL_MPS = 1.0f;
    private static final float VIRTUAL_STICK_MAX_VERTICAL_MPS = 0.8f;
    private static final float VIRTUAL_STICK_MAX_YAW_DPS = 30.0f;
    private static final int MINIMUM_TAKEOFF_BATTERY_PERCENT = 30;
    private static final int PREVIEW_WIDTH = 640;
    private static final int PREVIEW_HEIGHT = 360;
    private static final long PREVIEW_FRAME_INTERVAL_MS = 100L;
    private static final AtomicBoolean registrationStarted = new AtomicBoolean(false);
    private static final Object telemetryLock = new Object();
    private static final Object videoLock = new Object();
    private static final Object cameraCommandLock = new Object();
    private static final Object gimbalCommandLock = new Object();
    private static final Object flightActionLock = new Object();
    private static final Object virtualStickLock = new Object();
    private static String unityReceiver;
    private static FlightController boundFlightController;
    private static Battery boundBattery;
    private static Camera boundCamera;
    private static Gimbal boundGimbal;
    private static FlightControllerState latestFlightState;
    private static long latestFlightStateAtMs;
    private static BatteryState latestBatteryState;
    private static SystemState latestCameraState;
    private static StorageState latestCameraStorageState;
    private static GimbalState latestGimbalState;
    private static long lastTelemetryEmitMs;
    private static long telemetrySequence;
    private static boolean flightStateObserved;
    private static boolean batteryStateObserved;
    private static boolean cameraStateObserved;
    private static boolean cameraStorageStateObserved;
    private static boolean gimbalStateObserved;
    private static VideoFeeder.VideoFeed boundVideoFeed;
    private static VideoFeeder.VideoDataListener videoDataListener;
    private static DJICodecManager videoCodecManager;
    private static byte[] latestRgbaFrame;
    private static long latestVideoFrameSequence;
    private static long lastPolledVideoFrameSequence;
    private static long lastPreviewFrameCaptureMs;
    private static boolean videoPacketObserved;
    private static boolean decodedVideoFrameObserved;
    private static boolean yuvFormatLogged;
    private static boolean unsupportedYuvLayoutLogged;
    private static long activeCameraCommandId;
    private static String activeCameraCommandOperation;
    private static Camera activeCameraCommandCamera;
    private static long activeGimbalCommandId;
    private static float activeGimbalCommandPitch;
    private static Gimbal activeGimbalCommandGimbal;
    private static long activeFlightActionId;
    private static String activeFlightActionOperation;
    private static FlightController activeFlightActionController;
    private static ScheduledExecutorService virtualStickExecutor;
    private static boolean virtualStickEnabled;
    private static boolean virtualStickModeRequestPending;
    private static long latestVirtualStickSequence;
    private static long latestVirtualStickCommandAtMs;
    private static float latestVirtualStickRoll;
    private static float latestVirtualStickPitch;
    private static float latestVirtualStickYaw;
    private static float latestVirtualStickVertical;
    private static boolean latestVirtualStickAuthorized;

    private DjiSdkBridge() {
    }

    public static void register(String receiverObjectName) {
        unityReceiver = receiverObjectName;
        if (!registrationStarted.compareAndSet(false, true)) {
            emit("registration_already_started", true, "Registration was already requested.", null);
            return;
        }

        try {
            Activity activity = UnityPlayer.currentActivity;
            if (activity == null) {
                throw new IllegalStateException("UnityPlayer.currentActivity is null.");
            }

            DJISDKManager manager = DJISDKManager.getInstance();
            String version = manager.getSDKVersion();
            emit("sdk_loaded", true, "DJI SDK classes loaded; starting registration.", null, version);
            manager.registerApp(activity.getApplicationContext(), new DiagnosticCallback());
        } catch (Throwable throwable) {
            registrationStarted.set(false);
            Log.e(TAG, "DJI SDK startup failed", throwable);
            emit("startup_failed", false, throwable.getClass().getSimpleName() + ": " + safeMessage(throwable), null);
        }
    }

    private static final class DiagnosticCallback implements DJISDKManager.SDKManagerCallback {
        @Override
        public void onRegister(DJIError error) {
            boolean success = error == DJISDKError.REGISTRATION_SUCCESS;
            String description = error == null ? "Registration callback returned no result." : error.getDescription();
            emit(success ? "registered" : "registration_failed", success, description, null);
            if (success) {
                boolean started = DJISDKManager.getInstance().startConnectionToProduct();
                emit(
                    started ? "connection_search_started" : "waiting_for_product_link",
                    true,
                    started
                        ? "Searching for a DJI product."
                        : "Registration is complete; connect to the DJI product Wi-Fi/USB/Bluetooth data link and restart the app.",
                    null);
            }
        }

        @Override
        public void onProductDisconnect() {
            unbindVideoPreview();
            unbindReadOnlyTelemetry();
            emit("product_disconnected", true, "DJI product disconnected.", null);
        }

        @Override
        public void onProductConnect(BaseProduct product) {
            emit("product_connected", true, "DJI product connected.", productName(product));
            bindReadOnlyTelemetry(product);
            bindVideoPreview(product);
        }

        @Override
        public void onProductChanged(BaseProduct product) {
            emit("product_changed", true, "DJI product changed.", productName(product));
            bindReadOnlyTelemetry(product);
            bindVideoPreview(product);
        }

        @Override
        public void onComponentChange(
                BaseProduct.ComponentKey key,
                BaseComponent oldComponent,
                BaseComponent newComponent) {
            String component = key == null ? "unknown" : key.toString();
            emit("component_changed", true, "Component changed: " + component, currentProductName());
            BaseProduct product = DJISDKManager.getInstance().getProduct();
            if (product instanceof Aircraft) {
                bindReadOnlyTelemetry(product);
                bindVideoPreview(product);
            }
        }

        @Override
        public void onInitProcess(DJISDKInitEvent event, int progress) {
            String name = event == null ? "unknown" : event.toString();
            emit("initializing", true, name + " (" + progress + ")", null);
        }

        @Override
        public void onDatabaseDownloadProgress(long current, long total) {
            long percent = total <= 0 ? 0 : (current * 100L / total);
            emit("database_download", true, "Fly-safe database: " + percent + "%", null);
        }
    }

    public static byte[] pollLatestVideoFrame() {
        synchronized (videoLock) {
            if (latestRgbaFrame == null || latestVideoFrameSequence == lastPolledVideoFrameSequence) {
                return null;
            }
            lastPolledVideoFrameSequence = latestVideoFrameSequence;
            byte[] frame = latestRgbaFrame;
            latestRgbaFrame = null;
            return frame;
        }
    }

    public static int getPreviewWidth() {
        return PREVIEW_WIDTH;
    }

    public static int getPreviewHeight() {
        return PREVIEW_HEIGHT;
    }

    public static boolean requestCameraCommand(String operation, long requestId) {
        final Camera camera;
        final SystemState cameraState;
        final StorageState storageState;
        synchronized (telemetryLock) {
            camera = boundCamera;
            cameraState = latestCameraState;
            storageState = latestCameraStorageState;
        }

        if (camera == null || !camera.isConnected() || operation == null || requestId <= 0L) {
            Log.w(TAG, "Camera command rejected because the camera or request is unavailable");
            return false;
        }

        if (!"stop_recording".equals(operation)
                && !isCameraStorageReady(storageState)) {
            Log.w(TAG, "Camera command rejected because storage is not ready: " + operation);
            return false;
        }

        if ("start_recording".equals(operation) && cameraState != null && cameraState.isRecording()) {
            Log.w(TAG, "Start-recording command rejected because recording is already active");
            return false;
        }
        if ("stop_recording".equals(operation) && (cameraState == null || !cameraState.isRecording())) {
            Log.w(TAG, "Stop-recording command rejected because recording is not active");
            return false;
        }
        if ("take_photo".equals(operation) && cameraState != null && cameraState.isRecording()) {
            Log.w(TAG, "Photo command rejected while Spark recording is active");
            return false;
        }

        synchronized (cameraCommandLock) {
            if (activeCameraCommandId != 0L) {
                Log.w(TAG, "Camera command rejected because another command is pending");
                return false;
            }
            activeCameraCommandId = requestId;
            activeCameraCommandOperation = operation;
            activeCameraCommandCamera = camera;
        }

        Log.i(TAG, "Camera command accepted: " + operation + ", requestId=" + requestId);
        try {
            if ("start_recording".equals(operation)) {
                startCameraRecording(camera, requestId);
            } else if ("stop_recording".equals(operation)) {
                stopCameraRecording(camera, requestId);
            } else if ("take_photo".equals(operation)) {
                takeSinglePhoto(camera, requestId);
            } else {
                finishCameraCommand(requestId, false, "Unknown camera command.");
                return false;
            }
            return true;
        } catch (Throwable throwable) {
            Log.e(TAG, "Could not dispatch camera command: " + operation, throwable);
            finishCameraCommand(
                requestId,
                false,
                throwable.getClass().getSimpleName() + ": " + safeMessage(throwable));
            return true;
        }
    }

    public static boolean requestGimbalPitch(float pitchDegrees, long requestId) {
        final Gimbal gimbal;
        final GimbalState gimbalState;
        final FlightControllerState flightState;
        final long telemetryTimestampMs;
        synchronized (telemetryLock) {
            gimbal = boundGimbal;
            gimbalState = latestGimbalState;
            flightState = latestFlightState;
            telemetryTimestampMs = lastTelemetryEmitMs;
        }

        if (gimbal == null || !gimbal.isConnected() || requestId <= 0L || !isFinite(pitchDegrees)) {
            Log.w(TAG, "Gimbal command rejected because the gimbal or request is unavailable");
            return false;
        }
        if (pitchDegrees < -20.0f || pitchDegrees > 0.0f) {
            Log.w(TAG, "Gimbal command rejected outside the -20 to 0 degree development range");
            return false;
        }
        if (flightState == null || flightState.isFlying() || flightState.areMotorsOn()) {
            Log.w(TAG, "Gimbal command rejected because landed/motors-off state is not confirmed");
            return false;
        }
        if (telemetryTimestampMs <= 0L
                || SystemClock.elapsedRealtime() - telemetryTimestampMs > GIMBAL_TELEMETRY_MAXIMUM_AGE_MS) {
            Log.w(TAG, "Gimbal command rejected because telemetry is older than 500 ms");
            return false;
        }
        if (gimbalState == null || gimbalState.isCalibrating() || gimbalState.isMotorOverloaded()) {
            Log.w(TAG, "Gimbal command rejected because gimbal health is not confirmed");
            return false;
        }

        synchronized (gimbalCommandLock) {
            if (activeGimbalCommandId != 0L) {
                Log.w(TAG, "Gimbal command rejected because another command is pending");
                return false;
            }
            activeGimbalCommandId = requestId;
            activeGimbalCommandPitch = pitchDegrees;
            activeGimbalCommandGimbal = gimbal;
        }

        Log.i(TAG, "Gimbal pitch command accepted: " + pitchDegrees + ", requestId=" + requestId);
        try {
            Rotation rotation = new Rotation.Builder()
                .mode(RotationMode.ABSOLUTE_ANGLE)
                .pitch(pitchDegrees)
                .roll(Rotation.NO_ROTATION)
                .yaw(Rotation.NO_ROTATION)
                .time(0.35)
                .build();
            gimbal.rotate(rotation, new CommonCallbacks.CompletionCallback() {
                @Override
                public void onResult(DJIError error) {
                    finishGimbalCommand(
                        requestId,
                        error == null,
                        error == null
                            ? "Gimbal reached the requested head-tracking pitch."
                            : "Could not move gimbal: " + errorDescription(error));
                }
            });
            return true;
        } catch (Throwable throwable) {
            Log.e(TAG, "Could not dispatch gimbal command", throwable);
            finishGimbalCommand(
                requestId,
                false,
                throwable.getClass().getSimpleName() + ": " + safeMessage(throwable));
            return true;
        }
    }

    public static boolean requestFlightAction(String operation, long requestId) {
        final FlightController flightController;
        final FlightControllerState flightState;
        final BatteryState batteryState;
        final long flightStateAtMs;
        synchronized (telemetryLock) {
            flightController = boundFlightController;
            flightState = latestFlightState;
            batteryState = latestBatteryState;
            flightStateAtMs = latestFlightStateAtMs;
        }

        if (flightController == null
                || !flightController.isConnected()
                || flightState == null
                || operation == null
                || requestId <= 0L) {
            Log.w(TAG, "Flight action rejected because controller state is unavailable");
            return false;
        }
        if (flightStateAtMs <= 0L
                || SystemClock.elapsedRealtime() - flightStateAtMs
                    > FLIGHT_ACTION_TELEMETRY_MAXIMUM_AGE_MS) {
            Log.w(TAG, "Flight action rejected because flight telemetry is older than 500 ms");
            return false;
        }
        if (flightState.isFailsafeEnabled()) {
            Log.w(TAG, "Flight action rejected because DJI failsafe is active");
            return false;
        }
        if ("takeoff".equals(operation)) {
            if (flightState.isFlying() || flightState.areMotorsOn()) {
                Log.w(TAG, "Takeoff rejected because landed/motors-off state is not confirmed");
                return false;
            }
            if (batteryState == null
                    || batteryState.getChargeRemainingInPercent()
                        < MINIMUM_TAKEOFF_BATTERY_PERCENT) {
                Log.w(TAG, "Takeoff rejected because battery is unavailable or below 30 percent");
                return false;
            }
        } else if ("land".equals(operation)) {
            if (!flightState.isFlying()) {
                Log.w(TAG, "Landing rejected because flying state is not confirmed");
                return false;
            }
        } else if ("return_home".equals(operation)) {
            if (!flightState.isFlying() || !flightState.isHomeLocationSet()) {
                Log.w(TAG, "Return Home rejected because flying state or home point is not confirmed");
                return false;
            }
        } else {
            Log.w(TAG, "Unknown flight action rejected: " + operation);
            return false;
        }

        synchronized (flightActionLock) {
            if (activeFlightActionId != 0L) {
                Log.w(TAG, "Flight action rejected because another action is pending");
                return false;
            }
            activeFlightActionId = requestId;
            activeFlightActionOperation = operation;
            activeFlightActionController = flightController;
        }

        Log.w(TAG, "Confirmed flight action accepted for DJI dispatch: "
            + operation + ", requestId=" + requestId);
        try {
            CommonCallbacks.CompletionCallback callback = new CommonCallbacks.CompletionCallback() {
                @Override
                public void onResult(DJIError error) {
                    finishFlightAction(
                        requestId,
                        error == null,
                        error == null
                            ? "DJI accepted the high-level aircraft command. Monitor telemetry for completion."
                            : "DJI rejected the high-level aircraft command: " + errorDescription(error));
                }
            };

            if ("takeoff".equals(operation)) {
                flightController.startTakeoff(callback);
            } else if ("land".equals(operation)) {
                flightController.startLanding(callback);
            } else {
                flightController.startGoHome(callback);
            }
            return true;
        } catch (Throwable throwable) {
            Log.e(TAG, "Could not dispatch flight action: " + operation, throwable);
            finishFlightAction(
                requestId,
                false,
                throwable.getClass().getSimpleName() + ": " + safeMessage(throwable));
            return true;
        }
    }

    /**
     * Read-only API-reachability probe. DJI documents availability as false until
     * Virtual Stick mode is enabled, so a successful status getter—not the
     * pre-enable availability value—is what proves the interface is ready.
     * This method never enables Virtual Stick or sends flight-control data.
     */
    public static boolean queryVirtualStickStatus(final long requestId) {
        final FlightController flightController;
        synchronized (telemetryLock) {
            flightController = boundFlightController;
        }
        if (flightController == null || !flightController.isConnected() || requestId <= 0L) {
            return false;
        }

        final boolean available = flightController.isVirtualStickControlModeAvailable();
        try {
            flightController.getVirtualStickModeEnabled(
                new CommonCallbacks.CompletionCallbackWith<Boolean>() {
                    @Override
                    public void onSuccess(Boolean enabled) {
                        final boolean modeEnabled = Boolean.TRUE.equals(enabled);
                        emitVirtualStickStatus(
                            requestId,
                            "query",
                            true,
                            available,
                            modeEnabled,
                            modeEnabled
                                ? "Virtual Stick status received; mode is enabled."
                                : "Virtual Stick API ready; mode is disabled until operator confirmation.");
                    }

                    @Override
                    public void onFailure(DJIError error) {
                        emitVirtualStickStatus(
                            requestId,
                            "query",
                            false,
                            available,
                            false,
                            "Could not read Virtual Stick mode: " + errorDescription(error));
                    }
                });
            return true;
        } catch (Throwable throwable) {
            Log.e(TAG, "Virtual Stick status probe failed", throwable);
            emitVirtualStickStatus(
                requestId,
                "query",
                false,
                available,
                false,
                throwable.getClass().getSimpleName() + ": " + safeMessage(throwable));
            return true;
        }
    }

    /**
     * Guarded lifecycle entry point. The command-center controller invokes this
     * only after its deliberate preflight/unlock sequence has completed.
     */
    public static boolean requestVirtualStickMode(final boolean enabled, final long requestId) {
        final FlightController flightController;
        final FlightControllerState flightState;
        final long flightStateAtMs;
        synchronized (telemetryLock) {
            flightController = boundFlightController;
            flightState = latestFlightState;
            flightStateAtMs = latestFlightStateAtMs;
        }

        if (flightController == null || !flightController.isConnected() || requestId <= 0L) {
            return false;
        }
        if (enabled
                && (flightState == null
                    || flightState.isFailsafeEnabled()
                    || flightStateAtMs <= 0L
                    || SystemClock.elapsedRealtime() - flightStateAtMs
                        > FLIGHT_ACTION_TELEMETRY_MAXIMUM_AGE_MS)) {
            Log.w(TAG, "Virtual Stick enable rejected because fresh safe flight state is unavailable");
            return false;
        }

        synchronized (virtualStickLock) {
            if (virtualStickModeRequestPending) {
                return false;
            }
            virtualStickModeRequestPending = true;
            if (enabled) {
                latestVirtualStickCommandAtMs = SystemClock.elapsedRealtime();
                latestVirtualStickAuthorized = false;
                latestVirtualStickRoll = 0.0f;
                latestVirtualStickPitch = 0.0f;
                latestVirtualStickYaw = 0.0f;
                latestVirtualStickVertical = 0.0f;
            }
        }

        try {
            if (enabled) {
                flightController.setRollPitchControlMode(RollPitchControlMode.VELOCITY);
                flightController.setVerticalControlMode(VerticalControlMode.VELOCITY);
                flightController.setYawControlMode(YawControlMode.ANGULAR_VELOCITY);
                flightController.setRollPitchCoordinateSystem(FlightCoordinateSystem.BODY);
                flightController.setVirtualStickAdvancedModeEnabled(false);
            }
            flightController.setVirtualStickModeEnabled(
                enabled,
                new CommonCallbacks.CompletionCallback() {
                    @Override
                    public void onResult(DJIError error) {
                        final boolean succeeded = error == null;
                        synchronized (virtualStickLock) {
                            virtualStickModeRequestPending = false;
                            virtualStickEnabled = succeeded && enabled;
                            if (succeeded && enabled) {
                                // Start the stale-command window only after DJI
                                // finishes its asynchronous enable operation.
                                // Starting it before the callback can make a
                                // normal multi-second DJI response look stale on
                                // the scheduler's very first tick.
                                latestVirtualStickCommandAtMs = SystemClock.elapsedRealtime();
                                latestVirtualStickAuthorized = false;
                                latestVirtualStickRoll = 0.0f;
                                latestVirtualStickPitch = 0.0f;
                                latestVirtualStickYaw = 0.0f;
                                latestVirtualStickVertical = 0.0f;
                            }
                        }
                        if (succeeded && enabled) {
                            startVirtualStickScheduler();
                        } else if (!enabled || !succeeded) {
                            stopVirtualStickScheduler();
                        }
                        emitVirtualStickStatus(
                            requestId,
                            enabled ? "enable" : "disable",
                            succeeded,
                            flightController.isVirtualStickControlModeAvailable(),
                            succeeded && enabled,
                            succeeded
                                ? (enabled ? "Virtual Stick mode enabled." : "Virtual Stick mode disabled.")
                                : "Virtual Stick mode change failed: " + errorDescription(error));
                    }
                });
            return true;
        } catch (Throwable throwable) {
            synchronized (virtualStickLock) {
                virtualStickModeRequestPending = false;
                virtualStickEnabled = false;
            }
            stopVirtualStickScheduler();
            Log.e(TAG, "Virtual Stick mode change failed", throwable);
            emitVirtualStickStatus(
                requestId,
                enabled ? "enable" : "disable",
                false,
                false,
                false,
                throwable.getClass().getSimpleName() + ": " + safeMessage(throwable));
            return true;
        }
    }

    /**
     * Stores only a newly generated bounded command. The 20 Hz native scheduler
     * sends it; stale or unauthorized input is replaced by neutral immediately.
     */
    public static boolean submitVirtualStickCommand(
            long sequence,
            float roll,
            float pitch,
            float yaw,
            float vertical,
            boolean authorized) {
        if (sequence <= 0L
                || !isFinite(roll)
                || !isFinite(pitch)
                || !isFinite(yaw)
                || !isFinite(vertical)) {
            return false;
        }
        synchronized (virtualStickLock) {
            if (!virtualStickEnabled || sequence <= latestVirtualStickSequence) {
                return false;
            }
            latestVirtualStickSequence = sequence;
            latestVirtualStickCommandAtMs = SystemClock.elapsedRealtime();
            latestVirtualStickAuthorized = authorized;
            latestVirtualStickRoll = clamp(roll, VIRTUAL_STICK_MAX_HORIZONTAL_MPS);
            latestVirtualStickPitch = clamp(pitch, VIRTUAL_STICK_MAX_HORIZONTAL_MPS);
            latestVirtualStickYaw = clamp(yaw, VIRTUAL_STICK_MAX_YAW_DPS);
            latestVirtualStickVertical = clamp(vertical, VIRTUAL_STICK_MAX_VERTICAL_MPS);
            return true;
        }
    }

    private static void startVirtualStickScheduler() {
        synchronized (virtualStickLock) {
            if (virtualStickExecutor != null && !virtualStickExecutor.isShutdown()) {
                return;
            }
            virtualStickExecutor = Executors.newSingleThreadScheduledExecutor();
            virtualStickExecutor.scheduleAtFixedRate(
                new Runnable() {
                    @Override
                    public void run() {
                        runVirtualStickTick();
                    }
                },
                0L,
                VIRTUAL_STICK_PERIOD_MS,
                TimeUnit.MILLISECONDS);
        }
    }

    private static void stopVirtualStickScheduler() {
        final ScheduledExecutorService executor;
        synchronized (virtualStickLock) {
            executor = virtualStickExecutor;
            virtualStickExecutor = null;
            latestVirtualStickAuthorized = false;
            latestVirtualStickRoll = 0.0f;
            latestVirtualStickPitch = 0.0f;
            latestVirtualStickYaw = 0.0f;
            latestVirtualStickVertical = 0.0f;
        }
        if (executor != null) {
            executor.shutdownNow();
        }
    }

    private static void runVirtualStickTick() {
        final FlightController flightController;
        final FlightControllerState flightState;
        final long flightStateAtMs;
        synchronized (telemetryLock) {
            flightController = boundFlightController;
            flightState = latestFlightState;
            flightStateAtMs = latestFlightStateAtMs;
        }

        final long now = SystemClock.elapsedRealtime();
        final long commandAgeMs;
        final boolean authorized;
        final float roll;
        final float pitch;
        final float yaw;
        final float vertical;
        synchronized (virtualStickLock) {
            if (!virtualStickEnabled) {
                return;
            }
            commandAgeMs = latestVirtualStickCommandAtMs <= 0L
                ? Long.MAX_VALUE
                : now - latestVirtualStickCommandAtMs;
            authorized = latestVirtualStickAuthorized;
            roll = latestVirtualStickRoll;
            pitch = latestVirtualStickPitch;
            yaw = latestVirtualStickYaw;
            vertical = latestVirtualStickVertical;
        }

        if (flightController == null
                || !flightController.isConnected()
                || flightState == null
                || flightStateAtMs <= 0L
                || now - flightStateAtMs > FLIGHT_ACTION_TELEMETRY_MAXIMUM_AGE_MS
                || flightState.isFailsafeEnabled()
                || commandAgeMs > VIRTUAL_STICK_DISABLE_TIMEOUT_MS) {
            disableVirtualStickForSafety(flightController, "link, telemetry, failsafe, or command timeout");
            return;
        }

        final boolean movementAllowed = authorized
            && commandAgeMs <= VIRTUAL_STICK_COMMAND_STALE_MS
            && flightState.isFlying();
        final FlightControlData data = movementAllowed
            ? new FlightControlData(pitch, roll, yaw, vertical)
            : new FlightControlData(0.0f, 0.0f, 0.0f, 0.0f);
        try {
            flightController.sendVirtualStickFlightControlData(data, null);
        } catch (Throwable throwable) {
            Log.e(TAG, "Virtual Stick send failed; disabling control mode", throwable);
            disableVirtualStickForSafety(flightController, "send failure");
        }
    }

    private static void disableVirtualStickForSafety(
            final FlightController flightController,
            final String reason) {
        synchronized (virtualStickLock) {
            if (!virtualStickEnabled || virtualStickModeRequestPending) {
                return;
            }
            virtualStickEnabled = false;
            virtualStickModeRequestPending = true;
        }
        stopVirtualStickScheduler();
        Log.w(TAG, "Virtual Stick safety disable: " + reason);
        if (flightController == null || !flightController.isConnected()) {
            synchronized (virtualStickLock) {
                virtualStickModeRequestPending = false;
            }
            emit("virtual_stick_safety_disabled", false, reason, currentProductName());
            return;
        }
        try {
            flightController.setVirtualStickModeEnabled(
                false,
                new CommonCallbacks.CompletionCallback() {
                    @Override
                    public void onResult(DJIError error) {
                        synchronized (virtualStickLock) {
                            virtualStickModeRequestPending = false;
                        }
                        emit(
                            "virtual_stick_safety_disabled",
                            error == null,
                            error == null ? reason : reason + ": " + errorDescription(error),
                            currentProductName());
                    }
                });
        } catch (Throwable throwable) {
            synchronized (virtualStickLock) {
                virtualStickModeRequestPending = false;
            }
            emit("virtual_stick_safety_disabled", false, reason + ": " + safeMessage(throwable), currentProductName());
        }
    }

    private static float clamp(float value, float maximumMagnitude) {
        return Math.max(-maximumMagnitude, Math.min(maximumMagnitude, value));
    }

    private static void startCameraRecording(final Camera camera, final long requestId) {
        camera.setMode(SettingsDefinitions.CameraMode.RECORD_VIDEO, new CommonCallbacks.CompletionCallback() {
            @Override
            public void onResult(DJIError error) {
                if (error != null) {
                    finishCameraCommand(requestId, false, "Could not enter video mode: " + errorDescription(error));
                    return;
                }
                if (!isCameraCommandActive(requestId, camera)) {
                    return;
                }
                camera.startRecordVideo(new CommonCallbacks.CompletionCallback() {
                    @Override
                    public void onResult(DJIError startError) {
                        finishCameraCommand(
                            requestId,
                            startError == null,
                            startError == null
                                ? "Onboard recording started."
                                : "Could not start onboard recording: " + errorDescription(startError));
                    }
                });
            }
        });
    }

    private static void stopCameraRecording(final Camera camera, final long requestId) {
        camera.stopRecordVideo(new CommonCallbacks.CompletionCallback() {
            @Override
            public void onResult(DJIError error) {
                finishCameraCommand(
                    requestId,
                    error == null,
                    error == null
                        ? "Onboard recording stopped."
                        : "Could not stop onboard recording: " + errorDescription(error));
            }
        });
    }

    private static void takeSinglePhoto(final Camera camera, final long requestId) {
        camera.setMode(SettingsDefinitions.CameraMode.SHOOT_PHOTO, new CommonCallbacks.CompletionCallback() {
            @Override
            public void onResult(DJIError modeError) {
                if (modeError != null) {
                    finishCameraCommand(requestId, false, "Could not enter photo mode: " + errorDescription(modeError));
                    return;
                }
                if (!isCameraCommandActive(requestId, camera)) {
                    return;
                }
                camera.setShootPhotoMode(
                    SettingsDefinitions.ShootPhotoMode.SINGLE,
                    new CommonCallbacks.CompletionCallback() {
                        @Override
                        public void onResult(DJIError photoModeError) {
                            if (photoModeError != null) {
                                finishCameraCommand(
                                    requestId,
                                    false,
                                    "Could not select single-photo mode: " + errorDescription(photoModeError));
                                return;
                            }
                            if (!isCameraCommandActive(requestId, camera)) {
                                return;
                            }
                            camera.startShootPhoto(new CommonCallbacks.CompletionCallback() {
                                @Override
                                public void onResult(DJIError photoError) {
                                    finishCameraCommand(
                                        requestId,
                                        photoError == null,
                                        photoError == null
                                            ? "Spark accepted the single-photo command."
                                            : "Could not take photo: " + errorDescription(photoError));
                                }
                            });
                        }
                    });
            }
        });
    }

    private static boolean isCameraCommandActive(long requestId, Camera camera) {
        synchronized (cameraCommandLock) {
            return activeCameraCommandId == requestId && activeCameraCommandCamera == camera;
        }
    }

    private static void finishCameraCommand(long requestId, boolean succeeded, String message) {
        final String operation;
        synchronized (cameraCommandLock) {
            if (activeCameraCommandId != requestId) {
                return;
            }
            operation = activeCameraCommandOperation;
            activeCameraCommandId = 0L;
            activeCameraCommandOperation = null;
            activeCameraCommandCamera = null;
        }

        Log.i(
            TAG,
            "Camera command " + (succeeded ? "succeeded" : "failed")
                + ": " + operation + ", requestId=" + requestId);
        emitCameraCommand(requestId, operation, succeeded, message);
    }

    private static void failPendingCameraCommand(String message) {
        final long requestId;
        synchronized (cameraCommandLock) {
            requestId = activeCameraCommandId;
        }
        if (requestId != 0L) {
            finishCameraCommand(requestId, false, message);
        }
    }

    private static void finishGimbalCommand(long requestId, boolean succeeded, String message) {
        final float pitchDegrees;
        synchronized (gimbalCommandLock) {
            if (activeGimbalCommandId != requestId) {
                return;
            }
            pitchDegrees = activeGimbalCommandPitch;
            activeGimbalCommandId = 0L;
            activeGimbalCommandPitch = 0.0f;
            activeGimbalCommandGimbal = null;
        }

        Log.i(
            TAG,
            "Gimbal command " + (succeeded ? "succeeded" : "failed")
                + ": pitch=" + pitchDegrees + ", requestId=" + requestId);
        emitGimbalCommand(requestId, pitchDegrees, succeeded, message);
    }

    private static void failPendingGimbalCommand(String message) {
        final long requestId;
        synchronized (gimbalCommandLock) {
            requestId = activeGimbalCommandId;
        }
        if (requestId != 0L) {
            finishGimbalCommand(requestId, false, message);
        }
    }

    private static void finishFlightAction(long requestId, boolean succeeded, String message) {
        final String operation;
        synchronized (flightActionLock) {
            if (activeFlightActionId != requestId) {
                return;
            }
            operation = activeFlightActionOperation;
            activeFlightActionId = 0L;
            activeFlightActionOperation = null;
            activeFlightActionController = null;
        }

        Log.w(
            TAG,
            "Flight action " + (succeeded ? "accepted" : "failed")
                + ": " + operation + ", requestId=" + requestId);
        emitFlightAction(requestId, operation, succeeded, message);
    }

    private static void failPendingFlightAction(String message) {
        final long requestId;
        synchronized (flightActionLock) {
            requestId = activeFlightActionId;
        }
        if (requestId != 0L) {
            finishFlightAction(requestId, false, message);
        }
    }

    private static boolean isCameraStorageReady(StorageState state) {
        return state != null
            && state.isInserted()
            && !state.isInitializing()
            && !state.isFull()
            && !state.isReadOnly()
            && !state.hasError()
            && !state.isInvalidFormat();
    }

    private static String errorDescription(DJIError error) {
        if (error == null) {
            return "Unknown DJI error.";
        }
        final String description = error.getDescription() == null
            ? "No description supplied."
            : error.getDescription();
        return error.getClass().getSimpleName()
            + " code=" + error.getErrorCode()
            + " description=" + description;
    }

    private static void emitCameraCommand(
            long requestId,
            String operation,
            boolean succeeded,
            String message) {
        try {
            JSONObject payload = new JSONObject();
            payload.put("requestId", requestId);
            payload.put("operation", operation == null ? "" : operation);
            payload.put("succeeded", succeeded);
            payload.put("message", message == null ? "" : message);
            payload.put("monotonicMilliseconds", SystemClock.elapsedRealtime());
            UnityPlayer.UnitySendMessage(unityReceiver, "OnDjiCameraCommand", payload.toString());
        } catch (Throwable throwable) {
            Log.e(TAG, "Could not emit DJI camera command result", throwable);
        }
    }

    private static void emitGimbalCommand(
            long requestId,
            float pitchDegrees,
            boolean succeeded,
            String message) {
        try {
            JSONObject payload = new JSONObject();
            payload.put("requestId", requestId);
            payload.put("pitchDegrees", pitchDegrees);
            payload.put("succeeded", succeeded);
            payload.put("message", message == null ? "" : message);
            payload.put("monotonicMilliseconds", SystemClock.elapsedRealtime());
            UnityPlayer.UnitySendMessage(unityReceiver, "OnDjiGimbalCommand", payload.toString());
        } catch (Throwable throwable) {
            Log.e(TAG, "Could not emit DJI gimbal command result", throwable);
        }
    }

    private static void emitFlightAction(
            long requestId,
            String operation,
            boolean succeeded,
            String message) {
        try {
            JSONObject payload = new JSONObject();
            payload.put("requestId", requestId);
            payload.put("operation", operation == null ? "" : operation);
            payload.put("succeeded", succeeded);
            payload.put("message", message == null ? "" : message);
            payload.put("monotonicMilliseconds", SystemClock.elapsedRealtime());
            UnityPlayer.UnitySendMessage(unityReceiver, "OnDjiFlightActionResult", payload.toString());
        } catch (Throwable throwable) {
            Log.e(TAG, "Could not emit DJI flight-action result", throwable);
        }
    }

    private static void emitVirtualStickStatus(
            long requestId,
            String operation,
            boolean succeeded,
            boolean available,
            boolean enabled,
            String message) {
        try {
            JSONObject payload = new JSONObject();
            payload.put("requestId", requestId);
            payload.put("operation", operation == null ? "" : operation);
            payload.put("succeeded", succeeded);
            payload.put("available", available);
            payload.put("enabled", enabled);
            payload.put("message", message == null ? "" : message);
            payload.put("monotonicMilliseconds", SystemClock.elapsedRealtime());
            final String logMessage = "Virtual Stick result: requestId=" + requestId
                + ", operation=" + operation
                + ", succeeded=" + succeeded
                + ", available=" + available
                + ", enabled=" + enabled
                + ", message=" + (message == null ? "" : message);
            if (succeeded) {
                Log.i(TAG, logMessage);
            } else {
                Log.e(TAG, logMessage);
            }
            UnityPlayer.UnitySendMessage(
                unityReceiver,
                "OnDjiVirtualStickStatus",
                payload.toString());
        } catch (Throwable throwable) {
            Log.e(TAG, "Could not emit Virtual Stick status", throwable);
        }
    }

    private static void bindVideoPreview(BaseProduct product) {
        if (!(product instanceof Aircraft)) {
            return;
        }

        VideoFeeder feeder = VideoFeeder.getInstance();
        final VideoFeeder.VideoFeed feed = feeder == null ? null : feeder.getPrimaryVideoFeed();
        if (feed == null) {
            emit("video_unavailable", false, "DJI primary video feed is not ready.", productName(product));
            return;
        }

        synchronized (videoLock) {
            if (feed == boundVideoFeed && videoCodecManager != null) {
                return;
            }
        }

        unbindVideoPreview();

        Activity activity = UnityPlayer.currentActivity;
        if (activity == null) {
            emit("video_unavailable", false, "Unity activity is unavailable for video decoding.", productName(product));
            return;
        }

        final DJICodecManager codecManager;
        try {
            codecManager = new DJICodecManager(
                activity.getApplicationContext(),
                (SurfaceTexture) null,
                PREVIEW_WIDTH,
                PREVIEW_HEIGHT);
        } catch (Throwable throwable) {
            Log.e(TAG, "Could not create DJI video decoder", throwable);
            emit("video_decoder_failed", false, safeMessage(throwable), productName(product));
            return;
        }

        final VideoFeeder.VideoDataListener listener = new VideoFeeder.VideoDataListener() {
            @Override
            public void onReceive(byte[] videoBuffer, int size) {
                synchronized (videoLock) {
                    if (videoCodecManager != codecManager) {
                        return;
                    }
                    if (!videoPacketObserved) {
                        videoPacketObserved = true;
                        Log.i(TAG, "First encoded primary-video packet received");
                    }
                }

                try {
                    codecManager.sendDataToDecoder(videoBuffer, size);
                } catch (Throwable throwable) {
                    Log.e(TAG, "Could not send DJI video packet to decoder", throwable);
                }
            }
        };

        codecManager.setYuvDataCallback(new DJICodecManager.YuvDataCallback() {
            @Override
            public void onYuvDataReceived(
                    MediaFormat format,
                    ByteBuffer yuvFrame,
                    int dataSize,
                    int width,
                    int height) {
                final long now = SystemClock.elapsedRealtime();
                synchronized (videoLock) {
                    if (videoCodecManager != codecManager
                            || now - lastPreviewFrameCaptureMs < PREVIEW_FRAME_INTERVAL_MS) {
                        return;
                    }
                    lastPreviewFrameCaptureMs = now;
                }

                try {
                    if (!yuvFormatLogged) {
                        synchronized (videoLock) {
                            if (!yuvFormatLogged) {
                                yuvFormatLogged = true;
                                Log.i(
                                    TAG,
                                    "First decoded YUV frame: " + width + "x" + height
                                        + ", dataSize=" + dataSize
                                        + ", format=" + (format == null ? "unknown" : format.toString()));
                            }
                        }
                    }

                    byte[] rgba = convertI420ToRgba(
                        format,
                        yuvFrame,
                        dataSize,
                        width,
                        height,
                        PREVIEW_WIDTH,
                        PREVIEW_HEIGHT);
                    if (rgba == null) {
                        synchronized (videoLock) {
                            if (!unsupportedYuvLayoutLogged) {
                                unsupportedYuvLayoutLogged = true;
                                Log.w(
                                    TAG,
                                    "Decoded YUV frame has an unsupported or incomplete planar layout; "
                                        + "preview frame skipped");
                            }
                        }
                        return;
                    }

                    boolean firstFrame;
                    synchronized (videoLock) {
                        if (videoCodecManager != codecManager) {
                            return;
                        }
                        latestRgbaFrame = rgba;
                        latestVideoFrameSequence++;
                        firstFrame = !decodedVideoFrameObserved;
                        decodedVideoFrameObserved = true;
                    }

                    if (firstFrame) {
                        Log.i(TAG, "First decoded RGBA preview frame ready");
                        emit("video_frame_ready", true, "Live camera preview is active.", currentProductName());
                    }
                } catch (Throwable throwable) {
                    Log.e(TAG, "Could not obtain decoded RGBA preview frame", throwable);
                }
            }
        });

        synchronized (videoLock) {
            boundVideoFeed = feed;
            videoDataListener = listener;
            videoCodecManager = codecManager;
            latestRgbaFrame = null;
            latestVideoFrameSequence = 0L;
            lastPolledVideoFrameSequence = 0L;
            lastPreviewFrameCaptureMs = 0L;
            videoPacketObserved = false;
            decodedVideoFrameObserved = false;
            yuvFormatLogged = false;
            unsupportedYuvLayoutLogged = false;
        }

        try {
            codecManager.enabledYuvData(Boolean.TRUE);
            boolean added = feed.addVideoDataListener(listener);
            if (!added) {
                unbindVideoPreview();
                emit("video_unavailable", false, "DJI rejected the primary video listener.", productName(product));
                return;
            }
            Log.i(TAG, "Primary video feed bound to DJI hardware decoder");
            emit("video_bound", true, "Waiting for the first decoded camera frame.", productName(product));
        } catch (Throwable throwable) {
            Log.e(TAG, "Could not start DJI video preview", throwable);
            unbindVideoPreview();
            emit("video_decoder_failed", false, safeMessage(throwable), productName(product));
        }
    }

    private static void unbindVideoPreview() {
        VideoFeeder.VideoFeed feed;
        VideoFeeder.VideoDataListener listener;
        DJICodecManager codecManager;
        synchronized (videoLock) {
            feed = boundVideoFeed;
            listener = videoDataListener;
            codecManager = videoCodecManager;
            boundVideoFeed = null;
            videoDataListener = null;
            videoCodecManager = null;
            latestRgbaFrame = null;
            latestVideoFrameSequence = 0L;
            lastPolledVideoFrameSequence = 0L;
            lastPreviewFrameCaptureMs = 0L;
            videoPacketObserved = false;
            decodedVideoFrameObserved = false;
            yuvFormatLogged = false;
            unsupportedYuvLayoutLogged = false;
        }

        try {
            if (feed != null && listener != null) {
                feed.removeVideoDataListener(listener);
            }
        } catch (Throwable throwable) {
            Log.w(TAG, "Could not remove primary video listener", throwable);
        }

        try {
            if (codecManager != null) {
                codecManager.setYuvDataCallback(null);
                codecManager.enabledYuvData(Boolean.FALSE);
                codecManager.cleanSurface();
                codecManager.destroyCodec();
            }
        } catch (Throwable throwable) {
            Log.w(TAG, "Could not destroy DJI video decoder", throwable);
        }
    }

    private static byte[] convertI420ToRgba(
            MediaFormat format,
            ByteBuffer yuv,
            int dataSize,
            int sourceWidth,
            int sourceHeight,
            int outputWidth,
            int outputHeight) {
        if (yuv == null
                || sourceWidth <= 0
                || sourceHeight <= 0
                || outputWidth <= 0
                || outputHeight <= 0) {
            return null;
        }

        int stride = mediaFormatInteger(format, MediaFormat.KEY_STRIDE, sourceWidth);
        int sliceHeight = mediaFormatInteger(format, MediaFormat.KEY_SLICE_HEIGHT, sourceHeight);
        if (stride < sourceWidth || sliceHeight < sourceHeight) {
            return null;
        }

        int chromaStride = (stride + 1) / 2;
        int chromaSliceHeight = (sliceHeight + 1) / 2;
        int yPlaneSize = stride * sliceHeight;
        int uOffset = yPlaneSize;
        int vOffset = uOffset + chromaStride * chromaSliceHeight;
        int requiredSize = vOffset + chromaStride * chromaSliceHeight;
        int availableSize = Math.min(dataSize, yuv.capacity());
        if (requiredSize > availableSize) {
            return null;
        }

        // Center-crop to the presentation aspect ratio, then nearest-neighbor
        // downscale. Crop coordinates stay even so the 4:2:0 chroma planes align.
        int cropWidth = sourceWidth;
        int cropHeight = sourceHeight;
        if ((long) sourceWidth * outputHeight > (long) sourceHeight * outputWidth) {
            cropWidth = sourceHeight * outputWidth / outputHeight;
        } else if ((long) sourceWidth * outputHeight < (long) sourceHeight * outputWidth) {
            cropHeight = sourceWidth * outputHeight / outputWidth;
        }
        cropWidth &= ~1;
        cropHeight &= ~1;
        int cropLeft = ((sourceWidth - cropWidth) / 2) & ~1;
        int cropTop = ((sourceHeight - cropHeight) / 2) & ~1;

        ByteBuffer frame = yuv.duplicate();
        int pixelCount = outputWidth * outputHeight;
        byte[] rgba = new byte[pixelCount * 4];

        for (int outputY = 0; outputY < outputHeight; outputY++) {
            int sourceY = cropTop + outputY * cropHeight / outputHeight;
            int yRow = sourceY * stride;
            int chromaRow = (sourceY / 2) * chromaStride;
            int outputRow = outputY * outputWidth;
            for (int outputX = 0; outputX < outputWidth; outputX++) {
                int sourceX = cropLeft + outputX * cropWidth / outputWidth;
                int luma = frame.get(yRow + sourceX) & 0xff;
                int chromaIndex = chromaRow + (sourceX / 2);
                int u = (frame.get(uOffset + chromaIndex) & 0xff) - 128;
                int v = (frame.get(vOffset + chromaIndex) & 0xff) - 128;
                int c = Math.max(0, luma - 16);
                int r = clampColor((298 * c + 409 * v + 128) >> 8);
                int g = clampColor((298 * c - 100 * u - 208 * v + 128) >> 8);
                int b = clampColor((298 * c + 516 * u + 128) >> 8);
                int rgbaIndex = (outputRow + outputX) * 4;
                rgba[rgbaIndex] = (byte) r;
                rgba[rgbaIndex + 1] = (byte) g;
                rgba[rgbaIndex + 2] = (byte) b;
                rgba[rgbaIndex + 3] = (byte) 255;
            }
        }
        return rgba;
    }

    private static int mediaFormatInteger(MediaFormat format, String key, int fallback) {
        if (format == null || !format.containsKey(key)) {
            return fallback;
        }
        try {
            return format.getInteger(key);
        } catch (Throwable ignored) {
            return fallback;
        }
    }

    private static int clampColor(int value) {
        return Math.max(0, Math.min(255, value));
    }

    private static void bindReadOnlyTelemetry(BaseProduct product) {
        if (!(product instanceof Aircraft)) {
            unbindReadOnlyTelemetry();
            emit("telemetry_unavailable", false, "Connected product is not an aircraft.", productName(product));
            return;
        }

        Aircraft aircraft = (Aircraft) product;
        final FlightController flightController = aircraft.getFlightController();
        final Battery battery = aircraft.getBattery();
        final Camera camera = aircraft.getCamera();
        final Gimbal gimbal = aircraft.getGimbal();

        synchronized (telemetryLock) {
            if (flightController == boundFlightController
                    && battery == boundBattery
                    && camera == boundCamera
                    && gimbal == boundGimbal) {
                return;
            }
        }

        unbindReadOnlyTelemetry();

        synchronized (telemetryLock) {
            boundFlightController = flightController;
            boundBattery = battery;
            boundCamera = camera;
            boundGimbal = gimbal;
            latestFlightState = null;
            latestFlightStateAtMs = 0L;
            latestBatteryState = null;
            latestCameraState = null;
            latestCameraStorageState = null;
            latestGimbalState = null;
            lastTelemetryEmitMs = 0L;
            flightStateObserved = false;
            batteryStateObserved = false;
            cameraStateObserved = false;
            cameraStorageStateObserved = false;
            gimbalStateObserved = false;
        }

        if (flightController != null) {
            flightController.setStateCallback(new FlightControllerState.Callback() {
                @Override
                public void onUpdate(FlightControllerState state) {
                    synchronized (telemetryLock) {
                        if (boundFlightController != flightController) {
                            return;
                        }
                        latestFlightState = state;
                        latestFlightStateAtMs = SystemClock.elapsedRealtime();
                        if (state != null && !flightStateObserved) {
                            flightStateObserved = true;
                            Log.i(TAG, "First read-only flight-state callback received");
                        }
                    }
                    emitTelemetryIfDue();
                }
            });
        }

        if (battery != null) {
            battery.setStateCallback(new BatteryState.Callback() {
                @Override
                public void onUpdate(BatteryState state) {
                    synchronized (telemetryLock) {
                        if (boundBattery != battery) {
                            return;
                        }
                        latestBatteryState = state;
                        if (state != null && !batteryStateObserved) {
                            batteryStateObserved = true;
                            Log.i(TAG, "First read-only battery-state callback received");
                        }
                    }
                    emitTelemetryIfDue();
                }
            });
        }

        if (camera != null) {
            camera.setSystemStateCallback(new SystemState.Callback() {
                @Override
                public void onUpdate(SystemState state) {
                    synchronized (telemetryLock) {
                        if (boundCamera != camera) {
                            return;
                        }
                        latestCameraState = state;
                        if (state != null && !cameraStateObserved) {
                            cameraStateObserved = true;
                            Log.i(TAG, "First read-only camera-state callback received");
                        }
                    }
                    emitTelemetryIfDue();
                }
            });
            camera.setSDCardStateCallBack(new StorageState.Callback() {
                @Override
                public void onUpdate(StorageState state) {
                    synchronized (telemetryLock) {
                        if (boundCamera != camera) {
                            return;
                        }
                        latestCameraStorageState = state;
                        if (state != null && !cameraStorageStateObserved) {
                            cameraStorageStateObserved = true;
                            Log.i(TAG, "First read-only camera-storage callback received");
                        }
                    }
                    emitTelemetryIfDue();
                }
            });
        }

        if (gimbal != null) {
            gimbal.setStateCallback(new GimbalState.Callback() {
                @Override
                public void onUpdate(GimbalState state) {
                    synchronized (telemetryLock) {
                        if (boundGimbal != gimbal) {
                            return;
                        }
                        latestGimbalState = state;
                        if (state != null && !gimbalStateObserved) {
                            gimbalStateObserved = true;
                            Log.i(TAG, "First read-only gimbal-state callback received");
                        }
                    }
                    emitTelemetryIfDue();
                }
            });
        }

        if (flightController == null && battery == null && camera == null && gimbal == null) {
            emit("telemetry_unavailable", false, "Aircraft telemetry components are not ready.", productName(product));
            return;
        }

        Log.i(TAG, "Read-only telemetry callbacks bound");
        emit(
            "telemetry_bound",
            true,
            "Read-only telemetry callbacks are active.",
            productName(product));
    }

    private static void unbindReadOnlyTelemetry() {
        FlightController flightController;
        Battery battery;
        Camera camera;
        Gimbal gimbal;
        synchronized (telemetryLock) {
            flightController = boundFlightController;
            battery = boundBattery;
            camera = boundCamera;
            gimbal = boundGimbal;
            boundFlightController = null;
            boundBattery = null;
            boundCamera = null;
            boundGimbal = null;
            latestFlightState = null;
            latestFlightStateAtMs = 0L;
            latestBatteryState = null;
            latestCameraState = null;
            latestCameraStorageState = null;
            latestGimbalState = null;
            lastTelemetryEmitMs = 0L;
            flightStateObserved = false;
            batteryStateObserved = false;
            cameraStateObserved = false;
            cameraStorageStateObserved = false;
            gimbalStateObserved = false;
        }

        disableVirtualStickForSafety(flightController, "flight controller disconnected or rebound");

        try {
            if (flightController != null) {
                flightController.setStateCallback(null);
            }
        } catch (Throwable throwable) {
            Log.w(TAG, "Could not remove flight-state callback", throwable);
        }

        try {
            if (battery != null) {
                battery.setStateCallback(null);
            }
        } catch (Throwable throwable) {
            Log.w(TAG, "Could not remove battery-state callback", throwable);
        }

        try {
            if (camera != null) {
                camera.setSystemStateCallback(null);
                camera.setSDCardStateCallBack(null);
            }
        } catch (Throwable throwable) {
            Log.w(TAG, "Could not remove camera-state callback", throwable);
        }

        failPendingCameraCommand("Camera disconnected while the command was pending.");
        failPendingGimbalCommand("Gimbal disconnected while the command was pending.");
        failPendingFlightAction("Flight controller disconnected while the action was pending.");

        try {
            if (gimbal != null) {
                gimbal.setStateCallback(null);
            }
        } catch (Throwable throwable) {
            Log.w(TAG, "Could not remove gimbal-state callback", throwable);
        }
    }

    private static void emitTelemetryIfDue() {
        final long now = SystemClock.elapsedRealtime();
        final FlightControllerState flightState;
        final BatteryState batteryState;
        final SystemState cameraState;
        final StorageState cameraStorageState;
        final GimbalState gimbalState;
        final long sequence;

        synchronized (telemetryLock) {
            if (now - lastTelemetryEmitMs < TELEMETRY_EMIT_INTERVAL_MS) {
                return;
            }
            lastTelemetryEmitMs = now;
            sequence = ++telemetrySequence;
            flightState = latestFlightState;
            batteryState = latestBatteryState;
            cameraState = latestCameraState;
            cameraStorageState = latestCameraStorageState;
            gimbalState = latestGimbalState;
        }

        try {
            JSONObject payload = new JSONObject();
            payload.put("sequence", sequence);
            payload.put("monotonicMilliseconds", now);

            BaseProduct product = DJISDKManager.getInstance().getProduct();
            payload.put("isConnected", product != null && product.isConnected());
            payload.put("productModel", emptyIfNull(productName(product)));

            payload.put("hasBatteryState", batteryState != null);
            payload.put("batteryPercent", batteryState == null ? -1 : batteryState.getChargeRemainingInPercent());
            payload.put("batteryVoltageMillivolts", batteryState == null ? 0 : batteryState.getVoltage());
            payload.put("batteryCurrentMilliamps", batteryState == null ? 0 : batteryState.getCurrent());
            payload.put(
                "batteryTemperatureCelsius",
                batteryState == null ? 0.0 : finiteOrZero(batteryState.getTemperature()));

            payload.put("hasFlightState", flightState != null);
            putFlightState(payload, flightState);
            payload.put("hasCameraState", cameraState != null);
            putCameraState(payload, cameraState);
            payload.put("hasCameraStorageState", cameraStorageState != null);
            putCameraStorageState(payload, cameraStorageState);
            payload.put("hasGimbalState", gimbalState != null);
            putGimbalState(payload, gimbalState);

            UnityPlayer.UnitySendMessage(unityReceiver, "OnDjiTelemetry", payload.toString());
        } catch (Throwable throwable) {
            Log.e(TAG, "Could not emit DJI telemetry", throwable);
        }
    }

    private static void putCameraState(JSONObject payload, SystemState state) throws Exception {
        payload.put("cameraMode", state == null || state.getMode() == null ? "UNKNOWN" : state.getMode().name());
        payload.put("cameraRecording", state != null && state.isRecording());
        payload.put("cameraShootingPhoto", state != null && state.isShootingSinglePhoto());
        payload.put("cameraRecordingSeconds", state == null ? 0 : state.getCurrentVideoRecordingTimeInSeconds());
        payload.put("cameraOverheating", state != null && state.isOverheating());
        payload.put("cameraHasError", state != null && state.hasError());
    }

    private static void putCameraStorageState(JSONObject payload, StorageState state) throws Exception {
        payload.put("cameraStorageInserted", state != null && state.isInserted());
        payload.put("cameraStorageFull", state != null && state.isFull());
        payload.put("cameraStorageReadOnly", state != null && state.isReadOnly());
        payload.put(
            "cameraStorageHasError",
            state != null && (state.hasError() || state.isInvalidFormat()));
        payload.put("cameraStorageRemainingMegabytes", state == null ? 0 : state.getRemainingSpaceInMB());
        payload.put("cameraAvailablePhotoCount", state == null ? 0L : state.getAvailableCaptureCount());
        payload.put(
            "cameraAvailableRecordingSeconds",
            state == null ? 0 : state.getAvailableRecordingTimeInSeconds());
    }

    private static void putGimbalState(JSONObject payload, GimbalState state) throws Exception {
        dji.common.gimbal.Attitude attitude = state == null ? null : state.getAttitudeInDegrees();
        payload.put("gimbalPitchDegrees", attitude == null ? 0.0 : finiteOrZero(attitude.getPitch()));
        payload.put("gimbalRollDegrees", attitude == null ? 0.0 : finiteOrZero(attitude.getRoll()));
        payload.put("gimbalYawDegrees", attitude == null ? 0.0 : finiteOrZero(attitude.getYaw()));
        payload.put("gimbalMode", state == null || state.getMode() == null ? "UNKNOWN" : state.getMode().name());
        payload.put("gimbalCalibrating", state != null && state.isCalibrating());
        payload.put("gimbalCalibrationSuccessful", state != null && state.isCalibrationSuccessful());
        payload.put("gimbalMotorOverloaded", state != null && state.isMotorOverloaded());
        payload.put("gimbalPitchAtStop", state != null && state.isPitchAtStop());
    }

    private static void putFlightState(JSONObject payload, FlightControllerState state) throws Exception {
        if (state == null) {
            putEmptyFlightState(payload);
            return;
        }

        LocationCoordinate3D location = state.getAircraftLocation();
        double latitude = location == null ? 0.0 : location.getLatitude();
        double longitude = location == null ? 0.0 : location.getLongitude();
        boolean positionValid = location != null
            && isFinite(latitude)
            && isFinite(longitude)
            && LocationCoordinate2D.isValid(latitude, longitude);

        Attitude attitude = state.getAttitude();
        payload.put("positionValid", positionValid);
        payload.put("latitudeDegrees", positionValid ? latitude : 0.0);
        payload.put("longitudeDegrees", positionValid ? longitude : 0.0);
        payload.put("altitudeMeters", location == null ? 0.0 : finiteOrZero(location.getAltitude()));
        payload.put("pitchDegrees", attitude == null ? 0.0 : finiteOrZero(attitude.pitch));
        payload.put("rollDegrees", attitude == null ? 0.0 : finiteOrZero(attitude.roll));
        payload.put("yawDegrees", attitude == null ? 0.0 : finiteOrZero(attitude.yaw));
        payload.put("headingDegrees", state.getAircraftHeadDirection());
        payload.put("velocityNorthMetersPerSecond", finiteOrZero(state.getVelocityX()));
        payload.put("velocityEastMetersPerSecond", finiteOrZero(state.getVelocityY()));
        payload.put("velocityDownMetersPerSecond", finiteOrZero(state.getVelocityZ()));
        payload.put("satelliteCount", state.getSatelliteCount());
        payload.put("gpsSignalLevel", state.getGPSSignalLevel() == null ? -1 : state.getGPSSignalLevel().value());
        payload.put("flightMode", state.getFlightMode() == null ? "UNKNOWN" : state.getFlightMode().name());
        payload.put("motorsOn", state.areMotorsOn());
        payload.put("isFlying", state.isFlying());
        payload.put("isGoingHome", state.isGoingHome());
        payload.put("homePositionSet", state.isHomeLocationSet());
        payload.put("failsafeEnabled", state.isFailsafeEnabled());
    }

    private static void putEmptyFlightState(JSONObject payload) throws Exception {
        payload.put("positionValid", false);
        payload.put("latitudeDegrees", 0.0);
        payload.put("longitudeDegrees", 0.0);
        payload.put("altitudeMeters", 0.0);
        payload.put("pitchDegrees", 0.0);
        payload.put("rollDegrees", 0.0);
        payload.put("yawDegrees", 0.0);
        payload.put("headingDegrees", 0);
        payload.put("velocityNorthMetersPerSecond", 0.0);
        payload.put("velocityEastMetersPerSecond", 0.0);
        payload.put("velocityDownMetersPerSecond", 0.0);
        payload.put("satelliteCount", 0);
        payload.put("gpsSignalLevel", -1);
        payload.put("flightMode", "UNKNOWN");
        payload.put("motorsOn", false);
        payload.put("isFlying", false);
        payload.put("isGoingHome", false);
        payload.put("homePositionSet", false);
        payload.put("failsafeEnabled", false);
    }

    private static double finiteOrZero(double value) {
        return isFinite(value) ? value : 0.0;
    }

    private static boolean isFinite(double value) {
        return !Double.isNaN(value) && !Double.isInfinite(value);
    }

    private static String emptyIfNull(String value) {
        return value == null ? "" : value;
    }

    private static String currentProductName() {
        try {
            return productName(DJISDKManager.getInstance().getProduct());
        } catch (Throwable ignored) {
            return null;
        }
    }

    private static String productName(BaseProduct product) {
        if (product == null || product.getModel() == null) {
            return null;
        }
        return product.getModel().getDisplayName();
    }

    private static void emit(String type, boolean ok, String message, String productModel) {
        String version = null;
        try {
            version = DJISDKManager.getInstance().getSDKVersion();
        } catch (Throwable ignored) {
            // A loader failure is reported by the original event instead.
        }
        emit(type, ok, message, productModel, version);
    }

    private static void emit(
            String type,
            boolean ok,
            String message,
            String productModel,
            String sdkVersion) {
        try {
            JSONObject payload = new JSONObject();
            payload.put("type", type);
            payload.put("ok", ok);
            payload.put("message", message == null ? "" : message);
            payload.put("sdkVersion", sdkVersion == null ? "" : sdkVersion);
            payload.put("productModel", productModel == null ? "" : productModel);
            Log.i(TAG, "DJI diagnostic event: " + type);
            UnityPlayer.UnitySendMessage(unityReceiver, "OnDjiSdkEvent", payload.toString());
        } catch (Throwable throwable) {
            Log.e(TAG, "Could not emit DJI diagnostic event", throwable);
        }
    }

    private static String safeMessage(Throwable throwable) {
        return throwable.getMessage() == null ? "no message" : throwable.getMessage();
    }
}
