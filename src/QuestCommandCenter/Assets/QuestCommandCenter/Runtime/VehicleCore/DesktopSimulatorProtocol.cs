using System.Globalization;

namespace QuestCommandCenter.VehicleCore
{
    public static class DesktopSimulatorProtocol
    {
        public const int SchemaVersion = 1;
        public const int DefaultPort = 8765;

        public static string SerializeManualControl(
            long sequence,
            NormalizedFlightCommand command,
            bool authorized,
            bool brake,
            bool takeover)
        {
            return "{\"schemaVersion\":1,\"type\":\"manual_control\",\"sequence\":"
                + sequence.ToString(CultureInfo.InvariantCulture)
                + ",\"roll\":" + command.Roll.ToString("R", CultureInfo.InvariantCulture)
                + ",\"pitch\":" + command.Pitch.ToString("R", CultureInfo.InvariantCulture)
                + ",\"yaw\":" + command.Yaw.ToString("R", CultureInfo.InvariantCulture)
                + ",\"vertical\":" + command.Vertical.ToString("R", CultureInfo.InvariantCulture)
                + ",\"authorized\":" + BooleanLiteral(authorized)
                + ",\"brake\":" + BooleanLiteral(brake)
                + ",\"takeover\":" + BooleanLiteral(takeover)
                + "}\n";
        }

        public static string SerializeFlightAction(long sequence, FlightActionType action)
        {
            return "{\"schemaVersion\":1,\"type\":\"simulator_action\",\"sequence\":"
                + sequence.ToString(CultureInfo.InvariantCulture)
                + ",\"action\":\"" + FlightActionWireName(action) + "\"}\n";
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

        private static string BooleanLiteral(bool value)
        {
            return value ? "true" : "false";
        }
    }
}
