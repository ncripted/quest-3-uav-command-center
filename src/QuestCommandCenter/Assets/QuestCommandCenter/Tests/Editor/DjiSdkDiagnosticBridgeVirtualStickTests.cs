using System.Text.RegularExpressions;
using NUnit.Framework;
using QuestCommandCenter.Adapters.DjiSpark;
using UnityEngine;
using UnityEngine.TestTools;

namespace QuestCommandCenter.VehicleCore.Tests
{
    public sealed class DjiSdkDiagnosticBridgeVirtualStickTests
    {
        [Test]
        public void FailedEnable_PreservesSuccessfulApiProbeAndExactMessage()
        {
            var gameObject = new GameObject("DjiSdkDiagnosticBridgeTest");
            try
            {
                var bridge = gameObject.AddComponent<DjiSdkDiagnosticBridge>();
                bridge.OnDjiVirtualStickStatus(
                    "{\"requestId\":1,\"operation\":\"query\",\"succeeded\":true,"
                    + "\"available\":false,\"enabled\":false,"
                    + "\"message\":\"Virtual Stick API ready.\"}");

                LogAssert.Expect(
                    LogType.Error,
                    new Regex("DJI Virtual Stick result:.*operation=enable.*code=255"));
                bridge.OnDjiVirtualStickStatus(
                    "{\"requestId\":2,\"operation\":\"enable\",\"succeeded\":false,"
                    + "\"available\":false,\"enabled\":false,"
                    + "\"message\":\"Virtual Stick mode change failed: DJIError code=255\"}");

                Assert.That(bridge.IsManualControlInterfaceReady, Is.True);
                Assert.That(bridge.IsManualControlEnabled, Is.False);
                Assert.That(bridge.HasManualControlError, Is.True);
                Assert.That(
                    bridge.ManualControlStatusMessage,
                    Does.Contain("DJIError code=255"));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }
    }
}
