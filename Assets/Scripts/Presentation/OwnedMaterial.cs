using UnityEngine;

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
