using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Arena.Core;
using Arena.Inputs;

namespace Arena.Presentation
{
    /// <summary>
    /// In-match touch HUD for phones: a thumbstick bottom-left, a round skill
    /// dial cluster bottom-right (Mobile Legends-shaped: the primary spell is the
    /// big dial in the corner, the other two step up and to the left) with a
    /// radial cooldown sweep, a vertical wipe and a ready flash, a local-player
    /// health plate top-left, and a menu button on top. The opponents' health
    /// still lives on the floating nameplates.
    ///
    /// Everything is built in code (like the menu) so the layout survives
    /// refactors, and every label goes through <see cref="UiFactory"/> so the
    /// whole HUD shares one font, size and colour set.
    /// </summary>
    public class TouchHudView : MonoBehaviour
    {
        /// <summary>Fired with the ability slot (0..2) when a tile is tapped.</summary>
        public event Action<int> AbilityPressed;

        /// <summary>Phones have no Escape key — the HUD carries its own exit.</summary>
        public event Action MenuRequested;

        private TouchStick stick;

        private readonly AbilityButton[] buttons = new AbilityButton[3];
        private readonly Image[] cooldownFills = new Image[3];
        private readonly Image[] cooldownRings = new Image[3];
        private readonly Image[] tileGlows = new Image[3];
        private readonly Image[] tilePlates = new Image[3];
        private readonly Image[] tileIcons = new Image[3];
        private readonly UiPulse[] tilePulses = new UiPulse[3];
        private readonly TextMeshProUGUI[] cooldownNumbers = new TextMeshProUGUI[3];
        private readonly TextMeshProUGUI[] abilityNames = new TextMeshProUGUI[3];
        private readonly TextMeshProUGUI[] tileGlyphs = new TextMeshProUGUI[3];

        /// <summary>Character colour the dials are tinted with.</summary>
        private Color abilityTint = new Color(0.50f, 0.83f, 1f, 1f);

        // Cached cooldown values; applied to the widgets every frame so the
        // HUD can be shown/hidden or rebound without losing state.
        private readonly float[] cooldownLeft = new float[3];
        private readonly float[] cooldownTotal = new float[3];
        private readonly bool[] wasCooling = new bool[3];

        // --- local player health plate ---
        private Image localHpFill;
        private Image localHpGhost;
        private TextMeshProUGUI localHpPct;
        private TextMeshProUGUI localName;
        private Image localAccent;
        private float hpActual = 1f;
        private float hpGhost = 1f;

        /// <summary>Stick vector in [-1,1]; zero when nobody is touching it.</summary>
        public Vector2 MoveInput => stick != null ? stick.Value : Vector2.zero;

        /// <summary>True while a finger is down on the pad.</summary>
        public bool IsTouchingStick => stick != null && stick.IsDragging;

        // ------------------------------------------------------------------ build

        public static TouchHudView Build(Transform canvas)
        {
            var root = UiFactory.CreateRect("TouchHud", canvas);
            UiFactory.CreateStretch(root);

            var hud = root.gameObject.AddComponent<TouchHudView>();
            hud.BuildHealthPlate(root);
            hud.stick = BuildStick(root);
            hud.BuildAbilityRow(root);
            hud.BuildMenuButton(root);
            hud.Hide();
            return hud;
        }

