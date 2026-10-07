using System.Collections.Generic;
using UnityEngine;
using Arena.Core;
using Arena.Presentation;

namespace Arena.Gameplay
{
    /// <summary>Mutable runtime state for one fighter.</summary>
    public sealed class FighterSim
    {
        public CharacterView View;
        public CharacterDef Def;
        public bool IsBot;

        public Vector3 Position;
        public Vector3 PreviousPosition;
        public Vector3 Velocity;
        internal bool HitWall;
        /// <summary>Last travel/aim direction — spells fire this way.</summary>
        public Vector3 Forward = Vector3.forward;

        public float Health = GameConfig.MaxHealth;
        public bool Dead;
        public float Stun;
        public float Invulnerable;

        /// <summary>Remaining seconds in which a charge still connects on contact.</summary>
        public float ChargeTimer;
        public AbilityDef ChargeAbility;

        public readonly float[] Cooldowns = new float[3];

        // --- bot brain scratch state ---
        public float DecisionTimer;
        public Vector3 DesiredDir;
        public float StrafeSign = 1f;
    }

    /// <summary>
    /// Orchestrates one match: ice movement, perimeter walls, the fixed-step
    /// loop and the guest snapshot view. The heavy rules live in focused
    /// collaborators — <see cref="CombatResolver"/> (contact, damage, sweeps),
    /// <see cref="BotBrain"/> (opponent AI), <see cref="FxSpawner"/> (spell
    /// effects and replication) and <see cref="ZoneField"/> (zone pressure).
    ///
    /// Gameplay code feeds it an input direction each frame and reads
    /// cooldowns / deaths back out; the fighters' transforms and nameplates are
    /// written by the sim so HUD, camera and world bar can never disagree.
    ///
    /// It owns only transient spell FX — <see cref="Clear"/> wipes those when
    /// the match ends (fighter views are destroyed by the caller).
    /// </summary>
    public sealed class MatchSim
    {
        private readonly FighterSim local;
        private readonly FighterSim foe;
        private readonly ZoneField zone;
        private readonly FxSpawner fx = new FxSpawner();

        private double accumulatedTime;

        // Guest view smoothing. The guest has no prediction: it renders what
        // the host sent, so the view is eased from the snapshot that is on
        // screen towards the newest one instead of jumping at the snapshot
        // timeslot. Simulation state always jumps to the newest snapshot.
        private MatchSnapshot renderFrom;
        private MatchSnapshot renderTo;
        private float renderAge;
        private float renderInterval = 1f / 60f;
        private float lastSnapshotAt = -1f;
        public int StepsLastTick { get; private set; }
        public double PendingTime => accumulatedTime;

        public FighterSim Local => local;
        public FighterSim Foe => foe;
        public ZoneShape Zone => zone.Shape;

        public MatchSim(CharacterDef localDef, CharacterDef foeDef, bool foeIsBot,
            CharacterView localView, CharacterView foeView, ZoneShape zoneShape)
        {
            zone = new ZoneField(zoneShape);

            local = new FighterSim
            {
                View = localView,
                Def = localDef,
                IsBot = false,
                Position = localView.transform.position
            };
            foe = new FighterSim
            {
                View = foeView,
                Def = foeDef,
                IsBot = foeIsBot,
                Position = foeView.transform.position
            };

            // Face each other from the first frame so a point-blank charge
            // flies the right way even before anyone moves.
            Vector3 opening = foe.Position - local.Position;
            opening.y = 0f;
            if (opening.sqrMagnitude > 0.0001f)
            {
                local.Forward = opening.normalized;
                foe.Forward = -opening.normalized;
            }
            local.View.SetFacing(local.Forward);
            foe.View.SetFacing(foe.Forward);
        }

        // ---------------------------------------------------------------- tick

        public void Tick(float dt, Vector3 localInputDir) => Tick(dt, localInputDir, Vector3.zero);

        public void Tick(float dt, Vector3 localInputDir, Vector3 remoteInputDir)
        {
            if (!foe.IsBot) foe.DesiredDir = Vector3.ClampMagnitude(remoteInputDir, 1f);
            StepsLastTick = 0;
            if (dt < 0f || float.IsNaN(dt) || float.IsInfinity(dt)) return;
            accumulatedTime += dt;
            // Bound work per render frame, but retain backlog rather than deleting time.
            double step = GameConfig.FixedTimestep;
            while (accumulatedTime + 1e-8 >= step &&
                   StepsLastTick < GameConfig.MaxCatchUpSteps && !local.Dead && !foe.Dead)
            {
                accumulatedTime = System.Math.Max(0d, accumulatedTime - step);
                Step((float)step, localInputDir);
                StepsLastTick++;
            }
        }

