using System;
using System.Collections.Generic;
using System.Linq;

namespace QuestCommandCenter.VehicleCore
{
    public sealed class FlightPreflightInput
    {
        public VehicleTelemetry Telemetry { get; set; }
        public bool TelemetryFresh { get; set; }
        public bool ManualControlInterfaceReady { get; set; }
        public bool ControllerAxesNeutral { get; set; }
    }

    public sealed class FlightPreflightCheck
    {
        public FlightPreflightCheck(string label, bool passed, string detail)
        {
            Label = label;
            Passed = passed;
            Detail = detail;
        }

        public string Label { get; }
        public bool Passed { get; }
        public string Detail { get; }
    }

    public sealed class FlightPreflightResult
    {
        public FlightPreflightResult(IEnumerable<FlightPreflightCheck> checks)
        {
            Checks = (checks ?? throw new ArgumentNullException(nameof(checks))).ToArray();
        }

        public IReadOnlyList<FlightPreflightCheck> Checks { get; }
        public bool Passed => Checks.All(check => check.Passed);
        public int PassedCount => Checks.Count(check => check.Passed);
    }

    /// <summary>
    /// Conservative initial real-aircraft gate. This is evaluated before flight
    /// authority can be enabled and is independent of the DJI adapter.
    /// </summary>
    public sealed class FlightPreflightGate
    {
        public const int MinimumBatteryPercent = 40;

        public FlightPreflightResult Evaluate(FlightPreflightInput input)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            var telemetry = input.Telemetry;
            var connected = telemetry?.IsConnected == true;
            var flightState = connected && telemetry.HasFlightState;
            var batteryReady = connected
                && telemetry.HasBatteryState
                && telemetry.BatteryPercent >= MinimumBatteryPercent;
            var gpsReady = flightState
                && telemetry.PositionValid
                && telemetry.SatelliteCount > 0;
            var grounded = flightState && !telemetry.MotorsOn && !telemetry.IsFlying;

            return new FlightPreflightResult(new[]
            {
                Check("SPARK LINK", connected, connected ? "CONNECTED" : "DISCONNECTED"),
                Check("TELEMETRY", connected && input.TelemetryFresh,
                    input.TelemetryFresh ? "FRESH" : "WAITING / STALE"),
                Check("FLIGHT STATE", flightState,
                    flightState ? "VALID" : "UNAVAILABLE"),
                Check("BATTERY", batteryReady,
                    telemetry?.HasBatteryState == true
                        ? telemetry.BatteryPercent + "% (40% MIN)"
                        : "UNAVAILABLE"),
                Check("GPS FIX", gpsReady,
                    flightState ? telemetry.SatelliteCount + " SAT" : "UNAVAILABLE"),
                Check("HOME POINT", flightState && telemetry.HomePositionSet,
                    flightState && telemetry.HomePositionSet ? "SET" : "NOT SET"),
                Check("DJI FAILSAFE", flightState && !telemetry.FailsafeEnabled,
                    flightState && telemetry.FailsafeEnabled ? "ACTIVE" : flightState ? "CLEAR" : "UNKNOWN"),
                Check("AIRCRAFT", grounded,
                    grounded ? "LANDED / MOTORS OFF" : "MUST BE LANDED / MOTORS OFF"),
                Check("VIRTUAL STICK API", input.ManualControlInterfaceReady,
                    input.ManualControlInterfaceReady
                        ? "READY / ENABLES AFTER CONFIRM"
                        : "STATUS QUERY FAILED"),
                Check("CONTROLLER STICKS", input.ControllerAxesNeutral,
                    input.ControllerAxesNeutral ? "CENTERED" : "CENTER BOTH STICKS")
            });
        }

        private static FlightPreflightCheck Check(string label, bool passed, string detail)
        {
            return new FlightPreflightCheck(label, passed, detail);
        }
    }
}
