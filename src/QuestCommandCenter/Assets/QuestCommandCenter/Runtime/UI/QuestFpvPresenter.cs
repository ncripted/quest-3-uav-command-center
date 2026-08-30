using QuestCommandCenter.Video;
using UnityEngine;
using UnityEngine.UI;

namespace QuestCommandCenter.UI
{
    public sealed class QuestFpvPresenter : MonoBehaviour
    {
        [SerializeField] private RawImage display;
        [SerializeField] private MonoBehaviour videoSourceBehaviour;
        private IVideoTextureSource videoSource;

        public void Initialize(RawImage target, MonoBehaviour source)
        {
            display = target;
            videoSourceBehaviour = source;
            BindVideoSource();
            Render(videoSource != null ? videoSource.CurrentVideoTexture : null);
        }

        private void OnEnable()
        {
            BindVideoSource();
            Render(videoSource != null ? videoSource.CurrentVideoTexture : null);
        }

        private void OnDisable()
        {
            if (videoSource != null)
            {
                videoSource.VideoFrameUpdated -= Render;
            }
        }

        private void BindVideoSource()
        {
            if (videoSource != null)
            {
                videoSource.VideoFrameUpdated -= Render;
            }

            videoSource = videoSourceBehaviour as IVideoTextureSource;
            if (videoSource != null)
            {
                videoSource.VideoFrameUpdated += Render;
            }
        }

        private void Render(Texture texture)
        {
            if (display == null)
            {
                return;
            }

            display.texture = texture;
            display.enabled = texture != null;
        }
    }
}
