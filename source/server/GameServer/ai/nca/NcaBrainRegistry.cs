using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DOL.GS;
using DOL.GS.Styles;

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

        // Intent v0.1 verbs (A-lite): applied immediately in WorldSnapshotService.HandleIntent
        // on the game-loop thread — not routed through the pending-intent queue, so the ack
        // carries the real result (spell known? pet present?).
        public const string ActionCast = "cast";
        public const string ActionStyle = "style";
        public const string ActionPetPosture = "pet_posture";
        public const string ActionSummon = "summon";

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
            action is ActionMoveTo or ActionFollow or ActionAttack or ActionStop
                or ActionCast or ActionStyle or ActionPetPosture or ActionSummon;

        // Preferred melee style per body key ("style" verb). Sticky: honored on every melee
        // swing until replaced or cleared (styleid 0). Written from the intent ingress, read
        // by NpcAttackAction before BotMeleeStylePolicy.Select — endurance and execution
        // checks in the normal style path still apply to it. Same game-loop thread on both
        // ends; concurrent dictionary anyway so a peek can never race a style change.
        private static readonly ConcurrentDictionary<string, int> PreferredStyles = new(StringComparer.Ordinal);

        /// <summary>Queue a style (by Skill.ID) for the bot's melee swings; 0 clears. Returns
        /// false + reason when the bot does not know the style.</summary>
        public static bool SetPreferredStyle(GameBot bot, int styleId, out string reason)
        {
            string key = bot?.InternalID ?? string.Empty;

            if (styleId <= 0)
            {
                PreferredStyles.TryRemove(key, out _);
                reason = "preferred style cleared";
                return true;
            }

            List<Style> styles = bot.Styles;
            Style known = null;

            if (styles != null)
            {
                foreach (Style style in styles)
                {
                    if (style != null && style.ID == styleId)
                    {
                        known = style;
                        break;
                    }
                }
            }

            if (known == null)
            {
                // Diagnostic: the ack names what the bot actually knows, so a bad gym payload
                // is distinguishable from an untrained bot body.
                string knownIds = styles == null ? "none" : string.Join(",", styles.Where(s => s != null).Select(s => s.ID).Take(12));
                reason = $"style not known (styles: {knownIds})";
                return false;
            }

            PreferredStyles[key] = styleId;
            reason = $"style '{known.Name}' queued for every swing until replaced";
            return true;
        }

        /// <summary>Lookup used by NpcAttackAction per swing; null-safe, no allocation when unset.</summary>
        public static bool TryGetPreferredStyle(GameBot bot, out Style style)
        {
            style = null;

            if (bot == null || !PreferredStyles.TryGetValue(bot.InternalID ?? string.Empty, out int styleId))
                return false;

            List<Style> styles = bot.Styles;

            if (styles == null)
                return false;

            foreach (Style candidate in styles)
            {
                if (candidate != null && candidate.ID == styleId)
                {
                    style = candidate;
                    return true;
                }
            }

            return false;
        }
    }
}
