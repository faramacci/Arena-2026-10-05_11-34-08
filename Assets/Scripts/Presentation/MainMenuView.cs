using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Arena.Core;

namespace Arena.Presentation
{
    /// <summary>
    /// Solo or two-device LAN menu. The host chooses the zone; each phone
    /// chooses its own character. Joining uses an explicit IPv4 address.
    ///
    /// The object graph keeps the historical names the regression harness
    /// addresses by path — <c>MenuContent/Seg_&lt;label&gt;/Seg/Opt&lt;i&gt;</c> and
    /// <c>MenuContent/StartWrap/Action_&lt;label&gt;</c> — so a visual overhaul never
    /// breaks <c>ArenaRegressionTests.LanMenuFlow</c>.
    /// </summary>
    public class MainMenuView : MonoBehaviour
    {
        public event Action<GameMode, ZoneShape, CharacterId, CharacterId> StartRequested;

        [SerializeField] private RectTransform content;

        private GameMode mode = GameMode.Bot;
        private ZoneShape zone = ZoneShape.Circle;
        private CharacterId pick1 = CharacterId.Vanguard;
        private CharacterId pick2 = CharacterId.Storm;
        private Button[] modeButtons;
        private Button[] zoneButtons;

        private TextMeshProUGUI p2Title;
        private Button startButton;
        private RectTransform p1Row;
        private RectTransform p2Row;
        private TMP_InputField hostAddress;
        private TextMeshProUGUI status;
        private TextMeshProUGUI startLabel;
        private UiPulse startPulse;
        private bool busy;
        public string HostAddress => hostAddress.text;
        /// <summary>Live zone choice: the host may change it while still waiting.</summary>
        public ZoneShape Zone => zone;
        public event Action CancelRequested;
        private RectTransform p2Column;
        private readonly List<UiCardAnimator> cardAnimators = new List<UiCardAnimator>();
        private UiEnter contentEnter;

        public static MainMenuView Build(Transform canvas)
        {
            var backdrop = UiFactory.CreatePanel("MenuBackdrop", canvas,
                new Color(0.015f, 0.03f, 0.07f, 1f));
            UiFactory.CreateStretch((RectTransform)backdrop.transform);

            BuildAtmosphere(backdrop.transform);

            var view = backdrop.gameObject.AddComponent<MainMenuView>();
            view.content = (RectTransform)backdrop.transform;
            view.BuildInternal();
            return view;
        }

        /// <summary>
        /// Layered gradient + drifting glow blobs. Purely decorative: every layer
        /// is raycast-transparent so the buttons above stay tappable.
        /// </summary>
        private static void BuildAtmosphere(Transform parent)
        {
            // Vertical wash: deep navy at the top easing into near-black.
            var wash = UiFactory.CreateGradient("AtmosWash", parent,
                new Color(0.06f, 0.12f, 0.24f, 1f), new Color(0.012f, 0.02f, 0.05f, 1f));
            UiFactory.CreateStretch((RectTransform)wash.transform);

            // Big soft key lights, each slowly wandering on its own phase.
            AddBlob(parent, "GlowCyan", new Color(0.28f, 0.62f, 1f, 0.30f),
                new Vector2(-360f, 210f), 900f, 34f, 0.20f);
            AddBlob(parent, "GlowViolet", new Color(0.55f, 0.36f, 1f, 0.22f),
                new Vector2(430f, -190f), 780f, 41f, 0.27f);
            AddBlob(parent, "GlowFrost", new Color(0.62f, 0.92f, 1f, 0.16f),
                new Vector2(120f, 330f), 620f, 29f, 0.34f);

            // Horizon band so the lower half reads as a frozen floor.
            var horizon = UiFactory.CreatePanel("Horizon", parent,
                new Color(0.22f, 0.52f, 0.88f, 0.10f));
            var hrt = (RectTransform)horizon.transform;
            hrt.anchorMin = new Vector2(0f, 0f);
            hrt.anchorMax = new Vector2(1f, 0f);
            hrt.pivot = new Vector2(0.5f, 0f);
            hrt.sizeDelta = new Vector2(0f, 180f);
            hrt.anchoredPosition = new Vector2(0f, 0f);
        }

