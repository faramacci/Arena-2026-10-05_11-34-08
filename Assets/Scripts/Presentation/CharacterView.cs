using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Arena.Core;

namespace Arena.Presentation
{
    /// <summary>
    /// Procedural 3D fighter built from primitives: a two-tone body (trunk,
    /// head orb, swinging arms and legs, and a silhouette of extras that differs
    /// per character), a forward-facing crest that doubles as the facing arrow,
    /// a hit-flash shell, and a floating nameplate above the head that carries
    /// the character's English name, a rounded health bar and a live percentage.
    ///
    /// The body animates itself: a stride cycle driven by how fast the view is
    /// actually travelling, an idle breath at rest, a hit recoil, and a cast
    /// pose raised by <see cref="PlayCast"/>. Distance travelled is read back off
    /// the transform, so a guest view interpolating between host snapshots walks
    /// exactly like the host's.
    ///
    /// The nameplate is a world-space Canvas (uGUI), not mesh text: the menu
    /// and HUD already prove uGUI renders correctly here, so the same rounded
    /// sprite + TMP stack is reused in 3D. The whole widget billboards toward
    /// the camera every LateUpdate so it never goes edge-on as the unit turns.
    /// </summary>
    public class CharacterView : MonoBehaviour
    {
        [Header("Refs (auto-filled by factory)")]
        [SerializeField] private CharacterDef def;
        [SerializeField] private Transform bodyRoot;

        [Header("Feel")]
        [SerializeField] private float bobAmplitude = 0.10f;
        [SerializeField] private float bobSpeed = 3.4f;
        [SerializeField] private float turnSpeed = 14f;

        // --- head nameplate, in canvas units (100 units == 1 world unit) ---
        private const float UnitsPerWorldUnit = 100f;
        private const float HeadWidgetY = 2.65f;
        private const float CanvasWidth = 360f;
        private const float CanvasHeight = 132f;
        private const float PillWidth = 340f;
        private const float BarWidth = 340f;
        private const float NameFontSize = 46f;
        private const float PercentFontSize = 46f;

        private Renderer[] renderers;
        private MaterialPropertyBlock block;
        private Transform head;
        private Transform crest;

        // --- animated body parts, all under BodyRoot ---
        private Transform torso;
        private Transform legL, legR, armL, armR;
        private Vector3 torsoScaleHome;
        private float gait;
        private float stridePhase;
        private float castTimer;
        private Vector3 lastBodyPos;
        private bool hasLastBodyPos;
        private const float CastPoseSeconds = 0.32f;
        private const float WalkSpeedRef = 8f;      // u/s that reads as a full stride

        // --- nameplate widgets ---
        private Transform hpRoot;
        private Image hpFill;
        private Image hpGhost;
        private TextMeshProUGUI hpName;
        private TextMeshProUGUI hpPercent;
        private Camera cachedCamera;

        private float bobSeed;
        private float baseY;
        private Vector3 lastMoveDir = Vector3.forward;
        private float hitFlash;
        private const float HitFlashSeconds = 0.22f;

        // --- Frost Aegis bubble, built on first use ---
        private Transform shield;

        // --- Shadowstep aura, built on first use ---
        private Transform hasteAura;
        private float hasteLeft;
        private float hasteMax;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        public CharacterDef Definition => def;

        public void Configure(CharacterDef character, Vector3 spawnPosition)
        {
            def = character;
            transform.position = spawnPosition;
            if (bodyRoot == null) bodyRoot = transform;
            SetName(character.DisplayName);
            ApplyPalette();
            SetHealth(GameConfig.MaxHealth, GameConfig.MaxHealth);
        }

        private void Awake()
        {
            block = new MaterialPropertyBlock();
            bobSeed = Random.value * 100f;
            CacheRenderers();
        }

        private void CacheRenderers()
        {
            renderers = GetComponentsInChildren<Renderer>(true);
        }

        private void OnValidate()
        {
            // OnValidate can run on Unity's loading thread, before Awake.
            // Defer all renderer/native API work to the main-thread Update.
            paletteDirty = true;
        }

        private bool paletteDirty;

