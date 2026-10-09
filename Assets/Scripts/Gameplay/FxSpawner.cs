using System.Collections.Generic;
using UnityEngine;
using Arena.Core;
using Arena.Presentation;

namespace Arena.Gameplay
{
    /// <summary>
    /// Owns every transient spell effect for one match: the thrown orbs and
    /// blades, vortex hazards, expanding rings and ground rifts, plus the replica
    /// effects the guest rebuilds from host snapshots. <see cref="Clear"/>
    /// destroys all of them when the match ends; the fighter views are destroyed
    /// by the caller.
    /// </summary>
    internal sealed class FxSpawner
    {
        private readonly List<Projectile> projectiles = new List<Projectile>();
        private readonly List<VortexHazard> vortices = new List<VortexHazard>();
        private readonly List<RingFx> rings = new List<RingFx>();
        private readonly List<RiftFx> rifts = new List<RiftFx>();
        private readonly List<WarningFx> warnings = new List<WarningFx>();
        private readonly List<GameObject> replicatedEffects = new List<GameObject>();
        private GameObject fxRoot;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly Color RiftStone = new Color(0.17f, 0.18f, 0.23f);
        private static Shader fxShader;
        private static Material fxMaterialTemplate;

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

        // ------------------------------------------------------------- spawning

        public void SpawnProjectile(FighterSim caster, AbilityDef ab, Vector3 dir)
            => SpawnProjectile(caster, ab, dir, null);

        /// <summary>
        /// One thrown weapon: orb, blade, dagger or the aegis itself. Silhouette,
        /// speed and lifetime all come off the ability, and a throw that carries a
        /// <see cref="AbilityDef.ProjectileRange"/> turns around at the end of that
        /// run and homes back to the hand it left.
        ///
        /// <paramref name="volley"/> is the hit list a fan of blades shares, so a
        /// fighter standing inside the fan is charged by one blade of the throw
        /// rather than by every blade that crosses them.
        /// </summary>
        public void SpawnProjectile(FighterSim caster, AbilityDef ab, Vector3 dir,
            List<FighterSim> volley)
        {
            ProjectileShape shape = ab.ProjectileShape;
            bool orb = shape == ProjectileShape.Orb;
            var go = GameObject.CreatePrimitive(orb ? PrimitiveType.Sphere : PrimitiveType.Cube);
            Object.Destroy(go.GetComponent<Collider>());
            go.name = ShapeName(shape);
            go.transform.SetParent(FxRoot.transform, false);
            go.transform.position =
                caster.Position + Vector3.up * ShapeHeight(shape) + dir * 0.9f;
            go.transform.localScale = ShapeScale(shape);
            OwnedMaterial.Assign(go, MakeUnlit(orb
                ? caster.Def.ColorLight
                : Color.Lerp(caster.Def.ColorLight, Color.white, 0.35f)));

            float speed = ab.ProjectileSpeed > 0f ? ab.ProjectileSpeed : GameConfig.OrbSpeed;
            projectiles.Add(new Projectile
            {
                Go = go,
                Velocity = dir * speed,
                Ab = ab,
                Owner = caster,
                Life = ab.ProjectileLife > 0f ? ab.ProjectileLife : 1.5f,
                Origin = go.transform.position,
                Speed = speed,
                Height = ShapeHeight(shape),
                Spin = ShapeSpin(shape),
                TurnAt = ab.ProjectileRange,
                Touched = volley
            });
        }

        /// <summary>
        /// The Blade Wave: one fan of blades thrown together, spread across
        /// <see cref="AbilityDef.FanSpread"/> around the aim. The wave is there to
        /// catch a target who dodges sideways, so the blades share a single hit
        /// list: whoever is struck eats one blade, not the whole fan.
        /// </summary>
        public void SpawnBladeWave(FighterSim caster, AbilityDef ab, Vector3 dir)
        {
            int count = Mathf.Clamp(ab.FanCount, 2, 12);
            float spread = ab.FanSpread > 0f ? ab.FanSpread : 80f;
            Vector3 aim = new Vector3(dir.x, 0f, dir.z);
            aim = aim.sqrMagnitude > 0.0001f ? aim.normalized : Vector3.forward;

            var volley = new List<FighterSim>(count);
            for (int i = 0; i < count; i++)
            {
                float t = count == 1 ? 0.5f : i / (float)(count - 1);
                Vector3 shot = Quaternion.AngleAxis(
                    Mathf.Lerp(-spread * 0.5f, spread * 0.5f, t), Vector3.up) * aim;
                SpawnProjectile(caster, ab, shot, volley);
            }
        }

