using System;

namespace QuestCommandCenter.VehicleCore
{
    public enum FlightSafetyReason
    {
        None,
        Disconnected,
        FailsafeMode,
        AuthorityMismatch,
        CommandExpired,
        ControllerInputStale,
        TelemetryUnavailable,
        TelemetryStale,
        ManualAuthorizationMissing,
        InvalidControlMode,
        InvalidFlightState,
        CriticalBattery,
        IndoorProfileLimit,
        DevelopmentAltitudeLimit
    }

    public enum FlightSafetyDisposition
    {
        Accepted,
        Limited,
        Neutralized
    }

    public sealed class IndoorDevelopmentSafetyProfile
    {
        public float MaxHorizontalCommand { get; set; } = 0.35f;
        public float MaxVerticalCommand { get; set; } = 0.25f;
        public float MaxYawCommand { get; set; } = 0.30f;
        public float MaxRelativeAltitudeMeters { get; set; } = 2f;
        public int CriticalBatteryPercent { get; set; } = 20;
        public long ControllerStaleTimeoutMilliseconds { get; set; } = 150;
        public long TelemetryFailsafeTimeoutMilliseconds { get; set; } = 1000;

        public void Validate()
        {
            ValidateNormalizedLimit(MaxHorizontalCommand, nameof(MaxHorizontalCommand));
            ValidateNormalizedLimit(MaxVerticalCommand, nameof(MaxVerticalCommand));
            ValidateNormalizedLimit(MaxYawCommand, nameof(MaxYawCommand));
            if (float.IsNaN(MaxRelativeAltitudeMeters)
                || float.IsInfinity(MaxRelativeAltitudeMeters)
                || MaxRelativeAltitudeMeters <= 0f)
            {
                throw new InvalidOperationException("Maximum relative altitude must be finite and positive.");
            }
            if (CriticalBatteryPercent < 1 || CriticalBatteryPercent > 100)
            {
                throw new InvalidOperationException("Critical battery percentage must be between 1 and 100.");
            }
            if (ControllerStaleTimeoutMilliseconds <= 0 || TelemetryFailsafeTimeoutMilliseconds <= 0)
            {
                throw new InvalidOperationException("Safety timeouts must be positive.");
            }
        }

