using System;

namespace QuestCommandCenter.VehicleCore
{
    /// <summary>
    /// Projects a minimal synthetic flight phase for a no-dispatch control test.
    /// A real motors-on/flying state can never be masked as grounded.
    /// </summary>
    public static class ShadowVehicleTelemetry
    {
        public static VehicleTelemetry Project(
            VehicleTelemetry live,
            bool simulatedAirborne,
            long monotonicMilliseconds)
        {
            if (live == null)
            {
                return null;
            }
            if (monotonicMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(monotonicMilliseconds));
            }

            var airborne = simulatedAirborne || live.IsFlying || live.MotorsOn;
            return new VehicleTelemetry
            {
                Sequence = live.Sequence,
                MonotonicMilliseconds = monotonicMilliseconds,
                IsConnected = live.IsConnected,
                ProductName = (live.ProductName ?? "Vehicle") + " SHADOW",
                HasBatteryState = live.HasBatteryState,
                BatteryPercent = live.BatteryPercent,
                HasFlightState = live.HasFlightState,
                AltitudeMeters = airborne ? Math.Max(1f, live.AltitudeMeters) : live.AltitudeMeters,
                FlightMode = airborne ? "Airborne" : "Grounded",
                MotorsOn = airborne,
                IsFlying = airborne,
                HomePositionSet = live.HomePositionSet,
                FailsafeEnabled = live.FailsafeEnabled
            };
        }
    }
}