        private static string ShapeName(ProjectileShape shape) => shape switch
        {
            ProjectileShape.Blade => "Blade",
            ProjectileShape.Dagger => "Dagger",
            ProjectileShape.Shield => "Shield",
            _ => "Orb"
        };

        private static Vector3 ShapeScale(ProjectileShape shape) => shape switch
        {
            ProjectileShape.Blade => new Vector3(0.16f, 0.50f, 1.5f),
            // A knife, not a sword: half the blade, a quarter of the mass.
            ProjectileShape.Dagger => new Vector3(0.11f, 0.17f, 0.62f),
            // The aegis: a slab as tall as a torso and as thin as a plate.
            ProjectileShape.Shield => new Vector3(0.54f, 0.76f, 0.17f),
            _ => Vector3.one * 0.75f
        };

        private static float ShapeHeight(ProjectileShape shape) => shape switch
        {
            ProjectileShape.Dagger => 1.15f,
            ProjectileShape.Shield => 1.05f,
            ProjectileShape.Blade => 1.0f,
            _ => 1.1f
        };

        /// <summary>Degrees per second rolled about the travel axis; orbs do not spin.</summary>
        private static float ShapeSpin(ProjectileShape shape) => shape switch
        {
            ProjectileShape.Dagger => GameConfig.BladeSpinDegreesPerSecond * 2f,
            ProjectileShape.Shield => GameConfig.BladeSpinDegreesPerSecond * 0.85f,
            ProjectileShape.Blade => GameConfig.BladeSpinDegreesPerSecond,
            _ => 0f
        };

        /// <summary>
        /// The Vortex as a thrown hazard: the funnel starts at the caster's feet
        /// and slides <paramref name="length"/> down <paramref name="dir"/> over
        /// its lifetime, wounding and dragging everyone it passes, then bursting
        /// where it stops. The funnel rides the hazard's centre, so what the player
        /// watches travel is exactly what hits.
        /// </summary>
        public void SpawnVortex(FighterSim caster, AbilityDef ab, Vector3 dir, float length)
        {
            Vector3 flat = new Vector3(dir.x, 0f, dir.z);
            flat = flat.sqrMagnitude > 0.0001f ? flat.normalized : Vector3.forward;
            Vector3 centre = caster.Position;

            var go = new GameObject("Vortex");
            go.transform.SetParent(FxRoot.transform, false);
            go.transform.position = centre + Vector3.up * 0.02f;
            var bands = new List<FunnelBand>(3);
            // A tornado, not a pancake: bands stacked into a funnel that is widest
            // at the base and tightest at the top, each swirling at its own speed
            // so the silhouette twists as the whole thing travels.
            AddFunnelBand(go.transform, bands, ab.HazardRadius * 2f, 0.80f, 0.40f,
                caster.Def.Color, 340f, 0f);
            AddFunnelBand(go.transform, bands, ab.HazardRadius * 1.4f, 1.50f, 1.25f,
                caster.Def.Color, -470f, 1.9f);
            AddFunnelBand(go.transform, bands, ab.HazardRadius * 0.8f, 2.30f, 2.60f,
                caster.Def.ColorLight, 580f, 3.7f);
            SpawnRing(centre, ab.HazardRadius, caster.Def.ColorLight);

            vortices.Add(new VortexHazard
            {
                Go = go,
                Bands = bands,
                Centre = centre,
                Radius = ab.HazardRadius,
                Life = ab.HazardLifetime,
                TotalLife = ab.HazardLifetime,
                Ab = ab,
                Owner = caster,
                Direction = flat,
                Speed = length / Mathf.Max(0.2f, ab.HazardLifetime),
                Remaining = length
            });
        }

