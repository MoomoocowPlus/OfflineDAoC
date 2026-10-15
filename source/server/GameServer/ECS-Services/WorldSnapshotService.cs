using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using DOL.AI;
using DOL.Events;
using DOL.GS.Keeps;
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
    ///        {"cmd":"entities","region":<ushort>} -> snapshot v1 per-region entity dump (on-demand only)
    ///        {"cmd":"intent","target":"<botId|internalId>","action":"move_to|follow|attack|stop",
    ///         [x,y,z,region,victim],"ttl_ticks":N} -> {"type":"intent_ack","ok":bool,"reason":str}
    ///              routed to the target NcaBrain via NcaBrainRegistry (applied on its next think)
    ///   out (v1): {"type":"entities","tick":N,"region":id,"count":n,"total":n,"truncated":bool,
    ///              "entities":[{"id","name","kind","realm","x","y","z","hp","alive","target"}]}
    ///              {"type":"event","kind":"death|keep_taken|relic_stolen|relic_mounted","tick":N,
    ///               "utc":"..","data":{...}}   -- reward feed; new clients replay last <=64 events.
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

        // Reward feed (snapshot v1): bounded event ring + handler registration latch.
        private const int MaxEntitiesPerReply = 2000;
        private const int MaxRingEvents = 512;
        private const int ReplayOnConnect = 64;
        private static readonly ConcurrentQueue<string> _eventRing = new();
        private static readonly object _feedSync = new();
        private static bool _feedRegistered;

        public long TickCounter => Interlocked.Read(ref _tickCounter);
        public int ClientCount => _clients.Count;

        [GameServerStartedEvent]
        public static void Start(DOLEvent e, object sender, EventArgs args)
        {
            RegisterFeedHandlers();
            Instance.StartListener();
        }

        [GameServerStoppedEvent]
        public static void Stop(DOLEvent e, object sender, EventArgs args)
        {
            Instance.StopListener();
            UnregisterFeedHandlers();
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

                // Replay last <=64 buffered events before the reader thread starts; the send
                // lock keeps live fan-out lines from interleaving mid-replay. Ceiling: an event
                // fanning out between TryAdd and the ring snapshot can be delivered twice
                // (sub-millisecond window; a gap is possible neither way round this ordering).
                lock (client.SendLock)
                {
                    string[] replay = _eventRing.ToArray();
                    int start = Math.Max(0, replay.Length - ReplayOnConnect);

                    for (int i = start; i < replay.Length; i++)
                        client.SendLine(replay[i]);

                    if (log.IsDebugEnabled)
                        log.Debug($"{nameof(WorldSnapshotService)} replayed {replay.Length - start} events");
                }

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
                case "entities":
                    client.SendLine(BuildEntitiesJson(doc.RootElement));
                    break;
                case "intent":
                    HandleIntent(client, doc.RootElement);
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

        private static string KindOf(GameObject obj) => obj is GameBot ? "bot" : obj is GamePlayer ? "player" : "npc";

        /// <summary>
        /// Game-loop thread only. Intent v0 ingress: {"cmd":"intent","target":"<botId or internalId>",
        /// "action":"move_to|follow|attack|stop","x":..,"y":..,"z":..,"region":..,"victim":<objectId>,
        /// "ttl_ticks":N} -> validate (whitelisted action, coordinate bounds, existing zone, TTL clamp)
        /// and post to <see cref="NcaBrainRegistry"/> keyed by body InternalID. The target brain applies
        /// it on a later think tick (NpcService step, before Attack/Movement services in the same tick).
        /// Unknown target/action or missing fields -> safe no-op + intent_ack with ok=false.
        /// </summary>
        private static void HandleIntent(GmClient client, JsonElement root)
        {
            string target = root.TryGetProperty("target", out JsonElement targetElement) && targetElement.ValueKind == JsonValueKind.String
                ? targetElement.GetString()
                : null;

            if (!NcaBrainRegistry.TryResolveTarget(target, out NcaBrain brain))
            {
                if (log.IsDebugEnabled)
                    log.Debug($"{nameof(WorldSnapshotService)} intent rejected: unknown target '{target}'");

                client.SendLine(JsonSerializer.Serialize(new { type = "intent_ack", ok = false, reason = $"unknown nca target '{target}'" }));
                return;
            }

            string action = root.TryGetProperty("action", out JsonElement actionElement) && actionElement.ValueKind == JsonValueKind.String
                ? actionElement.GetString()
                : null;

            if (!NcaBrainRegistry.IsKnownAction(action))
            {
                client.SendLine(JsonSerializer.Serialize(new { type = "intent_ack", ok = false, reason = $"unknown action '{action}' (move_to|follow|attack|stop)" }));
                return;
            }

            int ttl = NcaBrainRegistry.DefaultTtlTicks;

            if (root.TryGetProperty("ttl_ticks", out JsonElement ttlElement) && ttlElement.ValueKind == JsonValueKind.Number)
                ttl = Math.Clamp(ttlElement.GetInt32(), 1, NcaBrainRegistry.MaxTtlTicks);

            // Defaults = the body's current state, so "stop" and partial move_to payloads are valid.
            ushort region = brain.Body.CurrentRegionID;
            int x = brain.Body.X;
            int y = brain.Body.Y;
            int z = brain.Body.Z;
            int victim = 0;

            if (action == NcaBrainRegistry.ActionMoveTo)
            {
                if (!TryGetInt(root, "region", out int requestedRegion) || !TryGetInt(root, "x", out x) || !TryGetInt(root, "y", out y))
                {
                    client.SendLine(JsonSerializer.Serialize(new { type = "intent_ack", ok = false, reason = "move_to needs region, x and y" }));
                    return;
                }

                if (requestedRegion < 0 || requestedRegion > ushort.MaxValue)
                {
                    client.SendLine(JsonSerializer.Serialize(new { type = "intent_ack", ok = false, reason = $"bad region {requestedRegion}" }));
                    return;
                }

                region = (ushort)requestedRegion;

                if (root.TryGetProperty("z", out JsonElement zElement) && zElement.ValueKind == JsonValueKind.Number)
                    z = zElement.GetInt32();

                if (x < 0 || x > NcaBrainRegistry.MaxHorizontalCoord || y < 0 || y > NcaBrainRegistry.MaxHorizontalCoord ||
                    z < -NcaBrainRegistry.MaxVerticalDelta || z > NcaBrainRegistry.MaxVerticalDelta)
                {
                    client.SendLine(JsonSerializer.Serialize(new { type = "intent_ack", ok = false, reason = "coordinates out of range" }));
                    return;
                }

                if (WorldMgr.GetRegion(region)?.GetZone(x, y) == null)
                {
                    client.SendLine(JsonSerializer.Serialize(new { type = "intent_ack", ok = false, reason = $"no zone at {region}:{x},{y}" }));
                    return;
                }
            }
            else if (action is NcaBrainRegistry.ActionFollow or NcaBrainRegistry.ActionAttack)
            {
                if (!TryGetInt(root, "victim", out victim) || victim <= 0)
                {
                    client.SendLine(JsonSerializer.Serialize(new { type = "intent_ack", ok = false, reason = $"{action} needs a victim object id" }));
                    return;
                }
            }

            NcaBrainRegistry.Post(NcaBrainRegistry.KeyOf(brain), new NcaBrainRegistry.NcaIntent(action, region, x, y, z, victim, ttl));

            if (log.IsDebugEnabled)
                log.Debug($"{nameof(WorldSnapshotService)} intent accepted: {action} for '{target}' ttl {ttl}");

            client.SendLine(JsonSerializer.Serialize(new { type = "intent_ack", ok = true, reason = $"{action} accepted for {target}, ttl {ttl}" }));
        }

        private static bool TryGetInt(JsonElement root, string name, out int value)
        {
            value = 0;
            return root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
        }

        private static string IdOf(GameObject obj) => obj.ObjectID != 0 ? obj.ObjectID.ToString() : obj.InternalID ?? "";

        /// <summary>Game-loop thread only. Snapshot v1: on-demand per-region entity dump; no per-tick cost.
        /// Optional exact "name" filter (additive v0.1) for probes; unfiltered calls keep the v0 shape.</summary>
        private static string BuildEntitiesJson(JsonElement root)
        {
            if (!root.TryGetProperty("region", out JsonElement regionElement)
                || regionElement.ValueKind != JsonValueKind.Number
                || !regionElement.TryGetUInt16(out ushort regionId))
                return JsonSerializer.Serialize(new { type = "error", message = "missing region id" });

            string nameFilter = root.TryGetProperty("name", out JsonElement nameElement) && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()
                : null;

            if (!WorldMgr.Regions.TryGetValue(regionId, out Region region) || region == null)
                return JsonSerializer.Serialize(new { type = "error", message = $"no region {regionId}" });

            GameObject[] objects = region.Objects;
            List<object> entities = new();
            int total = 0;

            if (objects != null)
            {
                for (int i = 0; i < objects.Length; i++)
                {
                    GameObject obj = objects[i];

                    if (obj == null)
                        continue;

                    total++;

                    if (entities.Count >= MaxEntitiesPerReply)
                        continue;

                    if (nameFilter != null && !string.Equals(obj.Name, nameFilter, StringComparison.Ordinal))
                        continue;

                    entities.Add(BuildEntity(regionId, i, obj));
                }
            }

            return JsonSerializer.Serialize(new
            {
                type = "entities",
                tick = Instance.TickCounter,
                region = regionId,
                count = entities.Count,
                total,
                truncated = total > entities.Count,
                entities
            });
        }

        private static object BuildEntity(ushort regionId, int index, GameObject obj)
        {
            // ponytail: hp 1.0 / alive true for non-living objects (doors etc.); refine if NCA needs them
            double hp = 1.0;
            bool alive = true;
            string target = null;

            if (obj is GameLiving living)
            {
                int maxHealth = living.MaxHealth;
                hp = maxHealth > 0 ? Math.Clamp(living.Health / (double)maxHealth, 0.0, 1.0) : 1.0;
                alive = living.IsAlive;

                if (living.TargetObject != null)
                    target = living.TargetObject.ObjectID.ToString();
            }

            return new
            {
                id = obj.ObjectID != 0 ? obj.ObjectID.ToString() : $"{regionId}:{index}",
                name = obj.Name ?? "",
                kind = KindOf(obj),
                realm = (int)obj.Realm,
                x = obj.X,
                y = obj.Y,
                z = obj.Z,
                hp,
                alive,
                target
            };
        }

        #region Reward / event feed (snapshot v1)

        private static void RegisterFeedHandlers()
        {
            while (_eventRing.TryDequeue(out _)) { } // never replay a previous server session's events

            lock (_feedSync)
            {
                if (_feedRegistered)
                    return;

                // The legacy chain throws on removing an absent delegate; pair add/remove with the flag.
                GameEventMgr.AddHandlerUnique(GameLivingEvent.Dying, OnLivingDying);
                GameEventMgr.AddHandlerUnique(KeepEvent.KeepTaken, OnKeepTaken);
                GameEventMgr.AddHandlerUnique(RelicPadEvent.RelicStolen, OnRelicPadChanged);
                GameEventMgr.AddHandlerUnique(RelicPadEvent.RelicMounted, OnRelicPadChanged);
                _feedRegistered = true;
            }
        }

        private static void UnregisterFeedHandlers()
        {
            lock (_feedSync)
            {
                if (!_feedRegistered)
                    return;

                GameEventMgr.RemoveHandler(GameLivingEvent.Dying, OnLivingDying);
                GameEventMgr.RemoveHandler(KeepEvent.KeepTaken, OnKeepTaken);
                GameEventMgr.RemoveHandler(RelicPadEvent.RelicStolen, OnRelicPadChanged);
                GameEventMgr.RemoveHandler(RelicPadEvent.RelicMounted, OnRelicPadChanged);
                _feedRegistered = false;
            }
        }

        /// <summary>
        /// Serialize once, ring-buffer (cap MaxRingEvents, oldest dropped), fan out to connected
        /// clients. Called from the notifying thread (death/keep/relic fire on the game-loop
        /// thread); must never throw into it — every failure is swallowed at Debug.
        /// </summary>
        private void EmitEvent(string kind, object data)
        {
            try
            {
                string line = JsonSerializer.Serialize(new
                {
                    type = "event",
                    kind,
                    tick = TickCounter,
                    utc = DateTime.UtcNow.ToString("O"),
                    data
                });

                _eventRing.Enqueue(line);
                while (_eventRing.Count > MaxRingEvents && _eventRing.TryDequeue(out _)) { }

                foreach (GmClient client in _clients.Keys)
                    client.SendLine(line);
            }
            catch (Exception ex)
            {
                if (log.IsDebugEnabled)
                    log.Debug($"{nameof(WorldSnapshotService)} event '{kind}' dropped", ex);
            }
        }

        private static void OnLivingDying(DOLEvent e, object sender, EventArgs args)
        {
            try
            {
                // Player-side deaths only; NPC grinding deaths would flood the ring.
                if (sender is not (GamePlayer or GameBot) || args is not DyingEventArgs dying)
                    return;

                GameObject victim = (GameObject)sender;
                GameObject killer = dying.Killer;

                // InternalID gives reward attribution a session-stable key (ObjectID churns per boot).
                // GameBot hides GameObject.InternalID ("public new"), so read the bot property first.
                static string InternalIdOf(GameObject o) => o switch
                {
                    GameBot bot => bot.InternalID,
                    GameLiving living => living.InternalID,
                    _ => null,
                };

                string victimInternal = InternalIdOf(victim);
                string killerInternal = InternalIdOf(killer);

                Instance.EmitEvent("death", new
                {
                    victim = IdOf(victim),
                    victimInternal,
                    victimName = victim.Name ?? "",
                    victimKind = KindOf(victim),
                    victimRealm = (int)victim.Realm,
                    killer = killer == null ? null : IdOf(killer),
                    killerInternal,
                    killerName = killer?.Name,
                    killerKind = killer == null ? null : KindOf(killer),
                    killerRealm = killer == null ? (int?)null : (int)killer.Realm,
                    region = victim.CurrentRegionID,
                    x = victim.X,
                    y = victim.Y,
                    z = victim.Z
                });
            }
            catch (Exception ex)
            {
                if (log.IsDebugEnabled)
                    log.Debug($"{nameof(WorldSnapshotService)} death feed failed", ex);
            }
        }

        private static void OnKeepTaken(DOLEvent e, object sender, EventArgs args)
        {
            try
            {
                if (args is not KeepEventArgs { Keep: not null } taken)
                    return;

                AbstractGameKeep keep = taken.Keep;

                Instance.EmitEvent("keep_taken", new
                {
                    keepId = keep.KeepID,
                    name = keep.Name ?? "",
                    oldRealm = (int)taken.Realm,
                    newRealm = (int)keep.Realm,
                    region = keep.Region,
                    x = keep.X,
                    y = keep.Y,
                    z = keep.Z
                });
            }
            catch (Exception ex)
            {
                if (log.IsDebugEnabled)
                    log.Debug($"{nameof(WorldSnapshotService)} keep feed failed", ex);
            }
        }

        private static void OnRelicPadChanged(DOLEvent e, object sender, EventArgs args)
        {
            try
            {
                if (sender is not GameRelicPad pad || args is not RelicPadEventArgs { Relic: not null } changed)
                    return;

                bool stolen = e == RelicPadEvent.RelicStolen;
                GameRelic relic = changed.Relic;
                GameLiving actor = changed.Actor;
                GameObject at = actor != null ? actor : pad;

                Instance.EmitEvent(stolen ? "relic_stolen" : "relic_mounted", new
                {
                    relic = relic.Name ?? "",
                    type = relic.RelicType.ToString(),
                    realmFrom = stolen ? (int)pad.Realm : (int)(actor?.Realm ?? eRealm.None),
                    realmTo = stolen ? (int)(actor?.Realm ?? eRealm.None) : (int)pad.Realm,
                    actor = actor == null ? null : IdOf(actor),
                    actorName = actor?.Name,
                    region = at.CurrentRegionID,
                    x = at.X,
                    y = at.Y,
                    z = at.Z
                });
            }
            catch (Exception ex)
            {
                if (log.IsDebugEnabled)
                    log.Debug($"{nameof(WorldSnapshotService)} relic feed failed", ex);
            }
        }

        #endregion
    }
}
