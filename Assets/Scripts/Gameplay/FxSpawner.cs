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
        private readonly List<Replica> replicatedEffects = new List<Replica>();

        /// <summary>
        /// The match's fighters in snapshot slot order - 0 is the local player, 1 the
        /// fighter the LAN path mirrors - handed over by the sim. An effect snapshot
        /// names the thrower by that slot, which is what lets the guest take the worn
        /// prop off the same arm the host did.
        /// </summary>
        private IList<FighterSim> roster;

        private GameObject fxRoot;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly Color RiftStone = new Color(0.17f, 0.18f, 0.23f);
        /// <summary>The hole itself: darker than the broken plates beside it.</summary>
        private static readonly Color RiftVoid = new Color(0.05f, 0.05f, 0.08f);
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
            // The real prop, when there is one: the Vanguard's lion shield and the
            // Assassin's knife are their own objects (see PropLibrary), so the throw is
            // the thing the fighter is actually carrying rather than a slab standing in
            // for it. Speed, lifetime and contact are the same either way, and a missing
            // asset falls back to the slab.
            int wire = PropLibrary.WireId(shape);
            GameObject prop = PropLibrary.Fly(wire, ShapeName(shape));
            var go = prop == null
                ? GameObject.CreatePrimitive(orb ? PrimitiveType.Sphere : PrimitiveType.Cube)
                : new GameObject(ShapeName(shape));
            if (prop == null) Object.Destroy(go.GetComponent<Collider>());
            go.name = ShapeName(shape);
            go.transform.SetParent(FxRoot.transform, false);
            // Out of the hand that holds it, when the fighter is wearing a rig. The
            // primitive body has no arm to read, so it keeps the old offset - which
            // also has to clear the wider silhouette those capsules have.
            bool rigged = caster.View != null && caster.View.HasModel;
            go.transform.position = LaunchPoint(caster, shape) + dir * (rigged ? 0.30f : 0.9f);
            Color tone = orb
                ? caster.Def.ColorLight
                : shape == ProjectileShape.Shield
                    ? AegisGold
                    : Color.Lerp(caster.Def.ColorLight, Color.white, 0.35f);
            if (prop != null)
            {
                // The prop is modelled life size, so it is not scaled up the way the
                // slab is: the shield in the air is the shield that was on the arm a
                // frame ago, and it keeps its own materials and its own colours.
                prop.transform.SetParent(go.transform, false);
                prop.transform.localPosition = Vector3.zero;
                prop.transform.localRotation = Quaternion.identity;
            }
            else
            {
                go.transform.localScale = ShapeScale(shape);
                OwnedMaterial.Assign(go, MakeUnlit(tone));
            }

            // An orb is not a ball: it crackles. The crackle has to survive the wire,
            // and a snapshot carries position, scale and colour but no rotation, so the
            // electricity is built from axis-aligned boxes and a halo - a breathing
            // glow around the core and four bolts that shoot out and snap back - and
            // every piece is captured per frame, which is what lets the guest draw the
            // same crackle without a single extra field on the wire.
            Transform glow = null;
            Transform[] bolts = null;
            if (orb)
            {
                var glowGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Object.Destroy(glowGo.GetComponent<Collider>());
                glowGo.name = "OrbGlow";
                glowGo.transform.SetParent(go.transform, false);
                glowGo.transform.localScale = Vector3.one * 1.7f;
                OwnedMaterial.Assign(glowGo, OwnedMaterial.TransparentUnlit(
                    new Color(tone.r, tone.g, tone.b, 0.32f)));
                glow = glowGo.transform;

                bolts = new Transform[4];
                for (int i = 0; i < bolts.Length; i++)
                {
                    var bolt = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Object.Destroy(bolt.GetComponent<Collider>());
                    bolt.name = "OrbBolt" + i;
                    bolt.transform.SetParent(go.transform, false);
                    bolt.transform.localScale = new Vector3(0.07f, 0.07f, 1f);
                    OwnedMaterial.Assign(bolt, MakeUnlit(Color.Lerp(tone, Color.white, 0.7f)));
                    bolts[i] = bolt.transform;
                }
            }

            // The assassin's knives drag a short red line behind them - the same
            // crimson the Shadowstep dash marks the sand with, cut to knife size
            // (see RedTrail). The orb and the aegis are not knives and get none.
            if (IsKnife(shape)) RedTrail.Attach(go);

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
                Spin = ShapeSpin(shape),
                TurnAt = ab.ProjectileRange,
                Touched = volley,
                Glow = glow,
                Bolts = bolts,
                Tone = tone,
                Wire = wire,
                Prop = prop
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

        /// <summary>
        /// The aegis is gold in the hand and gold in the air: it is the same artifact
        /// the Vanguard wears on its forearm, and CharacterView's Frost Aegis bubble
        /// carries the same colour, so the slab reads as one shield leaving the arm
        /// and coming back to it.
        /// </summary>
        private static readonly Color AegisGold = new Color(1f, 0.84f, 0.18f);

        /// <summary>
        /// Whether a throw is one of the assassin's knives. His knife and the blades
        /// of his wave are the same prop (see <see cref="PropLibrary.WireId"/>), so
        /// both drag the trail and nothing else does.
        /// </summary>
        private static bool IsKnife(ProjectileShape shape) =>
            shape == ProjectileShape.Dagger || shape == ProjectileShape.Blade;

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

        /// <summary>
        /// Where a thrown weapon leaves the fighter. The rigs carry their equipment,
        /// so the shapes are read off it: the aegis leaves the left forearm it is
        /// strapped to, and the knives and the mage's orbs leave the right fist - the
        /// hand the mage is not holding the staff in.
        /// </summary>
        private static FighterAnchor LaunchAnchor(ProjectileShape shape) => shape switch
        {
            ProjectileShape.Shield => FighterAnchor.LeftForearm,
            _ => FighterAnchor.RightHand
        };

        /// <summary>
        /// The point a throw starts from and comes home to, taken from the view every
        /// time it is asked: with a rig the aegis is thrown from, and caught by, a
        /// forearm that is still moving. Without one this is the old
        /// height-over-the-body point, so nothing moves for a fighter with no model.
        /// </summary>
        private static Vector3 LaunchPoint(FighterSim owner, ProjectileShape shape)
        {
            Vector3 body = owner.Position + Vector3.up * ShapeHeight(shape);
            return owner.View != null ? owner.View.Anchor(LaunchAnchor(shape), body) : body;
        }

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
            //
            // The base band stays exactly the hit diameter (2 * HazardRadius) - that
            // is the promise that what you watch travel is what wounds you - and that
            // diameter is now 9u rather than 12u, because the ability's own pull
            // radius came down to 4.5. Above the base the funnel is slimmer still
            // (the middle band is a quarter narrower) and about 29% shorter than the
            // column of wind it started as.
            AddFunnelBand(go.transform, bands, ab.HazardRadius * 2f, 0.62f, 0.32f,
                caster.Def.Color, 340f, 0f);
            AddFunnelBand(go.transform, bands, ab.HazardRadius * 1.05f, 1.10f, 0.95f,
                caster.Def.Color, -470f, 1.9f);
            AddFunnelBand(go.transform, bands, ab.HazardRadius * 0.6f, 1.60f, 1.85f,
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
        /// The ground warning the Earthrift lays down before it lands: the circle the
        /// caster is standing in the middle of, marked by flat discs on its rim and by
        /// two thinner rings inside it, blinking faster as the crack closes in. It is
        /// drawn around the caster and follows them, so the circle a player watches
        /// close on them is exactly the ground that opens.
        ///
        /// Discs rather than one ring mesh because snapshots carry no rotation — the
        /// guest has to rebuild the same circle from position, scale and colour alone,
        /// and a cylinder reads the same at every angle.
        /// </summary>
        public void SpawnRingWarning(FighterSim owner, float radius, Color color, float seconds)
        {
            var root = new GameObject("SpellWarning");
            root.transform.SetParent(FxRoot.transform, false);
            root.transform.position = owner.Position + Vector3.up * 0.04f;

            // Three bands - the rim that will crack, then two inside it, so the whole
            // disc the spell covers is painted rather than only its edge. Every marker
            // is the same size: the tick breathes them together, which is the blink.
            float[] bandRadius = { radius, radius * 0.60f, radius * 0.26f };
            int[] bandCount = { 14, 8, 4 };
            float marker = radius * 0.30f;
            int total = 0;
            for (int b = 0; b < bandCount.Length; b++) total += bandCount[b];
            var ring = new List<Transform>(total);
            for (int b = 0; b < bandRadius.Length; b++)
            {
                for (int i = 0; i < bandCount[b]; i++)
                {
                    // Each band is offset by a fraction of its own step, so the markers
                    // of one ring do not line up with the spokes of the next.
                    float angle = (i + b * 0.45f) / bandCount[b] * Mathf.PI * 2f;
                    var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    Object.Destroy(disc.GetComponent<Collider>());
                    disc.name = "Warn" + b + "_" + i;
                    disc.transform.SetParent(root.transform, false);
                    disc.transform.localPosition = new Vector3(
                        Mathf.Sin(angle) * bandRadius[b], 0f,
                        Mathf.Cos(angle) * bandRadius[b]);
                    disc.transform.localScale = new Vector3(marker, 0.04f, marker);
                    OwnedMaterial.Assign(disc, MakeUnlit(color));
                    ring.Add(disc.transform);
                }
            }

            warnings.Add(new WarningFx
            {
                Root = root.transform,
                Markers = ring,
                Follows = owner,
                T = 0f,
                Total = Mathf.Max(0.05f, seconds),
                Width = marker,
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
        /// The Earthrift's crack: a ring of stone plates lifted and tilted out of the
        /// ground, with a dark seam and a glowing vein running round the circle they
        /// break. The caster stands at the centre of it - the ring is their own
        /// circle, and the disc inside it is the hit. Purely visual — the damage and
        /// the launch are applied once, by the strike.
        ///
        /// The plates carry a yaw so they line up with the circle they trace, but the
        /// shape has to survive the wire as well: a snapshot carries no rotation, so
        /// each plate is kept near-square in plan and the guest, which draws every
        /// piece axis-aligned, still sees a ring rather than a scatter of slats. The
        /// band sits inside <paramref name="radius"/>, so the ground that breaks is the
        /// ground that hits.
        /// </summary>
        public void SpawnRift(Vector3 centre, float radius, float band, Color color)
        {
            var root = new GameObject("Rift");
            root.transform.SetParent(FxRoot.transform, false);
            root.transform.position = centre;

            const int pieces = 12;
            float step = Mathf.PI * 2f / pieces;
            float ring = Mathf.Max(0.5f, radius - band * 0.5f);
            float along = step * ring * 1.02f;      // tangential length: gaps just shut
            float across = band * 0.78f;            // radial thickness of one plate
            for (int i = 0; i < pieces; i++)
            {
                float angle = i * step;
                var radial = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
                Vector3 spot = radial * ring;
                // Patches of the ring stand taller than others, so it reads as a break
                // rather than a machined hoop.
                float lift = 0.5f + 0.5f * Mathf.Sin(i * 1.7f);

                var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.Destroy(slab.GetComponent<Collider>());
                slab.name = "RiftSlab" + i;
                slab.transform.SetParent(root.transform, false);
                slab.transform.localPosition = spot + new Vector3(0f, 0.05f + 0.05f * lift, 0f);
                // The tilt is the read: broken ground, not a painted circle. The yaw
                // turns the plate along its tangent, which puts its long local axis
                // round the ring and its short one across it.
                slab.transform.localRotation = Quaternion.Euler(
                    Mathf.Sin(i * 2.7f) * 14f, angle * Mathf.Rad2Deg, Mathf.Sin(i * 3.1f) * 13f);
                slab.transform.localScale = new Vector3(
                    along, 0.24f + 0.24f * lift, across * (0.92f + 0.16f * lift));
                OwnedMaterial.Assign(slab, MakeUnlit(RiftStone));

                // The hole itself: flat discs butted together make one dark circle, and
                // a disc survives the loss of its rotation on the way to a guest.
                float disc = step * ring * 1.12f;
                var seam = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Object.Destroy(seam.GetComponent<Collider>());
                seam.name = "RiftSeam" + i;
                seam.transform.SetParent(root.transform, false);
                seam.transform.localPosition = spot + new Vector3(0f, 0.03f, 0f);
                seam.transform.localScale = new Vector3(disc, 0.05f, disc);
                OwnedMaterial.Assign(seam, MakeUnlit(RiftVoid));

                // The light inside the break, on every other stretch of the ring.
                if (i % 2 != 0) continue;
                var vein = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Object.Destroy(vein.GetComponent<Collider>());
                vein.name = "RiftVein" + i;
                vein.transform.SetParent(root.transform, false);
                vein.transform.localPosition = spot + new Vector3(0f, 0.055f, 0f);
                vein.transform.localScale =
                    new Vector3(band * 0.34f, 0.03f, band * 0.34f);
                OwnedMaterial.Assign(vein, MakeUnlit(Color.Lerp(color, Color.white, 0.45f)));

                // A chip of ground thrown clear of the break, on alternating sides.
                if (i % 3 != 1) continue;
                float side = (i % 2) == 0 ? 1f : -1f;
                var shard = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.Destroy(shard.GetComponent<Collider>());
                shard.name = "RiftShard" + i;
                shard.transform.SetParent(root.transform, false);
                shard.transform.localPosition = spot + radial * (side * band * 0.62f) +
                    new Vector3(0f, 0.04f, 0f);
                shard.transform.localRotation = Quaternion.Euler(12f, i * 23f, -9f);
                shard.transform.localScale =
                    new Vector3(across * 0.30f, 0.10f + 0.06f * lift, along * 0.26f);
                OwnedMaterial.Assign(shard, MakeUnlit(Color.Lerp(RiftStone, Color.white, 0.22f)));
            }

            // Two chips close in, under the fighter in the middle: the whole disc gives
            // way, not only the rim.
            for (int i = 0; i < 3; i++)
            {
                float angle = i * 2.1f + 0.6f;
                var chip = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.Destroy(chip.GetComponent<Collider>());
                chip.name = "RiftShardInner" + i;
                chip.transform.SetParent(root.transform, false);
                chip.transform.localPosition = new Vector3(
                    Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * (ring * 0.34f) +
                    new Vector3(0f, 0.04f, 0f);
                chip.transform.localRotation = Quaternion.Euler(9f, i * 37f, -7f);
                chip.transform.localScale =
                    new Vector3(across * 0.34f, 0.08f, across * 0.42f);
                OwnedMaterial.Assign(chip, MakeUnlit(Color.Lerp(RiftStone, Color.white, 0.22f)));
            }

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
                            Vector3 home = LaunchPoint(p.Owner, p.Ab.ProjectileShape) +
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

                // The orb's crackle: the glow breathes, the core flickers between its
                // own tone and white, and the four bolts shoot out and snap back. All
                // of it through position, scale and colour, so the guest draws the same
                // thing from the snapshot it already gets.
                if (p.Bolts != null)
                {
                    p.Crackle += dt;
                    float flicker = 0.5f + 0.5f * Mathf.Sin(p.Crackle * 41f);
                    float breath = 1.55f + 0.30f * Mathf.Sin(p.Crackle * 27f);
                    if (p.Glow != null) p.Glow.localScale = new Vector3(breath, breath, breath);
                    for (int b = 0; b < p.Bolts.Length; b++)
                    {
                        Transform bolt = p.Bolts[b];
                        float phase = p.Crackle * 33f + b * 1.9f;
                        float reach = 0.75f + 0.55f * (0.5f + 0.5f * Mathf.Sin(phase));
                        bool alongZ = (b & 1) == 0;
                        float side = (b & 2) == 0 ? 1f : -1f;
                        float wobble = 0.10f * Mathf.Sin(phase * 0.7f);
                        bolt.localPosition = alongZ
                            ? new Vector3(wobble * side, 0f, side * reach * 0.45f)
                            : new Vector3(side * reach * 0.45f, 0f, wobble * side);
                        bolt.localScale = alongZ
                            ? new Vector3(0.07f, 0.07f, reach)
                            : new Vector3(reach, 0.07f, 0.07f);
                    }

                    var core = p.Go.GetComponent<OwnedMaterial>();
                    if (core != null && core.Material != null)
                    {
                        Color flash = Color.Lerp(p.Tone, Color.white, 0.25f + 0.45f * flicker);
                        if (core.Material.HasProperty(BaseColorId))
                            core.Material.SetColor(BaseColorId, flash);
                        if (core.Material.HasProperty(ColorId))
                            core.Material.SetColor(ColorId, flash);
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
                    // The Earthrift's ring rides its caster for the whole warning: the
                    // circle closes on the ground the Vanguard is standing on, and the
                    // crack opens where he took his last step.
                    if (w.Follows != null)
                        w.Root.position = w.Follows.Position + Vector3.up * 0.04f;
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
                    position = go.transform.position, scale = go.transform.lossyScale, color = color });
            }
            Color Tint(GameObject go) => go.GetComponent<Renderer>().sharedMaterial
                .GetColor(BaseColorId);
            // Blades, daggers and the shield are all slabs of a different size, and the
            // snapshot carries scale, so one primitive reproduces them all; the colour is
            // read off the object rather than taken from the caster's palette, so a shield
            // thrown in gold arrives gold.
            //
            // A throw that is wearing the real prop instead sends the prop's id and
            // the slot of the hand that threw it: a shield is not a slab at any scale,
            // and the guest has the same asset, so it draws the same object.
            foreach (var p in projectiles)
            {
                if (p.Prop != null)
                {
                    effects.Add(new EffectSnapshot
                    {
                        primitive = (int)PrimitiveType.Cube,
                        prop = p.Wire,
                        owner = Slot(p.Owner),
                        position = p.Go.transform.position,
                        scale = p.Go.transform.lossyScale,
                        color = p.Tone
                    });
                    continue;
                }
                if (p.Bolts == null)
                {
                    Add(p.Go, p.Ab.ProjectileShape == ProjectileShape.Orb
                        ? PrimitiveType.Sphere : PrimitiveType.Cube, Tint(p.Go));
                    continue;
                }
                // An orb ships its crackle with it: the core, the halo and the four
                // bolts, in the order they were built, so the guest's replica slots
                // stay matched from frame to frame.
                Add(p.Go, PrimitiveType.Sphere, Tint(p.Go));
                if (p.Glow != null) Add(p.Glow.gameObject, PrimitiveType.Sphere, Tint(p.Glow.gameObject));
                foreach (var bolt in p.Bolts)
                    if (bolt != null) Add(bolt.gameObject, PrimitiveType.Cube, Tint(bolt.gameObject));
            }
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
                {
                    bool round = part.name.StartsWith("RiftVein") ||
                        part.name.StartsWith("RiftSeam");
                    Color stone = part.name.StartsWith("RiftVein")
                            ? Color.Lerp(rift.Seam, Color.white, 0.45f)
                        : part.name.StartsWith("RiftSeam") ? RiftVoid
                        : part.name.StartsWith("RiftShard") ? Color.Lerp(RiftStone, Color.white, 0.22f)
                        : RiftStone;
                    // The seam and the vein are discs on this machine, so they are discs
                    // on the guest's too; the plates and chips stay cubes either way.
                    Add(part.gameObject,
                        round ? PrimitiveType.Cylinder : PrimitiveType.Cube, stone);
                }
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
                Reap(replicatedEffects[i]); replicatedEffects.RemoveAt(i);
            }
            for (int i = 0; i < count; i++)
            {
                var effect = effects[i];
                Replica slot = i < replicatedEffects.Count ? replicatedEffects[i] : null;
                // A slot that used to be a different effect is thrown away rather than
                // stretched into this one: a shield is not a stretched bolt.
                if (slot != null && !slot.Matches(effect))
                {
                    Reap(slot);
                    replicatedEffects[i] = null;
                    slot = null;
                }
                if (slot == null)
                {
                    slot = Build(effect);
                    if (slot == null) continue;
                    if (i < replicatedEffects.Count) replicatedEffects[i] = slot;
                    else replicatedEffects.Add(slot);
                }
                slot.Go.transform.position = effect.position;
                // A prop is modelled life size and keeps its own scale on both machines;
                // only the stand-in primitives are scaled by the snapshot.
                if (!slot.IsProp) slot.Go.transform.localScale = effect.scale;
                slot.Paint(effect.color);
            }
        }

        /// <summary>One guest effect slot, built from the snapshot that describes it.</summary>
        private Replica Build(EffectSnapshot effect)
        {
            var slot = new Replica { Primitive = effect.primitive, Prop = effect.prop,
                Owner = effect.owner };
            if (slot.IsProp)
            {
                slot.Go = PropLibrary.Fly(effect.prop, "Replica");
                // No prop asset on this machine: the throw is simply not drawn, which is
                // no worse than a guest that has never seen the fighter's art.
                if (slot.Go == null) return null;
                slot.Go.name = "Replica_prop" + effect.prop;
                slot.Go.transform.SetParent(FxRoot.transform, false);
                // The same knife on the guest's screen drags the same line: the
                // trail is dropped from the replica's own movement, so it needs
                // nothing on the wire.
                if (effect.prop == PropLibrary.WireDagger) RedTrail.Attach(slot.Go);
                return slot;
            }

            var go = GameObject.CreatePrimitive((PrimitiveType)effect.primitive);
            Object.Destroy(go.GetComponent<Collider>());
            go.name = "Replica_" + effect.primitive;
            go.transform.SetParent(FxRoot.transform, false);
            // A translucent piece on the host - an orb's halo - stays translucent on the
            // guest instead of turning into a solid ball.
            OwnedMaterial.Assign(go, effect.color.a < 0.99f
                ? OwnedMaterial.TransparentUnlit(effect.color)
                : MakeUnlit(effect.color));
            slot.Go = go;
            return slot;
        }

        private static void Reap(Replica slot)
        {
            if (slot != null && slot.Go != null) Object.Destroy(slot.Go);
        }

        /// <summary>Eases the guest's replica effects from the previous snapshot towards the newest.</summary>
        public void EaseReplicated(EffectSnapshot[] previous, EffectSnapshot[] to, float k)
        {
            if (to == null) return;
            for (int i = 0; i < replicatedEffects.Count && i < to.Length; i++)
            {
                Replica slot = replicatedEffects[i];
                if (slot == null || slot.Go == null || to[i] == null) continue;
                Vector3 position = to[i].position;
                Vector3 scale = to[i].scale;
                // Only blend when the slot still holds the same kind of effect;
                // a respawned effect simply appears at its newest transform.
                if (previous != null && i < previous.Length && previous[i] != null &&
                    SameKind(previous[i], to[i]))
                {
                    position = Vector3.Lerp(previous[i].position, position, k);
                    scale = Vector3.Lerp(previous[i].scale, scale, k);
                }
                slot.Go.transform.position = position;
                if (!slot.IsProp) slot.Go.transform.localScale = scale;
            }
        }

        /// <summary>Whether two snapshots describe the same object, prop or primitive.</summary>
        private static bool SameKind(EffectSnapshot a, EffectSnapshot b)
            => a.prop == b.prop && a.primitive == b.primitive;

        // ------------------------------------------------------- the hand's props

        /// <summary>
        /// Hands the sim's roster over, in snapshot slot order. The effects a fighter
        /// throws are attributed to its slot, so both machines empty the same hand.
        /// </summary>
        public void Track(IList<FighterSim> fighters) => roster = fighters;

        /// <summary>
        /// Empties the hand of every fighter whose weapon is in the air, and fills it
        /// when the last copy of that weapon is spent - so a shield that leaves the arm
        /// to be thrown comes back to it, and one lost against a wall does not leave the
        /// fighter holding a phantom. The host reads the throwing copies, the guest the
        /// replicas the host sent.
        /// </summary>
        public void SyncProps()
        {
            if (roster == null) return;
            for (int i = 0; i < roster.Count; i++)
            {
                FighterSim fighter = roster[i];
                if (fighter == null || fighter.View == null) continue;
                fighter.View.SetPropHidden(ProjectileShape.Shield,
                    Away(i, PropLibrary.WireShield, fighter));
                fighter.View.SetPropHidden(ProjectileShape.Dagger,
                    Away(i, PropLibrary.WireDagger, fighter));
            }
        }

        /// <summary>True when a copy of `wireId` is in the air from `thrower`'s slot.</summary>
        private bool Away(int slot, int wireId, FighterSim thrower)
        {
            for (int i = 0; i < projectiles.Count; i++)
                if (projectiles[i].Prop != null && projectiles[i].Wire == wireId &&
                    projectiles[i].Owner == thrower) return true;
            for (int i = 0; i < replicatedEffects.Count; i++)
            {
                Replica slotFx = replicatedEffects[i];
                if (slotFx != null && slotFx.Prop == wireId && slotFx.Owner == slot) return true;
            }
            return false;
        }

        /// <summary>
        /// Which fighter an effect belongs to, as a snapshot slot: 0 is the local player
        /// and 1 the fighter the LAN path mirrors. A free-for-all's extra bots are not
        /// mirrored at all, so they share the last slot.
        /// </summary>
        private int Slot(FighterSim fighter)
        {
            if (roster == null) return 0;
            for (int i = 0; i < roster.Count; i++)
                if (roster[i] == fighter) return Mathf.Min(i, 1);
            return 0;
        }

        /// <summary>Destroys every transient effect this spawner created.</summary>
        public void Clear()
        {
            foreach (var p in projectiles) if (p.Go != null) Object.Destroy(p.Go);
            foreach (var v in vortices) if (v.Go != null) Object.Destroy(v.Go);
            foreach (var r in rings) if (r.Go != null) Object.Destroy(r.Go);
            foreach (var r in rifts) if (r.Root != null) Object.Destroy(r.Root.gameObject);
            foreach (var w in warnings) if (w.Root != null) Object.Destroy(w.Root.gameObject);
            foreach (var slot in replicatedEffects) Reap(slot);
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
            /// <summary>The orb's glow shell and its four crackle bolts; null for a blade.</summary>
            public Transform Glow;
            public Transform[] Bolts;
            /// <summary>The colour the orb was thrown in, before the flicker mixes it.</summary>
            public Color Tone;
            public float Crackle;
            /// <summary>The prop this throw is wearing, and the object drawn in its place (see PropLibrary).</summary>
            public int Wire;
            public GameObject Prop;
        }

        /// <summary>
        /// One rebuilt guest effect. The slot remembers what it is made of - which
        /// primitive, or which prop - so a slot that changes kind is rebuilt instead of
        /// stretched, and so a prop in the air can be traced back to the hand that threw
        /// it.
        /// </summary>
        private sealed class Replica
        {
            public GameObject Go;
            public int Primitive;
            public int Prop;
            /// <summary>The slot of the fighter that threw it; unused for a primitive.</summary>
            public int Owner = -1;
            public bool IsProp => Prop != 0;

            public bool Matches(EffectSnapshot effect)
                => Prop == effect.prop && (IsProp || Primitive == effect.primitive);

            /// <summary>Repaint a stand-in primitive; a prop keeps the colours it was built with.</summary>
            public void Paint(Color color)
            {
                if (IsProp || Go == null) return;
                var renderer = Go.GetComponent<Renderer>();
                if (renderer == null || renderer.sharedMaterial == null) return;
                if (renderer.sharedMaterial.HasProperty(BaseColorId))
                    renderer.sharedMaterial.SetColor(BaseColorId, color);
            }
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
            /// <summary>The funnel's bands, for a gathering funnel; empty for a ring.</summary>
            public List<FunnelBand> Bands;
            /// <summary>Set when the warning rides its caster: the gathering vortex
            /// funnel and the Earthrift's ring both do.</summary>
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