        /// <summary>
        /// The ground warning a line spell lays down before it lands: a chain of
        /// flat markers along the corridor, blinking faster as the strike closes
        /// in. Markers rather than one long bar because snapshots carry no
        /// rotation — the guest has to rebuild the same corridor from position,
        /// scale and colour alone.
        /// </summary>
        public void SpawnWarning(Vector3 origin, Vector3 dir, float length, float width,
            Color color, float seconds)
        {
            Vector3 flat = new Vector3(dir.x, 0f, dir.z);
            flat = flat.sqrMagnitude > 0.0001f ? flat.normalized : Vector3.forward;

            const int markers = 7;
            var root = new GameObject("SpellWarning");
            root.transform.SetParent(FxRoot.transform, false);

            var bar = new List<Transform>(markers);
            for (int i = 0; i < markers; i++)
            {
                var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Object.Destroy(marker.GetComponent<Collider>());
                marker.name = "Warn" + i;
                marker.transform.SetParent(root.transform, false);
                float along = length * ((i + 0.5f) / markers);
                marker.transform.position = origin + flat * along + Vector3.up * 0.04f;
                marker.transform.localScale = new Vector3(width, 0.04f, width);
                OwnedMaterial.Assign(marker, MakeUnlit(color));
                bar.Add(marker.transform);
            }

            warnings.Add(new WarningFx
            {
                Root = root.transform,
                Markers = bar,
                T = 0f,
                Total = Mathf.Max(0.05f, seconds),
                Width = width,
                Color = color
            });
        }

        /// <summary>
        /// The wind-up for the thrown Vortex: the funnel gathers under the
        /// caster's feet, swelling and spinning up for the length of the
        /// telegraph, and it follows the caster because they are carrying it.
        /// When the telegraph runs out the throw starts from where the player
        /// last saw the funnel, not from a marker somewhere out in the lane.
        /// </summary>
        public void SpawnCharge(FighterSim owner, float radius, Color color, float seconds)
        {
            var root = new GameObject("SpellCharge");
            root.transform.SetParent(FxRoot.transform, false);
            root.transform.position = owner.Position + Vector3.up * 0.02f;

            var bands = new List<FunnelBand>(2);
            var parts = new List<Transform>(2);
            AddFunnelBand(root.transform, bands, radius * 1.7f, 0.70f, 0.35f, color, 320f, 0f);
            AddFunnelBand(root.transform, bands, radius * 1.0f, 1.40f, 1.15f, color, -430f, 2.2f);
            foreach (var band in bands) parts.Add(band.Part);
            root.transform.localScale = new Vector3(0.4f, 0.35f, 0.4f);

            warnings.Add(new WarningFx
            {
                Root = root.transform,
                Markers = parts,
                Bands = bands,
                Follows = owner,
                Charge = true,
                T = 0f,
                Total = Mathf.Max(0.05f, seconds),
                Width = radius,
                Color = color
            });
        }

        /// <summary>
        /// One band of a funnel. A unit cylinder is two units tall, so the height
        /// scale is half the band's real height.
        /// </summary>
        private static void AddFunnelBand(Transform root, List<FunnelBand> bands,
            float diameter, float height, float centreY, Color color, float spin, float phase)
        {
            var band = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Object.Destroy(band.GetComponent<Collider>());
            band.name = "Band";
            band.transform.SetParent(root, false);
            band.transform.localPosition = new Vector3(0f, centreY, 0f);
            band.transform.localScale = new Vector3(diameter, height * 0.5f, diameter);
            OwnedMaterial.Assign(band, MakeUnlit(color));
            bands.Add(new FunnelBand { Part = band.transform, Spin = spin, Phase = phase });
        }

        /// <summary>
        /// Swirls the bands of a funnel. A plain cylinder is rotationally symmetric,
        /// so turning it about its own axis shows nothing: the read comes from each
        /// band orbiting the funnel's axis, which is what makes the column look like
        /// it is turning as it travels.
        /// </summary>
        private static void SwirlFunnel(List<FunnelBand> bands, float dt, float rate)
        {
            for (int i = 0; i < bands.Count; i++)
            {
                FunnelBand band = bands[i];
                if (band.Part == null) continue;
                band.Phase = Mathf.Repeat(band.Phase + band.Spin * rate * dt * Mathf.Deg2Rad,
                    2f * Mathf.PI);
                float orbit = band.Part.localScale.x * 0.16f;
                band.Part.localPosition = new Vector3(Mathf.Sin(band.Phase) * orbit,
                    band.Part.localPosition.y, Mathf.Cos(band.Phase) * orbit);
            }
        }