        private void ApplyPalette()
        {
            if (renderers == null || renderers.Length == 0) CacheRenderers();
            if (renderers == null) return;

            foreach (var r in renderers)
            {
                if (r == null) continue;
                string n = r.gameObject.name;

                if (n.StartsWith("Head"))
                    SetColor(r, def.ColorLight);
                else if (n.StartsWith("Crest"))
                    SetColor(r, def.ColorLight);
                else if (n.StartsWith("Base"))
                    SetColor(r, def.Color * 0.55f);
                else if (n.StartsWith("Shield"))
                    continue;   // the bubble keeps its own translucent material
                else if (n.StartsWith("Badge"))
                    SetColor(r, Color.white);
                else if (n.StartsWith("Base"))
                    SetColor(r, def.Color * 0.55f);
                else
                    SetColor(r, def.Color);
            }
        }

        private void SetColor(Renderer r, Color c)
        {
            if (block == null) block = new MaterialPropertyBlock();
            r.GetPropertyBlock(block);
            block.SetColor(BaseColorId, c);
            block.SetColor(EmissionId, c * 0.35f);
            r.SetPropertyBlock(block);
        }

        /// <summary>Called by gameplay when this fighter takes damage.</summary>
        public void PlayHitFlash()
        {
            hitFlash = HitFlashSeconds;
        }

        /// <summary>
        /// Called by gameplay when this fighter actually casts, so the arms
        /// punch forward and the trunk leans into the spell for a beat.
        /// </summary>
        public void PlayCast()
        {
            castTimer = CastPoseSeconds;
        }

        /// <summary>
        /// Drives the floating nameplate: bar fill, bar colour and the percent
        /// readout. Called by gameplay whenever health changes (and at spawn).
        /// </summary>
        public void SetHealth(float current, float max)
        {
            if (max <= 0f) max = GameConfig.MaxHealth;
            float pct = Mathf.Clamp01(current / max);

            if (hpFill != null)
            {
                hpFill.fillAmount = pct;
                hpFill.color = HpColor(pct);
            }

            if (hpPercent != null)
                hpPercent.text = Mathf.CeilToInt(pct * 100f) + "%";
        }

        /// <summary>English display name shown above the bar.</summary>
        public void SetName(string displayName)
        {
            if (hpName != null) hpName.text = displayName;
        }

        /// <summary>Same green → amber → red ramp as the prototype.</summary>
        private static Color HpColor(float pct)
        {
            if (pct > 0.55f) return UiFactory.Good;
            if (pct > 0.28f) return UiFactory.Warn;
            return UiFactory.Bad;
        }

        /// <summary>Called by gameplay to face a world-space direction.</summary>
        public void SetFacing(Vector3 worldDir)
        {
            worldDir.y = 0f;
            if (worldDir.sqrMagnitude < 0.0001f) return;
            lastMoveDir = worldDir.normalized;
        }

        /// <summary>
        /// Shows the Frost Aegis bubble around the body and sizes it with the
        /// shield that is left. Built lazily and hidden at zero, so a fighter
        /// that never shields pays nothing for it.
        /// </summary>
        public void SetShield(float remaining, float max)
        {
            if (remaining <= 0f || max <= 0f || def == null)
            {
                if (shield != null) shield.gameObject.SetActive(false);
                return;
            }

            if (shield == null) shield = BuildShield();
            shield.gameObject.SetActive(true);
            float scale = Mathf.Lerp(1.02f, 1.55f, Mathf.Clamp01(remaining / max));
            shield.localScale = new Vector3(scale, scale * 1.25f, scale);
        }

        /// <summary>
        /// Called by gameplay while this fighter carries a grip/speed buff. The
        /// aura under the feet is spun up by <see cref="AnimateHaste"/> from these
        /// two numbers, so a guest driving its view from snapshots shows the same
        /// ring the host does.
        /// </summary>
        public void SetHaste(float remaining, float max)
        {
            hasteLeft = Mathf.Max(0f, remaining);
            hasteMax = Mathf.Max(0.01f, max);
        }