        private static void AddBlob(Transform parent, string name, Color color,
            Vector2 anchoredPos, float size, float drift, float speed)
        {
            var glow = UiFactory.CreateGlow(name, parent, color, size);
            var rt = (RectTransform)glow.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            var driftFx = glow.gameObject.AddComponent<UiDrift>();
            driftFx.Configure(drift, speed, 0.6f, 1f);
        }

        private void BuildInternal()
        {
            var root = UiFactory.CreateRect("MenuContent", content);
            UiFactory.CreateStretch(root, 28f);

            // Whole-menu fade/slide, so opening the menu reads as a transition
            // rather than a hard cut. Applied to the stretched root (no layout
            // group owns its anchoredPosition) so nothing fights the animation.
            contentEnter = root.gameObject.AddComponent<UiEnter>();

            UiFactory.CreateVStack(root, 3f);

            // --- title -----------------------------------------------------
            // A soft accent bloom behind the wordmark.
            var titleGlow = UiFactory.CreateGlow("TitleGlow", root, new Color(0.42f, 0.72f, 1f, 0.30f), 760f);
            UiFactory.Pin((RectTransform)titleGlow.transform, -1f, 4f);

            var title = UiFactory.CreateLabel("Title", root, "FROZEN ARENA", 54f,
                UiFactory.Accent, TextAlignmentOptions.Center);
            title.fontStyle = FontStyles.Bold;
            title.characterSpacing = 6f;
            GradientText(title, UiFactory.Frost, UiFactory.AccentDeep);
            UiFactory.Pin((RectTransform)title.transform, -1f, 56f);
            var titlePulse = title.gameObject.AddComponent<UiPulse>();
            titlePulse.SetAmplified(true);

            var subtitle = UiFactory.CreateLabel("Subtitle", root,
                "Knock your rival out of the zone with your character's spells!   ·   " +
                "Outside the zone = constant damage   ·   Play the momentum on the ice.",
                17f, UiFactory.TextDim, TextAlignmentOptions.Center);
            UiFactory.Pin((RectTransform)subtitle.transform, -1f, 38f);

            // --- option rows ----------------------------------------------
            modeButtons = AddSegment(root, "GAME MODE", out _, new[]
            {
                ("Solo · Bot", (Action)(() => SetMode(GameMode.Bot))),
                ("Host · Hotspot", () => SetMode(GameMode.LanHost)),
                ("Join · Hotspot", () => SetMode(GameMode.LanJoin))
            }, 0);

            zoneButtons = AddSegment(root, "ZONE SHAPE", out _, new[]
            {
                ("Circle", (Action)(() => SetZone(ZoneShape.Circle))),
                ("Square", () => SetZone(ZoneShape.Square))
            }, 0);

            // A wrapper row keeps the field centred at a readable width instead of
            // letting the vertical stack stretch it edge to edge.
            var addressWrap = UiFactory.CreateRect("AddressWrap", root);
            UiFactory.Pin(addressWrap, -1f, 38f);

            var addressPanel = UiFactory.CreateGradient("HostAddress", addressWrap,
                new Color(0.09f, 0.14f, 0.26f, 1f), new Color(0.05f, 0.09f, 0.18f, 1f));
            addressPanel.raycastTarget = true;
            UiFactory.AddOutline(addressPanel, UiFactory.Stroke, 2f);
            var apRt = (RectTransform)addressPanel.transform;
            apRt.anchorMin = apRt.anchorMax = new Vector2(0.5f, 0.5f);
            apRt.pivot = new Vector2(0.5f, 0.5f);
            apRt.sizeDelta = new Vector2(560f, 38f);
            hostAddress = addressPanel.gameObject.AddComponent<TMP_InputField>();
            var addressText = UiFactory.CreateLabel("Text", addressPanel.transform, "", 20f, Color.white);
            UiFactory.CreateStretch((RectTransform)addressText.transform, 10f);
            hostAddress.textViewport = (RectTransform)addressPanel.transform;
            hostAddress.textComponent = addressText;
            var placeholder = UiFactory.CreateLabel("Placeholder", addressPanel.transform,
                "Join: enter host IPv4 address", 17f, UiFactory.TextDim);
            UiFactory.CreateStretch((RectTransform)placeholder.transform, 10f);
            hostAddress.placeholder = placeholder;
            hostAddress.characterLimit = 15;
            hostAddress.contentType = TMP_InputField.ContentType.Standard;
            hostAddress.keyboardType = TouchScreenKeyboardType.NumbersAndPunctuation;

            // --- character pickers ----------------------------------------
            var pickRow = UiFactory.CreateRect("PickRow", root);
            UiFactory.Pin(pickRow, -1f, 288f);
            UiFactory.CreateHStack(pickRow, 18f);

            p1Column = BuildPicker(pickRow, "PLAYER 1", ref p1Row, CharacterId.Vanguard, onPick1);
            p2Column = BuildPicker(pickRow, "PLAYER 2 · BOT", ref p2Row, CharacterId.Storm, onPick2);
            // First TMP in the column is the heading; keep that invariant.
            p2Title = p2Column.GetComponentInChildren<TextMeshProUGUI>();

            // --- start -----------------------------------------------------
            var startWrap = UiFactory.CreateRect("StartWrap", root);
            UiFactory.Pin(startWrap, -1f, 62f);
            startButton = MakeActionButton(startWrap, "START BATTLE",
                UiFactory.Accent, 58f, 400f);
            startLabel = startButton.GetComponentInChildren<TextMeshProUGUI>();
            startPulse = startButton.gameObject.AddComponent<UiPulse>();
            startButton.onClick.AddListener(() =>
            {
                if (busy) CancelRequested?.Invoke();
                else { startPulse.PlayPop(0.10f); StartRequested?.Invoke(mode, zone, pick1, pick2); }
            });

            var hint = UiFactory.CreateLabel("Hint", root,
                "Move with the touch stick · the three tiles at the bottom are your spells",
                15f, UiFactory.Muted, TextAlignmentOptions.Center);
            UiFactory.Pin((RectTransform)hint.transform, -1f, 20f);

            status = UiFactory.CreateLabel("NetworkStatus", root,
                "Hotspot: enable it in phone settings, then connect the other phone.",
                16f, UiFactory.Accent);
            UiFactory.Pin((RectTransform)status.transform, -1f, 40f);

            SetMode(GameMode.Bot);
        }

