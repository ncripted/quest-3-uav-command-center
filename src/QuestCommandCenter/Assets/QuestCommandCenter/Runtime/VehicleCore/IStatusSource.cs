using System;

namespace QuestCommandCenter.VehicleCore
{
    public interface IStatusSource
    {
        string CurrentStatus { get; }
        event Action<string> StatusChanged;
    }
}
