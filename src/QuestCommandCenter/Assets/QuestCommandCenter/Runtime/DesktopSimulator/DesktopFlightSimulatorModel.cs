using System;
using UnityEngine;

namespace QuestCommandCenter.DesktopSimulator
{
    public enum SimulatorFlightPhase
    {
        Grounded,
        TakingOff,
        Airborne,
        ReturningHome,
        Landing
    }

    public struct DesktopControlSample
    {
        public DesktopControlSample(
            long sequence,
            float roll,
            float pitch,
            float yaw,
            float vertical,
            bool authorized,
            bool brake,
            bool takeover)
        {
            Sequence = sequence;
            Roll = ClampFinite(roll, nameof(roll));
            Pitch = ClampFinite(pitch, nameof(pitch));
            Yaw = ClampFinite(yaw, nameof(yaw));
            Vertical = ClampFinite(vertical, nameof(vertical));
            Authorized = authorized;
            Brake = brake;
            Takeover = takeover;
        }

        public long Sequence { get; }
        public float Roll { get; }
        public float Pitch { get; }
        public float Yaw { get; }
        public float Vertical { get; }
        public bool Authorized { get; }
        public bool Brake { get; }
        public bool Takeover { get; }

        public static DesktopControlSample Neutral(long sequence = 1)
        {
            return new DesktopControlSample(sequence, 0f, 0f, 0f, 0f, false, false, false);
        }

