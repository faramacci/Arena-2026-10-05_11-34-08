using System.Collections.Generic;
using UnityEngine;
using Arena.Core;

namespace Arena.Presentation
{
    /// <summary>
    /// A point on a fighter that gameplay wants in world space: where a throw leaves
    /// the body and where a returning one is caught.
    /// </summary>
    public enum FighterAnchor
    {
        /// <summary>The middle of the chest: the fighter's body, for impacts.</summary>
        Chest,
        Head,
        Hips,
        /// <summary>The fist, a little past the wrist.</summary>
        LeftHand,
        RightHand,
        /// <summary>The middle of the forearm, where the aegis is strapped.</summary>
        LeftForearm,
        RightForearm
    }

    /// <summary>
    /// One fighter's rigged model, loaded from Resources/Arena/Fighters and posed by
    /// hand.
    ///
    /// The FBX files carry a 21-bone humanoid skeleton and no animation clips, so
    /// the stride, the cast punch and the hit lean the primitive bodies played with
    /// capsule rotations are rebuilt here as bone rotations. Each joint is turned
    /// about the *character's* own left/right, up and forward axes, taken from the
    /// body root rather than from the bone's parents: the FBX rigs have per-bone
    /// rolls whose frames disagree (a 45° turn about `upper_arm.L`'s parent X abducts
    /// the arm where the same turn on `thigh.L` swings the leg forward), so posing in
    /// the bone frames would need one special case per joint. In the body frame one
    /// convention holds for all of them: +pitch swings forward, which is the
    /// character's +Z because the game's fighters face the way they travel.
    ///
    /// Each pose is rebuilt from the bone's rest rotation every frame, so it cannot
    /// drift, and a guest interpolating between host snapshots walks exactly like the
    /// host. <see cref="Attach"/> returns null when the model is missing, which
    /// leaves the caller on its primitive body: a fighter whose art has not landed
    /// still fights.
    /// </summary>
    public sealed class FighterModel : MonoBehaviour
    {
        /// <summary>The rigs are authored at this height, in metres.</summary>
        private const float BuildHeight = 1.92f;

        /// <summary>An import this far off the build height is a unit error, not taste.</summary>
        private const float HeightTolerance = 0.25f;

        /// <summary>Vertex stride for the deformation probe: enough to see the skin move.</summary>
        private const int ProbeStride = 137;

        /// <summary>
        /// The skinned export keeps the Blender to Unity axis conversion on the skin
        /// node instead of baking it into the vertices (a skinned mesh cannot bake it,
        /// the bind pose would come apart), and that conversion maps the rig's front,
        /// Blender's +Y, to Unity's -Z. The game's forward is +Z - the crest and the
        /// body root's LookRotation agree - so the model is yawed half a turn here.
        /// <see cref="FacesForward"/> is measured, not assumed, and asserted in the
        /// regression suite.
        /// </summary>
        private const float FacingYaw = 180f;

        /// <summary>
        /// Every bone in the kit, in the spelling this code uses. Lookups normalize
        /// both sides (see <see cref="Key"/>), so the FBX exporter's punctuation -
        /// `upper_arm.L`, `upper_arm_L` or `upperArmL` - does not matter.
        /// </summary>
        private static readonly string[] BoneNames =
        {
            "hips", "spine", "chest", "neck", "head",
            "shoulderL", "upperArmL", "forearmL", "handL",
            "shoulderR", "upperArmR", "forearmR", "handR",
            "thighL", "shinL", "footL", "toeL",
            "thighR", "shinR", "footR", "toeR"
        };

        private readonly Dictionary<string, Transform> joints = new Dictionary<string, Transform>();
        private readonly Dictionary<Transform, Quaternion> restInBody =
            new Dictionary<Transform, Quaternion>();
        private readonly List<string> missing = new List<string>();