        private RectTransform p1Column;

        /// <summary>
        /// A labelled column of character cards. Returns the column so the
        /// caller can retarget the header (e.g. "PLAYER 2 · BOT").
        /// </summary>
        private RectTransform BuildPicker(Transform parent, string heading,
            ref RectTransform cardRow, CharacterId initial, Action<CharacterId> onPick)
        {
            var col = UiFactory.CreateRect(heading + "Col", parent);
            // Width is left unset (not 0 — a LayoutElement of 0 means "zero wide",
            // it does not mean "stretch"), so the stack computes it from the cards.
            UiFactory.Pin(col, -1f, 288f);
            UiFactory.CreateVStack(col, 8f);

            var head = UiFactory.CreateLabel(heading + "Head", col, heading, 19f,
                UiFactory.Accent, TextAlignmentOptions.Center);
            head.fontStyle = FontStyles.Bold;
            head.characterSpacing = 3f;
            UiFactory.Pin((RectTransform)head.transform, -1f, 26f);

            cardRow = UiFactory.CreateRect("Cards", col);
            UiFactory.Pin(cardRow, -1f, 254f);
            UiFactory.CreateHStack(cardRow, 12f);

            foreach (CharacterId id in Enum.GetValues(typeof(CharacterId)))
            {
                var card = BuildCard(cardRow, id, id == initial, onPick);
                if (id == initial) card.interactable = false; // pressed state
            }
            return col;
        }