        private void Step(float dt, Vector3 localInputDir)
        {
            if (foe.IsBot && !foe.Dead) BotBrain.Tick(foe, local, dt, zone, Cast);
            // Capture after casts (especially Blink): teleports are not swept attacks.
            local.PreviousPosition = local.Position;
            foe.PreviousPosition = foe.Position;

            ApplyInput(local, localInputDir, dt);
            ApplyInput(foe, foe.DesiredDir, dt);

            Integrate(local, dt);
            Integrate(foe, dt);

            CombatResolver.ResolveCharge(local, foe);
            CombatResolver.ResolveCharge(foe, local);
            if (local.HitWall) local.ChargeTimer = 0f;
            if (foe.HitWall) foe.ChargeTimer = 0f;

            fx.TickProjectiles(dt, local, foe);
            fx.TickVortices(dt, local, foe);
            CombatResolver.ResolveBodyContact(local, foe);

            zone.ApplyDamage(local, dt);
            zone.ApplyDamage(foe, dt);
            fx.TickRings(dt);
            TickTimers(local, dt);
            TickTimers(foe, dt);

            local.View.transform.position = local.Position;
            foe.View.transform.position = foe.Position;
        }

        private void TickTimers(FighterSim f, float dt)
        {
            if (f.Stun > 0f) f.Stun = Mathf.Max(0f, f.Stun - dt);
            if (f.Invulnerable > 0f) f.Invulnerable = Mathf.Max(0f, f.Invulnerable - dt);
            if (f.ChargeTimer > 0f) f.ChargeTimer = Mathf.Max(0f, f.ChargeTimer - dt);
            for (int i = 0; i < f.Cooldowns.Length; i++)
                if (f.Cooldowns[i] > 0f)
                    f.Cooldowns[i] = Mathf.Max(0f, f.Cooldowns[i] - dt);
        }

        private void ApplyInput(FighterSim f, Vector3 dir, float dt)
        {
            if (f.Dead || f.Stun > 0f) dir = Vector3.zero;

            if (dir.sqrMagnitude > 0.0001f)
            {
                dir.y = 0f;
                dir = Vector3.ClampMagnitude(dir, 1f);
                f.Velocity += dir * (GameConfig.Acceleration * dt);
                f.Forward = dir.normalized;
                f.View.SetFacing(dir);
            }

            // Ice: exponential decay per 60Hz step, exactly like the prototype.
            float friction = Mathf.Pow(GameConfig.Friction, dt * 60f);
            f.Velocity *= friction;
        }

        private void Integrate(FighterSim f, float dt)
        {
            f.HitWall = false;
            Vector3 p = f.Position + f.Velocity * dt;
            float limit = GameConfig.PlayLimit;

            if (Mathf.Abs(p.x) > limit)
            {
                p.x = Mathf.Clamp(p.x, -limit, limit);
                f.Velocity.x *= GameConfig.WallBounce;
                f.HitWall = true;
            }
            if (Mathf.Abs(p.z) > limit)
            {
                p.z = Mathf.Clamp(p.z, -limit, limit);
                f.Velocity.z *= GameConfig.WallBounce;
                f.HitWall = true;
            }

            // The four cut corners. Their faces are what keep the arena square
            // from reaching into the round spectator bowl, so the bounce has to
            // happen on the diagonal normal rather than on x or z alone.
            float over = Mathf.Abs(p.x) + Mathf.Abs(p.z) - GameConfig.PlayDiagonal;
            if (over > 0f)
            {
                float cut = over * 0.5f;
                p.x -= Mathf.Sign(p.x) * cut;
                p.z -= Mathf.Sign(p.z) * cut;

                float nx = Mathf.Sign(p.x), nz = Mathf.Sign(p.z);
                float inv = 0.70710678f;
                nx *= inv; nz *= inv;
                float into = f.Velocity.x * nx + f.Velocity.z * nz;
                if (into > 0f)
                {
                    f.Velocity.x -= nx * into * (1f - GameConfig.WallBounce);
                    f.Velocity.z -= nz * into * (1f - GameConfig.WallBounce);
                }
                f.HitWall = true;
            }

            p.y = 0f;
            f.Position = p;
        }

        // ---------------------------------------------------------------- spells

