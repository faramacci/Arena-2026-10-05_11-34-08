using System.Collections.Generic;
using UnityEngine;
using Arena.Core;

namespace Arena.Presentation
{
    /// <summary>
    /// Builds the battleground out of primitives at runtime, themed as an
    /// arcane gladiator colosseum: Roman sand and travertine masonry, a glowing
    /// runic light-band and hanging candles (wizarding), and a rune circle,
    /// obelisks and a warded barrier ring on a tiered amphitheatre (Middle-earth).
    ///
    /// Everything is generated from <see cref="GameConfig"/>, so the sand floor,
    /// the perimeter wall and both zone shapes stay in lock-step with the
    /// simulation's own bounds. The play area is never obstructed: every prop
    /// sits outside the ±14.6 world clamp the fighters are held to, and each
    /// zone shape gets a lighter "safe sand" slab inside its boundary so the
    /// danger read is geometric rather than text.
    ///
    /// Geometry lives under a child "Geometry" node that is rebuilt on demand,
    /// so a rebuild can never delete the fighters (which are parented to this
    /// transform by <see cref="CharacterView.Create"/>).
    /// </summary>
    public class ArenaBuilder : MonoBehaviour
    {
        [Header("Arena palette")]
        [SerializeField] private Color zoneColor = new Color(0.50f, 0.86f, 1f);
        [SerializeField] private Color sandColor = new Color(0.78f, 0.69f, 0.49f);
        [SerializeField] private Color safeSandColor = new Color(0.90f, 0.83f, 0.63f);
        [SerializeField] private Color stoneColor = new Color(0.56f, 0.54f, 0.50f);
        [SerializeField] private Color darkStoneColor = new Color(0.21f, 0.20f, 0.23f);
        [SerializeField] private Color marbleColor = new Color(0.88f, 0.86f, 0.82f);
        [SerializeField] private Color bronzeColor = new Color(0.74f, 0.56f, 0.26f);
        [SerializeField] private Color runeColor = new Color(0.45f, 0.86f, 1f);
        [SerializeField] private Color arcaneColor = new Color(0.66f, 0.44f, 1f);
        [SerializeField] private Color fireColor = new Color(1f, 0.62f, 0.24f);
        [SerializeField] private Color candleColor = new Color(0.97f, 0.94f, 0.82f);

        [Header("Zone")]
        [SerializeField] private ZoneShape shape = ZoneShape.Circle;

        private Transform geometry;
        private ArenaAmbient ambient;
        private readonly List<Transform> bobbers = new List<Transform>();
        private readonly List<Transform> flames = new List<Transform>();
        private readonly List<Transform> spinners = new List<Transform>();

        // --- palette, built once per Build so every primitive of a look shares
        // one material and the SRP batcher can fold the colosseum into a few
        // batches instead of one draw call per object.
        private Material mSand, mSafe, mStone, mDark, mMarble, mBronze;
        private Material mRune, mArcane, mFire, mCandle, mZone, mWard;

        private float HalfWall => GameConfig.ArenaWidth * 0.5f; // 16

        public ZoneShape Shape => shape;

        private void Awake() => Build();

        public void SetShape(ZoneShape s)
        {
            shape = s;
            Build();
        }

        [ContextMenu("Rebuild Arena")]
        public void Build()
        {
            // In play mode Object.Destroy is deferred, so the previous geometry
            // is still alive this frame. Building into a fresh node keeps the two
            // sets from competing for the same names.
            if (geometry != null) DestroySafe(geometry.gameObject);

            var root = new GameObject("Geometry");
            root.transform.SetParent(transform, false);
            geometry = root.transform;

            bobbers.Clear();
            flames.Clear();
            spinners.Clear();
            MakePalette();

            BuildBase();
            BuildSandFloor();
            BuildSafeSand();
            BuildRuneCircle();
            BuildPerimeterWall();
            BuildColosseum();
            BuildZone();
            BuildCentreSigil();
            BuildObelisks();
            BuildBraziers();
            BuildFloatingCandles();
            BuildWard();
            BuildLights();

            // One ambient driver for the whole scene instead of a component per prop.
            if (ambient == null) ambient = GetComponent<ArenaAmbient>();
            if (ambient == null) ambient = gameObject.AddComponent<ArenaAmbient>();
            ambient.Configure(bobbers, flames, spinners);
        }