        /// <summary>
        /// The buff's read: a ring of dashes orbiting the feet that tightens as
        /// the timer runs out. Built lazily, so a fighter who never buffs pays
        /// nothing for it.
        /// </summary>
        private void AnimateHaste()
        {
            if (hasteLeft <= 0f)
            {
                if (hasteAura != null) hasteAura.gameObject.SetActive(false);
                return;
            }

            if (hasteAura == null) hasteAura = BuildHasteAura();
            if (!hasteAura.gameObject.activeSelf) hasteAura.gameObject.SetActive(true);

            float left = Mathf.Clamp01(hasteLeft / hasteMax);
            float pulse = 0.9f + 0.1f * Mathf.Sin(Time.time * 24f);
            float size = (0.8f + 0.4f * left) * pulse;
            hasteAura.localScale = new Vector3(size, 1f, size);
            hasteAura.Rotate(0f, 260f * Time.deltaTime, 0f, Space.Self);
        }

        private Transform BuildHasteAura()
        {
            var root = new GameObject("HasteAura");
            root.transform.SetParent(transform, false);
            root.transform.localPosition = new Vector3(0f, 0.03f, 0f);

            Color tint = def.ColorLight;
            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            DestroyComponent(disc.GetComponent<Collider>());
            disc.name = "HasteDisc";
            disc.transform.SetParent(root.transform, false);
            disc.transform.localScale = new Vector3(2.0f, 0.02f, 2.0f);
            OwnedMaterial.Assign(disc, OwnedMaterial.TransparentUnlit(
                new Color(tint.r, tint.g, tint.b, 0.22f)));

            // A cylinder is rotationally symmetric, so turning the disc shows
            // nothing from overhead. Three dashes that orbit it do.
            for (int i = 0; i < 3; i++)
            {
                float angle = i * (Mathf.PI * 2f / 3f);
                var dash = GameObject.CreatePrimitive(PrimitiveType.Cube);
                DestroyComponent(dash.GetComponent<Collider>());
                dash.name = "HasteDash";
                dash.transform.SetParent(root.transform, false);
                dash.transform.localPosition = new Vector3(
                    Mathf.Sin(angle) * 0.80f, 0.04f, Mathf.Cos(angle) * 0.80f);
                dash.transform.localRotation = Quaternion.Euler(0f, angle * Mathf.Rad2Deg, 0f);
                dash.transform.localScale = new Vector3(0.15f, 0.05f, 0.66f);
                OwnedMaterial.Assign(dash, OwnedMaterial.TransparentUnlit(
                    new Color(tint.r, tint.g, tint.b, 0.55f)));
            }
            return root.transform;
        }

