using QuestCommandCenter.VehicleCore;
using UnityEngine;
using UnityEngine.UI;

namespace QuestCommandCenter.UI
{
    public sealed class QuestShellStatusPanel : MonoBehaviour
    {
        [SerializeField] private Text statusText;
        [SerializeField] private MonoBehaviour statusSourceBehaviour;
        private IStatusSource statusSource;

        public void Initialize(Text target, MonoBehaviour source)
        {
            statusText = target;
            statusSourceBehaviour = source;
            BindStatusSource();
            Render();
        }

        private void OnEnable()
        {
            BindStatusSource();
            Render();
        }

        private void OnDisable()
        {
            if (statusSource != null)
            {
                statusSource.StatusChanged -= OnStatusChanged;
            }
        }

        private void BindStatusSource()
        {
            if (statusSource != null)
            {
                statusSource.StatusChanged -= OnStatusChanged;
            }

            statusSource = statusSourceBehaviour as IStatusSource;
            if (statusSource != null)
            {
                statusSource.StatusChanged += OnStatusChanged;
            }
        }

        private void OnStatusChanged(string value)
        {
            Render();
        }

        private void Render()
        {
            if (statusText == null)
            {
                return;
            }

            statusText.text = statusSource != null
                ? statusSource.CurrentStatus
                : "QUEST COMMAND CENTER\nStatus source unavailable.\n\nNo aircraft control is enabled in this build.";
        }
    }
}
