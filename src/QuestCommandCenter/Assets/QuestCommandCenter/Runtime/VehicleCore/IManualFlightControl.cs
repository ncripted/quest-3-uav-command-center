using System;

namespace QuestCommandCenter.VehicleCore
{
    /// <summary>
    /// Vehicle-independent manual-flight transport. The application submits only
    /// normalized, already-arbitrated commands; an adapter owns vehicle-specific
    /// units and SDK calls.
    /// </summary>
    public interface IManualFlightControl
    {
        /// <summary>
        /// True when the adapter can query and request its manual-control API.
        /// This does not mean that control mode is enabled or that commands may
        /// be sent; callers must separately require IsManualControlEnabled.
        /// </summary>
        bool IsManualControlInterfaceReady { get; }
        bool IsManualControlEnabled { get; }
        string ManualControlStatusMessage { get; }
        bool HasManualControlError { get; }

        event Action<bool> ManualControlModeChanged;

        bool RequestManualControlMode(bool enabled);
        bool SubmitManualFlightCommand(
            long sequence,
            NormalizedFlightCommand command,
            bool authorized);
    }

    /// <summary>
    /// Generic operator-facing state for the integrated preflight/control panel.
    /// </summary>
    public interface IManualFlightControlState : IStatusSource
    {
        ControlMode ControlMode { get; }
        bool IsLiveVehicleSelected { get; }
        bool IsFlightControlReady { get; }
        string StatusBadge { get; }
    }
}
