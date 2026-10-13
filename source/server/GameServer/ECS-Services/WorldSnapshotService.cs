using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using DOL.Events;
using DOL.Logging;

namespace DOL.GS
{
    /// <summary>
    /// Living-world bridge (Phase 5.1-5.3, v0). Publishes region/entity snapshots at ~10 Hz over a
    /// Unix domain socket and accepts newline-delimited JSON commands from the AI-GM middleware.
    ///
    /// Threading contract: snapshot BUILD + command DISPATCH happen only on the game-loop thread
    /// (Tick). All socket I/O happens on background threads; commands arrive via a queue and are
    /// drained in Tick, so a slow consumer can never stall a tick. Never touch game state off the
    /// tick thread.
    ///
    /// Transport: <c>/tmp/opendaoc_gm.sock</c>, newline-delimited JSON both directions.
    /// v0 message shapes:
    ///   out: {"type":"snapshot","tick":N,"utc":"..","regions":[{"id":..,"name":..,"p":..,"b":..,"n":..,"pA":..,"pM":..,"pH":..}]}
    ///   in : {"cmd":"ping"}                      -> {"type":"pong","tick":N}
    ///        {"cmd":"snapshot"}                  -> one immediate snapshot to that client
    /// MessagePack + richer commands arrive with Phase 5.3's executor; JSON first for debuggability (nc -U).
    /// </summary>
    public sealed class WorldSnapshotService : GameServiceBase
    {
        private const string SocketPath = "/tmp/opendaoc_gm.sock";
        private const int PublishEveryTicks = 3; // ~10 Hz at 30 Hz tick rate.

        private static readonly Logger log = LoggerManager.Create(typeof(WorldSnapshotService));

        public static WorldSnapshotService Instance { get; } = new();

        private sealed class GmClient
        {
            public readonly Socket Socket;
            public readonly object SendLock = new();
            public int Closed;

            public GmClient(Socket socket)
            {
                Socket = socket;
            }

            public void SendLine(string line)
            {
                if (Volatile.Read(ref Closed) != 0)
                    return;

                try
                {
                    lock (SendLock)
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                        Socket.Send(bytes);
                    }
                }
                catch (Exception)
                {
                    Close();
                }
            }

            public void Close()
            {
                if (Interlocked.Exchange(ref Closed, 1) != 0)
                    return;

                try { Socket.Shutdown(SocketShutdown.Both); } catch (Exception) { }
                try { Socket.Dispose(); } catch (Exception) { }
            }
        }

        private Socket _listener;
        private Thread _acceptThread;
        private readonly ConcurrentDictionary<GmClient, byte> _clients = new();
        private readonly ConcurrentQueue<(GmClient Client, string Line)> _commands = new();
        private long _tickCounter;

        public long TickCounter => Interlocked.Read(ref _tickCounter);
        public int ClientCount => _clients.Count;

        [GameServerStartedEvent]
        public static void Start(DOLEvent e, object sender, EventArgs args)
        {
            Instance.StartListener();
        }

        [GameServerStoppedEvent]
        public static void Stop(DOLEvent e, object sender, EventArgs args)
        {
            Instance.StopListener();
        }