        public void SpawnRing(Vector3 centre, float radius, Color color)
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

        /// <summary>
        /// The Earthrift's crack: a line of stone plates that are already lifted
        /// and tilted away from the ground, plus a glowing seam down the middle so
        /// it still reads from the overhead camera. Purely visual — the damage and
        /// the launch are applied once, by the cast.
        /// </summary>
        public void SpawnRift(Vector3 origin, Vector3 dir, float length, float width, Color color)
        {
            Vector3 flat = new Vector3(dir.x, 0f, dir.z);
            flat = flat.sqrMagnitude > 0.0001f ? flat.normalized : Vector3.forward;

            var root = new GameObject("Rift");
            root.transform.SetParent(FxRoot.transform, false);
            root.transform.position = origin;
            root.transform.rotation = Quaternion.Euler(
                0f, Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg, 0f);

            const int slabs = 6;
            float step = length / slabs;
            for (int i = 0; i < slabs; i++)
            {
                // Plates are tallest around the middle of the crack.
                float middle = 1f - Mathf.Abs(i - (slabs - 1) * 0.5f) / (slabs * 0.5f);
                var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.Destroy(slab.GetComponent<Collider>());
                slab.name = "RiftSlab" + i;
                slab.transform.SetParent(root.transform, false);
                slab.transform.localPosition = new Vector3(
                    Mathf.Sin(i * 2.4f) * width * 0.16f, 0.05f, step * (i + 0.5f));
                // The tilt is the read: broken ground, not a painted stripe.
                slab.transform.localRotation = Quaternion.Euler(
                    Mathf.Sin(i * 2.7f) * 15f,
                    Mathf.Cos(i * 1.9f) * 9f,
                    Mathf.Sin(i * 3.1f) * 13f);
                slab.transform.localScale = new Vector3(
                    width * (0.85f + 0.30f * middle), 0.30f + 0.24f * middle, step * 0.90f);
                OwnedMaterial.Assign(slab, MakeUnlit(RiftStone));
            }

            var seam = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(seam.GetComponent<Collider>());
            seam.name = "RiftSeam";
            seam.transform.SetParent(root.transform, false);
            seam.transform.localPosition = new Vector3(0f, 0.06f, length * 0.5f);
            seam.transform.localScale = new Vector3(width * 0.22f, 0.08f, length * 0.94f);
            OwnedMaterial.Assign(seam, MakeUnlit(color));

            rifts.Add(new RiftFx { Root = root.transform, T = 0f, Seam = color });
        }

        // --------------------------------------------------------------- ticking