        /// <summary>
        /// The hand props this fighter is wearing, by wire id (see
        /// <see cref="PropLibrary"/>). A prop is its own object, not part of the skin,
        /// which is what lets the game hide it and throw a copy of it.
        /// </summary>
        private readonly Dictionary<int, WornProp> props = new Dictionary<int, WornProp>();

        /// <summary>
        /// One prop on the body: the object, the bone that carries it, and the transform
        /// that mapped the body's coordinates into that bone's rest frame when the model
        /// bound. Nothing here assumes how the FBX writes a bone's axes - the mapping is
        /// read off the live skeleton - so the prop follows the skin, and a change in the
        /// exporter's bone convention moves the prop with the arm instead of leaving it
        /// sideways.
        /// </summary>
        private sealed class WornProp
        {
            public GameObject Go;
            public Transform Bone;
            /// <summary>Body space to bone space, sampled at the bind pose.</summary>
            public Matrix4x4 Rest;
        }

        private CharacterId charId;
        private SkinnedMeshRenderer skin;
        private Renderer[] renderers;
        private Transform body;
        private MaterialPropertyBlock block;
        private Color[] baseColors;
        private bool ticked;

        private Transform hips, spine, chest, armL, armR;
        private Transform foreL, foreR, handL, handR;
        private Transform thighL, thighR, shinL, shinR, footL, footR;

        private Mesh probe;
        private int[] probeIndices;
        private Vector3[] probeBuffer;

        /// <summary>The skeleton is bound and can be posed.</summary>
        public bool Ready => hips != null && spine != null && thighL != null && armL != null;

        /// <summary>Every renderer under the model, for visibility and hit flashes.</summary>
        public Renderer[] Renderers => renderers ?? new Renderer[0];

        /// <summary>Kit bones this import did not provide; empty on all three models.</summary>
        public IReadOnlyList<string> MissingBones => missing;

        /// <summary>True when the import lies on its back instead of standing up.</summary>
        public bool LyingDown { get; private set; }

        /// <summary>True when the toes point the way the game's fighters walk.</summary>
        public bool FacesForward { get; private set; }

        /// <summary>
        /// The model's extent in the body root's frame, metres, at the bind pose and
        /// after fitting. This is the mesh's own bounds through its transform, not
        /// `SkinnedMeshRenderer.bounds`: those come back inflated (the vanguard reads
        /// 2.076 m tall with its feet 16 cm under the sand on a figure that stands at
        /// 1.916 m with its soles on the origin).
        /// </summary>
        public Bounds RestBounds { get; private set; }

        /// <summary>Height measured on the imported asset, before any fitting.</summary>
        public float ImportedHeight { get; private set; }

        /// <summary>Scale applied to bring the import to <see cref="BuildHeight"/>.</summary>
        public float FittedScale { get; private set; } = 1f;

        /// <summary>The resource path for a character, e.g. `Arena/Fighters/mage`.</summary>
        public static string AssetPath(CharacterId id)
        {
            switch (id)
            {
                case CharacterId.Assassin: return "Arena/Fighters/assassin";
                case CharacterId.Mage: return "Arena/Fighters/mage";
                default: return "Arena/Fighters/vanguard";
            }
        }

        /// <summary>
        /// Loads the character's rigged model under `bodyRoot`, or returns null when
        /// the asset is missing so the caller can keep its primitive body.
        /// </summary>
        public static FighterModel Attach(Transform bodyRoot, CharacterId id)
        {
            var asset = Resources.Load<GameObject>(AssetPath(id));
            if (asset == null)
            {
                Debug.LogWarning("[Arena] no rigged model at Resources/" + AssetPath(id) +
                    " - the fighter keeps its primitive body");
                return null;
            }

            var root = Instantiate(asset, bodyRoot, false);
            root.name = "Model_" + id;
            var model = root.AddComponent<FighterModel>();
            model.charId = id;
            model.Bind();
            return model;
        }

