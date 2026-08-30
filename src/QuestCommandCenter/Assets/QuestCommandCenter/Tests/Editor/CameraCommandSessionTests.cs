using NUnit.Framework;

namespace QuestCommandCenter.VehicleCore.Tests
{
    public sealed class CameraCommandSessionTests
    {
        [Test]
        public void TryBegin_RejectsStartWhenStorageIsNotReady()
        {
            var session = new CameraCommandSession();

            var accepted = session.TryBegin(
                CameraCommandType.StartRecording,
                cameraAvailable: true,
                storageReady: false,
                isRecording: false,
                out var result);

            Assert.That(accepted, Is.False);
            Assert.That(result.Status, Is.EqualTo(CameraCommandStatus.Rejected));
            Assert.That(session.IsPending, Is.False);
        }

        [Test]
        public void TryBegin_AllowsStopWhenStorageReportsAnError()
        {
            var session = new CameraCommandSession();

            var accepted = session.TryBegin(
                CameraCommandType.StopRecording,
                cameraAvailable: true,
                storageReady: false,
                isRecording: true,
                out var result);

            Assert.That(accepted, Is.True);
            Assert.That(result.Status, Is.EqualTo(CameraCommandStatus.Pending));
        }

        [Test]
        public void TryBegin_RejectsASecondCommandWhilePending()
        {
            var session = new CameraCommandSession();
            Assert.That(
                session.TryBegin(CameraCommandType.TakePhoto, true, true, false, out _),
                Is.True);

            var accepted = session.TryBegin(
                CameraCommandType.StartRecording,
                cameraAvailable: true,
                storageReady: true,
                isRecording: false,
                out var result);

            Assert.That(accepted, Is.False);
            Assert.That(result.Status, Is.EqualTo(CameraCommandStatus.Rejected));
            Assert.That(session.IsPending, Is.True);
        }

        [Test]
        public void TryComplete_IgnoresAStaleRequestId()
        {
            var session = new CameraCommandSession();
            session.TryBegin(CameraCommandType.TakePhoto, true, true, false, out var pending);

            var completed = session.TryComplete(pending.RequestId + 1, true, "wrong", out var result);

            Assert.That(completed, Is.False);
            Assert.That(result.Status, Is.EqualTo(CameraCommandStatus.Pending));
        }

        [Test]
        public void TryComplete_CompletesTheActiveRequest()
        {
            var session = new CameraCommandSession();
            session.TryBegin(CameraCommandType.TakePhoto, true, true, false, out var pending);

            var completed = session.TryComplete(pending.RequestId, true, "Photo captured.", out var result);

            Assert.That(completed, Is.True);
            Assert.That(result.Status, Is.EqualTo(CameraCommandStatus.Succeeded));
            Assert.That(session.IsPending, Is.False);
        }
    }
}