        private Button BuildCard(RectTransform row, CharacterId id, bool selected,
            Action<CharacterId> onPick)
        {
            var def = GameConfig.Get(id);
            var img = UiFactory.CreateButton("Card_" + id, row,
                selected ? new Color(0.10f, 0.22f, 0.40f) : UiFactory.Card);
            var rt = (RectTransform)img.transform;
            UiFactory.Pin(rt, 176f, 254f);

            // Rounded gradient body — the selected card lifts to a brighter ramp.
            UiFactory.UseRoundedGradient(img,
                selected ? new Color(0.16f, 0.30f, 0.56f, 1f) : UiFactory.CardTop,
                selected ? new Color(0.08f, 0.16f, 0.32f, 1f) : UiFactory.CardBottom);

            var outline = rt.gameObject.AddComponent<Outline>();
            outline.effectColor = selected ? UiFactory.Accent : UiFactory.Stroke;
            outline.effectDistance = new Vector2(2, -2);

            // Pop-in on menu open + idle breathing; the picked card pulses.
            var animator = rt.gameObject.AddComponent<UiCardAnimator>();
            animator.SetSelected(selected);
            cardAnimators.Add(animator);

            var v = UiFactory.CreateVStack(rt, 3f);
            // Without this the avatar disc would be stretched to the card width.
            v.childForceExpandWidth = false;

            // --- avatar: disc + ring + initial ---
            var avatarWrap = UiFactory.CreateRect("AvatarWrap", rt);
            UiFactory.Pin(avatarWrap, 70f, 70f);
            var avGlow = UiFactory.CreateGlow("AvatarGlow", avatarWrap,
                new Color(def.Color.r, def.Color.g, def.Color.b, 0.35f), 96f);
            var agr = (RectTransform)avGlow.transform;
            agr.anchorMin = agr.anchorMax = new Vector2(0.5f, 0.5f);
            agr.anchoredPosition = Vector2.zero;

            var avatar = UiFactory.CreateRect("Avatar", avatarWrap);
            UiFactory.Pin(avatar, 60f, 60f);
            var avRt = (RectTransform)avatar.transform;
            avRt.anchorMin = avRt.anchorMax = new Vector2(0.5f, 0.5f);
            avRt.anchoredPosition = Vector2.zero;
            // Image and TMP both drive CanvasRenderer, so the emoji lives on a
            // child rather than sharing the disc's GameObject.
            var avImg = avatar.gameObject.AddComponent<Image>();
            avImg.color = def.Color;
            UiFactory.UseRounded(avImg);
            var ring = UiFactory.CreateRing("Ring", avatar, selected
                ? UiFactory.Accent : new Color(1f, 1f, 1f, 0.20f), 66f);
            var rrt = (RectTransform)ring.transform;
            rrt.anchorMin = rrt.anchorMax = new Vector2(0.5f, 0.5f);
            rrt.anchoredPosition = Vector2.zero;

            var emojiRt = UiFactory.CreateRect("Emoji", avatar.transform);
            UiFactory.CreateStretch(emojiRt);
            var avLabel = emojiRt.gameObject.AddComponent<TextMeshProUGUI>();
            // No character art yet, so the avatar leads with the character's
            // initial — English text, plain TMP, no RTL shaping involved.
            avLabel.text = def.DisplayName.Length > 0
                ? def.DisplayName.Substring(0, 1).ToUpperInvariant()
                : "?";
            // Always pin the font: without it TMP falls back to LiberationSans.
            if (UiFactory.UiFont != null) avLabel.font = UiFactory.UiFont;
            avLabel.fontSize = 32f;
            avLabel.alignment = TextAlignmentOptions.Center;
            avLabel.raycastTarget = false;

            var name = UiFactory.CreateLabel("Name", rt, def.DisplayName.ToUpperInvariant(), 21f,
                UiFactory.TextPrimary, TextAlignmentOptions.Center);
            name.fontStyle = FontStyles.Bold;
            name.characterSpacing = 1.5f;
            UiFactory.Pin((RectTransform)name.transform, -1f, 26f);

            var role = UiFactory.CreateLabel("Role", rt, def.Role, 12f,
                UiFactory.TextDim, TextAlignmentOptions.Center);
            UiFactory.Pin((RectTransform)role.transform, 158f, 16f);

            // --- stat strip: damage / cooldown / range ---
            var stats = UiFactory.CreateRect("Stats", rt);
            UiFactory.Pin(stats, -1f, 58f);
            UiFactory.CreateVStack(stats, 2f);
            var primary = def.Get(AbilitySlot.Primary);
            float maxDamage = 32f, maxRange = 14f, maxCooldown = 8f;
            AddStatRow(stats.transform, "DMG", primary.Damage, maxDamage, UiFactory.Bad);
            AddStatRow(stats.transform, "RANGE", primary.Range, maxRange, UiFactory.Accent);
            AddStatRow(stats.transform, "CD", primary.Cooldown, maxCooldown, UiFactory.Warn,
                invert: true);

            // --- ability chips ---
            var chips = UiFactory.CreateRect("Chips", rt);
            UiFactory.Pin(chips, -1f, 70f);
            UiFactory.CreateVStack(chips, 2f);
            for (int i = 0; i < def.Abilities.Length; i++)
                AddAbilityChip(chips.transform, i, def.Abilities[i], def.Color);

            var button = img.GetComponent<Button>();
            button.onClick.AddListener(() => onPick(id));
            return button;
        }

