using UnityEngine;
using UnityEngine.Rendering;

namespace Arena.Presentation
{
    /// <summary>Owns only newly allocated materials, never shared Resources assets.</summary>
    public sealed class OwnedMaterial : MonoBehaviour
    {
        private Material owned;
        public Material Material => owned;

        public static void Assign(GameObject target, Material material)
        {
            var owner = target.GetComponent<OwnedMaterial>();
            if (owner == null) owner = target.AddComponent<OwnedMaterial>();
            owner.Release();
            owner.owned = material;
            target.GetComponent<Renderer>().sharedMaterial = material;
        }

        /// <summary>
        /// A fresh unlit material that blends instead of writing depth, used for
        /// translucent shells (the arena ward, the Frost Aegis bubble). The shared
        /// Resources asset is only ever a template, so callers can own and destroy
        /// the result through <see cref="Assign"/>.
        /// </summary>
        public static Material TransparentUnlit(Color c)
        {
            if (unlitTemplate == null) unlitTemplate = Resources.Load<Material>("Arena/ArenaUnlit");
            Material m = unlitTemplate != null
                ? new Material(unlitTemplate) { name = "Arena_Translucent" }
                : FallbackUnlit();
            if (m == null) return null;

            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);   // Transparent
            if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);       // Alpha blend
            if (m.HasProperty("_SrcBlend"))
                m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (m.HasProperty("_DstBlend"))
                m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHATEST_ON");
            m.renderQueue = (int)RenderQueue.Transparent;
            return m;
        }

        private static Material unlitTemplate;

        private static Material FallbackUnlit()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            return shader != null ? new Material(shader) : null;
        }

        private void OnDestroy() => Release();

        private void Release()
        {
            if (owned == null) return;
            if (Application.isPlaying) Destroy(owned);
            else DestroyImmediate(owned);
            owned = null;
        }
    }
}
