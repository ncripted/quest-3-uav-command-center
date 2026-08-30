using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace QuestCommandCenter.DesktopSimulator
{
    public sealed class DesktopSimulatorApp : MonoBehaviour
    {
        private enum CameraView
        {
            Chase,
            Fpv,
            TopDown,
            Orbit
        }

        private readonly List<Vector3> trailPoints = new List<Vector3>();
        private DesktopFlightSimulatorModel model;
        private DesktopSimulatorTcpServer server;
        private Transform droneVisual;
        private Transform gimbalVisual;
        private Transform[] propellers;
        private Camera simulatorCamera;
        private LineRenderer trail;
        private Text telemetryText;
        private Text linkText;
        private Text alertText;
        private Text cameraText;
        private CameraView cameraView;
        private long observedInboundVersion;
        private bool observedConnection;
        private float nextTelemetryAt;
        private float nextTrailAt;
        private float cameraOrbitDegrees;
        private string lastAction = "Simulator initialized grounded at home";
        private Material darkMaterial;
        private Material bodyMaterial;
        private Material accentMaterial;
        private Material greenMaterial;
        private Material redMaterial;
        private Material glassMaterial;
        private Material greenLineMaterial;
        private Material cyanLineMaterial;
        private Material groundMaterial;
        private Material roadMaterial;

        private void Awake()
        {
            Application.targetFrameRate = 60;
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 1;
            model = new DesktopFlightSimulatorModel(startAirborne: false);
            server = gameObject.AddComponent<DesktopSimulatorTcpServer>();
            CreateMaterials();
            CreateWorld();
            CreateDrone();
            CreateCamera();
            CreateHud();
        }

        private void Update()
        {
            var nowMilliseconds = MonotonicMilliseconds();
            ProcessConnection();
            ProcessNetworkCommands(nowMilliseconds);
            ProcessKeyboard();
            model.Step(Mathf.Min(Time.unscaledDeltaTime, 0.1f), nowMilliseconds);
            UpdateDroneVisual();
            UpdateCamera();
            UpdateTrail();
            UpdateHud();

            if (Time.unscaledTime >= nextTelemetryAt)
            {
                nextTelemetryAt = Time.unscaledTime + 0.1f;
                server.QueueTelemetry(CreateTelemetryJson());
            }
        }

        private void ProcessConnection()
        {
            if (server.IsClientConnected == observedConnection)
            {
                return;
            }
            observedConnection = server.IsClientConnected;
            model.SetClientConnected(observedConnection);
            lastAction = observedConnection
                ? "Quest controller relay connected through USB/ADB"
                : "Quest relay disconnected — movement neutralized";
        }

        private void ProcessNetworkCommands(long nowMilliseconds)
        {
            var processed = 0;
            while (processed++ < 16
                && server.TryReadLatest(ref observedInboundVersion, out var json))
            {
                try
                {
                    var message = JsonUtility.FromJson<ControlMessageDto>(json);
                    if (message == null || message.schemaVersion != 1 || message.sequence <= 0)
                    {
                        lastAction = "Rejected malformed controller message";
                        continue;
                    }

                    if (message.type == "simulator_action")
                    {
                        ProcessSimulatorAction(message.action);
                        continue;
                    }

                    if (message.type != "manual_control"
                        || !AxisValid(message.roll)
                        || !AxisValid(message.pitch)
                        || !AxisValid(message.yaw)
                        || !AxisValid(message.vertical))
                    {
                        lastAction = "Rejected malformed controller message";
                        continue;
                    }

                    var sample = new DesktopControlSample(
                        message.sequence,
                        message.roll,
                        message.pitch,
                        message.yaw,
                        message.vertical,
                        message.authorized,
                        message.brake,
                        message.takeover);
                    if (model.TryApplyControl(sample, nowMilliseconds) && message.takeover)
                    {
                        lastAction = "Manual takeover received — autonomous state cancelled";
                    }
                }
                catch (Exception exception)
                {
                    lastAction = "Rejected controller message: " + exception.Message;
                }
            }
        }

        private void ProcessSimulatorAction(string action)
        {
            switch (action)
            {
                case "takeoff":
                    lastAction = model.RequestTakeoff()
                        ? "Quest confirmed simulated takeoff"
                        : "Simulated takeoff rejected by current phase";
                    break;
                case "land":
                    lastAction = model.RequestLand()
                        ? "Quest confirmed simulated landing"
                        : "Simulated landing rejected by current phase";
                    break;
                case "return_home":
                    lastAction = model.RequestReturnHome()
                        ? "Quest confirmed simulated RTH"
                        : "Simulated RTH rejected by current phase";
                    break;
                default:
                    lastAction = "Rejected unknown simulator action";
                    break;
            }
        }

        private void ProcessKeyboard()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }
            if (keyboard.cKey.wasPressedThisFrame)
            {
                cameraView = (CameraView)(((int)cameraView + 1) % 4);
            }
            if (keyboard.digit1Key.wasPressedThisFrame) cameraView = CameraView.Chase;
            if (keyboard.digit2Key.wasPressedThisFrame) cameraView = CameraView.Fpv;
            if (keyboard.digit3Key.wasPressedThisFrame) cameraView = CameraView.TopDown;
            if (keyboard.digit4Key.wasPressedThisFrame) cameraView = CameraView.Orbit;

            if (keyboard.tKey.wasPressedThisFrame)
            {
                lastAction = model.RequestTakeoff()
                    ? "Simulated takeoff started"
                    : "Takeoff rejected: simulated drone is not grounded";
            }
            if (keyboard.lKey.wasPressedThisFrame)
            {
                lastAction = model.RequestLand()
                    ? "Simulated landing started"
                    : "Landing rejected: already grounded or landing";
            }
            if (keyboard.hKey.wasPressedThisFrame)
            {
                lastAction = model.RequestReturnHome()
                    ? "Simulated RTH started"
                    : "RTH rejected: requires normal airborne state";
            }
            if (keyboard.rKey.wasPressedThisFrame)
            {
                model.ResetAirborne();
                trailPoints.Clear();
                lastAction = "Simulator reset airborne at home";
            }
            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                Application.Quit();
            }
        }

        private void UpdateDroneVisual()
        {
            var state = model.State;
            droneVisual.position = state.PositionMeters + Vector3.up * 0.16f;
            var yaw = Quaternion.Euler(0f, state.HeadingDegrees, 0f);
            var localVelocity = Quaternion.Inverse(yaw) * state.VelocityMetersPerSecond;
            var pitchTilt = -Mathf.Clamp(
                localVelocity.z / DesktopFlightSimulatorModel.MaximumHorizontalSpeedMetersPerSecond,
                -1f,
                1f) * 12f;
            var rollTilt = -Mathf.Clamp(
                localVelocity.x / DesktopFlightSimulatorModel.MaximumHorizontalSpeedMetersPerSecond,
                -1f,
                1f) * 12f;
            droneVisual.rotation = Quaternion.Euler(pitchTilt, state.HeadingDegrees, rollTilt);

            var rotorsActive = state.FlightPhase != SimulatorFlightPhase.Grounded;
            if (rotorsActive)
            {
                var rotation = 1100f * Time.unscaledDeltaTime;
                for (var index = 0; index < propellers.Length; index++)
                {
                    propellers[index].Rotate(0f, index % 2 == 0 ? rotation : -rotation, 0f, Space.Self);
                }
            }
            gimbalVisual.localRotation = Quaternion.Euler(-12f, 0f, 0f);
        }

        private void UpdateCamera()
        {
            var position = model.State.PositionMeters;
            var yaw = Quaternion.Euler(0f, model.State.HeadingDegrees, 0f);
            Vector3 targetPosition;
            Vector3 lookTarget;

            switch (cameraView)
            {
                case CameraView.Fpv:
                    targetPosition = position + yaw * new Vector3(0f, 0.05f, 0.42f);
                    lookTarget = targetPosition + yaw * (Quaternion.Euler(10f, 0f, 0f) * Vector3.forward) * 20f;
                    break;
                case CameraView.TopDown:
                    targetPosition = position + new Vector3(0f, 15f, -0.01f);
                    lookTarget = position;
                    break;
                case CameraView.Orbit:
                    cameraOrbitDegrees = Mathf.Repeat(cameraOrbitDegrees + 12f * Time.unscaledDeltaTime, 360f);
                    var orbit = Quaternion.Euler(20f, cameraOrbitDegrees, 0f) * new Vector3(0f, 1.0f, -3.1f);
                    targetPosition = position + orbit;
                    lookTarget = position + Vector3.up * 0.2f;
                    break;
                default:
                    targetPosition = position + yaw * new Vector3(0f, 1.0f, -2.65f);
                    lookTarget = position + Vector3.up * 0.15f;
                    break;
            }

            var smooth = cameraView == CameraView.Fpv ? 1f : 1f - Mathf.Exp(-8f * Time.unscaledDeltaTime);
            simulatorCamera.transform.position = Vector3.Lerp(simulatorCamera.transform.position, targetPosition, smooth);
            simulatorCamera.transform.rotation = Quaternion.Slerp(
                simulatorCamera.transform.rotation,
                Quaternion.LookRotation(lookTarget - targetPosition, Vector3.up),
                smooth);
        }

        private void UpdateTrail()
        {
            if (Time.unscaledTime < nextTrailAt)
            {
                return;
            }
            nextTrailAt = Time.unscaledTime + 0.08f;
            var point = model.State.PositionMeters + Vector3.up * 0.04f;
            if (trailPoints.Count == 0 || Vector3.Distance(trailPoints[trailPoints.Count - 1], point) >= 0.03f)
            {
                trailPoints.Add(point);
                if (trailPoints.Count > 500)
                {
                    trailPoints.RemoveAt(0);
                }
                trail.positionCount = trailPoints.Count;
                trail.SetPositions(trailPoints.ToArray());
            }
        }

        private void UpdateHud()
        {
            var state = model.State;
            var horizontalSpeed = new Vector2(
                state.VelocityMetersPerSecond.x,
                state.VelocityMetersPerSecond.z).magnitude;
            linkText.text = "SIMULATION ONLY   |   QUEST "
                + (state.ClientConnected ? "CONNECTED" : "WAITING")
                + "   |   " + state.FlightPhase.ToString().ToUpperInvariant()
                + "   |   BATTERY " + state.BatteryPercent.ToString("0.0") + "%";
            linkText.color = state.ClientConnected
                ? new Color(0.55f, 1f, 0.72f)
                : new Color(1f, 0.78f, 0.35f);

            telemetryText.text =
                "AIRCRAFT TELEMETRY\n"
                + "Control        " + (state.CommandAuthorized ? "AUTHORIZED" : "NEUTRAL / HOVER") + "\n"
                + "Input          " + (state.CommandStale ? "STALE" : "FRESH") + "\n"
                + "Sequence       " + state.LastSequence + "\n"
                + "Takeovers      " + state.TakeoverCount + "\n\n"
                + "Altitude       " + state.PositionMeters.y.ToString("0.00") + " m\n"
                + "Heading        " + state.HeadingDegrees.ToString("0.0") + "°\n"
                + "Horizontal     " + horizontalSpeed.ToString("0.00") + " m/s\n"
                + "Vertical       " + state.VelocityMetersPerSecond.y.ToString("0.00") + " m/s\n"
                + "North          " + state.PositionMeters.z.ToString("0.00") + " m\n"
                + "East           " + state.PositionMeters.x.ToString("0.00") + " m\n"
                + "Home distance  " + new Vector2(state.PositionMeters.x, state.PositionMeters.z).magnitude.ToString("0.00") + " m\n"
                + "Geofence       " + (state.GeofenceLimited ? "LIMITING" : "CLEAR");
            cameraText.text = "VIEW  " + cameraView.ToString().ToUpperInvariant()
                + "   |   1 CHASE   2 FPV   3 TOP   4 ORBIT   C CYCLE";
            alertText.text = lastAction;
            alertText.color = state.GeofenceLimited
                ? new Color(1f, 0.34f, 0.25f)
                : new Color(0.75f, 0.9f, 1f);
        }

        private string CreateTelemetryJson()
        {
            var state = model.State;
            return JsonUtility.ToJson(new TelemetryDto
            {
                schemaVersion = 1,
                type = "simulator_telemetry",
                north_meters = state.PositionMeters.z,
                east_meters = state.PositionMeters.x,
                altitude_meters = state.PositionMeters.y,
                heading_degrees = state.HeadingDegrees,
                north_velocity_mps = state.VelocityMetersPerSecond.z,
                east_velocity_mps = state.VelocityMetersPerSecond.x,
                vertical_velocity_mps = state.VelocityMetersPerSecond.y,
                yaw_rate_dps = state.YawRateDegreesPerSecond,
                battery_percent = state.BatteryPercent,
                connected = state.ClientConnected,
                command_authorized = state.CommandAuthorized,
                command_stale = state.CommandStale,
                last_sequence = state.LastSequence,
                takeover_count = state.TakeoverCount,
                flight_phase = state.FlightPhase.ToString(),
                geofence_limited = state.GeofenceLimited
            });
        }

        private void CreateMaterials()
        {
            darkMaterial = Material(new Color(0.035f, 0.05f, 0.065f), 0.15f, 0.75f);
            bodyMaterial = Material(new Color(0.76f, 0.79f, 0.82f), 0.55f, 0.25f);
            accentMaterial = Material(new Color(0.12f, 0.16f, 0.19f), 0.35f, 0.55f);
            greenMaterial = Material(new Color(0.2f, 1f, 0.42f), 0.15f, 0.45f);
            redMaterial = Material(new Color(1f, 0.12f, 0.08f), 0.15f, 0.45f);
            glassMaterial = Material(new Color(0.025f, 0.12f, 0.18f), 0.75f, 0.15f);
            greenLineMaterial = UnlitMaterial(new Color(0.25f, 1f, 0.42f, 0.95f));
            cyanLineMaterial = UnlitMaterial(new Color(0.2f, 0.78f, 1f, 0.9f));
            groundMaterial = Material(new Color(0.14f, 0.3f, 0.16f), 0f, 0.95f);
            roadMaterial = Material(new Color(0.1f, 0.11f, 0.12f), 0f, 0.8f);
        }

        private void CreateWorld()
        {
            RenderSettings.ambientLight = new Color(0.38f, 0.42f, 0.48f);
            RenderSettings.fog = true;
            RenderSettings.fogColor = new Color(0.52f, 0.67f, 0.78f);
            RenderSettings.fogDensity = 0.006f;

            var lightObject = new GameObject("Sun");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            light.color = new Color(1f, 0.94f, 0.82f);
            light.shadows = LightShadows.Soft;
            lightObject.transform.rotation = Quaternion.Euler(42f, -32f, 0f);

            CreatePrimitive("Ground", PrimitiveType.Plane, Vector3.zero, new Vector3(5f, 1f, 5f), groundMaterial);
            CreatePrimitive("Road North South", PrimitiveType.Cube, new Vector3(8f, 0.015f, 0f), new Vector3(3f, 0.03f, 48f), roadMaterial);
            CreatePrimitive("Road East West", PrimitiveType.Cube, new Vector3(0f, 0.018f, -9f), new Vector3(48f, 0.03f, 3f), roadMaterial);

            var pad = CreatePrimitive("Home Landing Pad", PrimitiveType.Cylinder, Vector3.zero, new Vector3(1.25f, 0.025f, 1.25f), darkMaterial);
            var homeRing = pad.AddComponent<LineRenderer>();
            ConfigureLine(homeRing, greenLineMaterial, 0.05f);
            SetCircle(homeRing, 0.72f, 48, 0.055f);

            var geofenceObject = new GameObject("Development Geofence");
            var geofence = geofenceObject.AddComponent<LineRenderer>();
            ConfigureLine(geofence, greenLineMaterial, 0.035f);
            SetCircle(geofence, DesktopFlightSimulatorModel.MaximumRadiusMeters, 96, 0.04f);

            var trailObject = new GameObject("Flight Trail");
            trail = trailObject.AddComponent<LineRenderer>();
            ConfigureLine(trail, cyanLineMaterial, 0.045f);

            CreateBuildings();
            CreateTrees();
        }

        private void CreateBuildings()
        {
            var positions = new[]
            {
                new Vector3(-8f, 1.5f, 7f), new Vector3(-12f, 2.3f, 2f),
                new Vector3(13f, 1.8f, 8f), new Vector3(14f, 3f, -4f),
                new Vector3(-11f, 1.2f, -12f), new Vector3(3f, 2f, 15f)
            };
            for (var index = 0; index < positions.Length; index++)
            {
                var height = positions[index].y * 2f;
                var building = CreatePrimitive(
                    "Building " + index,
                    PrimitiveType.Cube,
                    positions[index],
                    new Vector3(3.2f + (index % 2), height, 3f),
                    Material(new Color(0.22f + index * 0.025f, 0.27f, 0.31f), 0.1f, 0.75f));
                for (var floor = 1; floor < Mathf.FloorToInt(height); floor++)
                {
                    CreatePrimitive(
                        "Window Strip",
                        PrimitiveType.Cube,
                        building.transform.position + new Vector3(0f, -height * 0.5f + floor, -1.51f),
                        new Vector3(building.transform.localScale.x * 0.72f, 0.18f, 0.025f),
                        glassMaterial);
                }
            }
        }

        private void CreateTrees()
        {
            var positions = new[]
            {
                new Vector3(-5f, 0f, 11f), new Vector3(-15f, 0f, 10f),
                new Vector3(16f, 0f, 13f), new Vector3(5f, 0f, -15f),
                new Vector3(-6f, 0f, -17f), new Vector3(18f, 0f, -13f),
                new Vector3(-18f, 0f, -4f), new Vector3(12f, 0f, 17f)
            };
            foreach (var position in positions)
            {
                CreatePrimitive("Tree Trunk", PrimitiveType.Cylinder, position + Vector3.up * 0.8f, new Vector3(0.16f, 0.8f, 0.16f), roadMaterial);
                CreatePrimitive("Tree Crown", PrimitiveType.Sphere, position + Vector3.up * 2f, new Vector3(1.1f, 1.5f, 1.1f), groundMaterial);
            }
        }

        private void CreateDrone()
        {
            droneVisual = new GameObject("Simulated Spark Visual").transform;
            var shell = CreatePrimitive("Upper Shell", PrimitiveType.Sphere, Vector3.zero, new Vector3(0.52f, 0.16f, 0.34f), bodyMaterial, droneVisual);
            shell.transform.localPosition = new Vector3(0f, 0.02f, 0f);
            var belly = CreatePrimitive("Lower Body", PrimitiveType.Cube, Vector3.zero, new Vector3(0.42f, 0.12f, 0.3f), accentMaterial, droneVisual);
            belly.transform.localPosition = new Vector3(0f, -0.08f, 0f);

            var motors = new[]
            {
                new Vector3(-0.48f, 0.02f, 0.34f), new Vector3(0.48f, 0.02f, 0.34f),
                new Vector3(-0.48f, 0.02f, -0.34f), new Vector3(0.48f, 0.02f, -0.34f)
            };
            propellers = new Transform[motors.Length];
            for (var index = 0; index < motors.Length; index++)
            {
                CreateBeam(droneVisual, Vector3.zero, motors[index], 0.09f, bodyMaterial);
                var motor = CreatePrimitive("Motor " + index, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.13f, 0.06f, 0.13f), accentMaterial, droneVisual);
                motor.transform.localPosition = motors[index];
                var rotor = new GameObject("Propeller " + index).transform;
                rotor.SetParent(droneVisual, false);
                rotor.localPosition = motors[index] + Vector3.up * 0.09f;
                var bladeA = CreatePrimitive("Blade A", PrimitiveType.Cube, Vector3.zero, new Vector3(0.58f, 0.012f, 0.035f), darkMaterial, rotor);
                bladeA.transform.localPosition = Vector3.zero;
                var bladeB = CreatePrimitive("Blade B", PrimitiveType.Cube, Vector3.zero, new Vector3(0.035f, 0.012f, 0.58f), darkMaterial, rotor);
                bladeB.transform.localPosition = Vector3.zero;
                propellers[index] = rotor;

                var led = CreatePrimitive("LED " + index, PrimitiveType.Sphere, Vector3.zero, Vector3.one * 0.045f, index < 2 ? redMaterial : greenMaterial, droneVisual);
                led.transform.localPosition = motors[index] + Vector3.down * 0.075f;
            }

            var gimbalMount = new GameObject("Gimbal Mount").transform;
            gimbalMount.SetParent(droneVisual, false);
            gimbalMount.localPosition = new Vector3(0f, -0.14f, 0.24f);
            gimbalVisual = gimbalMount;
            var cameraBody = CreatePrimitive("Gimbal Camera", PrimitiveType.Cube, Vector3.zero, new Vector3(0.18f, 0.13f, 0.13f), darkMaterial, gimbalMount);
            cameraBody.transform.localPosition = Vector3.zero;
            var lens = CreatePrimitive("Camera Lens", PrimitiveType.Cylinder, Vector3.zero, new Vector3(0.055f, 0.035f, 0.055f), glassMaterial, gimbalMount);
            lens.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            lens.transform.localPosition = new Vector3(0f, 0f, 0.08f);

            var leftGear = CreatePrimitive("Left Landing Gear", PrimitiveType.Cube, Vector3.zero, new Vector3(0.32f, 0.035f, 0.05f), accentMaterial, droneVisual);
            leftGear.transform.localPosition = new Vector3(-0.22f, -0.18f, 0f);
            var rightGear = CreatePrimitive("Right Landing Gear", PrimitiveType.Cube, Vector3.zero, new Vector3(0.32f, 0.035f, 0.05f), accentMaterial, droneVisual);
            rightGear.transform.localPosition = new Vector3(0.22f, -0.18f, 0f);
        }

        private void CreateCamera()
        {
            var cameraObject = new GameObject("Simulator Camera");
            simulatorCamera = cameraObject.AddComponent<Camera>();
            simulatorCamera.clearFlags = CameraClearFlags.SolidColor;
            simulatorCamera.backgroundColor = new Color(0.44f, 0.65f, 0.82f);
            simulatorCamera.fieldOfView = 65f;
            simulatorCamera.nearClipPlane = 0.03f;
            simulatorCamera.farClipPlane = 250f;
            cameraObject.AddComponent<AudioListener>();
            simulatorCamera.transform.position = new Vector3(0f, 2.5f, -4f);
            simulatorCamera.transform.LookAt(Vector3.up);
        }

        private void CreateHud()
        {
            var canvasObject = new GameObject("Simulator HUD", typeof(RectTransform));
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600f, 900f);

            var top = Panel(canvasObject.transform, "Top Status", new Color(0.015f, 0.025f, 0.035f, 0.92f));
            SetAnchors(top.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -64f), Vector2.zero);
            linkText = Label(top.transform, 23, TextAnchor.MiddleLeft);
            Stretch(linkText.rectTransform, 22f, 8f, 22f, 8f);

            var left = Panel(canvasObject.transform, "Telemetry Panel", new Color(0.015f, 0.025f, 0.035f, 0.82f));
            SetAnchors(left.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(18f, -235f), new Vector2(365f, 235f));
            telemetryText = Label(left.transform, 19, TextAnchor.UpperLeft);
            Stretch(telemetryText.rectTransform, 18f, 18f, 18f, 18f);
            telemetryText.color = new Color(0.88f, 0.95f, 1f);

            var right = Panel(canvasObject.transform, "Controls Panel", new Color(0.015f, 0.025f, 0.035f, 0.82f));
            SetAnchors(right.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-365f, -235f), new Vector2(-18f, 235f));
            var controls = Label(right.transform, 18, TextAnchor.UpperLeft);
            Stretch(controls.rectTransform, 18f, 18f, 18f, 18f);
            controls.color = new Color(0.88f, 0.95f, 1f);
            controls.text =
                "QUEST TOUCH — MODE 2\n"
                + "Right Grip   Flight dead-man\n"
                + "Right Stick  Forward / strafe\n"
                + "Left Stick   Altitude / yaw\n"
                + "X hold       Return home\n"
                + "A hold       Land\n"
                + "B            Brake / hover\n"
                + "Left Grip    Manual takeover\n\n"
                + "DESKTOP ACTIONS\n"
                + "T   Takeoff     L   Land\n"
                + "H   Return home R   Reset\n"
                + "C   Cycle camera\n"
                + "Esc Quit\n\n"
                + "Kinematic development model\n"
                + "Not aerodynamically accurate";

            var bottom = Panel(canvasObject.transform, "Bottom Status", new Color(0.015f, 0.025f, 0.035f, 0.9f));
            SetAnchors(bottom.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, 82f));
            cameraText = Label(bottom.transform, 19, TextAnchor.UpperCenter);
            SetAnchors(cameraText.rectTransform, Vector2.zero, Vector2.one, new Vector2(10f, 38f), new Vector2(-10f, -4f));
            alertText = Label(bottom.transform, 17, TextAnchor.LowerCenter);
            SetAnchors(alertText.rectTransform, Vector2.zero, Vector2.one, new Vector2(10f, 4f), new Vector2(-10f, -40f));

            var crosshair = Label(canvasObject.transform, 38, TextAnchor.MiddleCenter);
            SetAnchors(crosshair.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-30f, -30f), new Vector2(30f, 30f));
            crosshair.text = "+";
            crosshair.color = new Color(0.4f, 1f, 0.55f, 0.85f);
        }

        private GameObject CreatePrimitive(
            string name,
            PrimitiveType type,
            Vector3 position,
            Vector3 scale,
            Material material,
            Transform parent = null)
        {
            var result = GameObject.CreatePrimitive(type);
            result.name = name;
            if (parent != null)
            {
                result.transform.SetParent(parent, false);
                result.transform.localPosition = position;
            }
            else
            {
                result.transform.position = position;
            }
            result.transform.localScale = scale;
            result.GetComponent<Renderer>().material = material;
            var collider = result.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }
            return result;
        }

        private void CreateBeam(Transform parent, Vector3 start, Vector3 end, float width, Material material)
        {
            var beam = CreatePrimitive("Drone Arm", PrimitiveType.Cube, Vector3.zero, Vector3.one, material, parent);
            var difference = end - start;
            beam.transform.localPosition = (start + end) * 0.5f;
            beam.transform.localRotation = Quaternion.FromToRotation(Vector3.right, difference.normalized);
            beam.transform.localScale = new Vector3(difference.magnitude, width, width);
        }

        private static void ConfigureLine(LineRenderer line, Material material, float width)
        {
            line.material = material;
            line.widthMultiplier = width;
            line.useWorldSpace = true;
            line.loop = false;
            line.numCornerVertices = 2;
        }

        private static void SetCircle(LineRenderer line, float radius, int segments, float altitude)
        {
            line.positionCount = segments + 1;
            for (var index = 0; index <= segments; index++)
            {
                var angle = index * Mathf.PI * 2f / segments;
                line.SetPosition(index, new Vector3(Mathf.Sin(angle) * radius, altitude, Mathf.Cos(angle) * radius));
            }
        }

        private static Material Material(Color color, float metallic, float smoothness)
        {
            var shader = Resources.Load<Shader>("QccSimulatorLit");
            if (shader == null)
            {
                throw new InvalidOperationException("Bundled simulator shader is unavailable.");
            }
            var material = new Material(shader)
            {
                color = color
            };
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Glossiness", smoothness);
            return material;
        }

        private static Material UnlitMaterial(Color color)
        {
            var shader = Resources.Load<Shader>("QccSimulatorUnlit");
            if (shader == null)
            {
                throw new InvalidOperationException("Bundled simulator line shader is unavailable.");
            }
            return new Material(shader)
            {
                color = color
            };
        }

        private static Image Panel(Transform parent, string name, Color color)
        {
            var panel = new GameObject(name, typeof(RectTransform));
            panel.transform.SetParent(parent, false);
            var image = panel.AddComponent<Image>();
            image.color = color;
            return image;
        }

        private static Text Label(Transform parent, int fontSize, TextAnchor alignment)
        {
            var label = new GameObject("Text", typeof(RectTransform));
            label.transform.SetParent(parent, false);
            var text = label.AddComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.color = Color.white;
            return text;
        }

        private static void Stretch(RectTransform rect, float left, float bottom, float right, float top)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        private static void SetAnchors(
            RectTransform rect,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private static bool AxisValid(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= -1f && value <= 1f;
        }

        private static long MonotonicMilliseconds()
        {
            return (long)(Time.realtimeSinceStartupAsDouble * 1000.0);
        }

        [Serializable]
        private sealed class ControlMessageDto
        {
            public int schemaVersion;
            public string type;
            public long sequence;
            public float roll;
            public float pitch;
            public float yaw;
            public float vertical;
            public bool authorized;
            public bool brake;
            public bool takeover;
            public string action;
        }

        [Serializable]
        private sealed class TelemetryDto
        {
            public int schemaVersion;
            public string type;
            public float north_meters;
            public float east_meters;
            public float altitude_meters;
            public float heading_degrees;
            public float north_velocity_mps;
            public float east_velocity_mps;
            public float vertical_velocity_mps;
            public float yaw_rate_dps;
            public float battery_percent;
            public bool connected;
            public bool command_authorized;
            public bool command_stale;
            public long last_sequence;
            public int takeover_count;
            public string flight_phase;
            public bool geofence_limited;
        }
    }
}
