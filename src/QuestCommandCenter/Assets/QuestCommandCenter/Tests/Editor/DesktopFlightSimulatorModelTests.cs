using NUnit.Framework;
using QuestCommandCenter.DesktopSimulator;
using UnityEngine;

namespace QuestCommandCenter.VehicleCore.Tests
{
    public sealed class DesktopFlightSimulatorModelTests
    {
        [Test]
        public void AuthorizedMode2Command_MovesNorthAtDevelopmentLimit()
        {
            var model = ConnectedModel();
            model.TryApplyControl(
                new DesktopControlSample(1, 0f, 1f, 0f, 0f, true, false, false),
                100);

            model.Step(1f, 100);

            Assert.That(model.State.PositionMeters.z, Is.EqualTo(0.5f).Within(0.0001f));
            Assert.That(model.State.VelocityMetersPerSecond.z, Is.EqualTo(0.5f));
            Assert.That(model.State.CommandAuthorized, Is.True);
        }

        [Test]
        public void StaleCommand_IsNeutralizedWithoutPositionReplay()
        {
            var model = ConnectedModel();
            model.TryApplyControl(
                new DesktopControlSample(1, 0f, 1f, 0f, 0f, true, false, false),
                100);
            model.Step(0.1f, 100);
            var freshPosition = model.State.PositionMeters;

            model.Step(1f, 351);

            Assert.That(model.State.PositionMeters, Is.EqualTo(freshPosition));
            Assert.That(model.State.VelocityMetersPerSecond, Is.EqualTo(Vector3.zero));
            Assert.That(model.State.CommandStale, Is.True);
        }

        [Test]
        public void BrakeAndDeadmanRelease_ProduceNeutralVelocity()
        {
            var model = ConnectedModel();
            model.TryApplyControl(
                new DesktopControlSample(1, 0f, 1f, 0f, 0f, true, true, false),
                100);
            model.Step(0.1f, 100);
            Assert.That(model.State.VelocityMetersPerSecond, Is.EqualTo(Vector3.zero));

            model.TryApplyControl(
                new DesktopControlSample(2, 0f, 1f, 0f, 0f, false, false, false),
                110);
            model.Step(0.1f, 110);
            Assert.That(model.State.VelocityMetersPerSecond, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void DevelopmentAltitudeAndRadius_AreBounded()
        {
            var model = ConnectedModel();
            model.TryApplyControl(
                new DesktopControlSample(1, 0f, 1f, 0f, 1f, true, false, false),
                100);

            model.Step(30f, 100);

            Assert.That(model.State.PositionMeters.y, Is.EqualTo(DesktopFlightSimulatorModel.MaximumAltitudeMeters));
            Assert.That(
                new Vector2(model.State.PositionMeters.x, model.State.PositionMeters.z).magnitude,
                Is.EqualTo(DesktopFlightSimulatorModel.MaximumRadiusMeters).Within(0.0001f));
            Assert.That(model.State.GeofenceLimited, Is.True);
        }

        [Test]
        public void SimulatedLandAndTakeoff_CompleteDeterministically()
        {
            var model = ConnectedModel();
            Assert.That(model.RequestLand(), Is.True);
            for (var index = 0; index < 40; index++)
            {
                model.Step(0.1f, 100 + index * 100);
            }
            Assert.That(model.State.FlightPhase, Is.EqualTo(SimulatorFlightPhase.Grounded));
            Assert.That(model.State.PositionMeters.y, Is.Zero);

            Assert.That(model.RequestTakeoff(), Is.True);
            for (var index = 0; index < 40; index++)
            {
                model.Step(0.1f, 5000 + index * 100);
            }
            Assert.That(model.State.FlightPhase, Is.EqualTo(SimulatorFlightPhase.Airborne));
            Assert.That(model.State.PositionMeters.y, Is.EqualTo(1f));
        }

        [Test]
        public void ReturnHome_MovesToHomeThenLands()
        {
            var model = ConnectedModel();
            model.TryApplyControl(
                new DesktopControlSample(1, 0f, 1f, 0f, 0f, true, false, false),
                100);
            model.Step(4f, 100);
            Assert.That(model.State.PositionMeters.z, Is.GreaterThan(1f));

            Assert.That(model.RequestReturnHome(), Is.True);
            for (var index = 0; index < 120; index++)
            {
                model.Step(0.1f, 1000 + index * 100);
            }

            Assert.That(model.State.FlightPhase, Is.EqualTo(SimulatorFlightPhase.Grounded));
            Assert.That(model.State.PositionMeters, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void GroundedTakeoffThenManualAuthorization_RestoresStickFlight()
        {
            var model = new DesktopFlightSimulatorModel(startAirborne: false);
            model.SetClientConnected(true);
            Assert.That(model.RequestTakeoff(), Is.True);
            for (var index = 0; index < 40; index++)
            {
                model.Step(0.1f, 100 + index * 100);
            }
            Assert.That(model.State.FlightPhase, Is.EqualTo(SimulatorFlightPhase.Airborne));

            model.TryApplyControl(
                new DesktopControlSample(1, 0f, 1f, 0f, 0f, true, false, false),
                5000);
            var before = model.State.PositionMeters.z;
            model.Step(1f, 5000);

            Assert.That(model.State.CommandAuthorized, Is.True);
            Assert.That(model.State.PositionMeters.z, Is.GreaterThan(before));
        }

        private static DesktopFlightSimulatorModel ConnectedModel()
        {
            var model = new DesktopFlightSimulatorModel();
            model.SetClientConnected(true);
            return model;
        }
    }
}