        /// <summary>Local player's name + health, top-left, with a trailing ghost bar.</summary>
        private void BuildHealthPlate(RectTransform root)
        {
            var plate = UiFactory.CreateGradient("HealthPlate", root,
                new Color(0.07f, 0.12f, 0.22f, 0.94f), new Color(0.03f, 0.06f, 0.12f, 0.94f));
            UiFactory.AddOutline(plate, UiFactory.Stroke, 2f);
            var prt = (RectTransform)plate.transform;
            prt.anchorMin = prt.anchorMax = new Vector2(0f, 1f);
            prt.pivot = new Vector2(0f, 1f);
            prt.anchoredPosition = new Vector2(26f, -22f);
            prt.sizeDelta = new Vector2(340f, 74f);

            localAccent = UiFactory.CreatePanel("Accent", prt, UiFactory.Accent);
            UiFactory.UseRounded(localAccent);
            var art = (RectTransform)localAccent.transform;
            art.anchorMin = new Vector2(0f, 0f);
            art.anchorMax = new Vector2(0f, 1f);
            art.pivot = new Vector2(0f, 0.5f);
            art.anchoredPosition = new Vector2(10f, 0f);
            art.sizeDelta = new Vector2(6f, -20f);

            localName = UiFactory.CreateLabel("Who", prt, "PLAYER 1", 17f,
                UiFactory.TextPrimary, TextAlignmentOptions.Left);
            localName.fontStyle = FontStyles.Bold;
            var nrt = (RectTransform)localName.transform;
            nrt.anchorMin = new Vector2(0f, 1f);
            nrt.anchorMax = new Vector2(1f, 1f);
            nrt.pivot = new Vector2(0f, 1f);
            nrt.offsetMin = new Vector2(28f, -30f);
            nrt.offsetMax = new Vector2(-14f, -8f);

            var track = UiFactory.CreatePanel("HpTrack", prt, new Color(0.02f, 0.04f, 0.08f, 1f));
            UiFactory.UseRounded(track);
            var trt = (RectTransform)track.transform;
            trt.anchorMin = new Vector2(0f, 0f);
            trt.anchorMax = new Vector2(1f, 0f);
            trt.pivot = new Vector2(0f, 0f);
            trt.offsetMin = new Vector2(28f, 12f);
            trt.offsetMax = new Vector2(-14f, 38f);
            UiFactory.AddOutline(track, UiFactory.Stroke, 1.5f);

            localHpGhost = UiFactory.CreateRect("Ghost", trt).gameObject.AddComponent<Image>();
            ConfigureFill(localHpGhost, null, UiFactory.Warn, 1f);
            var grt2 = (RectTransform)localHpGhost.transform;
            UiFactory.CreateStretch(grt2, 2f);

            localHpFill = UiFactory.CreateRect("Fill", trt).gameObject.AddComponent<Image>();
            ConfigureFill(localHpFill, null, UiFactory.Good, 1f);
            var frt = (RectTransform)localHpFill.transform;
            UiFactory.CreateStretch(frt, 2f);

            localHpPct = UiFactory.CreateLabel("Pct", trt, "100%", 15f, Color.white,
                TextAlignmentOptions.Right);
            localHpPct.fontStyle = FontStyles.Bold;
            var prt2 = (RectTransform)localHpPct.transform;
            prt2.anchorMin = new Vector2(1f, 0f);
            prt2.anchorMax = new Vector2(1f, 1f);
            prt2.pivot = new Vector2(1f, 0.5f);
            prt2.anchoredPosition = new Vector2(-10f, 0f);
            prt2.sizeDelta = new Vector2(70f, 0f);
        }

        private static void ConfigureFill(Image img, Sprite sprite, Color color, float amount)
        {
            img.sprite = sprite != null ? sprite : UiFactory.RoundedSprite;
            img.type = Image.Type.Filled;
            img.fillMethod = Image.FillMethod.Horizontal;
            img.fillOrigin = (int)Image.OriginHorizontal.Left;
            img.color = color;
            img.raycastTarget = false;
            img.fillAmount = amount;
        }

        private void BuildMenuButton(RectTransform root)
        {
            var img = UiFactory.CreateGradient("ExitToMenu", root,
                new Color(0.10f, 0.18f, 0.32f, 0.95f), new Color(0.04f, 0.08f, 0.16f, 0.95f));
            img.raycastTarget = true;
            UiFactory.AddOutline(img, UiFactory.Stroke, 2f);

            var rt = (RectTransform)img.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -22f);
            rt.sizeDelta = new Vector2(128f, 48f);

