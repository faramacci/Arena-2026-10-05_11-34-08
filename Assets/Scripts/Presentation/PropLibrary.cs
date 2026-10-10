using System.Collections.Generic;
using UnityEngine;
using Arena.Core;

namespace Arena.Presentation
{
    /// <summary>
    /// The fighters' hand props - the Vanguard's lion shield and the Assassin's
    /// right-hand knife - loaded from Resources/Arena/Fighters.
    ///
    /// They are separate assets because the game throws them: a prop welded into the
    /// body mesh can never leave the hand, so the Blender build lifts each one out of
    /// the body (Tools/Blender/prop_kit.py) and ships it as its own small FBX holding
    /// two copies of the same geometry - `_arm`, baked in the carrying bone's rest
    /// frame, and `_air`, centred and turned into the frame a thrown object is drawn
    /// in. This class is the only place that knows those names, and it hands out an
    /// instance of whichever copy a caller asks for.
    ///
    /// A prop that fails to load is not fatal: every caller falls back to the
    /// procedural slab it drew before, so a missing asset costs the look and not the
    /// game.
    /// </summary>
    public static class PropLibrary
    {
        /// <summary>
        /// Which prop a thrown weapon is, as the wire and the snapshot spell it: zero
        /// means "no prop - draw the primitive". The ids are small integers rather
        /// than asset names so a guest can rebuild the same object from a snapshot.
        /// </summary>
        public const int WireNone = 0;
        public const int WireShield = 1;
        public const int WireDagger = 2;

        private const string ArmSuffix = "_arm";
        private const string AirSuffix = "_air";

        private static readonly Dictionary<int, GameObject> prefabs = new Dictionary<int, GameObject>();
        private static readonly List<int> missing = new List<int>();

        /// <summary>The prop a projectile of this shape should be, or <see cref="WireNone"/>.</summary>
        public static int WireId(ProjectileShape shape)
        {
            switch (shape)
            {
                case ProjectileShape.Shield: return WireShield;
                case ProjectileShape.Dagger:
                case ProjectileShape.Blade: return WireDagger;
                default: return WireNone;
            }
        }

        /// <summary>The resource path of a prop, e.g. `Arena/Fighters/vanguard_shield`.</summary>
        public static string AssetPath(int wireId)
        {
            switch (wireId)
            {
                case WireShield: return "Arena/Fighters/vanguard_shield";
                case WireDagger: return "Arena/Fighters/assassin_dagger";
                default: return null;
            }
        }

        /// <summary>True when the prop's asset is there to be drawn.</summary>
        public static bool Available(int wireId) => Prefab(wireId) != null;

        /// <summary>
        /// An instance of the `_arm` copy, parented to the model root and left at an
        /// identity local transform, or null when the prop is missing. The copy is
        /// modelled where the body wore it, so it needs no placement here: the fighter
        /// puts it on the arm by carrying it with the live skeleton (see
        /// `FighterModel.WearProps`), which is also what keeps it at the body's own
        /// scale when an import has to be fitted.
        /// </summary>
        public static GameObject Wear(int wireId, Transform modelRoot, string who)
        {
            if (modelRoot == null) return null;
            GameObject instance = Copy(wireId, ArmSuffix, who);
            if (instance == null) return null;

            instance.transform.SetParent(modelRoot, false);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            return instance;
        }

        /// <summary>
        /// An instance of the `_air` copy, centred on its own middle and life size, for
        /// a caller to parent to whatever carries the throw. Null when it is missing.
        /// </summary>
        public static GameObject Fly(int wireId, string who) => Copy(wireId, AirSuffix, who);

        /// <summary>The resource path of the prefab, loaded once and cached.</summary>
        private static GameObject Prefab(int wireId)
        {
            if (wireId == WireNone) return null;
            GameObject cached;
            if (prefabs.TryGetValue(wireId, out cached)) return cached;
            if (missing.Contains(wireId)) return null;

            string path = AssetPath(wireId);
            var asset = path != null ? Resources.Load<GameObject>(path) : null;
            if (asset == null)
            {
                Debug.LogWarning("[Arena] no prop at Resources/" + path +
                    " - thrown weapons stay primitive slabs");
                missing.Add(wireId);
                return null;
            }
            prefabs[wireId] = asset;
            return asset;
        }

        /// <summary>
        /// One instance of one copy: the whole asset is instantiated and the copies
        /// other than `suffix` are switched off, so whatever conversion the importer
        /// put on the file's nodes still applies to the one that is kept.
        /// </summary>
        private static GameObject Copy(int wireId, string suffix, string who)
        {
            GameObject template = Prefab(wireId);
            if (template == null) return null;
            GameObject instance = Object.Instantiate(template);

            // Look the copy up before renaming the instance: a root called
            // "..." would answer to the same suffix search as the copy inside it, and
            // then every renderer would count as part of the copy and nothing would be
            // switched off.
            Transform wanted = Find(instance.transform, suffix);
            if (wanted == null)
            {
                Debug.LogError("[Arena] prop " + template.name + " has no " + suffix +
                    " copy - check Tools/Blender/prop_kit.py and rebuild it");
                Object.Destroy(instance);
                return null;
            }

            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
                if (renderer.transform != wanted && !renderer.transform.IsChildOf(wanted))
                    renderer.gameObject.SetActive(false);

            instance.name = (who ?? "Prop") + "(" + suffix.TrimStart('_') + ")";
            MakeRenderable(wanted, instance);
            return instance;
        }

        /// <summary>The first node whose name ends with `suffix`, e.g. `Shield_air`.</summary>
        private static Transform Find(Transform root, string suffix)
        {
            foreach (Transform node in root.GetComponentsInChildren<Transform>(true))
                if (node.name.ToLowerInvariant().EndsWith(suffix))
                    return node;
            return null;
        }

        /// <summary>
        /// The props are exported from the same Blender scene as the bodies, so they
        /// arrive on the same imported material, and the fighter takes the same route
        /// back to URP/Lit that <see cref="FighterModel.MakeUrp"/> gives a body.
        /// </summary>
        private static void MakeRenderable(Transform wanted, GameObject instance)
        {
            var created = FighterModel.MakeUrp(
                wanted.GetComponentsInChildren<Renderer>(true), "prop " + instance.name);
            if (created.Count > 0) OwnedMaterial.AssignAll(instance, created.ToArray());
        }
    }
}