        public void TickProjectiles(float dt, IList<FighterSim> fighters)
        {
            for (int i = projectiles.Count - 1; i >= 0; i--)
            {
                Projectile p = projectiles[i];
                Vector3 start = p.Go.transform.position;
                float travelTime = Mathf.Min(dt, Mathf.Max(0f, p.Life));
                Vector3 end = start + p.Velocity * travelTime;
                // Stop at the inner wall face, accounting for the orb radius.
                float wallFraction = CombatResolver.WallFraction(start, end, 0.375f);
                end = Vector3.Lerp(start, end, wallFraction);
                float targetFraction = travelTime / dt * wallFraction;

                // Whoever the thrower is not, earliest contact wins: in a
                // free-for-all the shot has to pick its own victim.
                FighterSim hit = null;
                float contact = 2f;
                for (int f = 0; wallFraction > 0f && f < fighters.Count; f++)
                {
                    FighterSim candidate = fighters[f];
                    if (candidate == p.Owner || candidate.Dead) continue;
                    // A thrown weapon that has already landed on this leg of its
                    // flight passes through: the shield scores once on the way out
                    // and once on the way home, and a blade of a fan scores once
                    // per fighter for the whole throw.
                    if (p.Scored) continue;
                    if (p.Touched != null && p.Touched.Contains(candidate)) continue;
                    Vector3 candidateEnd = Vector3.Lerp(candidate.PreviousPosition,
                        candidate.Position, targetFraction);
                    if (CombatResolver.SweptContact(start, end, candidate.PreviousPosition,
                        candidateEnd, GameConfig.PlayerRadius + 0.375f, out float found) &&
                        found < contact)
                    {
                        hit = candidate;
                        contact = found;
                    }
                }

                if (hit != null)
                {
                    p.Go.transform.position = Vector3.Lerp(start, end, contact);
                    CombatResolver.ApplyHit(hit, p.Ab, p.Velocity);
                    p.Scored = true;
                    if (p.Touched != null) p.Touched.Add(hit);
                    // A one-way throw is spent on the fighter it hit; a boomerang
                    // flies on, so the trip home is a second chance.
                    if (p.TurnAt <= 0f) p.Life = 0f;
                }
                else
                {
                    p.Go.transform.position = end;
                    p.Life -= dt;
                    if (wallFraction < 1f) p.Life = 0f;
                }

                // A boomerang throw turns around at the end of its run and homes
                // back to the hand it left, so the shield is thrown rather than
                // spent — and it is still dangerous on the way home.
                if (p.TurnAt > 0f && p.Life > 0f)
                {
                    Vector3 here = p.Go.transform.position;
                    if (!p.Returning && CombatResolver.FlatDistance(p.Origin, here) >= p.TurnAt)
                    {
                        p.Returning = true;
                        p.Scored = false;      // the way home is a fresh leg
                    }

                    if (p.Returning)
                    {
                        if (p.Owner.Dead)
                        {
                            p.TurnAt = 0f;      // nobody left to catch it
                        }
                        else
                        {
                            Vector3 home = p.Owner.Position + Vector3.up * p.Height +
                                p.Owner.Forward * 0.6f;
                            Vector3 toHome = home - here;
                            toHome.y = 0f;
                            if (toHome.magnitude <= 0.55f)
                            {
                                Object.Destroy(p.Go);
                                projectiles.RemoveAt(i);
                                continue;
                            }
                            p.Velocity = toHome.normalized * p.Speed;
                        }
                    }
                }

                // A blade rolls about its own travel axis, so the silhouette the
                // guest reproduces from the snapshot stays close enough.
                if (p.Spin > 0f && p.Go != null && p.Velocity.sqrMagnitude > 0.0001f)
                {
                    p.SpinAngle = Mathf.Repeat(p.SpinAngle + p.Spin * dt, 360f);
                    p.Go.transform.rotation = Quaternion.LookRotation(p.Velocity) *
                        Quaternion.AngleAxis(p.SpinAngle, Vector3.forward);
                }

                if (p.Life <= 0f)
                {
                    Object.Destroy(p.Go);
                    projectiles.RemoveAt(i);
                }
            }
        }