            img.gameObject.AddComponent<Button>();
            var button = img.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = img;
            button.onClick.AddListener(() => MenuRequested?.Invoke());

            var label = UiFactory.CreateLabel("L", rt, "MENU", 16f,
                UiFactory.TextDim, TextAlignmentOptions.Center);
            label.characterSpacing = 2f;
            UiFactory.CreateStretch((RectTransform)label.transform);
        }

        private static TouchStick BuildStick(RectTransform root)
        {
            var pad = UiFactory.CreateRect("MoveStick", root);
            pad.anchorMin = pad.anchorMax = new Vector2(0f, 0f);
            pad.pivot = new Vector2(0f, 0f);
            pad.anchoredPosition = new Vector2(44f, 44f);
            pad.sizeDelta = new Vector2(220f, 220f);

            var bg = pad.gameObject.AddComponent<Image>();
            bg.sprite = UiFactory.RoundedSprite;
            bg.type = Image.Type.Sliced;
            bg.color = new Color(0.05f, 0.10f, 0.20f, 0.50f);
            bg.raycastTarget = true;

            // Knob must stay the FIRST child: TouchStick falls back to child 0,
            // and Awake runs the moment the component is added below.
            var knob = UiFactory.CreateRect("Knob", pad);
            knob.anchorMin = knob.anchorMax = new Vector2(0.5f, 0.5f);
            knob.pivot = new Vector2(0.5f, 0.5f);
            knob.sizeDelta = new Vector2(96f, 96f);
            var knobImg = knob.gameObject.AddComponent<Image>();
            knobImg.sprite = UiFactory.RoundedSprite;
            knobImg.type = Image.Type.Sliced;
            knobImg.color = new Color(0.58f, 0.86f, 1f, 0.90f);
            knobImg.raycastTarget = false;
            var knobRing = UiFactory.CreateRing("KnobRing", knob, new Color(1f, 1f, 1f, 0.45f), 104f);
            var krr = (RectTransform)knobRing.transform;
            krr.anchorMin = krr.anchorMax = new Vector2(0.5f, 0.5f);
            krr.anchoredPosition = Vector2.zero;

            var rim = UiFactory.CreateRing("Rim", pad, new Color(0.55f, 0.80f, 1f, 0.35f), 210f);
            var rrt = (RectTransform)rim.transform;
            rrt.anchorMin = rrt.anchorMax = new Vector2(0.5f, 0.5f);
            rrt.anchoredPosition = Vector2.zero;

            var stick = pad.gameObject.AddComponent<TouchStick>();

            // Under-glow last: Awake already cached the knob, so inserting this
            // as sibling 0 only changes draw order, not the stick's reference.
            var glow = UiFactory.CreateGlow("StickGlow", pad, new Color(0.35f, 0.70f, 1f, 0.22f), 300f);
            var grt = (RectTransform)glow.transform;
            grt.anchorMin = grt.anchorMax = new Vector2(0.5f, 0.5f);
            grt.anchoredPosition = Vector2.zero;
            grt.SetAsFirstSibling();

            return stick;
        }

