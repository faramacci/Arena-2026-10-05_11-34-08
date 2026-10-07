using System.Collections;
using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// Lightweight card animation with no tween dependency:
    /// a staggered pop-in (ease-out-back) when the menu opens, then a subtle
    /// idle breathing scale. The selected card breathes deeper so the pick
    /// stays readable.
    ///
    /// It only touches localScale, never sizeDelta, so the menu's layout
    /// groups keep owning the card's size.
    /// </summary>
    [DisallowMultipleComponent]
    public class UiCardAnimator : MonoBehaviour
    {
        [SerializeField] private float enterSeconds = 0.34f;
        [SerializeField] private float enterFromScale = 0.80f;
        [SerializeField] private float idleAmplitude = 0.018f;
        [SerializeField] private float idleSpeed = 2.1f;
        [SerializeField] private float selectedAmplitude = 0.045f;

        private RectTransform rect;
        private float phase;
        private bool entering;
        private bool selected;

        private void Awake()
        {
            rect = (RectTransform)transform;
            phase = Random.value * Mathf.PI * 2f;
        }

        public void SetSelected(bool on) => selected = on;

        /// <summary>Replays the pop-in; delay staggers a row of cards.</summary>
        public void PlayEnter(float delaySeconds)
        {
            if (!isActiveAndEnabled) return;
            StopAllCoroutines();
            StartCoroutine(EnterRoutine(delaySeconds));
        }

        private IEnumerator EnterRoutine(float delay)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);

            entering = true;
            float t = 0f;
            while (t < enterSeconds)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / enterSeconds);
                float s = Mathf.LerpUnclamped(enterFromScale, 1f, EaseOutBack(k));
                if (rect != null) rect.localScale = Vector3.one * s;
                yield return null;
            }

            if (rect != null) rect.localScale = Vector3.one;
            entering = false;
        }

        private void Update()
        {
            if (entering || rect == null) return;

            float amp = selected ? selectedAmplitude : idleAmplitude;
            float s = 1f + amp * Mathf.Sin((Time.unscaledTime + phase) * idleSpeed);
            rect.localScale = new Vector3(s, s, 1f);
        }

        /// <summary>Standard ease-out-back, so the card overshoots then settles.</summary>
        private static float EaseOutBack(float x)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            float xm = x - 1f;
            return 1f + c3 * xm * xm * xm + c1 * xm * xm;
        }
    }
}