        private void MakePalette()
        {
            mSand = MakeLit(sandColor);
            mSafe = MakeLit(safeSandColor);
            mStone = MakeLit(stoneColor);
            mDark = MakeLit(darkStoneColor);
            mMarble = MakeLit(marbleColor);
            mBronze = MakeLit(bronzeColor);
            mRune = MakeUnlit(runeColor);
            mArcane = MakeUnlit(arcaneColor);
            mFire = MakeUnlit(fireColor);
            mCandle = MakeUnlit(candleColor);
            mZone = MakeUnlit(zoneColor);
            mWard = MakeTransparentUnlit(new Color(0.44f, 0.64f, 1f, 0.13f));
        }

        // ================================================================ layers

        /// <summary>Stepped stone plinth the whole colosseum sits on.</summary>
        private void BuildBase()
        {
            Prim(PrimitiveType.Cylinder, "Plinth_Outer", geometry,
                new Vector3(0f, -1.15f, 0f), new Vector3(62f, 0.25f, 62f), mDark);
            Prim(PrimitiveType.Cylinder, "Plinth_Inner", geometry,
                new Vector3(0f, -0.53f, 0f), new Vector3(54f, 0.45f, 54f), mStone);
        }

        /// <summary>Square sand arena, top face flush with the sim plane (y = 0).</summary>
        private void BuildSandFloor()
        {
            Prim(PrimitiveType.Cube, "Sand", geometry,
                new Vector3(0f, -0.06f, 0f),
                new Vector3(GameConfig.ArenaWidth + 0.4f, 0.12f, GameConfig.ArenaHeight + 0.4f),
                mSand);
        }

        /// <summary>
        /// Lighter slab inside the zone boundary: safe ground reads as a lit
        /// stage while the darker sand outside becomes the damage ring.
        /// </summary>
        private void BuildSafeSand()
        {
            if (shape == ZoneShape.Circle)
            {
                float d = GameConfig.ZoneCircleRadius * 2f - 0.2f;
                Prim(PrimitiveType.Cylinder, "SafeSand", geometry,
                    new Vector3(0f, -0.01f, 0f), new Vector3(d, 0.04f, d), mSafe);
            }
            else
            {
                Prim(PrimitiveType.Cube, "SafeSand", geometry,
                    new Vector3(0f, -0.01f, 0f),
                    new Vector3(GameConfig.ZoneHalfWidth * 2f - 0.2f, 0.04f,
                                GameConfig.ZoneHalfHeight * 2f - 0.2f),
                    mSafe);
            }
        }

        /// <summary>Inlaid rune circle between the zone edge and the wall.</summary>
        private void BuildRuneCircle()
        {
            const int count = 28;
            Ring("RuneOuter", 13.4f, count, 0.05f, 0.01f, 0.26f, mRune, 0.62f);
            Ring("RuneInner", 12.6f, count, 0.05f, 0.01f, 0.18f, mArcane, 0.74f);
        }

        /// <summary>
        /// The frame around the play square: a base course, an upper course, a
        /// glowing runic band, a colonnade and corner towers with crystals.
        /// </summary>
        private void BuildPerimeterWall()
        {
            float half = HalfWall;
            float thickness = 0.9f;
            float span = half * 2f + thickness;

            // Two courses plus a light band, repeated for all four sides.
            WallRun("E", new Vector3(half, 0f, 0f), thickness, span, false);
            WallRun("W", new Vector3(-half, 0f, 0f), thickness, span, false);
            WallRun("N", new Vector3(0f, 0f, half), thickness, span, true);
            WallRun("S", new Vector3(0f, 0f, -half), thickness, span, true);

            // Colonnade: two half-columns per side break up the flat wall run.
            float c = half + 0.15f;
            foreach (float t in new[] { -half * 0.42f, half * 0.42f })
            {
                Prim(PrimitiveType.Cylinder, "Col_E" + t, geometry, new Vector3(c, 1.25f, t), new Vector3(0.66f, 1.25f, 0.66f), mMarble);
                Prim(PrimitiveType.Cylinder, "Col_W" + t, geometry, new Vector3(-c, 1.25f, t), new Vector3(0.66f, 1.25f, 0.66f), mMarble);
                Prim(PrimitiveType.Cylinder, "Col_N" + t, geometry, new Vector3(t, 1.25f, c), new Vector3(0.66f, 1.25f, 0.66f), mMarble);
                Prim(PrimitiveType.Cylinder, "Col_S" + t, geometry, new Vector3(t, 1.25f, -c), new Vector3(0.66f, 1.25f, 0.66f), mMarble);
            }

            // Corner towers, pushed clear of the fighter clamp (14.6).
            foreach (int sx in new[] { -1, 1 })
            {
                foreach (int sz in new[] { -1, 1 })
                {
                    float x = sx * 16.6f, z = sz * 16.6f;
                    Prim(PrimitiveType.Cube, $"Tower_{sx}_{sz}", geometry,
                        new Vector3(x, 1.9f, z), new Vector3(2.0f, 3.8f, 2.0f), mStone);
                    Prim(PrimitiveType.Cube, $"TowerCap_{sx}_{sz}", geometry,
                        new Vector3(x, 3.95f, z), new Vector3(2.4f, 0.35f, 2.4f), mBronze);
                    var crystal = Prim(PrimitiveType.Sphere, $"Crystal_{sx}_{sz}", geometry,
                        new Vector3(x, 4.9f, z), new Vector3(0.85f, 1.5f, 0.85f), mArcane);
                    bobbers.Add(crystal.transform);
                }
            }
        }

