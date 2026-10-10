using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// The short red line a fast thing leaves behind it: dashes dropped along the
    /// path the object actually travelled, each one fading where it was laid. It is
    /// the same line the Shadowstep's dash leaves behind the runner (see
    /// <see cref="CharacterView"/>), drawn in the same crimson and the same width -
    /// the assassin's thrown daggers only drag it a shorter way.
    ///
    /// The line is derived from movement and never from the wire, which is what
    /// lets a guest draw it too: the guest walks its replica along the positions it
    /// was sent and drops its own dashes, exactly like the dash line. Nothing about
    /// the trail has to be replicated, and a knife that is gone still leaves its
    /// last dashes burning out behind it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RedTrail : MonoBehaviour
    {
        /// <summary>
        /// The crimson the dash and the knives both mark the ground with: one value
        /// for both, because the knife line is meant to read as the same ink.
        /// </summary>
        public static readonly Color LineRed = new Color(0.95f, 0.16f, 0.20f, 0.80f);

        /// <summary>The line the dashes go into, found or built under the effect root.</summary>
        private RedTrailLine line;
        private Vector3 anchor;
        private bool anchored;

        /// <summary>Puts a trail on a thrown weapon. Idempotent: one per object.</summary>
        public static RedTrail Attach(GameObject go)
        {
            if (go == null) return null;
            var trail = go.GetComponent<RedTrail>();
            if (trail == null) trail = go.AddComponent<RedTrail>();
            trail.anchored = false;
            return trail;
        }

        private void OnEnable() => anchored = false;

        // LateUpdate, not Update: the sim moves the throw earlier in the frame (and
        // the guest eases its replica there too), so the dash is dropped where the
        // knife actually ended up rather than a frame behind it.
        private void LateUpdate()
        {
            Vector3 here = transform.position;
            if (!anchored)
            {
                anchor = here;
                anchored = true;
                return;
            }

            Vector3 travelled = here - anchor;
            if (travelled.sqrMagnitude < RedTrailLine.Step * RedTrailLine.Step) return;
            Drop(anchor, travelled.normalized, travelled.magnitude);
            anchor = here;
        }

        private void Drop(Vector3 from, Vector3 dir, float length)
        {
            if (line == null)
            {
                Transform holder = transform.parent;
                if (holder == null) return;
                line = RedTrailLine.For(holder);
            }
            line?.Drop(from, dir, length);
        }
    }

    /// <summary>
    /// The pool of dashes one machine's thrown knives share, plus the fade that
    /// retires them. One line per effect root, so a match full of throws never grows
    /// the hierarchy beyond <see cref="Segments"/> slabs however many knives are in
    /// the air.
    /// </summary>
    internal sealed class RedTrailLine : MonoBehaviour
    {
        /// <summary>How far a knife travels before it drops another dash.</summary>
        public const float Step = 0.26f;

        // The same ink as the dash line - the same crimson, the same width, the same
        // thickness - laid shorter: a knife mark is 0.30 m where a dash is 1.1 m, and
        // it lives 0.10 s where a dash lives 0.30 s. Life is what sets how long the
        // streak looks, and the dagger is the fastest thing in the arena, so the
        // shorter life is what keeps its line shorter than the dash's own: the knife
        // runs its streak out at 36 m/s x 0.10 s = 3.6 m against the dash's
        // 18 m/s x 0.30 s = 5.4 m, however fast the knife moves.
        private const float MaxSpan = 0.30f;
        private const float Fade = 0.10f;
        private const float Width = 0.16f;
        private const float Thickness = 0.02f;
        private const int Segments = 24;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        /// <summary>The shared line under <paramref name="parent"/>, built on first use.</summary>
        public static RedTrailLine For(Transform parent)
        {
            Transform found = parent.Find(nameof(RedTrailLine));
            if (found != null)
            {
                var existing = found.GetComponent<RedTrailLine>();
                if (existing != null) return existing;
            }
            var go = new GameObject(nameof(RedTrailLine));
            go.transform.SetParent(parent, false);
            return go.AddComponent<RedTrailLine>();
        }

        private readonly Transform[] dashes = new Transform[Segments];
        private readonly float[] born = new float[Segments];
        private int next;

        /// <summary>Lays the marks a knife just travelled over, the whole way.</summary>
        public void Drop(Vector3 from, Vector3 dir, float length)
        {
            if (dir.sqrMagnitude < 0.0001f || length < 0.01f) return;
            // A knife covers 0.60 m between frames at 36 m/s - two marks' worth - so
            // the segment it just crossed is filled with as many marks as it takes.
            // One mark pinned per frame would leave the line dotted, and a dotted
            // line is not the line the dash draws. A jump too big to fill (a hitch, a
            // replica that respawned across the arena) is capped at the pool: that is
            // a gap in the line, never a smear across the arena.
            int marks = Mathf.Clamp(Mathf.CeilToInt(length / MaxSpan), 1, Segments);
            float step = length / marks;
            float span = Mathf.Min(step, MaxSpan);
            for (int i = 0; i < marks; i++)
                Lay(from + dir * (step * (i + 0.5f)), dir, span);
        }

        /// <summary>One mark of the line, drawn over the ground the knife covered.</summary>
        private void Lay(Vector3 centre, Vector3 dir, float span)
        {
            Transform dash = dashes[next];
            if (dash == null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                DestroyComponent(go.GetComponent<Collider>());
                go.name = "KnifeTrail";
                go.transform.SetParent(transform, false);
                // Every dash owns its own translucent red, so the fade below can
                // dim it without touching the other dashes - and it dies with the
                // slab that carries it, never outliving the match.
                OwnedMaterial.Assign(go, OwnedMaterial.TransparentUnlit(RedTrail.LineRed));
                dash = go.transform;
                dashes[next] = dash;
            }

            dash.gameObject.SetActive(true);
            dash.position = centre;
            dash.rotation = Quaternion.LookRotation(dir, Vector3.up);
            dash.localScale = new Vector3(Width, Thickness, span);
            born[next] = Time.time;
            next = (next + 1) % Segments;
        }

        /// <summary>
        /// Fades each dash on its own clock, so a line outlives the knife that drew
        /// it, and shrinks it as it goes - the same read as the dash's line.
        /// </summary>
        private void Update()
        {
            float now = Time.time;
            for (int i = 0; i < dashes.Length; i++)
            {
                Transform dash = dashes[i];
                if (dash == null || !dash.gameObject.activeSelf) continue;
                float k = 1f - (now - born[i]) / Fade;
                if (k <= 0f)
                {
                    dash.gameObject.SetActive(false);
                    continue;
                }

                Vector3 scale = dash.localScale;
                dash.localScale = new Vector3(Width * (0.35f + 0.65f * k), Thickness, scale.z);
                var renderer = dash.GetComponent<Renderer>();
                Material material = renderer != null ? renderer.sharedMaterial : null;
                if (material == null) continue;
                Color tint = new Color(RedTrail.LineRed.r, RedTrail.LineRed.g,
                    RedTrail.LineRed.b, RedTrail.LineRed.a * k);
                if (material.HasProperty(BaseColorId)) material.SetColor(BaseColorId, tint);
                if (material.HasProperty(ColorId)) material.SetColor(ColorId, tint);
            }
        }

        private static void DestroyComponent(Component c)
        {
            if (c == null) return;
            if (Application.isPlaying) Destroy(c);
            else DestroyImmediate(c);
        }
    }
}