        private Transform BuildShield()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            DestroyComponent(go.GetComponent<Collider>());
            go.name = "ShieldBubble";
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 1.15f, 0f);
            Color tint = def.ColorLight;
            OwnedMaterial.Assign(go, OwnedMaterial.TransparentUnlit(
                new Color(tint.r, tint.g, tint.b, 0.30f)));
            return go.transform;
        }

        public void SetVisible(bool visible)
        {
            foreach (var r in renderers)
                if (r != null) r.enabled = visible;
            if (hpRoot != null) hpRoot.gameObject.SetActive(visible);
        }

        private void Update()
        {
            if (def == null) return;
            if (block == null) block = new MaterialPropertyBlock();
            if (paletteDirty)
            {
                CacheRenderers();
                ApplyPalette();
                paletteDirty = false;
            }

            // Smoothly turn toward travel direction — the overhead read depends
            // on the crest pointing where the fighter is actually sliding.
            Quaternion goal = Quaternion.LookRotation(lastMoveDir, Vector3.up);
            if (bodyRoot != null)
                bodyRoot.rotation = Quaternion.Slerp(bodyRoot.rotation, goal, 1f - Mathf.Exp(-turnSpeed * Time.deltaTime));

            AnimateBody();
            AnimateHaste();

            if (bodyRoot != null)
            {
                // Idle breathing fades out as the stride takes over, and the
                // stride adds its own bounce so walking does not look like gliding.
                float bob = Mathf.Sin((Time.time + bobSeed) * bobSpeed) * bobAmplitude * (1f - gait);
                float bounce = Mathf.Abs(Mathf.Sin(stridePhase)) * 0.05f * gait;
                bodyRoot.localPosition = new Vector3(0f, baseY + bob + bounce, 0f);
            }

            if (hitFlash > 0f)
            {
                hitFlash -= Time.deltaTime;
                float k = Mathf.Clamp01(hitFlash / HitFlashSeconds);
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    string n = r.gameObject.name;
                    if (n.StartsWith("Head") || n.StartsWith("Shield")) continue;
                    r.GetPropertyBlock(block);
                    Color baseCol = n.StartsWith("Crest") ? def.ColorLight
                        : n.StartsWith("Base") ? def.Color * 0.55f
                        : def.Color;
                    block.SetColor(BaseColorId, Color.Lerp(baseCol, Color.white, k * 0.85f));
                    r.SetPropertyBlock(block);
                }
            }

            // The trailing ghost bar lags behind the real health so a hit is
            // legible for a beat, and the whole plate pops on impact.
            if (hpGhost != null && hpFill != null)
            {
                float target = hpFill.fillAmount;
                hpGhost.fillAmount = Mathf.MoveTowards(hpGhost.fillAmount, target,
                    Time.deltaTime * 0.45f);
                if (hpGhost.fillAmount < target) hpGhost.fillAmount = target;
            }

            if (hpRoot != null)
            {
                float k = hitFlash > 0f ? Mathf.Clamp01(hitFlash / HitFlashSeconds) : 0f;
                float s = 1f + k * 0.12f;
                hpRoot.localScale = new Vector3(s, s, s);
            }
        }

        /// <summary>
        /// Stride, idle breath, hit recoil and cast pose. Each pose is rebuilt
        /// from scratch every frame, so it can never drift or accumulate error.
        /// </summary>
        private void AnimateBody()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) dt = Time.unscaledDeltaTime;

            // Read the travel speed off the transform instead of asking gameplay:
            // the local fighter is moved by the simulation and a guest is moved by
            // snapshot interpolation, and both should walk.
            Vector3 here = transform.position;
            float speed = 0f;
            if (hasLastBodyPos && dt > 0.00001f)
            {
                float dx = here.x - lastBodyPos.x;
                float dz = here.z - lastBodyPos.z;
                speed = Mathf.Sqrt(dx * dx + dz * dz) / dt;
            }
            lastBodyPos = here;
            hasLastBodyPos = true;

            gait = Mathf.Clamp01(speed / WalkSpeedRef);
            stridePhase += dt * Mathf.Lerp(1.7f, 12f, gait);

            if (castTimer > 0f) castTimer = Mathf.Max(0f, castTimer - dt);
            float cast = Mathf.Clamp01(castTimer / CastPoseSeconds);
            float recoil = hitFlash > 0f ? Mathf.Clamp01(hitFlash / HitFlashSeconds) : 0f;

            float step = Mathf.Sin(stridePhase);
            float legSwing = step * Mathf.Lerp(3f, 36f, gait);
            float armSwing = step * Mathf.Lerp(3f, 30f, gait);

            if (legL != null) legL.localRotation = Quaternion.Euler(-legSwing, 0f, 0f);
            if (legR != null) legR.localRotation = Quaternion.Euler(legSwing, 0f, 0f);

            // Both arms punch forward on a cast; otherwise they counter-swing.
            float raise = 74f * cast;
            if (armL != null) armL.localRotation = Quaternion.Euler(armSwing + raise, 0f, 0f);
            if (armR != null) armR.localRotation = Quaternion.Euler(-armSwing + raise, 0f, 0f);

            if (torso != null)
            {
                torso.localRotation = Quaternion.Euler(-7f * cast + 17f * recoil, 0f, 0f);
                float breath = 1f + Mathf.Sin((Time.time + bobSeed) * 1.6f) * 0.02f;
                torso.localScale = new Vector3(torsoScaleHome.x,
                    torsoScaleHome.y * breath, torsoScaleHome.z);
            }
        }

        private void LateUpdate()
        {
            // The nameplate is a flat canvas: rotate it to the camera so it
            // always faces the player.
            if (hpRoot == null) return;
            if (cachedCamera == null) cachedCamera = Camera.main;
            if (cachedCamera == null) cachedCamera = FindFirstObjectByType<Camera>();
            if (cachedCamera != null)
                hpRoot.rotation = cachedCamera.transform.rotation;
        }

        /// <summary>
        /// Factory: assembles a fighter hierarchy under a new GameObject and
        /// returns the view. Kept here so scene/prefab authoring stays optional.
        /// </summary>
        public static CharacterView Create(CharacterDef def, Vector3 position, Transform parent)
        {
            var root = new GameObject($"{def.Id}_View");
            if (parent != null) root.transform.SetParent(parent, false);
            root.transform.position = position;

            var view = root.AddComponent<CharacterView>();

            // BodyRoot spins so the head/crest always face travel direction.
            var body = new GameObject("BodyRoot");
            body.transform.SetParent(root.transform, false);
            view.bodyRoot = body.transform;

            // --- trunk ---
            view.torso = AddPart(body.transform, PrimitiveType.Capsule, "Torso",
                new Vector3(0f, 1.00f, 0f), new Vector3(1.00f, 0.62f, 0.92f));
            view.torsoScaleHome = view.torso.localScale;

            view.head = AddPart(body.transform, PrimitiveType.Sphere, "Head",
                new Vector3(0f, 1.72f, 0f), new Vector3(0.72f, 0.72f, 0.72f));

            // Crest = the facing arrow, sitting on the chest side.
            view.crest = AddPart(body.transform, PrimitiveType.Cube, "Crest",
                new Vector3(0f, 1.02f, 0.60f), new Vector3(0.26f, 0.26f, 0.50f),
                new Vector3(35f, 0f, 0f));

            // Ground badge — a thin disc that keeps the unit planted on the ice.
            AddPart(body.transform, PrimitiveType.Cylinder, "Badge",
                new Vector3(0f, 0.05f, 0f), new Vector3(1.7f, 0.04f, 1.7f));

            // --- limbs: the darker half of the palette, so the silhouette
            // reads as a body rather than one coloured blob from above ---
            view.legL = AddPart(body.transform, PrimitiveType.Capsule, "BaseLegL",
                new Vector3(-0.27f, 0.40f, 0f), new Vector3(0.30f, 0.34f, 0.30f));
            view.legR = AddPart(body.transform, PrimitiveType.Capsule, "BaseLegR",
                new Vector3(0.27f, 0.40f, 0f), new Vector3(0.30f, 0.34f, 0.30f));
            view.armL = AddPart(body.transform, PrimitiveType.Capsule, "BaseArmL",
                new Vector3(-0.60f, 1.12f, 0f), new Vector3(0.24f, 0.36f, 0.24f));
            view.armR = AddPart(body.transform, PrimitiveType.Capsule, "BaseArmR",
                new Vector3(0.60f, 1.12f, 0f), new Vector3(0.24f, 0.36f, 0.24f));

            AddSilhouette(body.transform, def);

            BuildNameplate(root.transform, view);

            view.Configure(def, position);
            view.CacheRenderers();
            view.ApplyPalette();
            return view;
        }

        /// <summary>
        /// Per-character extras, so the two fighters tell apart at a glance under
        /// the overhead camera: the Vanguard is a slab of plate armour, the Storm
        /// mage is a robed caster.
        /// </summary>
        private static void AddSilhouette(Transform body, CharacterDef def)
        {
            if (def.Id == CharacterId.Vanguard)
            {
                AddPart(body, PrimitiveType.Cube, "BaseGuardL",
                    new Vector3(-0.64f, 1.40f, 0f), new Vector3(0.46f, 0.30f, 0.54f));
                AddPart(body, PrimitiveType.Cube, "BaseGuardR",
                    new Vector3(0.64f, 1.40f, 0f), new Vector3(0.46f, 0.30f, 0.54f));
                AddPart(body, PrimitiveType.Cube, "Plating",
                    new Vector3(0f, 1.06f, 0.36f), new Vector3(0.66f, 0.50f, 0.20f));
            }
            else if (def.Id == CharacterId.Assassin)
            {
                // Sleek and low: a hood, a crossed harness, a scarf trailing
                // behind and a dagger on each hip. Parts whose names start with
                // "Base" take the darkened half of the palette (see
                // ApplyPalette), which is what keeps the blades reading bright.
                AddPart(body, PrimitiveType.Cube, "BaseHood",
                    new Vector3(0f, 1.88f, -0.02f), new Vector3(0.52f, 0.46f, 0.54f),
                    new Vector3(0f, 45f, 45f));
                AddPart(body, PrimitiveType.Cube, "BasePadL",
                    new Vector3(-0.55f, 1.33f, 0f), new Vector3(0.30f, 0.19f, 0.40f),
                    new Vector3(0f, 0f, 26f));
                AddPart(body, PrimitiveType.Cube, "BasePadR",
                    new Vector3(0.55f, 1.33f, 0f), new Vector3(0.30f, 0.19f, 0.40f),
                    new Vector3(0f, 0f, -26f));
                AddPart(body, PrimitiveType.Cube, "Sash",
                    new Vector3(0f, 1.00f, 0.34f), new Vector3(0.66f, 0.12f, 0.20f),
                    new Vector3(0f, 0f, -34f));
                AddPart(body, PrimitiveType.Cube, "SashBack",
                    new Vector3(0f, 1.00f, -0.34f), new Vector3(0.66f, 0.12f, 0.20f),
                    new Vector3(0f, 0f, 34f));
                AddPart(body, PrimitiveType.Cube, "BaseScarf",
                    new Vector3(-0.30f, 1.36f, -0.40f), new Vector3(0.19f, 0.11f, 0.88f),
                    new Vector3(20f, 0f, 0f));
                AddPart(body, PrimitiveType.Cube, "BaseScarfTip",
                    new Vector3(-0.30f, 1.20f, -0.84f), new Vector3(0.15f, 0.09f, 0.46f),
                    new Vector3(38f, 0f, 0f));
                AddPart(body, PrimitiveType.Cube, "BladeHipL",
                    new Vector3(-0.33f, 0.60f, 0.22f), new Vector3(0.10f, 0.12f, 0.54f),
                    new Vector3(30f, 0f, 0f));
                AddPart(body, PrimitiveType.Cube, "BladeHipR",
                    new Vector3(0.33f, 0.60f, 0.22f), new Vector3(0.10f, 0.12f, 0.54f),
                    new Vector3(30f, 0f, 0f));
                AddPart(body, PrimitiveType.Cube, "BaseBootL",
                    new Vector3(-0.27f, 0.17f, 0.06f), new Vector3(0.33f, 0.21f, 0.46f));
                AddPart(body, PrimitiveType.Cube, "BaseBootR",
                    new Vector3(0.27f, 0.17f, 0.06f), new Vector3(0.33f, 0.21f, 0.46f));
            }
            else
            {
                AddPart(body, PrimitiveType.Cylinder, "BaseRobe",
                    new Vector3(0f, 0.62f, 0f), new Vector3(1.20f, 0.26f, 1.20f));
                AddPart(body, PrimitiveType.Cube, "BaseHood",
                    new Vector3(0f, 1.94f, -0.04f), new Vector3(0.58f, 0.58f, 0.58f),
                    new Vector3(0f, 45f, 45f));
                AddPart(body, PrimitiveType.Cube, "Sash",
                    new Vector3(0f, 0.94f, 0.30f), new Vector3(0.74f, 0.16f, 0.28f));
            }
        }

        /// <summary>
        /// Floating nameplate above the head: a rounded dark pill carrying the
        /// English name, and below it a rounded bar with a left-anchored fill
        /// plus the percentage centred on top of it. All uGUI, no mesh text.
        /// </summary>
        private static void BuildNameplate(Transform root, CharacterView view)
        {
            var hpGo = new GameObject("Nameplate");
            hpGo.transform.SetParent(root, false);
            hpGo.transform.localPosition = new Vector3(0f, HeadWidgetY, 0f);
            hpGo.transform.localScale = Vector3.one;
            view.hpRoot = hpGo.transform;

            var canvasGo = new GameObject("NameplateCanvas",
                typeof(RectTransform), typeof(Canvas));
            canvasGo.transform.SetParent(hpGo.transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var crt = (RectTransform)canvasGo.transform;
            crt.sizeDelta = new Vector2(CanvasWidth, CanvasHeight);
            crt.localScale = Vector3.one / UnitsPerWorldUnit;

            // --- name pill (top) ---
            var pill = UiFactory.CreatePanel("NamePill", crt,
                new Color(0.03f, 0.06f, 0.12f, 0.92f));
            UiFactory.UseRounded(pill);
            var prt = (RectTransform)pill.transform;
            prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 1f);
            prt.pivot = new Vector2(0.5f, 1f);
            prt.anchoredPosition = new Vector2(0f, -2f);
            prt.sizeDelta = new Vector2(PillWidth, 62f);

            var name = UiFactory.CreateLabel("Name", prt, string.Empty,
                NameFontSize, Color.white, TextAlignmentOptions.Center);
            UiFactory.CreateStretch((RectTransform)name.transform, 6f);
            name.fontStyle = FontStyles.Bold;
            UiFactory.AddShadow(name, new Color(0f, 0f, 0f, 0.65f), new Vector2(0f, -2f));
            view.hpName = name;

            // --- health bar (bottom) ---
            var bar = UiFactory.CreatePanel("HpBar", prt.parent,
                new Color(0.02f, 0.04f, 0.08f, 0.95f));
            UiFactory.UseRounded(bar);
            var brt = (RectTransform)bar.transform;
            brt.anchorMin = brt.anchorMax = new Vector2(0.5f, 0f);
            brt.pivot = new Vector2(0.5f, 0f);
            brt.anchoredPosition = new Vector2(0f, 2f);
            brt.sizeDelta = new Vector2(BarWidth, 60f);

            // Ghost trail sits behind the fill; it stays amber in the gap the
            // fill leaves behind as health drops.
            var ghostRt = UiFactory.CreateRect("Ghost", brt);
            UiFactory.CreateStretch(ghostRt, 6f);
            var ghost = ghostRt.gameObject.AddComponent<Image>();
            ghost.sprite = UiFactory.RoundedSprite;
            ghost.type = Image.Type.Filled;
            ghost.fillMethod = Image.FillMethod.Horizontal;
            ghost.fillOrigin = (int)Image.OriginHorizontal.Left;
            ghost.color = UiFactory.Warn;
            ghost.raycastTarget = false;
            view.hpGhost = ghost;

            var fillRt = UiFactory.CreateRect("Fill", brt);
            UiFactory.CreateStretch(fillRt, 6f);
            var fill = fillRt.gameObject.AddComponent<Image>();
            fill.sprite = UiFactory.RoundedSprite;
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.color = UiFactory.Good;
            fill.raycastTarget = false;
            view.hpFill = fill;

            var pct = UiFactory.CreateLabel("Pct", brt, "100%",
                PercentFontSize, Color.white, TextAlignmentOptions.Center);
            UiFactory.CreateStretch((RectTransform)pct.transform);
            pct.fontStyle = FontStyles.Bold;
            view.hpPercent = pct;
        }

        private static Material characterMaterialTemplate;

        private static Transform AddPart(Transform parent, PrimitiveType type, string name,
            Vector3 localPos, Vector3 localScale, Vector3? localEuler = null)
        {
            var go = GameObject.CreatePrimitive(type);
            DestroyComponent(go.GetComponent<Collider>());
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            if (localEuler.HasValue)
                go.transform.localRotation = Quaternion.Euler(localEuler.Value);
            go.transform.localScale = localScale;

            if (characterMaterialTemplate == null)
            {
                characterMaterialTemplate = Resources.Load<Material>("Arena/ArenaLit");
            }
            if (characterMaterialTemplate != null)
            {
                var r = go.GetComponent<Renderer>();
                if (r != null) r.sharedMaterial = characterMaterialTemplate;
            }

            return go.transform;
        }

        /// <summary>
        /// Primitives ship with colliders we do not want on purely visual parts;
        /// gameplay colliders are owned by the fighter root instead.
        /// </summary>
        private static void DestroyComponent(Component c)
        {
            if (c == null) return;
            if (Application.isPlaying) Destroy(c);
            else DestroyImmediate(c);
        }
    }
}
