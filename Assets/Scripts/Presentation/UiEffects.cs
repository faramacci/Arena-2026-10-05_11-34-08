using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.Presentation
{
    /// <summary>
    /// Continuous scale breathing with an optional one-shot overshoot pop.
    /// Used on the start button, selected cards and the countdown readout so
    /// key affordances read as "alive" without a tween dependency.
    ///
    /// It only writes localScale, so layout groups and anchors keep owning
    /// size and position.
    /// </summary>
    [DisallowMultipleComponent]
    public class UiPulse : MonoBehaviour
    {
        [SerializeField] private float idleAmplitude = 0.02f;
        [SerializeField] private float idleSpeed = 2.4f;
        [SerializeField] private float amplifiedAmplitude = 0.055f;

        private RectTransform rect;
        private float phase;
        private bool amplified;
        private float pop;

        private void Awake()
        {
            rect = (RectTransform)transform;
            phase = Random.value * Mathf.PI * 2f;
        }

        /// <summary>Deeper breathing for the currently-selected affordance.</summary>
        public void SetAmplified(bool on) => amplified = on;

        /// <summary>One-shot overshoot; safe to spam (it takes the max).</summary>
        public void PlayPop(float strength = 0.18f)
        {
            pop = Mathf.Max(pop, strength);
        }

        private void Update()
        {
            if (rect == null) return;

            float amp = amplified ? amplifiedAmplitude : idleAmplitude;
            float s = 1f + amp * Mathf.Sin((Time.unscaledTime + phase) * idleSpeed);

            if (pop > 0.0001f)
            {
                // Decay the overshoot with an ease-out so the pop snaps then settles.
                pop = Mathf.MoveTowards(pop, 0f, Time.unscaledDeltaTime * 0.9f);
                s += pop;
            }

            rect.localScale = new Vector3(s, s, 1f);
        }
    }

    /// <summary>
    /// Slow Lissajous drift for background glows, plus optional alpha breathing
    /// on a Graphic. Every instance gets a random phase, so a handful of these
    /// reads as an organically shifting backdrop rather than a loop.
    /// </summary>
    [DisallowMultipleComponent]
    public class UiDrift : MonoBehaviour
    {
        [SerializeField] private float radius = 26f;
        [SerializeField] private float speed = 0.35f;
        [SerializeField] private float alphaMin = 0.55f;
        [SerializeField] private float alphaMax = 1f;

        private RectTransform rect;
        private Graphic graphic;
        private Vector2 origin;
        private float phase;
        private float alphaBase = 1f;

        private void Awake()
        {
            rect = (RectTransform)transform;
            graphic = GetComponent<Graphic>();
            origin = rect.anchoredPosition;
            phase = Random.value * Mathf.PI * 2f;
            if (graphic != null) alphaBase = graphic.color.a;
        }

        /// <summary>
        /// Sets the motion envelope. Call right after AddComponent and before the
        /// first Update so the phase starts at rest.
        /// </summary>
        public void Configure(float driftRadius, float driftSpeed, float minAlpha, float maxAlpha)
        {
            radius = driftRadius;
            speed = driftSpeed;
            alphaMin = minAlpha;
            alphaMax = maxAlpha;
            if (graphic != null) alphaBase = graphic.color.a;
        }

        private void Update()
        {
            if (rect == null) return;

            float t = Time.unscaledTime * speed + phase;
            rect.anchoredPosition = origin + new Vector2(
                Mathf.Sin(t * 1.13f) * radius,
                Mathf.Cos(t * 0.87f) * radius * 0.6f);

            if (graphic != null)
            {
                float k = 0.5f + 0.5f * Mathf.Sin(t * 1.7f);
                var c = graphic.color;
                c.a = alphaBase * Mathf.Lerp(alphaMin, alphaMax, k);
                graphic.color = c;
            }
        }

        /// <summary>Re-anchor after the layout moved under us (e.g. on rebuild).</summary>
        public void Rebase()
        {
            if (rect != null) origin = rect.anchoredPosition;
        }
    }

    /// <summary>
    /// Fade + slide enter for a menu group. Nothing here touches layout size,
    /// so it composes with VerticalLayoutGroup / HorizontalLayoutGroup.
    /// </summary>
    [DisallowMultipleComponent]
    public class UiEnter : MonoBehaviour
    {
        [SerializeField] private float seconds = 0.30f;
        [SerializeField] private float slideFrom = 34f;
        [SerializeField] private float startAlpha = 0f;

        private RectTransform rect;
        private CanvasGroup group;
        private Vector2 target;
        private Coroutine running;

        private void Awake()
        {
            rect = (RectTransform)transform;
            group = UiFactory.EnsureCanvasGroup(gameObject);
            target = rect.anchoredPosition;
            // Start hidden so a group never flashes at full opacity for a frame.
            group.alpha = startAlpha;
        }

        /// <summary>Replays the fade/slide. <paramref name="delaySeconds"/> staggers a column.</summary>
        public void Play(float delaySeconds = 0f)
        {
            if (!isActiveAndEnabled) { if (group != null) group.alpha = 1f; return; }
            if (running != null) StopCoroutine(running);
            running = StartCoroutine(Routine(delaySeconds));
        }

        private IEnumerator Routine(float delay)
        {
            if (delay > 0f) yield return new WaitForSecondsRealtime(delay);

            float t = 0f;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / seconds);
                float e = 1f - Mathf.Pow(1f - k, 3f);   // ease-out cubic
                group.alpha = Mathf.Lerp(startAlpha, 1f, e);
                if (rect != null)
                    rect.anchoredPosition = target + new Vector2(0f, (1f - e) * slideFrom);
                yield return null;
            }

            group.alpha = 1f;
            if (rect != null) rect.anchoredPosition = target;
            running = null;
        }
    }
}