        private void StartListener()
        {
            StopListener();

            try
            {
                if (File.Exists(SocketPath))
                    File.Delete(SocketPath);

                _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
                _listener.Listen(8);

                _acceptThread = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "WorldSnapshotService.Accept"
                };
                _acceptThread.Start();

                if (log.IsInfoEnabled)
                    log.Info($"{nameof(WorldSnapshotService)} listening on {SocketPath}");
            }
            catch (Exception ex)
            {
                log.Error($"{nameof(WorldSnapshotService)} failed to start listener", ex);
            }
        }

        private void StopListener()
        {
            try { _listener?.Dispose(); } catch (Exception) { }
            _listener = null;

            foreach (GmClient client in _clients.Keys)
                client.Close();

            _clients.Clear();

            while (_commands.TryDequeue(out _)) { }

            try
            {
                if (File.Exists(SocketPath))
                    File.Delete(SocketPath);
            }
            catch (Exception) { }
        }

        private void AcceptLoop()
        {
            while (true)
            {
                Socket socket;

                try
                {
                    socket = _listener.Accept();
                }
                catch (Exception)
                {
                    return; // Listener disposed on shutdown.
                }

                GmClient client = new(socket);
                _clients.TryAdd(client, 0);

                Thread reader = new(() => ReadLoop(client))
                {
                    IsBackground = true,
                    Name = "WorldSnapshotService.Read"
                };
                reader.Start();

                if (log.IsInfoEnabled)
                    log.Info($"{nameof(WorldSnapshotService)} client connected ({_clients.Count} total)");
            }
        }

        private void ReadLoop(GmClient client)
        {
            try
            {
                byte[] buffer = new byte[8192];
                StringBuilder pending = new();

                while (true)
                {
                    int read = client.Socket.Receive(buffer);

                    if (read <= 0)
                        break;

                    pending.Append(Encoding.UTF8.GetString(buffer, 0, read));

                    // Guard against unbounded junk from a misbehaving client.
                    if (pending.Length > 64 * 1024)
                        break;

                    while (true)
                    {
                        string all = pending.ToString();
                        int newline = all.IndexOf('\n');

                        if (newline < 0)
                            break;

                        string line = all[..newline].Trim();
                        pending.Remove(0, newline + 1);

                        if (line.Length != 0)
                            _commands.Enqueue((client, line));
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                client.Close();
                _clients.TryRemove(client, out _);

                if (log.IsInfoEnabled)
                    log.Info($"{nameof(WorldSnapshotService)} client disconnected ({_clients.Count} left)");
            }
        }

        public override void Tick()
        {
            ProcessPostedActionsParallel();

            Interlocked.Increment(ref _tickCounter);

            DrainCommands();

            if (_tickCounter % PublishEveryTicks == 0)
                PublishSnapshot();
        }

        /// <summary>Game-loop thread only.</summary>
        private void DrainCommands()
        {
            // Bounded per tick so a command flood cannot lengthen the tick unboundedly.
            for (int i = 0; i < 64 && _commands.TryDequeue(out (GmClient Client, string Line) item); i++)
            {
                try
                {
                    DispatchCommand(item.Client, item.Line);
                }
                catch (Exception ex)
                {
                    log.Error($"{nameof(WorldSnapshotService)} command failed: {item.Line}", ex);
                    item.Client.SendLine(JsonSerializer.Serialize(new { type = "error", message = ex.Message }));
                }
            }
        }

        /// <summary>Game-loop thread only.</summary>
        private void DispatchCommand(GmClient client, string line)
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            string cmd = doc.RootElement.TryGetProperty("cmd", out JsonElement cmdElement) ? cmdElement.GetString() : null;

            switch (cmd)
            {
                case "ping":
                    client.SendLine(JsonSerializer.Serialize(new { type = "pong", tick = TickCounter }));
                    break;
                case "snapshot":
                    client.SendLine(BuildSnapshotJson());
                    break;
                default:
                    client.SendLine(JsonSerializer.Serialize(new { type = "error", message = $"unknown cmd '{cmd}'" }));
                    break;
            }
        }

        /// <summary>Game-loop thread only. Builds and fans out one snapshot line.</summary>
        private void PublishSnapshot()
        {
            if (_clients.IsEmpty)
                return;

            string json = BuildSnapshotJson();

            foreach (GmClient client in _clients.Keys)
                client.SendLine(json);
        }

        private readonly record struct RegionSnapshot(ushort id, string name, int p, int b, int n, int pA, int pM, int pH);

        /// <summary>Game-loop thread only. Counts are a direct scan of the region object arrays; keep trivial per-object work here.</summary>
        private string BuildSnapshotJson()
        {
            List<RegionSnapshot> regions = new();
            int totalPlayers = 0, totalBots = 0;

            foreach (KeyValuePair<ushort, Region> entry in WorldMgr.Regions)
            {
                Region region = entry.Value;

                if (region == null)
                    continue;

                GameObject[] objects = region.Objects;

                if (objects == null)
                    continue;

                int players = 0, bots = 0, npcs = 0, alb = 0, mid = 0, hib = 0;

                for (int i = 0; i < objects.Length; i++)
                {
                    GameObject obj = objects[i];

                    if (obj == null)
                        continue;

                    if (obj is GameBot)
                    {
                        bots++;
                    }
                    else if (obj is GamePlayer player)
                    {
                        players++;

                        switch (player.Realm)
                        {
                            case eRealm.Albion: alb++; break;
                            case eRealm.Midgard: mid++; break;
                            case eRealm.Hibernia: hib++; break;
                        }
                    }
                    else if (obj is GameNPC)
                    {
                        npcs++;
                    }
                }

                if (players == 0 && bots == 0 && npcs == 0)
                    continue;

                totalPlayers += players;
                totalBots += bots;
                regions.Add(new RegionSnapshot(entry.Key, region.Description ?? region.Name, players, bots, npcs, alb, mid, hib));
            }

            return JsonSerializer.Serialize(new
            {
                type = "snapshot",
                tick = TickCounter,
                utc = DateTime.UtcNow.ToString("O"),
                players = totalPlayers,
                bots = totalBots,
                regions
            });
        }
    }
}