        private static void ValidateNormalizedLimit(float value, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f || value > 1f)
            {
                throw new InvalidOperationException(name + " must be within (0, 1].");
            }
        }
    }

    public sealed class FlightSafetyContext
    {
        public long MonotonicMilliseconds { get; set; }
        public long AuthorityEpoch { get; set; }
        public ControlMode ControlMode { get; set; }
        public bool IsConnected { get; set; }
        public bool HasTelemetry { get; set; }
        public long TelemetryAgeMilliseconds { get; set; }
        public bool HasFlightState { get; set; }
        public bool HasBatteryState { get; set; }
        public int BatteryPercent { get; set; }
        public float RelativeAltitudeMeters { get; set; }
    }

    public sealed class FlightSafetyDecision
    {
        public FlightSafetyDecision(
            FlightSafetyDisposition disposition,
            FlightSafetyReason reason,
            NormalizedFlightCommand command)
        {
            Disposition = disposition;
            Reason = reason;
            Command = command;
        }

        public FlightSafetyDisposition Disposition { get; }
        public FlightSafetyReason Reason { get; }
        public NormalizedFlightCommand Command { get; }
    }

    /// <summary>
    /// Deterministic safety gate for normalized commands. It has no dispatch
    /// capability; all rejected input becomes neutral rather than being cached.
    /// </summary>
    public sealed class FlightSafetyManager
    {
        private readonly IndoorDevelopmentSafetyProfile profile;

        public FlightSafetyManager(IndoorDevelopmentSafetyProfile profile)
        {
            this.profile = profile ?? throw new ArgumentNullException(nameof(profile));
            profile.Validate();
        }

        public FlightSafetyDecision Evaluate(FlightCommandEnvelope envelope, FlightSafetyContext context)
        {
            if (envelope == null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            if (context.MonotonicMilliseconds < 0
                || context.TelemetryAgeMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(context));
            }

            if (!context.IsConnected)
            {
                return Neutral(FlightSafetyReason.Disconnected);
            }
            if (context.ControlMode == ControlMode.Failsafe
                && envelope.Source != FlightCommandSource.Failsafe)
            {
                return Neutral(FlightSafetyReason.FailsafeMode);
            }
            if (envelope.AuthorityEpoch != context.AuthorityEpoch)
            {
                return Neutral(FlightSafetyReason.AuthorityMismatch);
            }
            if (envelope.IsExpired(context.MonotonicMilliseconds))
            {
                return Neutral(FlightSafetyReason.CommandExpired);
            }
            if (!context.HasTelemetry)
            {
                return Neutral(FlightSafetyReason.TelemetryUnavailable);
            }
            if (context.TelemetryAgeMilliseconds > profile.TelemetryFailsafeTimeoutMilliseconds)
            {
                return Neutral(FlightSafetyReason.TelemetryStale);
            }
            if (!context.HasFlightState)
            {
                return Neutral(FlightSafetyReason.InvalidFlightState);
            }
            if (context.HasBatteryState && context.BatteryPercent <= profile.CriticalBatteryPercent)
            {
                return Neutral(FlightSafetyReason.CriticalBattery);
            }

            if (envelope.Source == FlightCommandSource.Manual
                || envelope.Source == FlightCommandSource.Assisted)
            {
                if (!envelope.Authorized)
                {
                    return Neutral(FlightSafetyReason.ManualAuthorizationMissing);
                }
                if (context.ControlMode != ControlMode.Manual
                    && context.ControlMode != ControlMode.Assisted)
                {
                    return Neutral(FlightSafetyReason.InvalidControlMode);
                }
                if (context.MonotonicMilliseconds - envelope.CreatedAtMilliseconds
                    > profile.ControllerStaleTimeoutMilliseconds)
                {
                    return Neutral(FlightSafetyReason.ControllerInputStale);
                }
            }
            else if (envelope.Source == FlightCommandSource.Autopilot
                && context.ControlMode != ControlMode.Autopilot)
            {
                return Neutral(FlightSafetyReason.InvalidControlMode);
            }

            var command = envelope.Command;
            var altitudeLimited = context.RelativeAltitudeMeters >= profile.MaxRelativeAltitudeMeters
                && command.Vertical > 0f;
            var vertical = altitudeLimited
                ? 0f
                : Clamp(command.Vertical, profile.MaxVerticalCommand);
            var yaw = Clamp(command.Yaw, profile.MaxYawCommand);
            var roll = command.Roll;
            var pitch = command.Pitch;
            LimitHorizontal(ref roll, ref pitch, profile.MaxHorizontalCommand);

            var applied = new NormalizedFlightCommand(roll, pitch, yaw, vertical);
            if (CommandsEqual(command, applied))
            {
                return new FlightSafetyDecision(
                    FlightSafetyDisposition.Accepted,
                    FlightSafetyReason.None,
                    applied);
            }

            return new FlightSafetyDecision(
                FlightSafetyDisposition.Limited,
                altitudeLimited
                    ? FlightSafetyReason.DevelopmentAltitudeLimit
                    : FlightSafetyReason.IndoorProfileLimit,
                applied);
        }

        private static FlightSafetyDecision Neutral(FlightSafetyReason reason)
        {
            return new FlightSafetyDecision(
                FlightSafetyDisposition.Neutralized,
                reason,
                NormalizedFlightCommand.Neutral);
        }

        private static float Clamp(float value, float magnitude)
        {
            return Math.Max(-magnitude, Math.Min(magnitude, value));
        }

        private static void LimitHorizontal(ref float roll, ref float pitch, float maximum)
        {
            var magnitude = Math.Sqrt((roll * roll) + (pitch * pitch));
            if (magnitude <= maximum || magnitude <= 0.000001)
            {
                return;
            }

            var scale = maximum / magnitude;
            roll = (float)(roll * scale);
            pitch = (float)(pitch * scale);
        }

        private static bool CommandsEqual(NormalizedFlightCommand left, NormalizedFlightCommand right)
        {
            const float tolerance = 0.0001f;
            return Math.Abs(left.Roll - right.Roll) <= tolerance
                && Math.Abs(left.Pitch - right.Pitch) <= tolerance
                && Math.Abs(left.Yaw - right.Yaw) <= tolerance
                && Math.Abs(left.Vertical - right.Vertical) <= tolerance;
        }
    }
}