        public void TickVortices(float dt, IList<FighterSim> fighters)
        {
            for (int i = vortices.Count - 1; i >= 0; i--)
            {
                VortexHazard v = vortices[i];
                // Thrown, not dropped: the whole hazard slides down its line, and
                // the pull travels with it.
                float travel = Mathf.Min(v.Speed * dt, v.Remaining);
                v.Centre += v.Direction * travel;
                v.Remaining -= travel;
                v.Life -= dt;

                // Visual: the funnel rides the hazard, so the centre the pull and
                // the burst use is the centre the player watches move.
                if (v.Go != null)
                {
                    v.Go.transform.position = v.Centre +
                        Vector3.up * (0.02f + 0.05f * Mathf.Sin(Time.time * 11f));
                    SwirlFunnel(v.Bands, dt, 1f);
                    // It tightens as it charges its burst.
                    float charge = Mathf.Lerp(0.78f, 1.06f, 1f - Mathf.Clamp01(v.Life / v.TotalLife));
                    v.Go.transform.localScale = new Vector3(charge, 1f, charge);
                }

                // The hazard drags every fighter but its owner, and it wounds them
                // on the way: a free-for-all vortex is an area denial. The suction
                // runs down the lane rather than back up it, so a thrown vortex
                // sweeps its victims along the throw instead of yanking them
                // backwards through the caster.
                for (int f = 0; f < fighters.Count; f++)
                {
                    FighterSim target = fighters[f];
                    if (target == v.Owner || target.Dead || target.Invulnerable > 0f) continue;
                    Vector3 toCentre = v.Centre - target.Position;
                    toCentre.y = 0f;
                    float d = toCentre.magnitude;
                    if (d <= 0.01f || d > v.Radius) continue;

                    // Hardest at the eye of the vortex, fading out to nothing at
                    // the rim, so the edge of the lane is the dodge.
                    float grip = 1f - d / v.Radius;
                    target.Velocity += v.Direction * (GameConfig.VortexPullSpeed * grip * dt);

                    // A nudge across the lane keeps whoever is caught inside the
                    // corridor the vortex is dragging them down.
                    Vector3 across = new Vector3(-v.Direction.z, 0f, v.Direction.x);
                    target.Velocity += across * (Vector3.Dot(toCentre, across) * 2.5f * dt);

                    // The touch is the hit: whoever the travelling vortex sweeps
                    // takes its damage, stun and launch right there instead of
                    // waiting for the burst at the end of the line. Each fighter
                    // is only touched once per throw, so the burst below never
                    // charges the same victim a second time.
                    if (v.Touched.Contains(target)) continue;
                    v.Touched.Add(target);
                    Vector3 flung = target.Position - v.Centre;
                    flung.y = 0f;
                    CombatResolver.ApplyHit(target, v.Ab,
                        flung.sqrMagnitude > 0.0001f ? flung : v.Direction);
                    SpawnRing(target.Position, 1.8f, v.Owner.Def.ColorLight);
                }

                if (v.Life > 0f && v.Remaining > 0f) continue;

                // Burst: whoever is inside and was not swept along the way eats
                // the same launch, so one throw still costs a fighter one hit.
                for (int f = 0; f < fighters.Count; f++)
                {
                    FighterSim target = fighters[f];
                    if (target == v.Owner || target.Dead) continue;
                    if (v.Touched.Contains(target)) continue;
                    Vector3 away = target.Position - v.Centre;
                    away.y = 0f;
                    if (away.sqrMagnitude <= v.Radius * v.Radius)
                        CombatResolver.ApplyHit(target, v.Ab, away.sqrMagnitude > 0.0001f ? away : Vector3.forward);
                }

                SpawnRing(v.Centre, v.Radius, v.Owner.Def.ColorLight);
                Object.Destroy(v.Go);
                vortices.RemoveAt(i);
            }
        }

        /// <summary>Blinks the ground warnings; the cadence tightens as impact nears.</summary>
        public void TickWarnings(float dt)
        {
            for (int i = warnings.Count - 1; i >= 0; i--)
            {
                WarningFx w = warnings[i];
                w.T += dt;
                float k = Mathf.Clamp01(w.T / w.Total);
                float pulse = 0.5f + 0.5f * Mathf.Sin(w.T * (7f + 16f * k));
                Color color = Color.Lerp(w.Color, Color.white, 0.10f + 0.65f * k * pulse);

                if (w.Charge)
                {
                    // The funnel gathering under the caster: it follows them,
                    // swells out of the ground and spins up until the throw.
                    if (w.Root == null) { warnings.RemoveAt(i); continue; }
                    if (w.Follows != null)
                        w.Root.position = w.Follows.Position + Vector3.up * 0.02f;
                    SwirlFunnel(w.Bands, dt, 0.35f + 1.15f * k);
                    for (int m = 0; m < w.Markers.Count; m++)
                    {
                        Transform band = w.Markers[m];
                        if (band == null) continue;
                        var bandRenderer = band.GetComponent<Renderer>();
                        if (bandRenderer == null || bandRenderer.sharedMaterial == null) continue;
                        if (bandRenderer.sharedMaterial.HasProperty(BaseColorId))
                            bandRenderer.sharedMaterial.SetColor(BaseColorId, color);
                        else bandRenderer.sharedMaterial.color = color;
                    }

                    float grow = 0.4f + 0.6f * k;
                    w.Root.localScale = new Vector3(grow, 0.35f + 0.65f * k, grow);

                    if (w.T < w.Total) continue;
                    Object.Destroy(w.Root.gameObject);
                    warnings.RemoveAt(i);
                    continue;
                }

                float diameter = w.Width * (0.62f + 0.38f * pulse);
                if (w.Root != null)
                {
                    for (int m = 0; m < w.Markers.Count; m++)
                    {
                        Transform marker = w.Markers[m];
                        if (marker == null) continue;
                        marker.localScale = new Vector3(diameter, 0.04f, diameter);
                        var renderer = marker.GetComponent<Renderer>();
                        if (renderer == null || renderer.sharedMaterial == null) continue;
                        if (renderer.sharedMaterial.HasProperty(BaseColorId))
                            renderer.sharedMaterial.SetColor(BaseColorId, color);
                        else renderer.sharedMaterial.color = color;
                    }
                }

                if (w.T < w.Total) continue;
                if (w.Root != null) Object.Destroy(w.Root.gameObject);
                warnings.RemoveAt(i);
            }
        }

