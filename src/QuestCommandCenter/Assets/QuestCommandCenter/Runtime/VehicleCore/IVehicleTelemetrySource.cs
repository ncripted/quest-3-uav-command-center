using System;

namespace QuestCommandCenter.VehicleCore
{
    public interface IVehicleTelemetrySource
    {
        VehicleTelemetry LatestTelemetry { get; }
        event Action<VehicleTelemetry> TelemetryUpdated;
    }
}
