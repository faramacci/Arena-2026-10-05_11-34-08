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
    /// The whole match in one place: ice movement, perimeter walls, body
    /// contact, every spell in <see cref="GameConfig"/>, zone pressure and the
    /// bot brain. Gameplay code feeds it an input direction each frame and
    /// reads cooldowns / deaths back out; the fighters' transforms and
    /// nameplates are written by the sim so HUD, camera and world bar can never
    /// disagree.
    ///
    /// It owns only transient spell FX — <see cref="Clear"/> wipes those when
    /// the match ends (fighter views are destroyed by the caller).
    /// </summary>
    public sealed class MatchSim
    {
        private readonly FighterSim local;
        private readonly FighterSim foe;
        private readonly ZoneShape zone;

        private readonly List<Projectile> projectiles = new List<Projectile>();
        private readonly List<VortexHazard> vortices = new List<VortexHazard>();
        private readonly List<RingFx> rings = new List<RingFx>();
        private readonly List<GameObject> replicatedEffects = new List<GameObject>();
        private GameObject fxRoot;
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

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static Shader fxShader;

        public FighterSim Local => local;
        public FighterSim Foe => foe;
        public ZoneShape Zone => zone;

        public MatchSim(CharacterDef localDef, CharacterDef foeDef, bool foeIsBot,
            CharacterView localView, CharacterView foeView, ZoneShape zoneShape)
        {
            zone = zoneShape;

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
            if (foe.IsBot && !foe.Dead) TickBot(foe, dt);
            // Capture after casts (especially Blink): teleports are not swept attacks.
            local.PreviousPosition = local.Position;
            foe.PreviousPosition = foe.Position;

            ApplyInput(local, localInputDir, dt);
            ApplyInput(foe, foe.DesiredDir, dt);

            Integrate(local, dt);
            Integrate(foe, dt);

            ResolveCharge(local, foe);
            ResolveCharge(foe, local);
            if (local.HitWall) local.ChargeTimer = 0f;
            if (foe.HitWall) foe.ChargeTimer = 0f;

            TickProjectiles(dt);
            TickVortices(dt);
            ResolveBodyContact();

            TickZoneDamage(local, dt);
            TickZoneDamage(foe, dt);
            TickRings(dt);
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

        private void ResolveBodyContact(FighterSim a, FighterSim b)
        {
            Vector3 delta = b.Position - a.Position;
            delta.y = 0f;
            float minDist = GameConfig.PlayerRadius * 2f;
            float dist = delta.magnitude;
            if (dist >= minDist) return;

            Vector3 n = dist > 0.0001f ? delta / dist : Vector3.right;
            float overlap = minDist - dist;
            a.Position -= n * (overlap * 0.5f);
            b.Position += n * (overlap * 0.5f);

            float rel = Vector3.Dot(a.Velocity - b.Velocity, n);
            if (rel > 0f)
            {
                float imp = rel * (1f + GameConfig.BodyRestitution) * 0.5f;
                a.Velocity -= n * imp;
                b.Velocity += n * imp;
            }
        }

        private void ResolveBodyContact() => ResolveBodyContact(local, foe);

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
                    SpawnRing(caster.Position, ab.Range, caster.Def.ColorLight);
                    TryHit(target, ab, target.Position - caster.Position, ab.Range);
                    break;

                case "thunder_orb":
                    SpawnProjectile(caster, ab, fwd);
                    break;

                case "blink":
                {
                    SpawnRing(caster.Position, 1.8f, caster.Def.ColorLight);
                    Vector3 dest = ClampToArena(caster.Position + fwd * ab.BlinkDistance);
                    caster.Position = dest;
                    if (caster.View != null)
                        caster.View.transform.position = dest;
                    caster.Invulnerable = ab.BlinkInvulnerability;
                    SpawnRing(dest, 1.8f, caster.Def.ColorLight);
                    break;
                }

                case "vortex":
                    SpawnVortex(caster, ab);
                    break;

                default:
                    TryHit(target, ab, target.Position - caster.Position, ab.Range);
                    break;
            }

            if (caster.View != null) caster.View.PlayCast();
            return true;
        }

        /// <summary>Contact window for the dash: whoever the charger touches goes flying.</summary>
        private void ResolveCharge(FighterSim caster, FighterSim target)
        {
            if (caster.ChargeTimer <= 0f || target.Dead) return;
            if (!SweptContact(caster.PreviousPosition, caster.Position,
                target.PreviousPosition, target.Position,
                GameConfig.PlayerRadius * 2f + 0.35f, out float contact)) return;

            caster.ChargeTimer = 0f;
            Vector3 dir = Vector3.Lerp(target.PreviousPosition, target.Position, contact) -
                Vector3.Lerp(caster.PreviousPosition, caster.Position, contact);
            if (dir.sqrMagnitude < 0.0001f) dir = caster.Forward;
            ApplyHit(target, caster.ChargeAbility, dir);
        }

        private void TryHit(FighterSim target, AbilityDef ab, Vector3 dir, float range)
        {
            if (range <= 0f || FlatDistance(local.Position, foe.Position) <= range)
                ApplyHit(target, ab, dir);
        }

        private void ApplyHit(FighterSim target, AbilityDef ab, Vector3 dir)
        {
            if (target.Dead || target.Invulnerable > 0f) return;

            target.Health = Mathf.Max(0f, target.Health - ab.Damage);
            if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
            target.Velocity += dir.normalized * ab.KnockImpulse;
            if (ab.Stun > 0f) target.Stun = Mathf.Max(target.Stun, ab.Stun);
            target.ChargeTimer = 0f; // a clean hit cancels their own charge

            if (target.View != null)
            {
                target.View.PlayHitFlash();
                target.View.SetHealth(target.Health, GameConfig.MaxHealth);
            }

            if (target.Health <= 0f) target.Dead = true;
        }

        // ---------------------------------------------------------------- bot

        private void TickBot(FighterSim bot, float dt)
        {
            if (bot.Stun > 0f)
            {
                bot.DesiredDir = Vector3.zero;
                return;
            }

            Vector3 toTarget = local.Position - bot.Position;
            toTarget.y = 0f;
            float d = toTarget.magnitude;

            bot.DecisionTimer -= dt;
            if (bot.DecisionTimer > 0f) return;

            bot.DecisionTimer = Random.Range(GameConfig.BotDecisionMin, GameConfig.BotDecisionMax);

            if (IsOutsideZone(bot.Position))
            {
                // Safety first: run back toward the middle of the arena.
                bot.DesiredDir = (-bot.Position).normalized;
            }
            else if (d > 7f)
            {
                bot.DesiredDir = toTarget / d;
            }
            else if (d <= 0.0001f)
            {
                bot.DesiredDir = Vector3.right;
            }
            else if (d < 2f)
            {
                // Too close — give the charge some room.
                bot.DesiredDir = -(toTarget / d);
            }
            else
            {
                bot.StrafeSign = Random.value < 0.5f ? 1f : -1f;
                Vector3 side = Vector3.Cross(Vector3.up, toTarget / d) * bot.StrafeSign;
                bot.DesiredDir = ((toTarget / d) * 0.6f + side * 0.8f).normalized;
            }

            TryBotCast(bot, d, toTarget);
        }

        private void TryBotCast(FighterSim bot, float d, Vector3 toTarget)
        {
            for (int slot = 0; slot < 3; slot++)
            {
                if (bot.Cooldowns[slot] > 0f) continue;

                AbilityDef ab = bot.Def.Get((AbilitySlot)slot);
                bool inRange;
                switch (ab.Id)
                {
                    case "charge":
                        inRange = d > 2.5f && d < 13f;
                        break;
                    case "blink":
                        inRange = d > 6f && d < 16f;
                        break;
                    default:
                        inRange = ab.Range > 0f ? d <= ab.Range * 0.95f : d <= 12f;
                        break;
                }

                if (!inRange) continue;
                if (Random.value > GameConfig.BotCastChance) continue; // hold the trigger

                if (toTarget.sqrMagnitude > 0.0001f)
                    bot.Forward = toTarget.normalized;
                Cast(bot, slot);
                return; // one spell per decision tick
            }
        }

        // ------------------------------------------------------------ zone / fx

        public bool IsOutsideZone(Vector3 p)
        {
            if (zone == ZoneShape.Circle)
            {
                float r = GameConfig.ZoneCircleRadius;
                return new Vector2(p.x, p.z).sqrMagnitude > r * r;
            }
            return Mathf.Abs(p.x) > GameConfig.ZoneHalfWidth ||
                   Mathf.Abs(p.z) > GameConfig.ZoneHalfHeight;
        }

        private void TickZoneDamage(FighterSim f, float dt)
        {
            if (f.Dead || !IsOutsideZone(f.Position)) return;

            f.Health = Mathf.Max(0f, f.Health - GameConfig.ZoneDamagePerSecond * dt);
            if (f.View != null) f.View.SetHealth(f.Health, GameConfig.MaxHealth);
            if (f.Health <= 0f) f.Dead = true;
        }

        private sealed class Projectile
        {
            public GameObject Go;
            public Vector3 Velocity;
            public AbilityDef Ab;
            public FighterSim Owner;
            public float Life;
        }

        private sealed class VortexHazard
        {
            public GameObject Go;
            public Vector3 Centre;
            public float Radius;
            public float Life;
            public float TotalLife;
            public AbilityDef Ab;
            public FighterSim Owner;
        }

        private sealed class RingFx
        {
            public GameObject Go;
            public float T;
            public float MaxRadius;
        }

        private void SpawnProjectile(FighterSim caster, AbilityDef ab, Vector3 dir)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(go.GetComponent<Collider>());
            go.name = "Orb";
            go.transform.SetParent(FxRoot.transform, false);
            go.transform.position = caster.Position + Vector3.up * 1.1f + dir * 0.9f;
            go.transform.localScale = Vector3.one * 0.75f;
            OwnedMaterial.Assign(go, MakeUnlit(caster.Def.ColorLight));

            projectiles.Add(new Projectile
            {
                Go = go,
                Velocity = dir * GameConfig.OrbSpeed,
                Ab = ab,
                Owner = caster,
                Life = 1.5f
            });
        }

        private void TickProjectiles(float dt)
        {
            for (int i = projectiles.Count - 1; i >= 0; i--)
            {
                Projectile p = projectiles[i];
                Vector3 start = p.Go.transform.position;
                float travelTime = Mathf.Min(dt, Mathf.Max(0f, p.Life));
                Vector3 end = start + p.Velocity * travelTime;
                // Stop at the inner wall face, accounting for the orb radius.
                float wallFraction = WallFraction(start, end, 0.375f);
                end = Vector3.Lerp(start, end, wallFraction);
                float targetFraction = travelTime / dt * wallFraction;
                FighterSim target = p.Owner == local ? foe : local;
                Vector3 targetEnd = Vector3.Lerp(target.PreviousPosition,
                    target.Position, targetFraction);
                if (wallFraction > 0f && !target.Dead && SweptContact(start, end,
                    target.PreviousPosition, targetEnd,
                    GameConfig.PlayerRadius + 0.375f, out float contact))
                {
                    p.Go.transform.position = Vector3.Lerp(start, end, contact);
                    ApplyHit(target, p.Ab, p.Velocity);
                    p.Life = 0f;
                }
                else
                {
                    p.Go.transform.position = end;
                    p.Life -= dt;
                    if (wallFraction < 1f) p.Life = 0f;
                }

                if (p.Life <= 0f)
                {
                    Object.Destroy(p.Go);
                    projectiles.RemoveAt(i);
                }
            }
        }

        private void SpawnVortex(FighterSim caster, AbilityDef ab)
        {
            Vector3 centre = caster.Position;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.Destroy(go.GetComponent<Collider>());
            go.name = "Vortex";
            go.transform.SetParent(FxRoot.transform, false);
            go.transform.position = centre + Vector3.up * 0.06f;
            go.transform.localScale = new Vector3(ab.HazardRadius * 2f, 0.05f, ab.HazardRadius * 2f);
            OwnedMaterial.Assign(go, MakeUnlit(caster.Def.Color));

            vortices.Add(new VortexHazard
            {
                Go = go,
                Centre = centre,
                Radius = ab.HazardRadius,
                Life = ab.HazardLifetime,
                TotalLife = ab.HazardLifetime,
                Ab = ab,
                Owner = caster
            });
        }

        private void TickVortices(float dt)
        {
            for (int i = vortices.Count - 1; i >= 0; i--)
            {
                VortexHazard v = vortices[i];
                v.Life -= dt;

                // Visual: the ring breathes while it charges its burst.
                if (v.Go != null)
                {
                    float pulse = 1f + 0.06f * Mathf.Sin(Time.time * 14f);
                    float s = v.Radius * 2f * pulse * Mathf.Lerp(0.55f, 1f,
                        1f - Mathf.Clamp01(v.Life / v.TotalLife));
                    v.Go.transform.localScale = new Vector3(s, 0.05f, s);
                }

                FighterSim target = v.Owner == local ? foe : local;
                if (!target.Dead && target.Invulnerable <= 0f)
                {
                    Vector3 toCentre = v.Centre - target.Position;
                    toCentre.y = 0f;
                    float d = toCentre.magnitude;
                    if (d > 0.01f && d <= v.Radius)
                        target.Velocity += (toCentre / d) * (GameConfig.VortexPullSpeed * dt);
                }

                if (v.Life > 0f) continue;

                // Burst: whoever is still inside eats the launch.
                if (!target.Dead)
                {
                    Vector3 away = target.Position - v.Centre;
                    away.y = 0f;
                    if (away.sqrMagnitude <= v.Radius * v.Radius)
                        ApplyHit(target, v.Ab, away.sqrMagnitude > 0.0001f ? away : Vector3.forward);
                }

                SpawnRing(v.Centre, v.Radius, v.Owner.Def.ColorLight);
                Object.Destroy(v.Go);
                vortices.RemoveAt(i);
            }
        }

        private void SpawnRing(Vector3 centre, float radius, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.Destroy(go.GetComponent<Collider>());
            go.name = "RingFx";
            go.transform.SetParent(FxRoot.transform, false);
            go.transform.position = centre + Vector3.up * 0.05f;
            go.transform.localScale = new Vector3(0.3f, 0.05f, 0.3f);
            OwnedMaterial.Assign(go, MakeUnlit(color));

            rings.Add(new RingFx { Go = go, MaxRadius = radius });
        }

        private void TickRings(float dt)
        {
            float duration = GameConfig.RingFxSeconds;
            for (int i = rings.Count - 1; i >= 0; i--)
            {
                RingFx r = rings[i];
                r.T += dt;
                float k = Mathf.Clamp01(r.T / duration);
                // A unit cylinder is 1 across, so diameter == 2 * radius.
                float diameter = Mathf.Lerp(0.4f, r.MaxRadius * 2f, k);
                if (r.Go != null)
                    r.Go.transform.localScale = new Vector3(diameter, 0.05f, diameter);

                if (r.T < duration) continue;
                Object.Destroy(r.Go);
                rings.RemoveAt(i);
            }
        }

        // ---------------------------------------------------------------- utils

        private GameObject FxRoot
        {
            get
            {
                if (fxRoot == null)
                {
                    fxRoot = new GameObject("MatchFx");
                }
                return fxRoot;
            }
        }

        public MatchSnapshot CaptureSnapshot()
        {
            var effects = new List<EffectSnapshot>();
            void Add(GameObject go, PrimitiveType type, Color color)
            {
                effects.Add(new EffectSnapshot { primitive = (int)type,
                    position = go.transform.position, scale = go.transform.localScale, color = color });
            }
            foreach (var p in projectiles) Add(p.Go, PrimitiveType.Sphere, p.Owner.Def.ColorLight);
            foreach (var v in vortices) Add(v.Go, PrimitiveType.Cylinder, v.Owner.Def.Color);
            foreach (var r in rings)
                Add(r.Go, PrimitiveType.Cylinder, r.Go.GetComponent<Renderer>().sharedMaterial.GetColor(BaseColorId));
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
            int count = snapshot.effects.Length;
            for (int i = replicatedEffects.Count - 1; i >= count; i--)
            {
                Object.Destroy(replicatedEffects[i]); replicatedEffects.RemoveAt(i);
            }
            for (int i = 0; i < count; i++)
            {
                var effect = snapshot.effects[i];
                string name = "Replica_" + effect.primitive;
                if (i < replicatedEffects.Count && replicatedEffects[i].name != name)
                { Object.Destroy(replicatedEffects[i]); replicatedEffects[i] = null; }
                GameObject go = i < replicatedEffects.Count ? replicatedEffects[i] : null;
                if (go == null)
                {
                    go = GameObject.CreatePrimitive((PrimitiveType)effect.primitive);
                    Object.Destroy(go.GetComponent<Collider>());
                    go.name = name; go.transform.SetParent(FxRoot.transform, false);
                    OwnedMaterial.Assign(go, MakeUnlit(effect.color));
                    if (i < replicatedEffects.Count) replicatedEffects[i] = go;
                    else replicatedEffects.Add(go);
                }
                go.transform.position = effect.position;
                go.transform.localScale = effect.scale;
                go.GetComponent<Renderer>().sharedMaterial.SetColor(BaseColorId, effect.color);
            }
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

            var to = renderTo.effects;
            if (to == null) return;
            var previous = from?.effects;
            for (int i = 0; i < replicatedEffects.Count && i < to.Length; i++)
            {
                GameObject go = replicatedEffects[i];
                if (go == null || to[i] == null) continue;
                Vector3 position = to[i].position;
                Vector3 scale = to[i].scale;
                // Only blend when the slot still holds the same kind of effect;
                // a respawned effect simply appears at its newest transform.
                if (previous != null && i < previous.Length && previous[i] != null &&
                    previous[i].primitive == to[i].primitive)
                {
                    position = Vector3.Lerp(previous[i].position, position, k);
                    scale = Vector3.Lerp(previous[i].scale, scale, k);
                }
                go.transform.position = position;
                go.transform.localScale = scale;
            }

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
            foreach (var p in projectiles) if (p.Go != null) Object.Destroy(p.Go);
            foreach (var v in vortices) if (v.Go != null) Object.Destroy(v.Go);
            foreach (var r in rings) if (r.Go != null) Object.Destroy(r.Go);
            foreach (var go in replicatedEffects) if (go != null) Object.Destroy(go);
            replicatedEffects.Clear();
            accumulatedTime = 0d;
            projectiles.Clear();
            vortices.Clear();
            rings.Clear();
            if (fxRoot != null) Object.Destroy(fxRoot);
            fxRoot = null;
        }

        /// <summary>Where a blink may land: inside the same playable octagon.</summary>
        private Vector3 ClampToArena(Vector3 p) => GameConfig.ClampToPlay(p);

        // The four diagonal faces of the playable octagon, as (x, z) signs.
        private static readonly float[] DiagonalX = { 1f, 1f, -1f, -1f };
        private static readonly float[] DiagonalZ = { 1f, -1f, 1f, -1f };

        private static float WallFraction(Vector3 start, Vector3 end, float radius)
        {
            float x = GameConfig.ArenaWidth * 0.5f - 0.3f - radius;
            float z = GameConfig.ArenaHeight * 0.5f - 0.3f - radius;
            float diagonal = Mathf.Abs(start.x) + Mathf.Abs(start.z);
            if (Mathf.Abs(start.x) >= x || Mathf.Abs(start.z) >= z ||
                diagonal >= GameConfig.PlayDiagonal) return 0f;
            Vector3 delta = end - start;
            float fraction = 1f;
            if (end.x > x) fraction = Mathf.Min(fraction, (x - start.x) / delta.x);
            if (end.x < -x) fraction = Mathf.Min(fraction, (-x - start.x) / delta.x);
            if (end.z > z) fraction = Mathf.Min(fraction, (z - start.z) / delta.z);
            if (end.z < -z) fraction = Mathf.Min(fraction, (-z - start.z) / delta.z);

            // A shot aimed at a corner leaves through the cut face, not a wall.
            for (int i = 0; i < 4; i++)
            {
                float before = start.x * DiagonalX[i] + start.z * DiagonalZ[i];
                float after = end.x * DiagonalX[i] + end.z * DiagonalZ[i];
                if (after <= GameConfig.PlayDiagonal || after == before) continue;
                fraction = Mathf.Min(fraction,
                    (GameConfig.PlayDiagonal - before) / (after - before));
            }
            return Mathf.Clamp01(fraction);
        }

        // Relative-motion circle sweep on the ground plane, earliest time of impact.
        private static bool SweptContact(Vector3 a0, Vector3 a1,
            Vector3 b0, Vector3 b1, float radius, out float time)
        {
            Vector3 offset = a0 - b0;
            Vector3 motion = (a1 - a0) - (b1 - b0);
            offset.y = motion.y = 0f;
            time = 0f;
            float c = offset.sqrMagnitude - radius * radius;
            if (c <= 0f) return true;
            float a = motion.sqrMagnitude;
            if (a <= 1e-10f) return false;
            float b = Vector3.Dot(offset, motion);
            float discriminant = b * b - a * c;
            if (discriminant < 0f) return false;
            time = (-b - Mathf.Sqrt(discriminant)) / a;
            return time >= 0f && time <= 1f;
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static Material fxMaterialTemplate;

        private static Material MakeUnlit(Color c)
        {
            Material m = null;
            if (fxMaterialTemplate == null)
            {
                fxMaterialTemplate = Resources.Load<Material>("Arena/ArenaUnlit");
            }

            if (fxMaterialTemplate != null)
            {
                m = new Material(fxMaterialTemplate) { name = "SpellFx" };
            }
            else
            {
                if (fxShader == null)
                {
                    fxShader = Shader.Find("Universal Render Pipeline/Unlit");
                    if (fxShader == null) fxShader = Shader.Find("Unlit/Color");
                }
                if (fxShader != null)
                {
                    m = new Material(fxShader) { name = "SpellFx" };
                }
            }

            if (m != null)
            {
                if (m.HasProperty(BaseColorId)) m.SetColor(BaseColorId, c);
                else m.color = c;
            }
            return m;
        }
    }
}