        /// <summary>One side of the perimeter: lower course, upper course, rune band.</summary>
        private void WallRun(string side, Vector3 anchor, float thickness, float span, bool alongZ)
        {
            Vector3 Pos(float y) => new Vector3(anchor.x, y, anchor.z);
            Vector3 Size(float h, float pad = 0f) => alongZ
                ? new Vector3(span + pad, h, thickness + pad)
                : new Vector3(thickness + pad, h, span + pad);

            Prim(PrimitiveType.Cube, "WallLo_" + side, geometry, Pos(0.55f), Size(1.1f), mStone);
            Prim(PrimitiveType.Cube, "WallHi_" + side, geometry, Pos(1.60f), Size(1.0f), mStone);
            // The rune band overhangs slightly so it reads as a lit cornice.
            Prim(PrimitiveType.Cube, "Band_" + side, geometry, Pos(2.28f), Size(0.16f, 0.12f), mZone);
        }

        /// <summary>
        /// Tiered amphitheatre seating rising outward, with a lit parapet. The
        /// whole bowl is placed from <see cref="GameConfig.SpectatorInnerRadius"/>,
        /// which is also the radius the simulation keeps fighters inside: the
        /// first row can never end up standing on the sand, and the sand can
        /// never end up under a spectator.
        /// </summary>
        private void BuildColosseum()
        {
            const float bowl = GameConfig.SpectatorInnerRadius;
            const float step = 2.4f;      // rise between tiers

            Ring("TierA", bowl + step * 0.35f, 20, 1.1f, 0f, 1.6f, mStone, 0.06f);
            Ring("TierB", bowl + step * 1.35f, 20, 1.9f, 0f, 1.6f, mStone, 0.06f);
            Ring("TierC", bowl + step * 2.35f, 20, 2.9f, 0f, 1.8f, mDark, 0.06f);

            // Runes along the first tier's inner rim, facing the arena.
            Ring("TierARune", bowl, 14, 0.18f, 1.12f, 0.5f, mArcane, 0.40f);

            // Parapet with an enchanted rim band.
            Ring("Parapet", bowl + step * 3.35f, 20, 1.4f, 3.0f, 1.2f, mStone, 0.05f);
            Ring("ParapetRune", bowl + step * 3.65f, 20, 0.22f, 4.34f, 0.32f, mRune, 0.18f);
        }

        /// <summary>Glowing boundary for the current zone shape.</summary>
        private void BuildZone()
        {
            if (shape == ZoneShape.Circle)
            {
                Ring("ZoneRing", GameConfig.ZoneCircleRadius, 56, 0.10f, 0.02f, 0.30f, mZone, 0.12f);
            }
            else
            {
                float hw = GameConfig.ZoneHalfWidth;
                float hh = GameConfig.ZoneHalfHeight;
                float t = 0.32f;
                Prim(PrimitiveType.Cube, "Zone_N", geometry, new Vector3(0f, 0.07f, hh), new Vector3(hw * 2f, 0.10f, t), mZone);
                Prim(PrimitiveType.Cube, "Zone_S", geometry, new Vector3(0f, 0.07f, -hh), new Vector3(hw * 2f, 0.10f, t), mZone);
                Prim(PrimitiveType.Cube, "Zone_E", geometry, new Vector3(hw, 0.07f, 0f), new Vector3(t, 0.10f, hh * 2f), mZone);
                Prim(PrimitiveType.Cube, "Zone_W", geometry, new Vector3(-hw, 0.07f, 0f), new Vector3(t, 0.10f, hh * 2f), mZone);
            }
        }