        public void TickRings(float dt)
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

        public void TickRifts(float dt)
        {
            for (int i = rifts.Count - 1; i >= 0; i--)
            {
                RiftFx r = rifts[i];
                r.T += dt;
                float k = Mathf.Clamp01(r.T / GameConfig.RiftFxSeconds);
                // The plates burst up out of the ground, then sink back under it.
                if (r.Root != null)
                    r.Root.localScale = new Vector3(1f, Mathf.Max(0.04f, Mathf.Sin(k * Mathf.PI)), 1f);

                if (r.T < GameConfig.RiftFxSeconds) continue;
                if (r.Root != null) Object.Destroy(r.Root.gameObject);
                rifts.RemoveAt(i);
            }
        }

        // ---------------------------------------------------------- replication

        public void CaptureEffects(List<EffectSnapshot> effects)
        {
            void Add(GameObject go, PrimitiveType type, Color color)
            {
                effects.Add(new EffectSnapshot { primitive = (int)type,
                    position = go.transform.position, scale = go.transform.localScale, color = color });
            }
            // Blades, daggers and the shield are all slabs of a different size,
            // and the snapshot carries scale, so one primitive reproduces them all.
            foreach (var p in projectiles) Add(p.Go, p.Ab.ProjectileShape == ProjectileShape.Orb
                ? PrimitiveType.Sphere : PrimitiveType.Cube, p.Owner.Def.ColorLight);
            foreach (var v in vortices)
            {
                if (v.Go == null) continue;
                // The funnel is a stack of bands; the guest rebuilds the whole
                // silhouette because each part carries position, scale and colour.
                foreach (Transform band in v.Go.transform)
                    Add(band.gameObject, PrimitiveType.Cylinder,
                        band.GetComponent<Renderer>().sharedMaterial.GetColor(BaseColorId));
            }
            foreach (var r in rings)
                Add(r.Go, PrimitiveType.Cylinder, r.Go.GetComponent<Renderer>().sharedMaterial.GetColor(BaseColorId));
            foreach (var rift in rifts)
            {
                if (rift.Root == null) continue;
                // The replica is a flat stone patch: snapshots carry no rotation,
                // so the plates cannot be re-tilted on the guest. They still read
                // as broken ground because they keep their scale and colour.
                foreach (Transform part in rift.Root)
                    Add(part.gameObject, PrimitiveType.Cube,
                        part.name == "RiftSeam" ? rift.Seam : RiftStone);
            }
            foreach (var warning in warnings)
            {
                if (warning.Root == null) continue;
                // Markers are position + scale + colour only, so a guest rebuilds
                // the exact corridor that is about to be hit, blink and all.
                foreach (Transform marker in warning.Markers)
                    if (marker != null)
                        Add(marker.gameObject, PrimitiveType.Cylinder,
                            marker.GetComponent<Renderer>().sharedMaterial.GetColor(BaseColorId));
            }
        }

