using System;
using UnityEngine;

namespace QuestCommandCenter.Video
{
    /// <summary>
    /// Presentation-facing video boundary. Consumers never access DJI feed or
    /// decoder objects and can be reused with local or remote vehicle backends.
    /// </summary>
    public interface IVideoTextureSource
    {
        Texture CurrentVideoTexture { get; }
        bool HasVideoFrame { get; }
        event Action<Texture> VideoFrameUpdated;
    }
}