        private void Bind()
        {
            renderers = GetComponentsInChildren<Renderer>(true);
            skin = GetComponentInChildren<SkinnedMeshRenderer>(true);
            body = transform.parent != null ? transform.parent : transform;

            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                string key = Key(t.name);
                if (key.Length == 0 || joints.ContainsKey(key)) continue;
                joints.Add(key, t);
            }

            foreach (string wanted in BoneNames)
            {
                Transform found;
                if (!joints.TryGetValue(Key(wanted), out found)) missing.Add(wanted);
            }

            hips = Joint("hips");
            spine = Joint("spine");
            chest = Joint("chest");
            armL = Joint("upperArmL");
            armR = Joint("upperArmR");
            foreL = Joint("forearmL");
            foreR = Joint("forearmR");
            handL = Joint("handL");
            handR = Joint("handR");
            thighL = Joint("thighL");
            thighR = Joint("thighR");
            shinL = Joint("shinL");
            shinR = Joint("shinR");
            footL = Joint("footL");
            footR = Joint("footR");

            Face();
            Fit();
            Remember();
            FixShaders();
            WearProps();

            Debug.Log("[Arena] model " + name + ": " + joints.Count + " joints, " +
                (skin != null && skin.sharedMesh != null
                    ? skin.sharedMesh.vertexCount.ToString() : "0") + " vertices, " +
                (missing.Count == 0 ? "all 21 kit bones" : "missing " + string.Join(", ", missing)) +
                ", stands " + RestBounds.size.y.ToString("0.000") + " m (imported " +
                ImportedHeight.ToString("0.000") + " m, fitted x" + FittedScale.ToString("0.000") +
                "), feet at y=" + RestBounds.min.y.ToString("0.000") +
                ", toes " + (FacesForward ? "forward" : "BACKWARD") +
                ", bounds x " + RestBounds.min.x.ToString("0.000") + ".." +
                RestBounds.max.x.ToString("0.000") + ", z " + RestBounds.min.z.ToString("0.000") +
                ".." + RestBounds.max.z.ToString("0.000"));
        }

        private Transform Joint(string key)
        {
            Transform found;
            return joints.TryGetValue(Key(key), out found) ? found : null;
        }

        /// <summary>`upper_arm.L` and `UpperArmL` both answer to `upperarml`.</summary>
        private static string Key(string name)
        {
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>
        /// Yaw the model onto the game's forward axis (see <see cref="FacingYaw"/>),
        /// then measure which way the toes point from the bones, so a rig that comes
        /// out of the exporter the other way round is reported instead of running
        /// backwards.
        /// </summary>
        private void Face()
        {
            transform.localRotation = Quaternion.Euler(0f, FacingYaw, 0f) * transform.localRotation;
            Transform toe = Joint("toeL");
            if (toe == null || footL == null) return;
            Vector3 ankle = body.InverseTransformPoint(footL.position);
            Vector3 tip = body.InverseTransformPoint(toe.position);
            FacesForward = tip.z > ankle.z + 0.02f;
        }

        /// <summary>
        /// Measure the import, then stand it on the origin at the build height. A
        /// model that lands 100x or 0.01x off is a unit mismatch and gets fitted; one
        /// that lands within tolerance is left exactly as the importer made it, which
        /// is what all three do (1.916 m, 2.088 m with the mage's staff, 1.919 m).
        /// </summary>
        private void Fit()
        {
            if (skin == null)
            {
                Debug.LogError("[Arena] model " + name + " has no SkinnedMeshRenderer");
                return;
            }

            // The body root is unscaled and only ever turns about Y, so a metre in
            // its frame is a metre on the sand.
            Bounds measured = MeshBounds(body);
            ImportedHeight = measured.size.y;
            float across = Mathf.Max(measured.size.x, measured.size.z);
            if (across > ImportedHeight * 1.6f)
            {
                LyingDown = true;
                Debug.LogError("[Arena] model " + name + " imports lying down (" +
                    ImportedHeight.ToString("0.000") + " m tall, " + across.ToString("0.000") +
                    " m across): the FBX axis conversion is missing");
            }

            Vector3 home = transform.localScale;
            float scale = Mathf.Abs(ImportedHeight - BuildHeight) > HeightTolerance &&
                ImportedHeight > 0.001f ? BuildHeight / ImportedHeight : 1f;
            FittedScale = scale;
            transform.localScale = home * scale;
            float lift = body.position.y - measured.min.y;
            transform.localPosition = new Vector3(0f, lift, 0f);

            RestBounds = new Bounds(
                new Vector3(measured.center.x, measured.center.y + lift, measured.center.z),
                measured.size * scale);
        }

        /// <summary>
        /// The mesh's bind-pose bounds expressed in `space`. The mesh's own bounds are
        /// the honest ones: `SkinnedMeshRenderer.localBounds` is in the root bone's
        /// frame and padded, so it cannot say where the soles are.
        /// </summary>
        private Bounds MeshBounds(Transform space)
        {
            Bounds source = skin.sharedMesh.bounds;
            Matrix4x4 toSpace = space.worldToLocalMatrix * skin.transform.localToWorldMatrix;
            var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int corner = 0; corner < 8; corner++)
            {
                var point = new Vector3(
                    (corner & 1) == 0 ? source.min.x : source.max.x,
                    (corner & 2) == 0 ? source.min.y : source.max.y,
                    (corner & 4) == 0 ? source.min.z : source.max.z);
                Vector3 local = toSpace.MultiplyPoint3x4(point);
                min = Vector3.Min(min, local);
                max = Vector3.Max(max, local);
            }
            return new Bounds((min + max) * 0.5f, max - min);
        }

