using System;
using System.Numerics;
using DOL.AI;
using DOL.Events;
using DOL.Logging;

namespace DOL.GS
{
    /// <summary>
    /// ONE NCA-controlled probe (v0): a single autonomous <c>GameBot</c> driven by
    /// <see cref="NcaBrain"/>, spawned once per server start in frontier region 1
    /// (Albion). Follows the <c>AutonomousPopulationController</c> spawning pattern:
    /// post the spawn work to <c>NpcService</c> so it runs on the game-loop thread.
    ///
    /// Persistence decision: the <c>OfflineWorldBotRecord</c> is created in memory with
    /// <c>IsRetired = true</c>. The status flush therefore writes one inert row that the
    /// population roster never auto-spawns (its query filters IsRetired = false,
    /// AutonomousPopulationController.RefreshControlPlane) and the launcher shows as
    /// deletion-requested. The bot body itself persists like other bots.
    ///
    /// Watchdog: stuck-recovery is only driven from <c>BotBrain</c>
    /// (bots/BotBrain.cs:1226), which NcaBrain replaces — no 15-minute capital
    /// relocation for this bot.
    /// </summary>
    public static class NcaTestBot
    {
        public const long BotId = 900001;
        public const string Name = "NcaProbe";

        private const ushort RegionId = 1; // Albion, frontier world set
        // Known-walkable Mob rows in region 1 (world DB), across several loaded zones; the
        // first one whose floor snaps onto the navmesh wins. No capital fallback: region 10
        // is not part of this frontier world set, so a bad spot must skip the spawn instead.
        private static readonly (int X, int Y, int Z)[] CandidateSpots =
        {
            (488120, 609958, 1624), // Campacorentin Forest, 'river spriteling'
            (473405, 627963, 2048), // Avalon Marsh, 'Wylie Edyn'
            (553515, 608640, 2908), // Salisbury Plains, 'spriggarn ambusher'
            (524466, 602677, 1968), // Campacorentin Forest
            (488315, 440689, 5315), // Black Mtns. South, 'goblin shaman'
        };

        private static readonly Logger log = LoggerManager.Create(typeof(NcaTestBot));

        [GameServerStartedEvent]
        public static void OnServerStarted(DOLEvent e, object sender, EventArgs args)
        {
            NpcService.Instance.Post(SpawnOnGameLoop, 0);
        }

        /// <summary>Game-loop thread (posted via NpcService).</summary>
        private static void SpawnOnGameLoop(int state)
        {
            try
            {
                if (WorldMgr.GetRegion(RegionId) == null)
                {
                    log.Error($"{nameof(NcaTestBot)} region {RegionId} not loaded; skipping spawn");
                    return;
                }

                OfflineWorldBotRecord record = new()
                {
                    BotId = BotId,
                    Name = Name,
                    Realm = (int)eRealm.Albion,
                    ClassId = (int)eCharacterClass.Armsman,
                    ClassName = "Armsman",
                    RaceId = (int)eRace.Briton,
                    RaceName = "Briton",
                    Gender = 0,
                    Level = 5,
                    RegionId = RegionId,
                    X = CandidateSpots[0].X,
                    Y = CandidateSpots[0].Y,
                    Z = CandidateSpots[0].Z,
                    IsRetired = true,
                    Activity = "NCA probe",
                    LastUpdateUtc = DateTime.UtcNow.ToString("O"),
                };

                if (!TryPlaceOnWalkableFloor(record))
                {
                    log.Error($"{nameof(NcaTestBot)} no candidate spot in region {RegionId} snapped onto a walkable mesh; skipping spawn");
                    return;
                }

                GameBot bot = new(record, null);
                bot.Health = bot.MaxHealth;
                bot.Mana = bot.MaxMana;
                bot.Endurance = bot.MaxEndurance;

                if (!bot.AddToWorld())
                {
                    log.Error($"{nameof(NcaTestBot)} AddToWorld failed at {RegionId}:{bot.X},{bot.Y}");
                    return;
                }

                // Replace the stock BotBrain (override boundary; SetOwnBrain starts the new brain).
                bot.SetOwnBrain(new NcaBrain());

                log.Info($"{nameof(NcaTestBot)} '{Name}' spawned in region {RegionId} at {bot.X},{bot.Y},{bot.Z} with NcaBrain");
            }
            catch (Exception exception)
            {
                log.Error($"{nameof(NcaTestBot)} spawn failed", exception);
            }
        }

        /// <summary>
        /// Places the record's coordinates onto a verified walkable mesh floor. Returns false when
        /// no candidate works (the caller skips the spawn). Never relocates to a capital: region 10
        /// is not loaded in this frontier world set.
        /// </summary>
        private static bool TryPlaceOnWalkableFloor(OfflineWorldBotRecord record)
        {
            Region region = WorldMgr.GetRegion((ushort)record.RegionId);
            var nav = PathfindingProvider.Instance;

            if (region == null)
                return false;

            foreach ((int x, int y, int z) in CandidateSpots)
            {
                Zone zone = region.GetZone(x, y);

                if (zone == null)
                    continue;

                int px = x, py = y, pz = z;

                if (nav.IsAvailable && nav.HasNavmesh(zone))
                {
                    Vector3 snapped = new(x, y, z);

                    if (!nav.TrySnapToMesh(zone, ref snapped, 768f))
                        continue;

                    px = (int)Math.Round(snapped.X);
                    py = (int)Math.Round(snapped.Y);
                    pz = (int)Math.Round(snapped.Z);
                }

                record.X = px;
                record.Y = py;
                record.Z = pz;
                return true;
            }
            return false;
        }
    }
}