        /// <summary>Label + progress track + value, one per tuning stat.</summary>
        private static void AddStatRow(Transform parent, string label, float value,
            float max, Color color, bool invert = false)
        {
            var row = UiFactory.CreateRect("Stat_" + label, parent);
            UiFactory.Pin(row, -1f, 18f);

            var key = UiFactory.CreateLabel("K", row, label, 10.5f, UiFactory.Muted,
                TextAlignmentOptions.Left);
            var krt = (RectTransform)key.transform;
            krt.anchorMin = new Vector2(0f, 0f);
            krt.anchorMax = new Vector2(0f, 1f);
            krt.pivot = new Vector2(0f, 0.5f);
            krt.anchoredPosition = new Vector2(8f, 0f);
            krt.sizeDelta = new Vector2(52f, 0f);

            var track = UiFactory.CreatePanel("Track", row, new Color(0.07f, 0.11f, 0.19f, 1f));
            UiFactory.UseRounded(track);
            var trt = (RectTransform)track.transform;
            trt.anchorMin = new Vector2(0f, 0.5f);
            trt.anchorMax = new Vector2(1f, 0.5f);
            trt.pivot = new Vector2(0.5f, 0.5f);
            trt.offsetMin = new Vector2(58f, -4f);
            trt.offsetMax = new Vector2(-36f, 4f);

            // A single filled Image on the track's child, so the bar reads as a
            // precise fraction instead of a hand-sized rect.
            var fillRt = UiFactory.CreateRect("Fill", trt);
            UiFactory.CreateStretch(fillRt);
            var fillImg = fillRt.gameObject.AddComponent<Image>();
            fillImg.sprite = UiFactory.RoundedSprite;
            fillImg.type = Image.Type.Filled;
            fillImg.fillMethod = Image.FillMethod.Horizontal;
            fillImg.fillOrigin = (int)Image.OriginHorizontal.Left;
            fillImg.color = color;
            fillImg.raycastTarget = false;
            fillImg.fillAmount = Mathf.Clamp01(value / max);

            var val = UiFactory.CreateLabel("V", row, Mathf.RoundToInt(value) + (invert ? "s" : ""),
                10.5f, UiFactory.TextDim, TextAlignmentOptions.Right);
            var vrt = (RectTransform)val.transform;
            vrt.anchorMin = new Vector2(1f, 0f);
            vrt.anchorMax = new Vector2(1f, 1f);
            vrt.pivot = new Vector2(1f, 0.5f);
            vrt.anchoredPosition = new Vector2(-6f, 0f);
            vrt.sizeDelta = new Vector2(34f, 0f);
        }