        /// <summary>Remember every bone's rest rotation, expressed in the body frame.</summary>
        private void Remember()
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
                restInBody[t] = Quaternion.Inverse(body.rotation) * t.rotation;
        }

        /// <summary>
        /// Put the skin on a shader this pipeline renders. The rigs are exported with
        /// per-part colours and no textures and Unity imports them with its own
        /// materials; anything the importer left on a shader URP cannot draw is
        /// rebuilt from the shared Arena/ArenaLit template, keeping its colour,
        /// metallic, smoothness and emission.
        /// </summary>
        private void FixShaders()
        {
            var created = MakeUrp(renderers, "model " + name);
            if (created.Count > 0) OwnedMaterial.AssignAll(gameObject, created.ToArray());
        }

        /// <summary>
        /// Rebuild every material slot that is not on a shader this pipeline draws,
        /// and return the materials it allocated so the caller can own them. Shared
        /// with the hand props, which are exported from the same scene and so arrive
        /// on the same imported material.
        /// </summary>
        public static List<Material> MakeUrp(Renderer[] renderers, string what)
        {
            var created = new List<Material>();
            if (renderers == null) return created;
            Material template = Resources.Load<Material>("Arena/ArenaLit");
            foreach (var r in renderers)
            {
                if (r == null) continue;
                Material[] slots = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < slots.Length; i++)
                {
                    Material source = slots[i];
                    if (source == null || IsUniversal(source.shader) || template == null) continue;
                    var copy = new Material(template) { name = "Arena_Model_" + source.name };
                    CopyLook(source, copy);
                    slots[i] = copy;
                    created.Add(copy);
                    changed = true;
                }
                if (!changed) continue;
                r.sharedMaterials = slots;
                Debug.Log("[Arena] " + what + ": rebuilt " + slots.Length +
                    " material slot(s) on URP/Lit (" + r.name + ", was on " +
                    (slots.Length > 0 && slots[0] != null ? "an imported shader" : "nothing") + ")");
            }
            return created;
        }

        /// <summary>
        /// Put this character's own equipment on the body. The shield rides the left
        /// forearm and the throwing knife the right fist, because that is where the body
        /// mesh used to carry them: they were cut out of it by the Blender build so the
        /// game could take them away.
        /// </summary>
        private void WearProps()
        {
            int[] carried = CarriedProps(charId);
            for (int i = 0; i < carried.Length; i++)
            {
                int wire = carried[i];
                Transform bone = Joint(PropBoneName(wire));
                if (bone == null) continue;
                var prop = PropLibrary.Wear(wire, transform, name);
                if (prop == null) continue;
                var worn = new WornProp
                {
                    Go = prop,
                    Bone = bone,
                    // Sampled here, at the bind pose, while the body is where it was
                    // built: this is the whole of the prop's placement.
                    Rest = bone.worldToLocalMatrix * transform.localToWorldMatrix
                };
                props[wire] = worn;
                Carry(worn);
            }
        }

        /// <summary>
        /// Moves one prop with the bone that wears it. The prop's geometry is in the
        /// body's own frame, so the bone's rest mapping is what says where in the arm it
        /// sits; applying the bone's *current* transform on top of that carries it
        /// through the stride, the cast punch and the hit lean.
        /// </summary>
        private static void Carry(WornProp prop)
        {
            if (prop == null || prop.Go == null || prop.Bone == null) return;
            Matrix4x4 want = prop.Bone.localToWorldMatrix * prop.Rest;
            prop.Go.transform.SetPositionAndRotation(want.GetColumn(3), want.rotation);
        }

        /// <summary>The bone a worn prop rides: the aegis on the left forearm, the knife in the right fist.</summary>
        private static string PropBoneName(int wireId)
            => wireId == PropLibrary.WireShield ? "forearmL" : "handR";

        /// <summary>The props a character carries, by wire id.</summary>
        private static int[] CarriedProps(CharacterId id)
        {
            switch (id)
            {
                case CharacterId.Assassin: return new[] { PropLibrary.WireDagger };
                case CharacterId.Mage: return new int[0];
                default: return new[] { PropLibrary.WireShield };
            }
        }

        /// <summary>The prop this fighter wears for `wireId`, or null.</summary>
        public GameObject Prop(int wireId)
        {
            WornProp prop;
            return props.TryGetValue(wireId, out prop) && prop != null ? prop.Go : null;
        }

        /// <summary>
        /// Show or hide a worn prop. Gameplay hides it for exactly as long as a copy of
        /// it is in the air, so a throw empties the hand and the catch fills it again.
        /// </summary>
        public void SetPropHidden(int wireId, bool hidden)
        {
            WornProp prop;
            if (!props.TryGetValue(wireId, out prop) || prop == null || prop.Go == null) return;
            if (prop.Go.activeSelf != !hidden) prop.Go.SetActive(!hidden);
        }

        private static bool IsUniversal(Shader shader)
        {
            return shader != null && shader.name.StartsWith("Universal Render Pipeline");
        }

        private static void CopyLook(Material source, Material copy)
        {
            Color baseColor = source.HasProperty("_BaseColor")
                ? source.GetColor("_BaseColor")
                : source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white;
            if (copy.HasProperty("_BaseColor")) copy.SetColor("_BaseColor", baseColor);
            if (copy.HasProperty("_Color")) copy.SetColor("_Color", baseColor);

            float metallic = source.HasProperty("_Metallic") ? source.GetFloat("_Metallic") : 0f;
            if (copy.HasProperty("_Metallic")) copy.SetFloat("_Metallic", metallic);

            // The built-in Standard shader stores gloss, URP stores smoothness.
            float smoothness = source.HasProperty("_Smoothness")
                ? source.GetFloat("_Smoothness")
                : source.HasProperty("_Glossiness") ? source.GetFloat("_Glossiness") : 0.5f;
            if (copy.HasProperty("_Smoothness")) copy.SetFloat("_Smoothness", smoothness);

            Color emission = source.HasProperty("_EmissionColor")
                ? source.GetColor("_EmissionColor")
                : Color.black;
            if (!copy.HasProperty("_EmissionColor")) return;
            copy.SetColor("_EmissionColor", emission);
            if (emission.maxColorComponent > 0.01f)
            {
                copy.EnableKeyword("_EMISSION");
                copy.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
        }

        /// <summary>
        /// The pose the primitive body played, as bone rotations: a stride cycle with
        /// knees and counter-swung arms, both arms punching forward on a cast, the hit
        /// lean, and a slow idle sway at rest. `time` only drives the sway.
        ///
        /// Bones are set in the order a skeleton solves, parents first, and the
        /// rotations are absolute in the body frame rather than relative to the
        /// animated parent: that keeps the sums honest (a knee bend is the thigh's
        /// angle minus the bend) and lets the soles stay level no matter what the leg
        /// above them is doing.
        /// </summary>
        public void Pose(float stridePhase, float gait, float cast, float recoil, float time)
        {
            if (!Ready) return;

            float step = Mathf.Sin(stridePhase);
            float legSwing = step * Mathf.Lerp(3f, 36f, gait);
            float armSwing = step * Mathf.Lerp(3f, 30f, gait);
            float raise = 74f * cast;
            float lean = 7f * cast - 17f * recoil;
            float sway = Mathf.Sin(time * 1.7f) * 2.5f * (1f - gait);

            // The knee closes as its leg swings forward, which is the leg that lifts.
            float kneeL = Mathf.Max(0f, legSwing) * 0.85f;
            float kneeR = Mathf.Max(0f, -legSwing) * 0.85f;

            Turn(hips, 0f, 0f, step * 3f * gait);
            Turn(thighL, legSwing);
            Turn(thighR, -legSwing);
            Turn(shinL, legSwing - kneeL);
            Turn(shinR, -legSwing - kneeR);
            Turn(footL, 0f);
            Turn(footR, 0f);
            Turn(armL, raise - armSwing, 0f, -sway);
            Turn(armR, raise + armSwing, 0f, sway);
            Turn(spine, lean);
            Turn(chest, lean * 0.7f);
            Turn(Joint("head"), lean * 0.3f);

            // The props come last, after the bones they ride have been placed: a shield
            // on a forearm is only where it should be once that forearm has moved.
            foreach (var prop in props.Values) Carry(prop);
        }

        /// <summary>
        /// A world point for a throw to leave from - and, for a boomerang throw, the
        /// point it comes home to. Hands report just past the wrist so a knife leaves
        /// the fist, forearms report their middle where the aegis is strapped, and
        /// both are read off the live skeleton, so a throw follows the arm that is
        /// swinging it and a returning shield is caught by a hand that moved.
        /// </summary>
        public bool TryAnchor(FighterAnchor anchor, out Vector3 point)
        {
            Transform bone = AnchorBone(anchor);
            if (bone == null)
            {
                point = Vector3.zero;
                return false;
            }

            switch (anchor)
            {
                case FighterAnchor.LeftForearm:
                case FighterAnchor.RightForearm:
                {
                    Transform wrist = anchor == FighterAnchor.LeftForearm ? handL : handR;
                    point = wrist != null
                        ? Vector3.Lerp(bone.position, wrist.position, 0.5f)
                        : bone.position;
                    return true;
                }
                case FighterAnchor.LeftHand:
                case FighterAnchor.RightHand:
                {
                    Transform elbow = anchor == FighterAnchor.LeftHand ? foreL : foreR;
                    Vector3 reach = elbow != null
                        ? (bone.position - elbow.position).normalized
                        : Vector3.zero;
                    point = bone.position + reach * 0.06f;
                    return true;
                }
                default:
                    point = bone.position;
                    return true;
            }
        }

        private Transform AnchorBone(FighterAnchor anchor)
        {
            switch (anchor)
            {
                case FighterAnchor.LeftHand: return handL;
                case FighterAnchor.RightHand: return handR;
                case FighterAnchor.LeftForearm: return foreL;
                case FighterAnchor.RightForearm: return foreR;
                case FighterAnchor.Head: return Joint("head");
                case FighterAnchor.Hips: return hips;
                default: return chest;
            }
        }

        /// <summary>Back to the rig's rest pose.</summary>
        public void Rest()
        {
            foreach (var pair in restInBody)
                if (pair.Key != null) pair.Key.rotation = body.rotation * pair.Value;
        }

        /// <summary>
        /// Turns one bone about the character's own axes, in the body root's frame:
        /// `pitch` swings it forward, `yaw` turns it about the character's up axis and
        /// `roll` tips it sideways.
        /// </summary>
        private void Turn(Transform bone, float pitch, float yaw = 0f, float roll = 0f)
        {
            if (bone == null) return;
            Quaternion home;
            if (!restInBody.TryGetValue(bone, out home))
                home = Quaternion.Inverse(body.rotation) * bone.rotation;
            Quaternion delta = Quaternion.AngleAxis(pitch, Vector3.right)
                * Quaternion.AngleAxis(yaw, Vector3.up)
                * Quaternion.AngleAxis(roll, Vector3.forward);
            bone.rotation = body.rotation * (delta * home);
        }

        /// <summary>
        /// Paints the model white for a hit: the imported parts keep their own
        /// colours, so the flash blends each slot's colour toward white instead of
        /// replacing the palette the way the primitives did.
        /// </summary>
        public void SetFlash(float amount)
        {
            if (renderers == null) return;
            float k = Mathf.Clamp01(amount);
            if (k <= 0f && !ticked) return;
            ticked = k > 0f;

            if (block == null) block = new MaterialPropertyBlock();
            if (baseColors == null)
            {
                var colors = new List<Color>();
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    foreach (var m in r.sharedMaterials)
                        colors.Add(m != null && m.HasProperty("_BaseColor")
                            ? m.GetColor("_BaseColor")
                            : m != null ? m.color : Color.white);
                }
                baseColors = colors.ToArray();
            }

            int index = 0;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                for (int slot = 0; slot < r.sharedMaterials.Length; slot++)
                {
                    Color from = index < baseColors.Length ? baseColors[index] : Color.white;
                    index++;
                    block.SetColor(BaseColorId, Color.Lerp(from, Color.white, k * 0.85f));
                    r.SetPropertyBlock(block, slot);
                }
            }
        }

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        /// <summary>
        /// How far the skin actually moves between the rest pose and a mid-stride
        /// pose, in metres. It bakes the skinned mesh twice and samples every
        /// <see cref="ProbeStride"/>th vertex; a mesh that is not bound to the bones
        /// reports 0.000 m, which is what makes this worth asserting in a test.
        /// </summary>
        public float SkinTravel()
        {
            if (skin == null) return 0f;
            Rest();
            Vector3[] before = Sample();
            Pose(1.2f, 1f, 0f, 0f, 0f);
            Vector3[] after = Sample();
            float most = 0f;
            for (int i = 0; i < before.Length; i++)
                most = Mathf.Max(most, (after[i] - before[i]).magnitude);
            Rest();
            return most;
        }

        private Vector3[] Sample()
        {
            if (probe == null) probe = new Mesh { name = "ArenaSkinProbe" };
            skin.BakeMesh(probe);
            Vector3[] vertices = probe.vertices;
            if (probeIndices == null)
            {
                var indices = new List<int>();
                for (int i = 0; i < vertices.Length; i += ProbeStride) indices.Add(i);
                probeIndices = indices.ToArray();
                probeBuffer = new Vector3[probeIndices.Length];
            }
            for (int i = 0; i < probeIndices.Length; i++)
                probeBuffer[i] = vertices[Mathf.Min(probeIndices[i], vertices.Length - 1)];
            // A copy: the caller compares two samples and the buffer is reused.
            return (Vector3[])probeBuffer.Clone();
        }

        private void OnDestroy()
        {
            if (probe != null) Destroy(probe);
        }
    }
}
