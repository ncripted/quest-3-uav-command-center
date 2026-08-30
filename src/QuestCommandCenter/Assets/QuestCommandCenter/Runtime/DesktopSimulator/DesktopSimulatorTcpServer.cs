using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using QuestCommandCenter.VehicleCore;
using UnityEngine;

namespace QuestCommandCenter.DesktopSimulator
{
    /// <summary>
    /// Localhost-only single-client server. Networking stays off Unity's render
    /// thread; the main thread exchanges only the newest complete JSON line.
    /// </summary>
    public sealed class DesktopSimulatorTcpServer : MonoBehaviour
    {
        private readonly object gate = new object();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private Thread worker;
        private TcpListener listener;
        private TcpClient client;
        private volatile bool stopping;
        private volatile bool clientConnected;
        private string latestInbound;
        private long inboundVersion;
        private readonly Queue<string> discreteInbound = new Queue<string>();
        private string latestOutbound;
        private long outboundVersion;

        public bool IsClientConnected => clientConnected;

        private void OnEnable()
        {
            StartServer();
        }

        private void OnDisable()
        {
            StopServer();
        }

        private void OnDestroy()
        {
            wake.Dispose();
        }

        public bool TryReadLatest(ref long observedVersion, out string jsonLine)
        {
            lock (gate)
            {
                if (discreteInbound.Count > 0)
                {
                    jsonLine = discreteInbound.Dequeue();
                    return true;
                }
                if (observedVersion == inboundVersion)
                {
                    jsonLine = null;
                    return false;
                }
                observedVersion = inboundVersion;
                jsonLine = latestInbound;
                return !string.IsNullOrEmpty(jsonLine);
            }
        }

        public void QueueTelemetry(string jsonLine)
        {
            if (string.IsNullOrEmpty(jsonLine))
            {
                return;
            }
            lock (gate)
            {
                latestOutbound = jsonLine.EndsWith("\n", StringComparison.Ordinal)
                    ? jsonLine
                    : jsonLine + "\n";
                outboundVersion++;
            }
            wake.Set();
        }

        private void StartServer()
        {
            if (worker != null && worker.IsAlive)
            {
                return;
            }
            stopping = false;
            worker = new Thread(ServerLoop)
            {
                IsBackground = true,
                Name = "QCC Unity Desktop Simulator Server"
            };
            worker.Start();
        }

        private void StopServer()
        {
            stopping = true;
            wake.Set();
            try
            {
                client?.Close();
                listener?.Stop();
            }
            catch (Exception)
            {
                // Socket shutdown is best effort; simulator state still neutralizes.
            }
            if (worker != null && worker.IsAlive)
            {
                worker.Join(1000);
            }
            worker = null;
            client = null;
            listener = null;
            clientConnected = false;
        }

        private void ServerLoop()
        {
            try
            {
                listener = new TcpListener(IPAddress.Loopback, DesktopSimulatorProtocol.DefaultPort);
                listener.Start(1);
                while (!stopping)
                {
                    try
                    {
                        client = listener.AcceptTcpClient();
                        client.NoDelay = true;
                        clientConnected = true;
                        RunClient(client);
                    }
                    catch (SocketException)
                    {
                        if (!stopping)
                        {
                            Thread.Sleep(100);
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    finally
                    {
                        clientConnected = false;
                        try
                        {
                            client?.Close();
                        }
                        catch (Exception)
                        {
                            // Connection is already unusable.
                        }
                        client = null;
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogError("Desktop simulator server stopped: " + exception.Message);
            }
            finally
            {
                clientConnected = false;
            }
        }

        private void RunClient(TcpClient connectedClient)
        {
            var stream = connectedClient.GetStream();
            stream.WriteTimeout = 250;
            var buffer = new byte[4096];
            var receiveText = string.Empty;
            long sentVersion = -1;

            while (!stopping && connectedClient.Connected)
            {
                if (PeerClosed(connectedClient))
                {
                    return;
                }
                while (stream.DataAvailable)
                {
                    var count = stream.Read(buffer, 0, buffer.Length);
                    if (count <= 0)
                    {
                        return;
                    }
                    receiveText += Encoding.UTF8.GetString(buffer, 0, count);
                    var newline = receiveText.IndexOf('\n');
                    while (newline >= 0)
                    {
                        var line = receiveText.Substring(0, newline).Trim();
                        receiveText = receiveText.Substring(newline + 1);
                        if (line.Length > 0)
                        {
                            lock (gate)
                            {
                                if (line.Contains("\"type\":\"simulator_action\""))
                                {
                                    if (discreteInbound.Count < 16)
                                    {
                                        discreteInbound.Enqueue(line);
                                    }
                                }
                                else
                                {
                                    latestInbound = line;
                                    inboundVersion++;
                                }
                            }
                        }
                        newline = receiveText.IndexOf('\n');
                    }
                }

                string outbound = null;
                long version;
                lock (gate)
                {
                    version = outboundVersion;
                    if (version != sentVersion)
                    {
                        outbound = latestOutbound;
                    }
                }
                if (!string.IsNullOrEmpty(outbound))
                {
                    var bytes = Encoding.UTF8.GetBytes(outbound);
                    stream.Write(bytes, 0, bytes.Length);
                    sentVersion = version;
                }

                wake.WaitOne(10);
            }
        }

        private static bool PeerClosed(TcpClient connectedClient)
        {
            try
            {
                var socket = connectedClient.Client;
                return socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0;
            }
            catch (SocketException)
            {
                return true;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
    }
}