        private static void AddAbilityChip(Transform parent, int index, AbilityDef ability, Color tint)
        {
            var row = UiFactory.CreateRect("Chip" + index, parent);
            UiFactory.Pin(row, -1f, 22f);

            var badge = UiFactory.CreateGradient("Num", row,
                new Color(tint.r, tint.g, tint.b, 1f),
                new Color(tint.r * 0.55f, tint.g * 0.55f, tint.b * 0.55f, 1f));
            var brt = (RectTransform)badge.transform;
            brt.anchorMin = brt.anchorMax = new Vector2(0f, 0.5f);
            brt.pivot = new Vector2(0f, 0.5f);
            brt.anchoredPosition = new Vector2(8f, 0f);
            brt.sizeDelta = new Vector2(20f, 18f);
            var num = UiFactory.CreateLabel("N", badge.transform, (index + 1).ToString(), 12f,
                new Color(0.02f, 0.04f, 0.08f), TextAlignmentOptions.Center);
            UiFactory.CreateStretch((RectTransform)num.transform);
            num.fontStyle = FontStyles.Bold;

            var name = UiFactory.CreateLabel("Ab", row, ability.DisplayName, 12.5f,
                new Color(0.72f, 0.80f, 0.94f), TextAlignmentOptions.Left);
            var nrt = (RectTransform)name.transform;
            nrt.anchorMin = new Vector2(0f, 0f);
            nrt.anchorMax = new Vector2(1f, 1f);
            nrt.offsetMin = new Vector2(34f, 0f);
            nrt.offsetMax = new Vector2(-6f, 0f);
        }

        // --- state setters -------------------------------------------------
        private CharacterId currentP1 = CharacterId.Vanguard;
        private CharacterId currentP2 = CharacterId.Storm;

        private void onPick1(CharacterId id) { if (busy) return; pick1 = id; RefreshCard(p1Row, id); }
        private void onPick2(CharacterId id) { if (busy) return; pick2 = id; RefreshCard(p2Row, id); }

        private void RefreshCard(RectTransform row, CharacterId chosen)
        {
            if (row == null) return;
            for (int i = 0; i < row.childCount; i++)
            {
                var child = row.GetChild(i);
                bool isChosen = child.name == "Card_" + chosen;
                var img = child.GetComponent<Image>();
                if (img != null)
                    UiFactory.UseRoundedGradient(img,
                        isChosen ? new Color(0.16f, 0.30f, 0.56f, 1f) : UiFactory.CardTop,
                        isChosen ? new Color(0.08f, 0.16f, 0.32f, 1f) : UiFactory.CardBottom);
                var outline = child.GetComponent<Outline>();
                if (outline != null)
                    outline.effectColor = isChosen ? UiFactory.Accent : UiFactory.Stroke;
                var btn = child.GetComponent<Button>();
                if (btn != null) btn.interactable = !isChosen;
                var anim = child.GetComponent<UiCardAnimator>();
                if (anim != null) anim.SetSelected(isChosen);
                var ring = child.Find("AvatarWrap/Avatar/Ring");
                if (ring != null)
                {
                    var ringImg = ring.GetComponent<Image>();
                    if (ringImg != null)
                        ringImg.color = isChosen ? UiFactory.Accent : new Color(1f, 1f, 1f, 0.20f);
                }
            }
        }

