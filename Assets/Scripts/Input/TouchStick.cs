using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Arena.Inputs
{
    /// <summary>
    /// Virtual thumbstick for phones. Driven by the EventSystem (so it works for
    /// both touch and mouse in the editor) and reports a normalized vector in
    /// [-1, 1] on both axes. The knob is clamped to the pad radius, and the
    /// stick springs back to centre on release.
    ///
    /// We deliberately use pointer events rather than Input System touch state:
    /// InputSystemUIInputModule already routes both through the same path, which
    /// keeps one code path for device testing and for device.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class TouchStick : MonoBehaviour,
        IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        [SerializeField] private RectTransform knob;
        [SerializeField] private float deadZone = 0.12f;
        [SerializeField] private float smoothTime = 0.06f;

        private RectTransform pad;
        private bool dragging;
        private int activePointerId;
        private Vector2 rawInput;
        private Vector2 smoothed;
        private Vector2 vel;

        /// <summary>Unit-ish direction the player wants to move. Zero when idle.</summary>
        public Vector2 Value => smoothed.magnitude < deadZone ? Vector2.zero : smoothed;

        /// <summary>True while a finger is on the pad — used to gate aim logic.</summary>
        public bool IsDragging => dragging;

        private void Awake()
        {
            pad = GetComponent<RectTransform>();
            if (knob == null && transform.childCount > 0)
                knob = transform.GetChild(0) as RectTransform;
            ResetKnob();
        }

        public void OnPointerDown(PointerEventData e)
        {
            if (dragging) return;
            dragging = true;
            activePointerId = e.pointerId;
            Apply(e.position, e.pressEventCamera);
        }

        public void OnDrag(PointerEventData e)
        {
            if (!dragging || e.pointerId != activePointerId) return;
            Apply(e.position, e.pressEventCamera);
        }

        public void OnPointerUp(PointerEventData e)
        {
            if (!dragging || e.pointerId != activePointerId) return;
            dragging = false;
            rawInput = Vector2.zero;
        }

        private void OnDisable() => ResetStick();

        private void Apply(Vector2 screenPos, Camera uiCamera)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                pad, screenPos, uiCamera, out Vector2 local)) return;
            // Local coordinates are relative to the pivot, not the rect centre.
            local -= pad.rect.center;

            Vector2 half = pad.rect.size * 0.5f;
            if (half.x <= 0f || half.y <= 0f) return;

            // Normalise by half-size so the stick reaches 1.0 at the pad rim on
            // both axes even though the pad is a circle in screen space.
            Vector2 v = new Vector2(local.x / half.x, local.y / half.y);
            if (v.magnitude > 1f) v = v.normalized;

            rawInput = v;

            if (knob != null)
                knob.anchoredPosition = v * half * 0.72f;
        }

        private void ResetKnob()
        {
            if (knob != null) knob.anchoredPosition = Vector2.zero;
        }

        private void Update() => StepInput(Time.deltaTime);

        private void StepInput(float dt)
        {
            // Spring the knob home when nobody is touching the pad.
            if (!dragging && knob != null && knob.anchoredPosition != Vector2.zero)
                knob.anchoredPosition = Vector2.Lerp(knob.anchoredPosition, Vector2.zero, 1f - Mathf.Exp(-14f * dt));

            // Light smoothing stops the movement feeling twitchy on glass.
            smoothed = Vector2.SmoothDamp(smoothed, rawInput, ref vel,
                smoothTime, Mathf.Infinity, dt);

            // Apply the dead zone on read, not to the smoother's internal state.
            // Resetting smoothed each frame can prevent small inputs from converging.
            smoothed = Vector2.ClampMagnitude(smoothed, 1f);
        }

        /// <summary>Hard reset, used when a match starts or the menu opens.</summary>
        public void ResetStick()
        {
            dragging = false;
            rawInput = Vector2.zero;
            smoothed = Vector2.zero;
            vel = Vector2.zero;
            ResetKnob();
        }
    }

    /// <summary>
    /// One-shot ability button. Reports the slot it is bound to and flashes its
    /// cooldown fill so the HUD can mirror the prototype's keyboard tiles.
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class AbilityButton : MonoBehaviour
    {
        [SerializeField] private int slot;
        [SerializeField] private Image cooldownFill;

        public int Slot => slot;
        public event Action<int> Pressed;

        private Button button;

        private void Awake()
        {
            button = GetComponent<Button>();
            button.onClick.AddListener(() => Pressed?.Invoke(slot));
        }

        public void Bind(int abilitySlot) => slot = abilitySlot;

        /// <summary>Code-driven binding: the HUD builds its tile in code, so
        /// both the slot and the cooldown wipe image are handed over here.</summary>
        public void Bind(int abilitySlot, Image fill)
        {
            slot = abilitySlot;
            cooldownFill = fill;
        }

        /// <summary>0 = ready, 1 = just spent.</summary>
        public void SetCooldown(float remaining, float total)
        {
            if (cooldownFill == null) return;
            float pct = total <= 0f ? 0f : Mathf.Clamp01(remaining / total);
            cooldownFill.fillAmount = pct;
            cooldownFill.enabled = pct > 0.001f;
        }

        public void SetInteractable(bool ready) => button.interactable = ready;
    }
}
