using QuestCommandCenter.VehicleCore;
using UnityEngine;
using UnityEngine.UI;

namespace QuestCommandCenter.UI
{
    /// <summary>
    /// Presents generic real-flight readiness without depending on a DJI adapter.
    /// The detailed checklist is shown only while a live vehicle is selected and
    /// locked; the compact badge remains visible in every mode.
    /// </summary>
    public sealed class QuestManualFlightStatusPanel : MonoBehaviour
    {
        [SerializeField] private Text badgeText;
        [SerializeField] private Image badgeBackground;
        [SerializeField] private Text detailText;
        [SerializeField] private GameObject detailPanel;
        [SerializeField] private Text flightActionRailText;
        [SerializeField] private MonoBehaviour stateSourceBehaviour;
        private IManualFlightControlState stateSource;

        public void Initialize(
            Text badge,
            Image badgePanel,
            Text detail,
            GameObject details,
            Text actionRail,
            MonoBehaviour source)
        {
            badgeText = badge;
            badgeBackground = badgePanel;
            detailText = detail;
            detailPanel = details;
            flightActionRailText = actionRail;
            stateSourceBehaviour = source;
            Bind();
            Render();
        }

        private void OnEnable()
        {
            Bind();
            Render();
        }

        private void OnDisable()
        {
            if (stateSource != null)
            {
                stateSource.StatusChanged -= OnStatusChanged;
            }
        }

        private void Bind()
        {
            if (stateSource != null)
            {
                stateSource.StatusChanged -= OnStatusChanged;
            }
            stateSource = stateSourceBehaviour as IManualFlightControlState;
            if (stateSource != null)
            {
                stateSource.StatusChanged += OnStatusChanged;
            }
        }

        private void OnStatusChanged(string _)
        {
            Render();
        }

        private void Render()
        {
            if (stateSource == null)
            {
                if (badgeText != null)
                {
                    badgeText.text = "FLIGHT CONTROL UNAVAILABLE";
                }
                if (detailPanel != null)
                {
                    detailPanel.SetActive(false);
                }
                return;
            }

            if (badgeText != null)
            {
                badgeText.text = stateSource.StatusBadge;
            }
            if (detailText != null)
            {
                detailText.text = stateSource.CurrentStatus;
            }
            if (detailPanel != null)
            {
                detailPanel.SetActive(
                    stateSource.IsLiveVehicleSelected
                    && !stateSource.IsFlightControlReady);
            }
            if (flightActionRailText != null)
            {
                flightActionRailText.text = ActionRailText(stateSource);
                flightActionRailText.color = stateSource.ControlMode == ControlMode.Failsafe
                    ? new Color(1f, 0.3f, 0.25f, 1f)
                    : stateSource.IsFlightControlReady
                        ? new Color(0.55f, 1f, 0.72f, 1f)
                        : new Color(0.9f, 0.94f, 0.98f, 1f);
            }
            if (badgeBackground != null)
            {
                badgeBackground.color = BadgeColor(stateSource);
            }
        }

        private static Color BadgeColor(IManualFlightControlState source)
        {
            if (source.ControlMode == ControlMode.Failsafe)
            {
                return new Color(0.55f, 0.035f, 0.025f, 0.96f);
            }
            if (source.IsFlightControlReady)
            {
                return new Color(0.02f, 0.32f, 0.16f, 0.94f);
            }
            if (source.IsLiveVehicleSelected)
            {
                return new Color(0.48f, 0.22f, 0.015f, 0.96f);
            }
            return new Color(0.025f, 0.075f, 0.13f, 0.9f);
        }

        private static string ActionRailText(IManualFlightControlState source)
        {
            if (!source.IsLiveVehicleSelected)
            {
                return "SIM\nFLIGHT\n\nSTICKS\nTAKEOFF\n\nX  RTH\n\nA  LAND\n\nB  HOVER";
            }
            if (source.ControlMode == ControlMode.Failsafe)
            {
                return "⚠\nFAILSAFE\n\nCONTROL\nDISABLED\n\nRELEASE\nGRIPS";
            }
            if (!source.IsFlightControlReady)
            {
                return "REAL\nFLIGHT\n\nTAKEOFF\nLOCKED\n\nRTH\nLOCKED\n\nLAND\nLOCKED";
            }
            return "REAL\nFLIGHT\n\nSTICKS\nTAKEOFF\n\nX  RTH\n\nA  LAND\n\nB  HOVER";
        }
    }
}