        private void SetMode(GameMode m)
        {
            if (busy) return;
            mode = m;
            if (p2Title != null)
                p2Title.text = m == GameMode.Bot ? "PLAYER 2 · BOT" : "OPPONENT PICKS ON THEIR PHONE";
            p2Column.gameObject.SetActive(m == GameMode.Bot);
            hostAddress.interactable = m == GameMode.LanJoin;
            // The host alone decides the zone, so the join phone's picker is
            // disabled instead of silently ignoring a tap.
            bool zoneSelectable = m != GameMode.LanJoin;
            if (zoneButtons != null)
                foreach (var b in zoneButtons) if (b != null) b.interactable = zoneSelectable;
            SetNetworkStatus(m == GameMode.Bot ? "Solo battle" :
                m == GameMode.LanJoin ? "Choose PLAYER 1 and the host IP. The host decides the zone." :
                "Choose PLAYER 1 and the zone. You can change the zone while waiting.", false);
        }

        private void SetZone(ZoneShape z)
        {
            // Allowed while waiting: the host re-reads Zone when the guest joins.
            if (mode != GameMode.LanJoin) zone = z;
        }

        public void SetNetworkStatus(string text, bool waiting)
        {
            busy = waiting;
            // The mode is fixed once a session exists; disable instead of
            // repainting a selection the setter will reject.
            if (modeButtons != null)
                foreach (var b in modeButtons) if (b != null) b.interactable = !waiting;
            status.text = text;
            status.color = waiting ? UiFactory.Warn : UiFactory.Accent;
            startLabel.text = waiting ? "CANCEL CONNECTION" :
                mode == GameMode.LanHost ? "HOST BATTLE" : mode == GameMode.LanJoin ? "JOIN BATTLE" : "START BATTLE";
            if (startPulse != null) startPulse.SetAmplified(!waiting);
        }

        // --- small builders ------------------------------------------------
        private RectTransform p2Column2;

        private Button[] AddSegment(Transform parent, string label, out Button first,
            (string, Action)[] options, int initialIndex)
        {
            var created = new Button[options.Length];
            var row = UiFactory.CreateRect("Seg_" + label, parent);
            UiFactory.Pin(row, -1f, 46f);
            UiFactory.CreateHStack(row, 12f);

            var lbl = UiFactory.CreateLabel("SegLabel", row, label, 15f,
                UiFactory.Muted, TextAlignmentOptions.Right);
            lbl.characterSpacing = 2f;
            UiFactory.Pin((RectTransform)lbl.transform, 150f, 46f);

            var seg = UiFactory.CreateRect("Seg", row);
            // Preferred width must cover every option plus the gaps, or the
            // horizontal group overflows its own container.
            UiFactory.Pin(seg, options.Length * 200f + Mathf.Max(0, options.Length - 1) * 6f, 46f);
            UiFactory.CreateHStack(seg, 6f);

            first = null;
            for (int i = 0; i < options.Length; i++)
            {
                int captured = i;
                bool on0 = i == initialIndex;
                var btnImg = UiFactory.CreateButton("Opt" + i, seg, UiFactory.Card);
                // Explicit width: 0 here would collapse the button to nothing.
                UiFactory.Pin((RectTransform)btnImg.transform, 200f, 46f);
                UiFactory.UseRoundedGradient(btnImg,
                    on0 ? new Color(0.20f, 0.42f, 0.72f, 1f) : new Color(0.075f, 0.12f, 0.21f, 1f),
                    on0 ? new Color(0.10f, 0.26f, 0.52f, 1f) : new Color(0.04f, 0.07f, 0.13f, 1f));
                UiFactory.AddOutline(btnImg, on0 ? UiFactory.Accent : UiFactory.Stroke, 2f);

                var t = UiFactory.CreateLabel("T", btnImg.transform, options[i].Item1, 16f,
                    on0 ? Color.white : UiFactory.TextDim);
                UiFactory.Pin((RectTransform)t.transform, -1f, 46f);
                t.fontStyle = on0 ? FontStyles.Bold : FontStyles.Normal;

                // Underline marker for the active option (child, so it travels
                // with its button and never fights the parent's layout group).
                var underline = UiFactory.CreatePanel("Underline", btnImg.transform,
                    UiFactory.Accent);
                UiFactory.UseRounded(underline);
                var urt = (RectTransform)underline.transform;
                urt.anchorMin = new Vector2(0.5f, 0f);
                urt.anchorMax = new Vector2(0.5f, 0f);
                urt.pivot = new Vector2(0.5f, 0f);
                urt.anchoredPosition = new Vector2(0f, 4f);
                urt.sizeDelta = new Vector2(120f, 4f);
                underline.gameObject.SetActive(on0);

                var btn = btnImg.GetComponent<Button>();
                created[i] = btn;
                if (i == initialIndex) first = btn;
                btn.onClick.AddListener(() =>
                {
                    options[captured].Item2();
                    // repaint the group
                    for (int k = 0; k < seg.childCount; k++)
                    {
                        var b = seg.GetChild(k);
                        bool on = k == captured;
                        var im = b.GetComponent<Image>();
                        if (im != null)
                            UiFactory.UseRoundedGradient(im,
                                on ? new Color(0.20f, 0.42f, 0.72f, 1f) : new Color(0.075f, 0.12f, 0.21f, 1f),
                                on ? new Color(0.10f, 0.26f, 0.52f, 1f) : new Color(0.04f, 0.07f, 0.13f, 1f));
                        var ol = b.GetComponent<Outline>();
                        if (ol != null) ol.effectColor = on ? UiFactory.Accent : UiFactory.Stroke;
                        var txt = b.GetComponentInChildren<TextMeshProUGUI>();
                        if (txt != null)
                        {
                            txt.color = on ? Color.white : UiFactory.TextDim;
                            txt.fontStyle = on ? FontStyles.Bold : FontStyles.Normal;
                        }
                        var ul = b.Find("Underline");
                        if (ul != null) ul.gameObject.SetActive(on);
                    }
                });
            }
            return created;
        }