        /// <summary>
        /// Mobile Legends-shaped skill dial, bottom-right: the three spells sit on
        /// a quarter-circle <em>fan</em> around the corner rather than on a
        /// diagonal line, so the whole kit lands under the sweep of the right
        /// thumb. The primary is the lowest and furthest out (13° above the bottom
        /// edge), the ultimate rides up the right edge (76°), and the dials are
        /// near-tangent but never overlapping.
        /// </summary>
        private void BuildAbilityRow(RectTransform root)
        {
            var cluster = UiFactory.CreateRect("AbilityCluster", root);
            cluster.anchorMin = cluster.anchorMax = new Vector2(1f, 0f);
            cluster.pivot = new Vector2(1f, 0f);
            cluster.anchoredPosition = Vector2.zero;
            cluster.sizeDelta = new Vector2(520f, 340f);

            // Diameter and centre of each dial, measured in from the corner:
            // r ≈ 265 at 13°, 45° and 76°. Each one is pulled just far enough off
            // the screen edge that no dial (and no name label) gets sliced off.
            var dials = new (float size, Vector2 at)[]
            {
                (130f, new Vector2(-244f, 69f)),
                (124f, new Vector2(-174f, 174f)),
                (118f, new Vector2(-68f, 257f)),
            };

            for (int i = 0; i < dials.Length; i++)
            {
                int slot = i;
                float size = dials[i].size;

                var tile = UiFactory.CreateRect("Ability_" + i, cluster);
                var trt = (RectTransform)tile.transform;
                trt.anchorMin = trt.anchorMax = new Vector2(1f, 0f);
                trt.pivot = new Vector2(0.5f, 0.5f);
                trt.sizeDelta = new Vector2(size, size);
                trt.anchoredPosition = dials[i].at;

                // Soft bloom behind the dial; it lights up on the ready flash.
                tileGlows[i] = UiFactory.CreateGlow("Glow", tile, new Color(0.5f, 0.8f, 1f, 0.0f), size * 1.5f);
                Centre(tileGlows[i].transform);

                // The plate is the tap target and the dial's dark base.
                var plate = tile.gameObject.AddComponent<Image>();
                UiFactory.UseDisc(plate, new Color(0.07f, 0.12f, 0.22f, 0.94f));
                plate.raycastTarget = true;
                tilePlates[i] = plate;

                // Coloured core, tinted with the character's own colour.
                var icon = UiFactory.CreateRect("Icon", tile).gameObject.AddComponent<Image>();
                UiFactory.UseDisc(icon, UiFactory.Accent);
                Centre(icon.transform, 0.80f);
                tileIcons[i] = icon;

                // The ability's initial, so each dial is identifiable without art.
                var glyph = UiFactory.CreateLabel("Glyph", tile, string.Empty,
                    size * 0.34f, new Color(0.04f, 0.07f, 0.14f, 0.92f), TextAlignmentOptions.Center);
                glyph.fontStyle = FontStyles.Bold;
                Centre(glyph.transform);
                tileGlyphs[i] = glyph;

                // Dark radial sweep + vertical wipe: the two cooldown reads the
                // game has always used, now over a circular icon.
                var sweepRt = UiFactory.CreateRect("CdSweep", tile);
                Centre(sweepRt, 0.94f);
                var sweep = sweepRt.gameObject.AddComponent<Image>();
                UiFactory.UseDisc(sweep, new Color(0.01f, 0.03f, 0.07f, 0.72f));
                sweep.type = Image.Type.Filled;
                sweep.fillMethod = Image.FillMethod.Radial360;
                sweep.fillOrigin = (int)Image.Origin360.Top;
                sweep.fillClockwise = true;
                sweep.fillAmount = 0f;
                sweep.raycastTarget = false;
                sweep.enabled = false;
                cooldownRings[i] = sweep;

                var fillRt = UiFactory.CreateRect("Cooldown", tile);
                Centre(fillRt, 0.94f);
                var fill = fillRt.gameObject.AddComponent<Image>();
                UiFactory.UseDisc(fill, new Color(0.0f, 0.02f, 0.06f, 0.56f));
                fill.type = Image.Type.Filled;
                fill.fillMethod = Image.FillMethod.Vertical;
                fill.fillOrigin = (int)Image.OriginVertical.Top;
                fill.raycastTarget = false;
                fill.enabled = false;
                cooldownFills[i] = fill;

                var number = UiFactory.CreateLabel("Cd", tile, string.Empty, size * 0.30f,
                    Color.white, TextAlignmentOptions.Center);
                number.fontStyle = FontStyles.Bold;
                Centre(number.transform, 1f, new Vector2(0f, size * 0.02f));
                cooldownNumbers[i] = number;

                // Light rim last so it sits over the sweep and keeps the dial crisp.
                var rim = UiFactory.CreateRing("Rim", tile, new Color(0.62f, 0.86f, 1f, 0.55f), size * 1.06f);
                Centre(rim.transform);

                // The fan is tight enough that there is no room underneath the
                // lowest dial, so every name sits out to the left of its dial.
                var name = UiFactory.CreateLabel("AbName", tile, string.Empty, 12f,
                    UiFactory.TextDim, TextAlignmentOptions.Right);
                SetRect((RectTransform)name.transform, new Vector2(0.5f, 0.5f),
                    new Vector2(-(size * 1.15f + 10f), 0f), new Vector2(size * 1.3f, 22f));
                abilityNames[i] = name;

                var slotBadge = UiFactory.CreateRect("Slot", tile).gameObject.AddComponent<Image>();
                UiFactory.UseDisc(slotBadge, new Color(0.30f, 0.52f, 0.86f, 0.95f));
                var sbrt = (RectTransform)slotBadge.transform;
                sbrt.anchorMin = sbrt.anchorMax = new Vector2(1f, 1f);
                sbrt.pivot = new Vector2(0.5f, 0.5f);
                sbrt.sizeDelta = new Vector2(30f, 30f);
                sbrt.anchoredPosition = new Vector2(-4f, -4f);
                var snum = UiFactory.CreateLabel("N", slotBadge.transform, (i + 1).ToString(),
                    14f, Color.white, TextAlignmentOptions.Center);
                UiFactory.CreateStretch((RectTransform)snum.transform);
                snum.fontStyle = FontStyles.Bold;

                var btn = tile.gameObject.AddComponent<AbilityButton>();
                // ColorTint would repaint the dial; the cooldown sweep is the
                // only visual state these buttons need.
                var selectable = tile.GetComponent<Button>();
                if (selectable != null)
                {
                    selectable.transition = Selectable.Transition.None;
                    selectable.targetGraphic = plate;
                }
                btn.Bind(slot, fill);
                btn.Pressed += s => AbilityPressed?.Invoke(s);
                buttons[i] = btn;
                tilePulses[i] = tile.gameObject.AddComponent<UiPulse>();
            }
        }

