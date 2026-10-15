using System;
using System.Collections.Concurrent;
using DOL.GS;

namespace DOL.AI
{
    /// <summary>
    /// Intent v0 ingress registry for the NCA living-world loop (server half).
    ///
    /// Fed by <c>WorldSnapshotService</c> command dispatch (game-loop thread), consumed by
    /// <see cref="NcaBrain.Think"/> (game-loop thread, NpcService step). Both ends run on the
    /// game-loop thread, but the dictionaries are concurrent anyway so validation can never
    /// race a brain start/stop from another service.
    ///
    /// Keyed by body <c>InternalID</c> (for autonomous bots that is
    /// <c>AutonomousBotEconomy.GetOwnerId(botId)</c>, e.g. "offlinebot:900001").
    /// A slow or absent NCA forward pass never blocks a tick: an unconsumed intent simply
    /// stays pending until the next think, and an expired one is dropped by the brain.
    /// </summary>
    public static class NcaBrainRegistry
    {
        public const string ActionMoveTo = "move_to";
        public const string ActionFollow = "follow";
        public const string ActionAttack = "attack";
        public const string ActionStop = "stop";

        // Ticks are brain thinks (~300 ms). Default ~12 s of intent lifetime; hard cap ~3 min.
        public const int DefaultTtlTicks = 40;
        public const int MaxTtlTicks = 600;

        // ponytail: loose bounds; every shipped region coordinate fits well inside 2^21.
        public const int MaxHorizontalCoord = 1 << 21;
        public const int MaxVerticalDelta = 65536;

        // MaxFollowDistance semantics: farther than this, Follow cancels itself
        // (NpcMovementComponent.FollowTick compares distanceSquared against
        // MaxFollowDistance * MaxFollowDistance in int arithmetic — keep the square
        // inside int.MaxValue, so 32768 is the safe ceiling).
        public const int FollowMinDistance = 64;
        public const int FollowMaxDistance = 32768;

        /// <summary>One pending intent. Coords/region are only meaningful for move_to,
        /// victimObjectId (region ObjectID) only for follow/attack.</summary>
        public sealed record NcaIntent(string Action, ushort Region, int X, int Y, int Z, int VictimObjectId, int TtlTicks);

        private static readonly ConcurrentDictionary<string, NcaBrain> Brains = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, NcaIntent> Pending = new(StringComparer.Ordinal);

        public static string KeyOf(NcaBrain brain)
        {
            GameNPC body = brain?.Body;

            if (body == null)
                return string.Empty;

            // GameBot *hides* GameObject.InternalID ("public new", bots/GameBot.cs:42); the
            // GameObject-level value stays null for bots, so read the bot's own property first.
            return body is GameBot bot
                ? bot.InternalID ?? body.InternalID ?? string.Empty
                : body.InternalID ?? string.Empty;
        }

        public static bool Register(NcaBrain brain) => brain?.Body != null && Brains.TryAdd(KeyOf(brain), brain);

        public static void Unregister(NcaBrain brain)
        {
            if (brain?.Body != null)
                Brains.TryRemove(KeyOf(brain), out _);
        }

        /// <summary>
        /// Accepts a raw InternalID ("offlinebot:900001") or a bare record BotId ("900001").
        /// Unknown or non-NCA targets resolve to false (ingress answers a safe no-op).
        /// </summary>
        public static bool TryResolveTarget(string target, out NcaBrain brain)
        {
            brain = null;

            if (string.IsNullOrEmpty(target))
                return false;

            if (Brains.TryGetValue(target, out brain))
                return brain?.Body != null;

            if (long.TryParse(target, out long botId))
                return Brains.TryGetValue(AutonomousBotEconomy.GetOwnerId(botId), out brain) && brain?.Body != null;

            return false;
        }

        public static void Post(string key, NcaIntent intent) => Pending[key] = intent;

        public static bool TryConsume(string key, out NcaIntent intent) => Pending.TryRemove(key, out intent);

        public static bool IsKnownAction(string action) =>
            action is ActionMoveTo or ActionFollow or ActionAttack or ActionStop;
    }
}