        private Button MakeActionButton(Transform parent, string label, Color color, float height, float width = -1f)
        {
            var img = UiFactory.CreateButton("Action_" + label, parent, color);
            var rt = (RectTransform)img.transform;
            if (width > 0f)
            {
                // Fixed, centred block inside a full-width wrapper row.
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(width, height);
            }
            else
            {
                UiFactory.Pin(rt, -1f, height);
            }
            // Flat, saturated blue. A pale gradient head plus a bright bloom
            // behind the pill is exactly what reads as a white halo around the
            // label, so this button carries neither: just the fill and a shadow.
            UiFactory.UseRoundedGradient(img,
                new Color(0.24f, 0.55f, 0.96f, 1f), new Color(0.10f, 0.31f, 0.72f, 1f));

            UiFactory.AddShadow(img, new Color(0f, 0f, 0f, 0.50f), new Vector2(0f, -5f));

            var t = UiFactory.CreateLabel("L", rt, label, 23f, new Color(0.02f, 0.06f, 0.12f));
            t.fontStyle = FontStyles.Bold;
            t.characterSpacing = 2f;
            UiFactory.CreateStretch((RectTransform)t.transform);
            t.alignment = TextAlignmentOptions.Center;
            return img.GetComponent<Button>();
        }

        /// <summary>Bakes a top→bottom vertex gradient into a TMP label.</summary>
        private static void GradientText(TextMeshProUGUI label, Color top, Color bottom)
        {
            label.enableVertexGradient = true;
            label.colorGradient = new VertexGradient(top, top, bottom, bottom);
            label.color = Color.white;
        }

        public void Show()
        {
            gameObject.SetActive(true);

            // Staggered pop-in for the two rows of cards.
            for (int i = 0; i < cardAnimators.Count; i++)
                cardAnimators[i].PlayEnter(i * 0.06f);
            if (contentEnter != null) contentEnter.Play();
        }

        public void Hide()
        {
            gameObject.SetActive(false);
        }
    }
}