        /// <summary>Arcane sigil at the arena centre: two rings and a four-point star.</summary>
        private void BuildCentreSigil()
        {
            var spinner = new GameObject("Sigil");
            spinner.transform.SetParent(geometry, false);
            var root = spinner.transform;
            spinners.Add(root);

            Ring(root, "SigilOuter", 3.1f, 24, 0.05f, 0.015f, 0.22f, mRune, 0.18f);
            Ring(root, "SigilInner", 2.2f, 18, 0.05f, 0.015f, 0.16f, mArcane, 0.20f);

            // Arms point radially outward (local +Z aligned with the radius) so
            // the four of them read as a four-point star, not a broken ring.
            for (int i = 0; i < 4; i++)
            {
                float a = i / 4f * Mathf.PI * 2f;
                Prim(PrimitiveType.Cube, "SigilSpoke" + i, root,
                    new Vector3(Mathf.Cos(a) * 1.5f, 0.02f, Mathf.Sin(a) * 1.5f),
                    new Vector3(0.16f, 0.05f, 2.9f), mArcane, 90f - a * Mathf.Rad2Deg);
            }

            var core = Prim(PrimitiveType.Sphere, "SigilCore", root,
                new Vector3(0f, 0.35f, 0f), new Vector3(0.7f, 0.7f, 0.7f), mRune);
            bobbers.Add(core.transform);
        }

        /// <summary>Four obelisks just outside the wall, clear of the play area.</summary>
        private void BuildObelisks()
        {
            const float d = 17.9f;
            foreach (int sx in new[] { -1, 1 })
            {
                foreach (int sz in new[] { -1, 1 })
                {
                    float x = sx * d, z = sz * d;
                    Prim(PrimitiveType.Cube, $"ObeliskBase_{sx}_{sz}", geometry,
                        new Vector3(x, 0.45f, z), new Vector3(1.7f, 0.9f, 1.7f), mStone);
                    Prim(PrimitiveType.Cube, $"Obelisk_{sx}_{sz}", geometry,
                        new Vector3(x, 3.4f, z), new Vector3(0.95f, 5.0f, 0.95f), mMarble, 45f);
                    Prim(PrimitiveType.Cube, $"ObeliskCap_{sx}_{sz}", geometry,
                        new Vector3(x, 6.1f, z), new Vector3(1.1f, 0.45f, 1.1f), mBronze, 45f);
                    var gem = Prim(PrimitiveType.Sphere, $"ObeliskGem_{sx}_{sz}", geometry,
                        new Vector3(x, 6.75f, z), new Vector3(0.7f, 0.9f, 0.7f), mRune);
                    bobbers.Add(gem.transform);
                }
            }
        }

        /// <summary>Flame bowls on the arena's flanks, just outside the wall.</summary>
        private void BuildBraziers()
        {
            var spots = new[]
            {
                new Vector3(0f, 0f, 17.2f), new Vector3(0f, 0f, -17.2f),
                new Vector3(17.2f, 0f, 0f), new Vector3(-17.2f, 0f, 0f)
            };

            for (int i = 0; i < spots.Length; i++)
            {
                var p = spots[i];
                Prim(PrimitiveType.Cylinder, "BrazierStand" + i, geometry,
                    new Vector3(p.x, 1.0f, p.z), new Vector3(0.5f, 1.0f, 0.5f), mDark);
                Prim(PrimitiveType.Cylinder, "BrazierBowl" + i, geometry,
                    new Vector3(p.x, 2.15f, p.z), new Vector3(1.5f, 0.35f, 1.5f), mBronze);
                var flame = Prim(PrimitiveType.Sphere, "BrazierFlame" + i, geometry,
                    new Vector3(p.x, 2.85f, p.z), new Vector3(1.0f, 1.5f, 1.0f), mFire);
                flames.Add(flame.transform);
            }
        }

