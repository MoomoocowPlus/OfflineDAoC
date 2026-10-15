using System;
using System.Collections.Generic;
using DOL.GS;
using DOL.Logging;

namespace DOL.AI
{
    /// <summary>
    /// NCA entity brain (Phase 6 server half, v0). Replaces the stock brain per entity
    /// (<c>GameNPC.SetOwnBrain</c>, gameobjects/GameNPC.cs:2197) — the override boundary:
    /// nothing in the stock bot AI runs beside it.
    ///
    /// <see cref="Think"/> runs on the game-loop thread inside the NpcService tick, which is
    /// ordered before AttackService and MovementService in the same tick
    /// (Managers/GameLoop/GameLoop.cs) — the exact window stock brains use, so every intent
    /// application below is tick-safe by construction.
    ///
    /// Active bodies must be autonomous <c>GameBot</c>s so <c>ABrain.IsActive</c> (ai/ABrain.cs:17)
    /// passes without relaxing the base contract.
    ///
    /// Intent decode (delivered via <see cref="NcaBrainRegistry"/>):
    ///   move_to -> GameObject.MoveTo (gameobjects/GameObject.cs:664; GameBot override releases pets)
    ///   follow  -> GameNPC.Follow (gameobjects/GameNPC.cs:876)
    ///   attack  -> GameNPC.StartAttack (gameobjects/GameNPC.cs:2720, request-queue semantics)
    ///   stop    -> StopMoving (:871) + StopFollowing (:881) + StopAttack (:2711)
    ///
    /// Each intent is applied ONCE when consumed (re-issuing MoveTo every think would re-teleport);
    /// TTL then counts down per think. Expiry is a safe no-op + Debug log — v0 does not revert
    /// server-side follow/attack state on expiry.
    /// </summary>
    public sealed class NcaBrain : ABrain
    {
        public const int RingCap = 256;

        /// <summary>One per-think body observation; same fields as the entities dump.</summary>
        public sealed record Observation(long Tick, ushort Region, int X, int Y, int Z, double Hp, string TargetId, string IntentState);

        private static readonly Logger log = LoggerManager.Create(typeof(NcaBrain));

        private readonly Queue<Observation> _ring = new(RingCap);
        private NcaBrainRegistry.NcaIntent _active;
        private int _activeRemaining;

        private string BodyKey => NcaBrainRegistry.KeyOf(this);

        /// <summary>Read-mostly diagnostics; Think is game-loop only, so the lock only guards cross-thread peeks.</summary>
        public IReadOnlyCollection<Observation> Observations
        {
            get { lock (_ring) return _ring.ToArray(); }
        }

        public NcaBrain()
        {
            // ~300 ms cadence per LIVING-WORLD-GAME-MAPPING.md §1.2; NpcService gates on ShouldTick only.
            ThinkInterval = 300;
        }

        public override bool Start()
        {
            bool started = base.Start();

            if (started)
            {
                NcaBrainRegistry.Register(this);

                if (log.IsDebugEnabled)
                    log.Debug($"NcaBrain started for {Body?.InternalID ?? "(null body)"} active={IsActive} key='{NcaBrainRegistry.KeyOf(this)}'");
            }

            return started;
        }

        public override bool Stop()
        {
            if (log.IsDebugEnabled)
                log.Debug($"NcaBrain stopped for {Body?.InternalID ?? "(null body)"} (was active={IsActive})");

            NcaBrainRegistry.Unregister(this);
            return base.Stop();
        }

        public override void Think()
        {
            GameNPC body = Body;

            if (body == null)
                return;

            string state = _active == null ? "idle" : $"{_active.Action}:{_activeRemaining}";

            lock (_ring)
            {
                _ring.Enqueue(new Observation(
                    GameLoop.GameLoopTime,
                    body.CurrentRegionID,
                    body.X,
                    body.Y,
                    body.Z,
                    body.MaxHealth > 0 ? Math.Clamp(body.Health / (double)body.MaxHealth, 0.0, 1.0) : 1.0,
                    body.TargetObject != null ? body.TargetObject.ObjectID.ToString() : string.Empty,
                    state));

                if (_ring.Count > RingCap)
                    _ring.Dequeue();
            }

            // Consume the latest pending intent (if any). No intent = keep last state, never block.
            if (NcaBrainRegistry.TryConsume(NcaBrainRegistry.KeyOf(this), out NcaBrainRegistry.NcaIntent intent))
            {
                _active = intent;
                _activeRemaining = intent.TtlTicks;
                Apply(body, intent);
                return;
            }

            if (_active != null && --_activeRemaining <= 0)
            {
                // TTL-exceeded: safe no-op. Server-side follow/attack state is left as-is in v0;
                // send an explicit "stop" intent to clear it.
                if (log.IsDebugEnabled)
                    log.Debug($"NcaBrain intent '{_active.Action}' for {BodyKey} expired after {_active.TtlTicks} ticks (no-op)");

                _active = null;
            }
        }

        public override void KillFSM() { } // FSM prop (ai/ABrain.cs:13) unused by NcaBrain.

        private void Apply(GameNPC body, NcaBrainRegistry.NcaIntent intent)
        {
            try
            {
                switch (intent.Action)
                {
                    case NcaBrainRegistry.ActionMoveTo:
                    {
                        bool moved = body.MoveTo(intent.Region, intent.X, intent.Y, intent.Z, body.Heading);

                        if (!moved && log.IsDebugEnabled)
                            log.Debug($"NcaBrain move_to failed for {BodyKey} -> {intent.Region}:{intent.X},{intent.Y},{intent.Z}");

                        break;
                    }
                    case NcaBrainRegistry.ActionFollow:
                    case NcaBrainRegistry.ActionAttack:
                    {
                        GameLiving victim = FindVictim(body, intent.VictimObjectId);

                        if (victim == null)
                        {
                            if (log.IsDebugEnabled)
                                log.Debug($"NcaBrain {intent.Action} for {BodyKey}: victim {intent.VictimObjectId} not found/alive");

                            break;
                        }

                        if (intent.Action == NcaBrainRegistry.ActionFollow)
                        {
                            body.Follow(victim, NcaBrainRegistry.FollowMinDistance, NcaBrainRegistry.FollowMaxDistance);

                            if (log.IsDebugEnabled)
                                log.Debug($"NcaBrain {BodyKey} follow -> victim={victim.Name} oid={victim.ObjectID} " +
                                          $"followTarget={(body as GameNPC)?.FollowTarget?.Name ?? "none"} zone={body.CurrentZone?.ID} " +
                                          $"victimZone={victim.CurrentZone?.ID} dist={body.GetDistanceTo(victim)}");
                        }
                        else
                        {
                            body.StartAttack(victim);

                            if (log.IsDebugEnabled)
                                log.Debug($"NcaBrain {BodyKey} attack -> victim={victim.Name} oid={victim.ObjectID}");
                        }

                        break;
                    }
                    case NcaBrainRegistry.ActionStop:
                        body.StopMoving();
                        body.StopFollowing();
                        body.StopAttack();
                        break;
                }
            }
            catch (Exception ex)
            {
                if (log.IsDebugEnabled)
                    log.Debug($"NcaBrain apply failed for {BodyKey} ({intent.Action})", ex);
            }
        }

        private static GameLiving FindVictim(GameNPC body, int objectId)
        {
            GameObject[] objects = body.CurrentRegion?.Objects;

            if (objectId <= 0 || objects == null)
                return null;

            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i] is GameLiving living && living != body && living.IsAlive &&
                    living.ObjectState == GameObject.eObjectState.Active && living.ObjectID == objectId)
                    return living;
            }

            return null;
        }
    }
}