        /// <summary>Rebuilds the guest's replica hierarchy to match a snapshot's effects.</summary>
        public void SyncReplicated(EffectSnapshot[] effects)
        {
            int count = effects.Length;
            for (int i = replicatedEffects.Count - 1; i >= count; i--)
            {
                Object.Destroy(replicatedEffects[i]); replicatedEffects.RemoveAt(i);
            }
            for (int i = 0; i < count; i++)
            {
                var effect = effects[i];
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

        /// <summary>Eases the guest's replica effects from the previous snapshot towards the newest.</summary>
        public void EaseReplicated(EffectSnapshot[] previous, EffectSnapshot[] to, float k)
        {
            if (to == null) return;
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
        }

        /// <summary>Destroys every transient effect this spawner created.</summary>
        public void Clear()
        {
            foreach (var p in projectiles) if (p.Go != null) Object.Destroy(p.Go);
            foreach (var v in vortices) if (v.Go != null) Object.Destroy(v.Go);
            foreach (var r in rings) if (r.Go != null) Object.Destroy(r.Go);
            foreach (var r in rifts) if (r.Root != null) Object.Destroy(r.Root.gameObject);
            foreach (var w in warnings) if (w.Root != null) Object.Destroy(w.Root.gameObject);
            foreach (var go in replicatedEffects) if (go != null) Object.Destroy(go);
            replicatedEffects.Clear();
            projectiles.Clear();
            vortices.Clear();
            rings.Clear();
            rifts.Clear();
            warnings.Clear();
            if (fxRoot != null) Object.Destroy(fxRoot);
            fxRoot = null;
        }

        // ------------------------------------------------------------ effect data

        private sealed class Projectile
        {
            public GameObject Go;
            public Vector3 Velocity;
            public AbilityDef Ab;
            public FighterSim Owner;
            public float Life;
            /// <summary>Where it was thrown from: the run a boomerang turns around at.</summary>
            public Vector3 Origin;
            /// <summary>Constant travel speed, in u/s — kept so the way home is as fast as the way out.</summary>
            public float Speed;
            /// <summary>Height above the ground the throw travels at, so it returns to the hand.</summary>
            public float Height;
            /// <summary>Metres out before it turns around; 0 for a one-way throw.</summary>
            public float TurnAt;
            /// <summary>Set once a boomerang throw has turned and is heading home.</summary>
            public bool Returning;
            /// <summary>Set once this leg of the flight has already landed a hit.</summary>
            public bool Scored;
            /// <summary>Set shared across a fan of blades: fighters this throw has already charged.</summary>
            public List<FighterSim> Touched;
            /// <summary>Degrees per second rolled about the travel axis; 0 for orbs.</summary>
            public float Spin;
            public float SpinAngle;
        }

        /// <summary>One swirling band of a funnel: the part, its spin rate and its phase.</summary>
        private sealed class FunnelBand
        {
            public Transform Part;
            /// <summary>Degrees per second around the funnel's axis.</summary>
            public float Spin;
            /// <summary>Radians, advanced by the spin.</summary>
            public float Phase;
        }

        private sealed class VortexHazard
        {
            public GameObject Go;
            /// <summary>The stacked bands that make the funnel, and their swirl.</summary>
            public List<FunnelBand> Bands;
            /// <summary>Fighters this throw has already wounded, so it never hits twice.</summary>
            public readonly List<FighterSim> Touched = new List<FighterSim>();
            public Vector3 Centre;
            public float Radius;
            public float Life;
            public float TotalLife;
            public AbilityDef Ab;
            public FighterSim Owner;
            /// <summary>Flat direction it slides along, and how far it still has to go.</summary>
            public Vector3 Direction;
            public float Speed;
            public float Remaining;
        }

        private sealed class RingFx
        {
            public GameObject Go;
            public float T;
            public float MaxRadius;
        }

        private sealed class RiftFx
        {
            public Transform Root;
            public float T;
            public Color Seam;
        }

        private sealed class WarningFx
        {
            public Transform Root;
            public List<Transform> Markers;
            /// <summary>The funnel's bands, for a gathering funnel; empty for a corridor.</summary>
            public List<FunnelBand> Bands;
            /// <summary>Set for a gathering funnel: it follows this fighter until it is thrown.</summary>
            public FighterSim Follows;
            public bool Charge;
            public float T;
            public float Total;
            public float Width;
            public Color Color;
        }

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
