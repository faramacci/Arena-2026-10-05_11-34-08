using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using Arena.Core;

namespace Arena.Gameplay
{
    [Serializable]
    public sealed class LanMessage
    {
        public int version = 1;
        public string kind;
        public CharacterId character;
        public CharacterId hostCharacter;
        public ZoneShape zone;
        public Vector3 input;
        public int slot;
        public MatchSnapshot snapshot;
        public int phase; // 0 countdown, 1 playing, 2 result
        public float timer;
        public string result;
    }

    /// <summary>One trusted LAN peer; all Unity and protocol work stays on the main thread.</summary>
    public sealed class LanSession : IDisposable
    {
        public const int Port = 28741;
        private const int MaxFrame = 16384;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Queue<byte[]> outgoing = new Queue<byte[]>();
        private readonly List<byte> incoming = new List<byte>();
        private readonly byte[] readBuffer = new byte[8192];
        private TcpListener listener;
        private TcpClient client;
        private IAsyncResult connecting;
        private byte[] sending;
        private int sent;
        private double lastReceive;
        private bool disposed;
        public bool IsHost { get; private set; }
        public bool Connected { get; private set; }
        public bool Active => !disposed;
        public int ListeningPort => ((IPEndPoint)listener.LocalEndpoint).Port;
        public string Error { get; private set; }
        public event Action<LanMessage> Received;
        public event Action ConnectedToPeer;

        public static LanSession Host(int port = Port)
        {
            var s = new LanSession { IsHost = true };
            try { s.listener = new TcpListener(IPAddress.Any, port); s.listener.Start(1); }
            catch { s.Dispose(); throw; }
            return s;
        }

        public static LanSession Join(string address, int port = Port)
        {
            if (!IPAddress.TryParse(address.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("Enter the host's IPv4 address (for example 192.168.43.1).");
            var s = new LanSession();
            try
            {
                s.client = new TcpClient(AddressFamily.InterNetwork);
                s.connecting = s.client.BeginConnect(ip, port, null, null);
            }
            catch { s.Dispose(); throw; }
            return s;
        }

        public static string LocalAddresses()
        {
            var addresses = new List<string>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address.Address)) addresses.Add(address.Address.ToString());
            }
            return addresses.Count == 0 ? "Check hotspot/gateway IP in Wi-Fi settings" : string.Join(" / ", addresses);
        }

        public void Send(LanMessage message)
        {
            if (!Connected || disposed) return;
            byte[] payload = Encoding.UTF8.GetBytes(JsonUtility.ToJson(message));
            if (payload.Length > MaxFrame) { Fail("Network frame too large."); return; }
            if (outgoing.Count >= 8) { Fail("Connection too slow; reconnect to the hotspot."); return; }
            byte[] frame = new byte[payload.Length + 4];
            byte[] size = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(payload.Length));
            Buffer.BlockCopy(size, 0, frame, 0, 4);
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            outgoing.Enqueue(frame);
        }

        public bool CheckTimeout(double elapsedSeconds)
        {
            if (disposed) return false;
            if ((!IsHost && !Connected && elapsedSeconds > 10) ||
                (Connected && elapsedSeconds - lastReceive > 10))
                Fail(Connected ? "Connection lost (10 seconds without data)." :
                    "Connection timed out. Check host IP and hotspot.");
            return disposed;
        }

        public void Pump()
        {
            if (disposed) return;
            try
            {
                if (!Connected)
                {
                    if (IsHost && listener.Pending()) { client = listener.AcceptTcpClient(); listener.Stop(); }
                    else if (!IsHost && connecting.IsCompleted) { client.EndConnect(connecting); connecting = null; }
                    else
                    {
                        CheckTimeout(clock.Elapsed.TotalSeconds);
                        return;
                    }
                    client.NoDelay = true;
                    client.Client.Blocking = false;
                    Connected = true;
                    lastReceive = clock.Elapsed.TotalSeconds;
                    ConnectedToPeer?.Invoke();
                }
                var socket = client.Client;
                if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0)
                { Fail("Other player disconnected."); return; }
                // Bounded work and storage, including malformed/oversized packets.
                for (int i = 0; i < 4 && socket.Available > 0; i++)
                {
                    int count = socket.Receive(readBuffer, 0, Math.Min(readBuffer.Length, socket.Available), SocketFlags.None);
                    if (count == 0) { Fail("Other player disconnected."); return; }
                    for (int j = 0; j < count; j++) incoming.Add(readBuffer[j]);
                    if (incoming.Count > MaxFrame * 4) throw new InvalidDataException("Receive buffer exceeded.");
                }
                for (int i = 0; i < 16 && incoming.Count >= 4; i++)
                {
                    int size = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(incoming.GetRange(0, 4).ToArray(), 0));
                    if (size <= 0 || size > MaxFrame) throw new InvalidDataException("Invalid frame size.");
                    if (incoming.Count < size + 4) break;
                    string json = Encoding.UTF8.GetString(incoming.GetRange(4, size).ToArray());
                    incoming.RemoveRange(0, size + 4);
                    var message = JsonUtility.FromJson<LanMessage>(json);
                    if (message == null || message.version != 1 || string.IsNullOrEmpty(message.kind))
                        throw new InvalidDataException("Incompatible Arena protocol.");
                    lastReceive = clock.Elapsed.TotalSeconds;
                    Received?.Invoke(message);
                    if (disposed) return;
                }
                for (int i = 0; i < 8 && socket.Poll(0, SelectMode.SelectWrite); i++)
                {
                    if (sending == null)
                    {
                        if (outgoing.Count == 0) break;
                        sending = outgoing.Dequeue(); sent = 0;
                    }
                    try { sent += socket.Send(sending, sent, sending.Length - sent, SocketFlags.None); }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock) { break; }
                    if (sent == sending.Length) sending = null;
                }
                CheckTimeout(clock.Elapsed.TotalSeconds);
            }
            catch (Exception ex) when (ex is SocketException || ex is IOException || ex is InvalidDataException || ex is ArgumentException || ex is ObjectDisposedException)
            { Fail("LAN connection failed: " + ex.Message); }
        }

        public void Reject(string reason) => Fail(reason);
        private void Fail(string reason) { Error = reason; Dispose(); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; Connected = false;
            listener?.Stop(); client?.Close();
            outgoing.Clear(); incoming.Clear(); sending = null;
        }
    }
}
