using QuestCommandCenter.VehicleCore;
using UnityEngine;

namespace QuestCommandCenter.XRInput
{
    /// <summary>
    /// Routes generic controller intents to a generic high-level flight-action
    /// capability. It contains no DJI SDK types or aircraft-specific calls.
    /// </summary>
    public sealed class QuestFlightActionCoordinator : MonoBehaviour
    {
        [SerializeField] private MonoBehaviour inputSourceBehaviour;
        [SerializeField] private MonoBehaviour flightActionControlBehaviour;
        private IManualControlIntentSource inputSource;
        private IFlightActionControl flightActionControl;

        public void Initialize(IManualControlIntentSource source, MonoBehaviour control)
        {
            Unsubscribe();
            inputSourceBehaviour = source as MonoBehaviour;
            flightActionControlBehaviour = control;
            inputSource = source;
            flightActionControl = control as IFlightActionControl;
            Subscribe();
        }

        private void OnEnable()
        {
            if (inputSource == null)
            {
                inputSource = inputSourceBehaviour as IManualControlIntentSource;
            }
            if (flightActionControl == null)
            {
                flightActionControl = flightActionControlBehaviour as IFlightActionControl;
            }
            Subscribe();
        }

        private void Start()
        {
            // QuestManualFlightCoordinator is the production single-authority
            // route for both simulator and live-aircraft actions. Older generated
            // scenes may still contain this legacy direct router; disable it
            // before the first input Update so one gesture cannot be dispatched
            // twice.
            if (GetComponent<QuestManualFlightCoordinator>() != null)
            {
                Debug.Log(
                    "Legacy simulator flight-action router disabled; integrated coordinator owns actions.");
                enabled = false;
            }
        }

        private void OnDisable()
        {
            Unsubscribe();
            flightActionControl?.CancelFlightActionConfirmation();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                flightActionControl?.CancelFlightActionConfirmation();
            }
        }

        private void Subscribe()
        {
            if (inputSource != null)
            {
                inputSource.OperatorIntentRaised -= OnOperatorIntent;
                inputSource.OperatorIntentRaised += OnOperatorIntent;
            }
        }

        private void Unsubscribe()
        {
            if (inputSource != null)
            {
                inputSource.OperatorIntentRaised -= OnOperatorIntent;
            }
        }

        private void OnOperatorIntent(OperatorIntentType intent)
        {
            if (flightActionControl == null)
            {
                return;
            }

            switch (intent)
            {
                case OperatorIntentType.TakeoffConfirmationRequested:
                    flightActionControl.RequestFlightActionConfirmation(FlightActionType.Takeoff);
                    break;
                case OperatorIntentType.LandConfirmationRequested:
                    flightActionControl.RequestFlightActionConfirmation(FlightActionType.Land);
                    break;
                case OperatorIntentType.ReturnHomeConfirmationRequested:
                    flightActionControl.RequestFlightActionConfirmation(FlightActionType.ReturnHome);
                    break;
                case OperatorIntentType.TakeoffConfirmed:
                    flightActionControl.ConfirmFlightAction(FlightActionType.Takeoff);
                    break;
                case OperatorIntentType.LandConfirmed:
                    flightActionControl.ConfirmFlightAction(FlightActionType.Land);
                    break;
                case OperatorIntentType.ReturnHomeConfirmed:
                    flightActionControl.ConfirmFlightAction(FlightActionType.ReturnHome);
                    break;
                case OperatorIntentType.BrakeHover:
                case OperatorIntentType.ManualTakeover:
                    flightActionControl.CancelFlightActionConfirmation();
                    break;
            }
        }
    }
}