        /// <summary>
        /// Fires a slot for <paramref name="caster"/>. Returns false when the
        /// cast was rejected (stunned, dead or still on cooldown), so the HUD
        /// only starts a cooldown bar for real casts.
        /// </summary>
        public bool Cast(FighterSim caster, int slot)
        {
            if (caster == null || caster.Dead || caster.Stun > 0f) return false;
            if (slot < 0 || slot > 2 || caster.Cooldowns[slot] > 0f) return false;

            AbilityDef ab = caster.Def.Get((AbilitySlot)slot);
            caster.Cooldowns[slot] = ab.Cooldown;

            FighterSim target = caster == local ? foe : local;
            Vector3 fwd = caster.Forward.sqrMagnitude > 0.0001f
                ? caster.Forward
                : Vector3.forward;

            switch (ab.Id)
            {
                case "charge":
                    caster.ChargeTimer = GameConfig.ChargeHitWindow;
                    caster.ChargeAbility = ab;
                    caster.Velocity += fwd * ab.DashImpulse;
                    break;

                case "shockwave":
                case "earthquake":
                    fx.SpawnRing(caster.Position, ab.Range, caster.Def.ColorLight);
                    CombatResolver.TryHit(caster, target, ab, target.Position - caster.Position, ab.Range);
                    break;

                case "thunder_orb":
                    fx.SpawnProjectile(caster, ab, fwd);
                    break;

                case "blink":
                {
                    fx.SpawnRing(caster.Position, 1.8f, caster.Def.ColorLight);
                    Vector3 dest = ClampToArena(caster.Position + fwd * ab.BlinkDistance);
                    caster.Position = dest;
                    if (caster.View != null)
                        caster.View.transform.position = dest;
                    caster.Invulnerable = ab.BlinkInvulnerability;
                    fx.SpawnRing(dest, 1.8f, caster.Def.ColorLight);
                    break;
                }

                case "vortex":
                    fx.SpawnVortex(caster, ab);
                    break;

                default:
                    CombatResolver.TryHit(caster, target, ab, target.Position - caster.Position, ab.Range);
                    break;
            }

            if (caster.View != null) caster.View.PlayCast();
            return true;
        }

        /// <summary>Whether a world point falls outside the shrinking zone.</summary>
        public bool IsOutsideZone(Vector3 p) => zone.IsOutside(p);

        // ------------------------------------------------------------ snapshots

        public MatchSnapshot CaptureSnapshot()
        {
            var effects = new List<EffectSnapshot>();
            fx.CaptureEffects(effects);
            return new MatchSnapshot { host = FighterSnapshot.Capture(local),
                guest = FighterSnapshot.Capture(foe), effects = effects.ToArray() };
        }

        /// <summary>Guest rendering only: never advances combat or cooldowns locally.</summary>
        public void ApplySnapshot(MatchSnapshot snapshot)
        {
            // Remember the outgoing snapshot for interpolation. The spacing is
            // measured from the wire cadence (clamped so a stalled host cannot
            // stretch the ease-out over seconds).
            float now = Time.unscaledTime;
            renderFrom = renderTo ?? snapshot;
            renderTo = snapshot;
            if (lastSnapshotAt >= 0f)
                renderInterval = Mathf.Clamp(now - lastSnapshotAt, 1f / 120f, 1f / 10f);
            lastSnapshotAt = now;
            renderAge = 0f;

            snapshot.host.Apply(local);
            snapshot.guest.Apply(foe);
            fx.SyncReplicated(snapshot.effects);
        }

        /// <summary>
        /// Guest rendering only: eases the fighter views and the replica
        /// effects from the previous snapshot towards the newest one. Call it
        /// once per rendered frame after the snapshot has been applied; with
        /// no snapshot yet it does nothing.
        /// </summary>
        public void AdvanceSnapshotView(float dt)
        {
            if (renderTo == null) return;
            if (dt > 0f && !float.IsNaN(dt) && !float.IsInfinity(dt)) renderAge += dt;
            float k = renderInterval > 0f ? Mathf.Clamp01(renderAge / renderInterval) : 1f;
            var from = renderFrom;
            Ease(local, from?.host, renderTo.host, k);
            Ease(foe, from?.guest, renderTo.guest, k);
            fx.EaseReplicated(from?.effects, renderTo.effects, k);

            static void Ease(FighterSim fighter, FighterSnapshot fromSnap,
                FighterSnapshot toSnap, float k)
            {
                if (toSnap == null || fighter.View == null) return;
                Vector3 position = toSnap.position;
                Vector3 forward = toSnap.forward;
                if (fromSnap != null)
                {
                    position = Vector3.Lerp(fromSnap.position, position, k);
                    forward = Vector3.Lerp(fromSnap.forward, forward, k);
                }
                fighter.View.transform.position = position;
                if (forward.sqrMagnitude > 0.0001f) fighter.View.SetFacing(forward.normalized);
            }
        }

        /// <summary>Destroys every transient effect this sim created.</summary>
        public void Clear()
        {
            renderFrom = null;
            renderTo = null;
            renderAge = 0f;
            renderInterval = 1f / 60f;
            lastSnapshotAt = -1f;
            accumulatedTime = 0d;
            fx.Clear();
        }

        /// <summary>Where a blink may land: inside the same playable octagon.</summary>
        private Vector3 ClampToArena(Vector3 p) => GameConfig.ClampToPlay(p);
    }
}
