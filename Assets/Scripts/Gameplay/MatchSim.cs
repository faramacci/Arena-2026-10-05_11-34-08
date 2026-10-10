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

        /// <summary>Damage the Frost Aegis can still soak; 0 when no shield is up.</summary>
        public float Shield;
        /// <summary>Amount the running shield started at, for the bubble's scale.</summary>
        public float ShieldMax;
        /// <summary>Seconds left on the shield; it drops when this runs out.</summary>
        public float ShieldTimer;

        // --- Shadowstep buff ---
        /// <summary>
        /// Seconds left of grip and speed. While it runs the ice is switched off
        /// for this fighter: see <see cref="MatchSim.ApplyInput"/>.
        /// </summary>
        public float Haste;
        /// <summary>What the buff started at, for the aura's read.</summary>
        public float HasteMax;
        /// <summary>Top speed the buff holds, and how hard it pulls there.</summary>
        public float HasteSpeed;
        public float HasteGrip;

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

        /// <summary>
        /// Every fighter in slot order: index 0 is the local player's, index 1 is
        /// the one the LAN path mirrors (host↔guest), and anything from index 2 on
        /// is an extra free-for-all opponent — today always a bot. The two
        /// references above stay because the two-fighter match is the shape the
        /// LAN protocol, the HUD and the regression tests are built on.
        /// </summary>
        private readonly List<FighterSim> all = new List<FighterSim>();

        private double accumulatedTime;

        /// <summary>
        /// Damage the world still owes: an area spell that has laid down its ground
        /// warning but has not landed yet. The origin and direction are frozen at
        /// cast time, so walking out of the warned ground before the timer runs out
        /// is exactly what dodging means.
        /// </summary>
        private readonly List<PendingStrike> pending = new List<PendingStrike>();

        private sealed class PendingStrike
        {
            public FighterSim Caster;
            public AbilityDef Ability;
            public Vector3 Forward;
            /// <summary>Vortex-only: how far down the lane the funnel travels.</summary>
            public float Length;
            /// <summary>Rift-only: the radius of the ring the ground opens inside.</summary>
            public float Radius;
            public float Timer;
            /// <summary>True for the Vortex: the impact throws a travelling hazard.</summary>
            public bool Thrown;
            /// <summary>True for the Earthrift: the warning and the hit ride the caster.</summary>
            public bool Ring;
        }

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
        /// <summary>Slot-ordered roster: [0] local player, [1] first opponent, then the extras.</summary>
        public IReadOnlyList<FighterSim> Fighters => all;
        public ZoneShape Zone => zone.Shape;

        /// <summary>Fighters still standing, including the local player.</summary>
        public int AliveCount
        {
            get
            {
                int alive = 0;
                for (int i = 0; i < all.Count; i++) if (!all[i].Dead) alive++;
                return alive;
            }
        }

        /// <summary>The only fighter left standing, or null while the match is live.</summary>
        public FighterSim LastAlive
        {
            get
            {
                if (AliveCount != 1) return null;
                for (int i = 0; i < all.Count; i++) if (!all[i].Dead) return all[i];
                return null;
            }
        }

        /// <summary>A free-for-all is decided once one fighter (or nobody) is left.</summary>
        public bool MatchOver => AliveCount <= 1;

        /// <summary>
        /// One fighter handed to the sim: which character, which view, and whether
        /// the game drives it. The caller puts the local player first.
        /// </summary>
        public sealed class Setup
        {
            public CharacterDef Def;
            public CharacterView View;
            public bool IsBot;
        }

        /// <summary>
        /// Full free-for-all. The first setup is the local player; everything after
        /// the first opponent is placed around the arena and fights everyone.
        /// </summary>
        public MatchSim(IList<Setup> setups, ZoneShape zoneShape)
            : this(setups[0].Def, setups[1].Def, setups[1].IsBot,
                   setups[0].View, setups[1].View, zoneShape)
        {
            for (int i = 2; i < setups.Count; i++)
            {
                var extra = new FighterSim
                {
                    View = setups[i].View,
                    Def = setups[i].Def,
                    IsBot = setups[i].IsBot,
                    Position = setups[i].View.transform.position
                };
                extra.Forward = FaceNearest(extra);
                extra.View.SetFacing(extra.Forward);
                all.Add(extra);
            }

            Debug.Log($"[MatchSim] free-for-all started with {all.Count} fighters " +
                      $"({AliveCount} alive).");
        }

        /// <summary>Points a new fighter at whichever other fighter is closest.</summary>
        private Vector3 FaceNearest(FighterSim fighter)
        {
            Vector3 best = Vector3.forward;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < all.Count; i++)
            {
                Vector3 to = all[i].Position - fighter.Position;
                to.y = 0f;
                if (to.sqrMagnitude < 0.0001f) continue;
                if (to.sqrMagnitude >= bestDistance) continue;
                bestDistance = to.sqrMagnitude;
                best = to.normalized;
            }
            return best;
        }

        /// <summary>The closest fighter that is not the one asking — the bot's target.</summary>
        private FighterSim NearestOpponent(FighterSim fighter)
        {
            FighterSim best = null;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < all.Count; i++)
            {
                var other = all[i];
                if (other == fighter || other.Dead) continue;
                float distance = CombatResolver.FlatDistance(fighter.Position, other.Position);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = other;
            }
            return best;
        }

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
            all.Add(local);
            all.Add(foe);
            // The effects layer attributes a thrown prop to a slot, so it needs the
            // roster in the same order the snapshots use.
            fx.Track(all);
            Vector3 opening = foe.Position - local.Position;
            opening.y = 0f;
            if (opening.sqrMagnitude > 0.0001f)
            {
                local.Forward = opening.normalized;
                foe.Forward = -opening.normalized;
            }
            local.View.SetFacing(local.Forward);
            foe.View.SetFacing(foe.Forward);

            Debug.Log($"[MatchSim] match started (local={localDef.DisplayName}, " +
                      $"foe={foeDef.DisplayName}, bot={foeIsBot}, zone={zoneShape}).");
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
                   StepsLastTick < GameConfig.MaxCatchUpSteps && AliveCount > 1)
            {
                accumulatedTime = System.Math.Max(0d, accumulatedTime - step);
                Step((float)step, localInputDir);
                StepsLastTick++;
            }
        }

        private void Step(float dt, Vector3 localInputDir)
        {
            if (foe.IsBot && !foe.Dead) BotBrain.Tick(foe, local, dt, zone, Cast);
            // Every extra fighter is a bot in this build; each one hunts its own
            // nearest living opponent instead of a fixed rival.
            for (int i = 2; i < all.Count; i++)
            {
                var extra = all[i];
                if (!extra.IsBot || extra.Dead) continue;
                FighterSim hunt = NearestOpponent(extra);
                if (hunt != null) BotBrain.Tick(extra, hunt, dt, zone, Cast);
            }
            // Capture after casts (especially Blink): teleports are not swept attacks.
            local.PreviousPosition = local.Position;
            foe.PreviousPosition = foe.Position;
            for (int i = 2; i < all.Count; i++) all[i].PreviousPosition = all[i].Position;

            ApplyInput(local, localInputDir, dt);
            ApplyInput(foe, foe.DesiredDir, dt);
            for (int i = 2; i < all.Count; i++) ApplyInput(all[i], all[i].DesiredDir, dt);

            Integrate(local, dt);
            Integrate(foe, dt);
            for (int i = 2; i < all.Count; i++) Integrate(all[i], dt);

            CombatResolver.ResolveCharge(local, foe);
            CombatResolver.ResolveCharge(foe, local);
            if (local.HitWall) local.ChargeTimer = 0f;
            if (foe.HitWall) foe.ChargeTimer = 0f;
            // Charging and body contact are pairwise now: five fighters is ten
            // pairs, still far cheaper than anything else in the step.
            for (int i = 0; i < all.Count; i++)
            {
                var a = all[i];
                for (int j = i + 1; j < all.Count; j++)
                {
                    var b = all[j];
                    CombatResolver.ResolveCharge(a, b);
                    CombatResolver.ResolveCharge(b, a);
                }
                if (a.HitWall) a.ChargeTimer = 0f;
            }

            TickStrikes(dt);
            fx.TickProjectiles(dt, all);
            // Whose weapon is in the air right now: their hand stays empty until it
            // comes home, which is the point of throwing your own shield.
            fx.SyncProps();
            fx.TickVortices(dt, all);
            for (int i = 0; i < all.Count; i++)
                for (int j = i + 1; j < all.Count; j++)
                    CombatResolver.ResolveBodyContact(all[i], all[j]);

            zone.ApplyDamage(local, dt);
            zone.ApplyDamage(foe, dt);
            for (int i = 2; i < all.Count; i++) zone.ApplyDamage(all[i], dt);
            fx.TickWarnings(dt);
            fx.TickRings(dt);
            fx.TickRifts(dt);
            TickTimers(local, dt);
            TickTimers(foe, dt);
            for (int i = 2; i < all.Count; i++) TickTimers(all[i], dt);

            local.View.transform.position = local.Position;
            foe.View.transform.position = foe.Position;
            for (int i = 2; i < all.Count; i++) all[i].View.transform.position = all[i].Position;
        }

        private void TickTimers(FighterSim f, float dt)
        {
            if (f.Stun > 0f) f.Stun = Mathf.Max(0f, f.Stun - dt);
            if (f.Invulnerable > 0f) f.Invulnerable = Mathf.Max(0f, f.Invulnerable - dt);
            if (f.ChargeTimer > 0f) f.ChargeTimer = Mathf.Max(0f, f.ChargeTimer - dt);
            if (f.Haste > 0f) f.Haste = Mathf.Max(0f, f.Haste - dt);
            // Pushed every step, expiry included, so the view's aura is told when
            // the buff is over instead of keeping the last live value forever.
            if (f.View != null) f.View.SetHaste(f.Haste, f.HasteMax);
            if (f.ShieldTimer > 0f)
            {
                f.ShieldTimer = Mathf.Max(0f, f.ShieldTimer - dt);
                if (f.ShieldTimer <= 0f)
                {
                    f.Shield = 0f;
                    if (f.View != null) f.View.SetShield(0f, 0f);
                }
            }
            for (int i = 0; i < f.Cooldowns.Length; i++)
                if (f.Cooldowns[i] > 0f)
                    f.Cooldowns[i] = Mathf.Max(0f, f.Cooldowns[i] - dt);
        }

        private void ApplyInput(FighterSim f, Vector3 dir, float dt)
        {
            if (f.Dead || f.Stun > 0f) dir = Vector3.zero;
            dir.y = 0f;
            dir = Vector3.ClampMagnitude(dir, 1f);

            if (dir.sqrMagnitude > 0.0001f)
            {
                f.Forward = dir.normalized;
                f.View.SetFacing(dir);
            }

            if (f.Haste > 0f)
            {
                // Shadowstep: no ice. Velocity is pulled onto the stick instead
                // of accumulated and decayed, so he turns and stops on a coin and
                // tops out faster than anyone else. A knockback is shrugged off
                // with the same grip, which is the point of a three-second dive.
                Vector3 desired = dir * f.HasteSpeed;
                f.Velocity = Vector3.Lerp(f.Velocity, desired,
                    1f - Mathf.Exp(-Mathf.Max(0.1f, f.HasteGrip) * dt));
                return;
            }

            f.Velocity += dir * (GameConfig.Acceleration * dt);

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
        /// cast was rejected (stunned, dead, still on cooldown, or with nobody
        /// left to aim at), so the HUD only starts a cooldown bar for real casts
        /// and a refusal never spends the slot.
        /// </summary>
        public bool Cast(FighterSim caster, int slot)
        {
            if (caster == null || caster.Dead || caster.Stun > 0f) return false;
            if (slot < 0 || slot > 2 || caster.Cooldowns[slot] > 0f) return false;

            AbilityDef ab = caster.Def.Get((AbilitySlot)slot);

            FighterSim target = NearestOpponent(caster);
            if (target == null) return false;
            // Committed only once the spell has somewhere to land: a cast the sim
            // refuses must not leave the slot on cooldown for a spell that never ran.
            caster.Cooldowns[slot] = ab.Cooldown;
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

                case "dagger_throw":
                case "thunder_orb":
                case "shield_throw":
                    // One thrown weapon, whatever its silhouette: the spawner reads
                    // speed, life and shape off the ability, and turns the shield
                    // around once it has flown its ProjectileRange.
                    fx.SpawnProjectile(caster, ab, fwd);
                    break;

                case "blade_wave":
                    // The ultimate throws a whole fan at once; the volley shares
                    // one hit list, so a fighter in the middle of it is struck by
                    // one blade rather than seven.
                    fx.SpawnBladeWave(caster, ab, fwd);
                    break;

                case "shadowstep":
                {
                    caster.Haste = ab.BuffDuration;
                    caster.HasteMax = ab.BuffDuration;
                    caster.HasteSpeed = ab.BuffSpeed > 0f
                        ? ab.BuffSpeed : GameConfig.HasteTopSpeed;
                    caster.HasteGrip = ab.BuffGrip > 0f ? ab.BuffGrip : GameConfig.HasteGrip;
                    fx.SpawnRing(caster.Position, 2.4f, caster.Def.ColorLight);
                    if (caster.View != null)
                        caster.View.SetHaste(caster.Haste, caster.HasteMax);
                    break;
                }

                case "frost_aegis":
                    caster.Shield = ab.ShieldAmount;
                    caster.ShieldMax = ab.ShieldAmount;
                    caster.ShieldTimer = ab.ShieldDuration;
                    fx.SpawnRing(caster.Position, 2.4f, caster.Def.ColorLight);
                    if (caster.View != null)
                        caster.View.SetShield(caster.Shield, caster.ShieldMax);
                    break;

                case "earthrift":
                    // The crack opens after a ground warning, so whoever is standing
                    // on the ring still has time to step out of it.
                    QueueStrike(caster, ab, fwd, false);
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
                    // Thrown down a line instead of dropped at the caster's feet.
                    QueueStrike(caster, ab, fwd, true);
                    break;

                default:
                    CombatResolver.TryHit(caster, target, ab, target.Position - caster.Position, ab.Range);
                    break;
            }

            if (caster.View != null) caster.View.PlayCast();
            Debug.Log($"[MatchSim] {caster.Def.DisplayName} cast '{ab.DisplayName}' " +
                      $"(slot={(AbilitySlot)slot})");
            return true;
        }

        /// <summary>
        /// Lays down the warning an area spell gives before it lands. Nothing is
        /// spawned here beyond the warning itself: the strike is resolved when its
        /// timer runs out, against whoever is still standing on the warned ground.
        /// </summary>
        private void QueueStrike(FighterSim caster, AbilityDef ab, Vector3 fwd, bool thrown)
        {
            // A thrown vortex is carried, not called down: the funnel gathers
            // under the caster's feet and is thrown from there, so its telegraph
            // is the wind-up itself rather than a corridor painted on the ground.
            // The Earthrift is the other way round - the ring is drawn around the
            // caster and the caster carries it, so the Vanguard stands in the middle
            // of his own circle for the whole warning and the ground opens there.
            if (thrown)
                fx.SpawnCharge(caster, ab.HazardRadius, caster.Def.Color, ab.Telegraph);
            else
                fx.SpawnRingWarning(caster, ab.Range, caster.Def.Color, ab.Telegraph);
            pending.Add(new PendingStrike
            {
                Caster = caster,
                Ability = ab,
                Forward = fwd,
                Length = thrown ? (ab.LineLength > 0f ? ab.LineLength : ab.Range) : 0f,
                Radius = thrown ? 0f : ab.Range,
                Timer = Mathf.Max(0.01f, ab.Telegraph),
                Thrown = thrown,
                Ring = !thrown
            });
        }

        /// <summary>Resolves every telegraph whose warning has run out.</summary>
        private void TickStrikes(float dt)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                PendingStrike strike = pending[i];
                strike.Timer -= dt;
                if (strike.Timer > 0f) continue;

                if (strike.Thrown)
                {
                    fx.SpawnVortex(strike.Caster, strike.Ability, strike.Forward, strike.Length);
                }
                else if (strike.Ring)
                {
                    // The ring is the caster's own circle, so it lands where they are
                    // standing now rather than where they stood when they cast it: a
                    // Vanguard who walks into the fight carries the crack with him.
                    Vector3 centre = strike.Caster.Position;
                    fx.SpawnRift(centre, strike.Radius, strike.Ability.RiftBand,
                        strike.Caster.Def.Color);
                    // The disc is the hit: everyone inside the ring when the ground
                    // opens takes the launch, thrown outward from the fighter in the
                    // middle of it - never the caster himself, who is standing there.
                    for (int f = 0; f < all.Count; f++)
                    {
                        FighterSim victim = all[f];
                        if (victim == strike.Caster || victim.Dead) continue;
                        CombatResolver.TryCircleHit(centre, victim, strike.Ability,
                            strike.Radius);
                    }
                }

                pending.RemoveAt(i);
            }
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
            // The replica of a prop in the air empties the same hand on this machine
            // that it did on the host's.
            fx.SyncProps();
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
            pending.Clear();
            fx.Clear();
        }

        /// <summary>Where a blink may land: inside the same playable octagon.</summary>
        private Vector3 ClampToArena(Vector3 p) => GameConfig.ClampToPlay(p);
    }
}