        /// <summary>Hogwarts-style candles hovering above the tiered seating.</summary>
        private void BuildFloatingCandles()
        {
            const int count = 18;
            for (int i = 0; i < count; i++)
            {
                float a = i / (float)count * Mathf.PI * 2f;
                float radius = 19.5f + Mathf.Repeat(i * 1.7f, 4.5f);
                float height = 3.4f + Mathf.Repeat(i * 2.3f, 3.2f);
                float x = Mathf.Cos(a) * radius, z = Mathf.Sin(a) * radius;

                var candle = Prim(PrimitiveType.Cube, "Candle" + i, geometry,
                    new Vector3(x, height, z), new Vector3(0.22f, 0.75f, 0.22f), mCandle);
                bobbers.Add(candle.transform);

                var halo = Prim(PrimitiveType.Sphere, "CandleHalo" + i, geometry,
                    new Vector3(x, height + 0.55f, z), new Vector3(0.34f, 0.42f, 0.34f), mFire);
                bobbers.Add(halo.transform);
            }
        }

        /// <summary>A translucent warded barrier standing on the parapet.</summary>
        private void BuildWard()
        {
            Ring("Ward", 26.9f, 26, 7.2f, 4.45f, 0.22f, mWard, 0.30f);
            Ring("WardRim", 26.9f, 26, 0.22f, 11.65f, 0.46f, mArcane, 0.12f);
        }

        private void BuildLights()
        {
            AddPointLight("Light_Fire", new Vector3(0f, 4.2f, -17.2f), fireColor, 3.2f, 22f);
            AddPointLight("Light_Arcane", new Vector3(0f, 5.0f, 17.2f), arcaneColor, 2.6f, 24f);
        }

        private void AddPointLight(string name, Vector3 pos, Color color, float intensity, float range)
        {
            var go = new GameObject(name, typeof(Light));
            go.transform.SetParent(geometry, false);
            go.transform.localPosition = pos;
            var light = go.GetComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.shadows = LightShadows.None;
        }

        // ================================================================ helpers

        /// <summary>Ring of tangential slabs: cheap way to build a cylinder wall.</summary>
        private void Ring(string name, float radius, int segments, float height,
            float bottom, float thickness, Material material, float gap) =>
            Ring(geometry, name, radius, segments, height, bottom, thickness, material, gap);

        private void Ring(Transform parent, string name, float radius, int segments, float height,
            float bottom, float thickness, Material material, float gap)
        {
            for (int i = 0; i < segments; i++)
            {
                float a = (i + 0.5f) / segments * Mathf.PI * 2f;
                float len = Mathf.PI * 2f * radius / segments * (1f - gap);
                Prim(PrimitiveType.Cube, name + i, parent,
                    new Vector3(Mathf.Cos(a) * radius, bottom + height * 0.5f, Mathf.Sin(a) * radius),
                    new Vector3(thickness, height, len), material, -a * Mathf.Rad2Deg);
            }
        }

        private static GameObject Prim(PrimitiveType type, string name, Transform parent,
            Vector3 localPos, Vector3 localScale, Material material, float yRotation = 0f)
        {
            var go = GameObject.CreatePrimitive(type);
            var collider = go.GetComponent<Collider>();
            if (collider != null) DestroySafe(collider);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, yRotation, 0f);
            go.transform.localScale = localScale;
            if (material != null) OwnedMaterial.Assign(go, material);
            return go;
        }

        // --- materials ------------------------------------------------------
        // The shared Resources assets are only ever used as templates; a fresh
        // instance is handed to OwnedMaterial, so the assets survive every rebuild.

        private static Material litTemplate;
        private static Material unlitTemplate;

        private static Material MakeLit(Color c)
        {
            if (litTemplate == null) litTemplate = Resources.Load<Material>("Arena/ArenaLit");
            Material m = litTemplate != null
                ? new Material(litTemplate) { name = "Arena_Stone" }
                : Fallback("Universal Render Pipeline/Lit");
            ApplyColor(m, c);
            return m;
        }

        private static Material MakeUnlit(Color c)
        {
            if (unlitTemplate == null) unlitTemplate = Resources.Load<Material>("Arena/ArenaUnlit");
            Material m = unlitTemplate != null
                ? new Material(unlitTemplate) { name = "Arena_Glow" }
                : Fallback("Universal Render Pipeline/Unlit");
            ApplyColor(m, c);
            return m;
        }

        private static Material MakeTransparentUnlit(Color c)
        {
            Material m = MakeUnlit(c);
            if (m == null) return null;
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);   // Transparent
            if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);       // Alpha blend
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHATEST_ON");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            m.name = "Arena_Ward";
            return m;
        }

        private static Material Fallback(string shaderName)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null) shader = Shader.Find("Unlit/Color");
            return shader != null ? new Material(shader) : null;
        }

        private static void ApplyColor(Material m, Color c)
        {
            if (m == null) return;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        private static void DestroySafe(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