        private static float ClampFinite(float value, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(name);
            }
            return Mathf.Clamp(value, -1f, 1f);
        }
    }

    public sealed class DesktopFlightSimulatorState
    {
        public Vector3 PositionMeters { get; internal set; }
        public float HeadingDegrees { get; internal set; }
        public Vector3 VelocityMetersPerSecond { get; internal set; }
        public float YawRateDegreesPerSecond { get; internal set; }
        public float BatteryPercent { get; internal set; } = 100f;
        public bool ClientConnected { get; internal set; }
        public bool CommandAuthorized { get; internal set; }
        public bool CommandStale { get; internal set; } = true;
        public long LastSequence { get; internal set; }
        public int TakeoverCount { get; internal set; }
        public SimulatorFlightPhase FlightPhase { get; internal set; } = SimulatorFlightPhase.Airborne;
        public bool GeofenceLimited { get; internal set; }
    }

    /// <summary>
    /// Deterministic development kinematics. This validates application controls;
    /// it is not an aerodynamic or DJI flight-controller model.
    /// </summary>
    public sealed class DesktopFlightSimulatorModel
    {
        public const float MaximumHorizontalSpeedMetersPerSecond = 0.5f;
        public const float MaximumVerticalSpeedMetersPerSecond = 0.3f;
        public const float MaximumYawRateDegreesPerSecond = 20f;
        public const float MaximumAltitudeMeters = 2f;
        public const float MaximumRadiusMeters = 10f;
        public const long CommandTimeoutMilliseconds = 250;

        private DesktopControlSample command = DesktopControlSample.Neutral();
        private long lastCommandAtMilliseconds = -1;

        public DesktopFlightSimulatorModel(bool startAirborne = true)
        {
            State = new DesktopFlightSimulatorState
            {
                PositionMeters = startAirborne ? new Vector3(0f, 1f, 0f) : Vector3.zero,
                FlightPhase = startAirborne
                    ? SimulatorFlightPhase.Airborne
                    : SimulatorFlightPhase.Grounded
            };
        }

        public DesktopFlightSimulatorState State { get; }

        public void SetClientConnected(bool connected)
        {
            State.ClientConnected = connected;
            if (!connected)
            {
                Neutralize(true);
            }
        }

        public bool TryApplyControl(DesktopControlSample sample, long receivedAtMilliseconds)
        {
            if (receivedAtMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(receivedAtMilliseconds));
            }
            if (sample.Sequence <= State.LastSequence)
            {
                return false;
            }

            State.LastSequence = sample.Sequence;
            lastCommandAtMilliseconds = receivedAtMilliseconds;
            if (sample.Takeover)
            {
                State.TakeoverCount++;
                if (State.FlightPhase == SimulatorFlightPhase.ReturningHome)
                {
                    State.FlightPhase = SimulatorFlightPhase.Airborne;
                }
            }

            if (!sample.Authorized || sample.Brake)
            {
                command = DesktopControlSample.Neutral(sample.Sequence);
                State.CommandAuthorized = false;
            }
            else
            {
                command = sample;
                State.CommandAuthorized = true;
            }
            State.CommandStale = false;
            return true;
        }

        public void Step(float deltaSeconds, long nowMilliseconds)
        {
            if (float.IsNaN(deltaSeconds) || float.IsInfinity(deltaSeconds) || deltaSeconds < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
            }
            if (nowMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(nowMilliseconds));
            }

            if (lastCommandAtMilliseconds < 0
                || nowMilliseconds - lastCommandAtMilliseconds > CommandTimeoutMilliseconds)
            {
                Neutralize(true);
            }

            switch (State.FlightPhase)
            {
                case SimulatorFlightPhase.Grounded:
                    State.PositionMeters = new Vector3(
                        State.PositionMeters.x,
                        0f,
                        State.PositionMeters.z);
                    State.VelocityMetersPerSecond = Vector3.zero;
                    State.YawRateDegreesPerSecond = 0f;
                    break;
                case SimulatorFlightPhase.TakingOff:
                    StepVerticalAutomation(deltaSeconds, 1f, SimulatorFlightPhase.Airborne);
                    break;
                case SimulatorFlightPhase.Landing:
                    StepVerticalAutomation(deltaSeconds, 0f, SimulatorFlightPhase.Grounded);
                    break;
                case SimulatorFlightPhase.ReturningHome:
                    StepReturnHome(deltaSeconds);
                    break;
                default:
                    StepManual(deltaSeconds);
                    break;
            }

            if (State.ClientConnected && State.FlightPhase != SimulatorFlightPhase.Grounded)
            {
                State.BatteryPercent = Mathf.Max(0f, State.BatteryPercent - (deltaSeconds / 300f));
            }
        }

        public bool RequestTakeoff()
        {
            if (State.FlightPhase != SimulatorFlightPhase.Grounded)
            {
                return false;
            }
            State.FlightPhase = SimulatorFlightPhase.TakingOff;
            Neutralize(false);
            return true;
        }

        public bool RequestLand()
        {
            if (State.FlightPhase == SimulatorFlightPhase.Grounded
                || State.FlightPhase == SimulatorFlightPhase.Landing)
            {
                return false;
            }
            State.FlightPhase = SimulatorFlightPhase.Landing;
            Neutralize(false);
            return true;
        }

        public bool RequestReturnHome()
        {
            if (State.FlightPhase != SimulatorFlightPhase.Airborne)
            {
                return false;
            }
            State.FlightPhase = SimulatorFlightPhase.ReturningHome;
            Neutralize(false);
            return true;
        }

        public void ResetAirborne()
        {
            State.PositionMeters = new Vector3(0f, 1f, 0f);
            State.HeadingDegrees = 0f;
            State.VelocityMetersPerSecond = Vector3.zero;
            State.YawRateDegreesPerSecond = 0f;
            State.BatteryPercent = 100f;
            State.CommandAuthorized = false;
            State.CommandStale = true;
            State.LastSequence = 0;
            State.TakeoverCount = 0;
            State.FlightPhase = SimulatorFlightPhase.Airborne;
            State.GeofenceLimited = false;
            command = DesktopControlSample.Neutral();
            lastCommandAtMilliseconds = -1;
        }

        private void StepManual(float deltaSeconds)
        {
            var yawRate = command.Yaw * MaximumYawRateDegreesPerSecond;
            State.HeadingDegrees = RepeatDegrees(State.HeadingDegrees + yawRate * deltaSeconds);
            var rotation = Quaternion.Euler(0f, State.HeadingDegrees, 0f);
            var localVelocity = new Vector3(
                command.Roll * MaximumHorizontalSpeedMetersPerSecond,
                command.Vertical * MaximumVerticalSpeedMetersPerSecond,
                command.Pitch * MaximumHorizontalSpeedMetersPerSecond);
            var worldHorizontal = rotation * new Vector3(localVelocity.x, 0f, localVelocity.z);
            var velocity = new Vector3(worldHorizontal.x, localVelocity.y, worldHorizontal.z);

            var next = State.PositionMeters + velocity * deltaSeconds;
            next.y = Mathf.Clamp(next.y, 0f, MaximumAltitudeMeters);
            if ((next.y <= 0f && velocity.y < 0f)
                || (next.y >= MaximumAltitudeMeters && velocity.y > 0f))
            {
                velocity.y = 0f;
            }

            var horizontal = new Vector2(next.x, next.z);
            State.GeofenceLimited = horizontal.magnitude > MaximumRadiusMeters;
            if (State.GeofenceLimited)
            {
                horizontal = horizontal.normalized * MaximumRadiusMeters;
                next.x = horizontal.x;
                next.z = horizontal.y;
                velocity.x = 0f;
                velocity.z = 0f;
            }

            State.PositionMeters = next;
            State.VelocityMetersPerSecond = velocity;
            State.YawRateDegreesPerSecond = yawRate;
        }

        private void StepVerticalAutomation(
            float deltaSeconds,
            float targetAltitude,
            SimulatorFlightPhase completedPhase)
        {
            var difference = targetAltitude - State.PositionMeters.y;
            var step = Mathf.Sign(difference) * MaximumVerticalSpeedMetersPerSecond * deltaSeconds;
            if (Mathf.Abs(step) >= Mathf.Abs(difference))
            {
                State.PositionMeters = new Vector3(
                    State.PositionMeters.x,
                    targetAltitude,
                    State.PositionMeters.z);
                State.VelocityMetersPerSecond = Vector3.zero;
                State.FlightPhase = completedPhase;
                return;
            }

            State.PositionMeters += Vector3.up * step;
            State.VelocityMetersPerSecond = Vector3.up * Mathf.Sign(difference) * MaximumVerticalSpeedMetersPerSecond;
            State.YawRateDegreesPerSecond = 0f;
        }

        private void StepReturnHome(float deltaSeconds)
        {
            var horizontal = new Vector3(-State.PositionMeters.x, 0f, -State.PositionMeters.z);
            if (horizontal.magnitude <= 0.08f)
            {
                State.PositionMeters = new Vector3(0f, State.PositionMeters.y, 0f);
                State.FlightPhase = SimulatorFlightPhase.Landing;
                State.VelocityMetersPerSecond = Vector3.zero;
                return;
            }

            var direction = horizontal.normalized;
            var speed = Mathf.Min(MaximumHorizontalSpeedMetersPerSecond, horizontal.magnitude / Math.Max(deltaSeconds, 0.001f));
            var velocity = direction * speed;
            State.PositionMeters += velocity * deltaSeconds;
            State.VelocityMetersPerSecond = velocity;
            State.HeadingDegrees = RepeatDegrees(Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg);
            State.YawRateDegreesPerSecond = 0f;
        }

        private void Neutralize(bool stale)
        {
            command = DesktopControlSample.Neutral(Math.Max(1, State.LastSequence));
            State.CommandAuthorized = false;
            State.CommandStale = stale;
            State.VelocityMetersPerSecond = Vector3.zero;
            State.YawRateDegreesPerSecond = 0f;
        }

        private static float RepeatDegrees(float value)
        {
            value %= 360f;
            return value < 0f ? value + 360f : value;
        }
    }
}