        /// <summary>Pins a child to the centre of its dial, at an optional scale.</summary>
        private static void Centre(Transform child, float fill = 1f, Vector2 offset = default)
        {
            var rt = (RectTransform)child;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = offset;
            if (fill < 1f)
            {
                var parent = (RectTransform)rt.parent;
                rt.sizeDelta = parent.sizeDelta * fill;
            }
        }

        private static void SetRect(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        // ------------------------------------------------------------------ api

        /// <summary>Points the tiles at a character's three abilities.</summary>
        public void BindCharacter(CharacterDef def)
        {
            if (def == null) return;
            for (int i = 0; i < 3 && i < def.Abilities.Length; i++)
            {
                cooldownTotal[i] = def.Abilities[i].Cooldown;
                cooldownLeft[i] = 0f;
                if (abilityNames[i] != null)
                    abilityNames[i].text = def.Abilities[i].DisplayName;
                if (tileGlyphs[i] != null)
                {
                    string n = def.Abilities[i].DisplayName;
                    tileGlyphs[i].text = string.IsNullOrEmpty(n)
                        ? (i + 1).ToString() : n.Substring(0, 1).ToUpperInvariant();
                }
                if (buttons[i] != null)
                    buttons[i].SetCooldown(0f, cooldownTotal[i]);
            }
            abilityTint = def.Color;
            SetLocalName(def.DisplayName.ToUpperInvariant(), def.Color);
        }

        /// <summary>Local player's name + accent colour on the health plate.</summary>
        public void SetLocalName(string displayName, Color color)
        {
            if (localName != null) localName.text = displayName;
            if (localAccent != null) localAccent.color = color;
        }

        /// <summary>Local player's health for the top-left plate.</summary>
        public void SetLocalHealth(float current, float max)
        {
            if (max <= 0f) max = GameConfig.MaxHealth;
            hpActual = Mathf.Clamp01(current / max);

            if (localHpFill != null)
                localHpFill.color = hpActual > 0.55f ? UiFactory.Good
                    : hpActual > 0.28f ? UiFactory.Warn : UiFactory.Bad;
            if (localHpPct != null)
                localHpPct.text = Mathf.CeilToInt(hpActual * 100f) + "%";
        }

        /// <summary>Report remaining cooldown for a slot (seconds).</summary>
        public void SetCooldown(int slot, float remaining)
        {
            if (slot < 0 || slot > 2) return;
            cooldownLeft[slot] = Mathf.Max(0f, remaining);
        }

        /// <summary>Enable/disable a tile (stunned, dead, casting…).</summary>
        public void SetAbilityReady(int slot, bool ready)
        {
            if (slot < 0 || slot > 2 || buttons[slot] == null) return;
            buttons[slot].SetInteractable(ready);
        }

        public void ResetInput()
        {
            if (stick != null) stick.ResetStick();
            for (int i = 0; i < 3; i++)
            {
                cooldownLeft[i] = 0f;
                wasCooling[i] = false;
                if (buttons[i] != null) buttons[i].SetInteractable(true);
            }
        }

        public void Show() => gameObject.SetActive(true);

        public void Hide() => gameObject.SetActive(false);

        // ------------------------------------------------------------------ frame

        private void Update()
        {
            for (int i = 0; i < 3; i++)
            {
                float total = cooldownTotal[i];
                float left = cooldownLeft[i];
                float pct = total <= 0f ? 0f : Mathf.Clamp01(left / total);
                bool cooling = pct > 0.001f;

                if (cooldownFills[i] != null)
                {
                    cooldownFills[i].fillAmount = pct;
                    cooldownFills[i].enabled = cooling;
                }

                if (cooldownRings[i] != null)
                {
                    cooldownRings[i].fillAmount = pct;
                    cooldownRings[i].enabled = cooling;
                }

                // Ready flash: the moment the last frame of cooldown drops off,
                // pop the tile and bloom it so the player notices.
                if (wasCooling[i] && !cooling && buttons[i] != null && buttons[i].gameObject.activeInHierarchy)
                {
                    if (tilePulses[i] != null) tilePulses[i].PlayPop(0.20f);
                    if (tileGlows[i] != null) tileGlows[i].color = new Color(0.55f, 0.9f, 1f, 0.55f);
                }
                wasCooling[i] = cooling;

                // Glow fades back down after the flash; dims while on cooldown.
                if (tileGlows[i] != null)
                {
                    var gc = tileGlows[i].color;
                    float target = cooling ? 0.10f : 0.28f;
                    gc.a = Mathf.MoveTowards(gc.a, target, Time.unscaledDeltaTime * (cooling ? 2.6f : 0.9f));
                    tileGlows[i].color = gc;
                }

                if (cooldownNumbers[i] != null)
                    cooldownNumbers[i].text = left > 0.05f
                        ? Mathf.CeilToInt(left).ToString()
                        : string.Empty;

                // The dial dims toward the plate while it recharges, which is
                // what makes a spent spell obvious at a glance on a small screen.
                if (tileIcons[i] != null)
                    tileIcons[i].color = cooling
                        ? Color.Lerp(abilityTint, new Color(0.07f, 0.10f, 0.18f, 1f), 0.62f)
                        : abilityTint;

                if (buttons[i] != null)
                    buttons[i].SetInteractable(left <= 0f);
            }

            // Ghost health trails the real bar so a hit is legible for a beat.
            if (localHpFill != null) localHpFill.fillAmount = hpActual;
            if (localHpGhost != null)
            {
                hpGhost = Mathf.MoveTowards(hpGhost, hpActual, Time.unscaledDeltaTime * 0.55f);
                if (hpGhost < hpActual) hpGhost = hpActual;
                localHpGhost.fillAmount = hpGhost;
            }
        }
    }
}
