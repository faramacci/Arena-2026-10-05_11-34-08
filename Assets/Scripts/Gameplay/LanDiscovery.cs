using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Arena.Gameplay
{
    /// <summary>
    /// How two phones find each other on a hotspot without anyone reading an
    /// address off one screen and typing it into the other.
    ///
    /// A hosting phone shouts a tiny UDP beacon at the broadcast address of
    /// every network it is on; a joining phone binds the discovery port and
    /// listens. The beacon carries nothing but the TCP port to try - it is not
    /// a service announcement, and a joining phone still has to complete the
    /// normal handshake before anything is trusted.
    ///
    /// This exists because the host's own IPv4 list is the weak link on
    /// Android: a hotspot interface is not always reported by the network API,
    /// so the address a player reads out loud (or picks from a list) is often
    /// the wrong one, and the join then looks like a dead button. A broadcast
    /// reaches the other phone whatever its address is.
    /// </summary>
    public static class LanDiscovery
    {
        /// <summary>UDP port the beacon is broadcast on and a finder listens on.</summary>
        public const int Port = 28742;
        /// <summary>Payload magic. A version bump makes older builds ignore newer beacons.</summary>
        public const string Token = "ARENA-LAN-1";
        /// <summary>Seconds a host that stopped shouting is still offered by a finder.</summary>
        public const double HostLifetime = 4.0;
        /// <summary>How often the host re-shouts, in seconds.</summary>
        public const double BeaconInterval = 0.4;
        /// <summary>How often the host re-reads the networks it shouts at, in seconds.</summary>
        public const double TargetRefreshInterval = 4.0;
    }

    /// <summary>One host a finder heard from, as it was heard.</summary>
    public sealed class LanHost
    {
        public string Address;
        public int Port;
        /// <summary>Seconds on the finder's own clock when the beacon arrived.</summary>
        public double Seen;
    }

    /// <summary>
    /// The host's half of discovery: an ephemeral UDP socket shouting its TCP
    /// port at the broadcast address of every live network interface, plus the
    /// limited broadcast address. It never receives anything, so it can run
    /// beside a finder on the same machine.
    /// </summary>
    public sealed class LanBeacon : IDisposable
    {
        private readonly int hostPort;
        private readonly int discoveryPort;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<IPEndPoint> targets = new List<IPEndPoint>();
        private UdpClient socket;
        private double nextSend;
        private double nextRefresh;

        /// <summary>Why the beacon is not running, or null when it is.</summary>
        public string Error { get; private set; }

        public LanBeacon(int hostPort, int discoveryPort = LanDiscovery.Port)
        {
            this.hostPort = hostPort;
            this.discoveryPort = discoveryPort;
            try
            {
                // Source port 0 on purpose: only the joining phone binds the
                // discovery port, so two sessions on one machine never fight
                // over it.
                socket = new UdpClient(0);
                socket.EnableBroadcast = true;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                socket = null;
            }
            // The addresses to shout at have to exist before the first Tick:
            // a beacon that was never aimed anywhere shouts into nothing.
            RefreshTargets();
        }

        /// <summary>The addresses this beacon is currently shouting at (also the test's handle on it).</summary>
        public IReadOnlyList<IPEndPoint> Targets => targets;

        /// <summary>
        /// Addresses to shout at: the limited broadcast plus each interface's
        /// own subnet broadcast, which is the one that survives routers and
        /// APs that drop 255.255.255.255.
        /// </summary>
        public void RefreshTargets()
        {
            targets.Clear();
            targets.Add(new IPEndPoint(IPAddress.Broadcast, discoveryPort));
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                    if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var info in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IPAddress.IsLoopback(info.Address)) continue;
                        byte[] ip = info.Address.GetAddressBytes();
                        byte[] mask = info.IPv4Mask != null ? info.IPv4Mask.GetAddressBytes() : null;
                        if (mask == null || mask.Length != 4) continue;
                        var broadcast = new byte[4];
                        for (int i = 0; i < 4; i++) broadcast[i] = (byte)((ip[i] & mask[i]) | ~mask[i]);
                        targets.Add(new IPEndPoint(new IPAddress(broadcast), discoveryPort));
                    }
                }
            }
            catch (Exception ex) when (ex is NetworkInformationException || ex is SocketException)
            {
                // A network API that refuses to talk is not fatal: the limited
                // broadcast address is already in the list.
                Error = Error ?? ex.Message;
            }
        }

        /// <summary>Shouts at most once per <see cref="LanDiscovery.BeaconInterval"/>.</summary>
        public void Tick()
        {
            if (socket == null) return;
            double now = clock.Elapsed.TotalSeconds;
            // The set of networks moves while a host waits (a phone can join the
            // hotspot, or drop to its own data), so it is re-read now and then.
            if (now >= nextRefresh)
            {
                nextRefresh = now + LanDiscovery.TargetRefreshInterval;
                RefreshTargets();
            }
            if (now < nextSend) return;
            nextSend = now + LanDiscovery.BeaconInterval;
            foreach (var target in targets) Send(target);
        }

        /// <summary>One datagram to one address. This is what tests drive, and the directed half of <see cref="Tick"/>.</summary>
        public void SendTo(IPAddress address)
        {
            Send(new IPEndPoint(address, discoveryPort));
        }

        private void Send(IPEndPoint target)
        {
            if (socket == null) return;
            byte[] payload = Encoding.UTF8.GetBytes(LanDiscovery.Token + " " + hostPort);
            try { socket.Send(payload, payload.Length, target); }
            catch (SocketException)
            {
                // One network refusing a datagram must not stop the others.
            }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            try { socket?.Close(); } catch (SocketException) { }
            socket = null;
        }
    }

    /// <summary>
    /// The joining phone's half of discovery: binds the discovery port and
    /// collects the hosts shouting on it. Malformed or foreign datagrams are
    /// ignored rather than trusted, and a host that goes quiet drops off the
    /// list on its own.
    /// </summary>
    public sealed class LanFinder : IDisposable
    {
        private const int MaxDatagram = 512;
        private readonly int discoveryPort;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<LanHost> hosts = new List<LanHost>();
        private UdpClient socket;
        private readonly byte[] scratch = new byte[MaxDatagram];

        /// <summary>Why discovery is unavailable, or null when it is listening.</summary>
        public string Error { get; private set; }

        public LanFinder(int discoveryPort = LanDiscovery.Port)
        {
            this.discoveryPort = discoveryPort;
            try
            {
                socket = new UdpClient();
                // Two guests on one network (and a rerun on one machine) both
                // want to listen, so sharing the port is allowed.
                socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
                socket.Client.Blocking = false;
            }
            catch (Exception ex)
            {
                Error = "Discovery unavailable: " + ex.Message;
                try { socket?.Close(); } catch (SocketException) { }
                socket = null;
            }
        }

        /// <summary>Hosts heard from recently, most recent first.</summary>
        public IReadOnlyList<LanHost> Hosts => hosts;

        /// <summary>The most recently heard live host, or null.</summary>
        public LanHost Best
        {
            get
            {
                LanHost best = null;
                for (int i = 0; i < hosts.Count; i++)
                    if (best == null || hosts[i].Seen > best.Seen) best = hosts[i];
                return best;
            }
        }

        /// <summary>Drains whatever arrived and forgets hosts that went quiet.</summary>
        public void Poll()
        {
            if (socket != null)
            {
                for (int i = 0; i < 16; i++)
                {
                    int available;
                    try { available = socket.Available; }
                    catch (SocketException) { break; }
                    if (available <= 0) break;
                    try
                    {
                        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        int count = socket.Client.ReceiveFrom(scratch, 0,
                            Math.Min(scratch.Length, available), SocketFlags.None, ref from);
                        if (count > 0 && from is IPEndPoint endpoint) Observe(scratch, count, endpoint.Address);
                    }
                    catch (SocketException) { break; }
                }
            }

            double now = clock.Elapsed.TotalSeconds;
            for (int i = hosts.Count - 1; i >= 0; i--)
                if (now - hosts[i].Seen > LanDiscovery.HostLifetime) hosts.RemoveAt(i);
        }

        /// <summary>Reads one datagram. Returns true when it was a live beacon.</summary>
        public bool Observe(byte[] datagram, IPAddress from)
        {
            return Observe(datagram, datagram.Length, from);
        }

        private bool Observe(byte[] datagram, int count, IPAddress from)
        {
            if (datagram == null || count <= 0 || from == null) return false;
            int usable = Math.Min(count, datagram.Length);
            string text = Encoding.UTF8.GetString(datagram, 0, usable).Trim();
            string[] parts = text.Split(' ');
            if (parts.Length != 2 || parts[0] != LanDiscovery.Token) return false;
            if (!int.TryParse(parts[1], out int port) || port <= 0 || port > 65535) return false;

            string address = from.ToString();
            for (int i = 0; i < hosts.Count; i++)
            {
                if (hosts[i].Address != address || hosts[i].Port != port) continue;
                hosts[i].Seen = clock.Elapsed.TotalSeconds;
                return true;
            }
            hosts.Add(new LanHost
            {
                Address = address,
                Port = port,
                Seen = clock.Elapsed.TotalSeconds
            });
            return true;
        }

        public void Dispose()
        {
            try { socket?.Close(); } catch (SocketException) { }
            socket = null;
        }
    }
}
