using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Arena.Core;

namespace Arena.Presentation
{
    /// <summary>
    /// Screen two: pick your fighter and the options that mode needs, then start.
    /// It is deliberately only UI — no arena, no clock, no practice sim. The
    /// match (and its countdown) begins the moment START is pressed.
    ///
    /// The object graph keeps stable paths —
    /// <c>SelectContent/CardRow/Card_&lt;id&gt;</c>,
    /// <c>SelectContent/Options/Seg_&lt;label&gt;/Seg/Opt&lt;i&gt;</c>,
    /// <c>SelectContent/Options/LanBox/Seg/Role/Seg/Opt&lt;i&gt;</c>,
    /// <c>SelectContent/Options/LanBox/Seg/IpField</c>,
    /// <c>SelectContent/Actions/Action_&lt;label&gt;</c> — because the regression
    /// harness drives the real buttons rather than the fields behind them.
    /// </summary>
    public class CharacterSelectView : MonoBehaviour
    {
        public event Action<CharacterId> CharacterChosen;
        public event Action<ZoneShape> ZoneChosen;
        public event Action<int> BotCountChosen;
        public event Action StartRequested;
        public event Action CancelRequested;

        private GameMode mode = GameMode.Bot;
        private bool busy;
        private CharacterId pick = CharacterId.Vanguard;
        private ZoneShape zone = ZoneShape.Circle;
        private int botCount = 1;
        private int stage = 1;

        private TextMeshProUGUI header;
        private TextMeshProUGUI hint;
        private TextMeshProUGUI status;
        private TextMeshProUGUI startLabel;
        private Button[] cards;
        private Button[] zoneButtons;
        private Button[] botButtons;
        private Button[] roleButtons;
        private Button startButton;
        private TMP_InputField hostAddress;
        private RectTransform zoneGroup;
        private RectTransform botGroup;
        private RectTransform lanGroup;
        private UiPulse startPulse;

        /// <summary>Which mode this picker is currently set up for.</summary>
        public GameMode Mode => mode;
        /// <summary>The last address discovery offered, so a newer one may replace it.</summary>
        private string offeredAddress;
        /// <summary>Live zone pick: a host may still change it while waiting.</summary>
        public ZoneShape Zone => zone;
        /// <summary>The address typed for a join.</summary>
        public string HostAddress => hostAddress != null ? hostAddress.text : string.Empty;
        /// <summary>The fighter the player is taking into the match.</summary>
        public CharacterId Character => pick;

        private static readonly int[] BotCounts = { 1, 2, 3, 5 };

        // ------------------------------------------------------------------ build

        public static CharacterSelectView Build(Transform canvas)
        {
            var backdrop = MenuChrome.Backdrop(canvas, "SelectBackdrop");
            var root = UiFactory.CreateRect("SelectContent", backdrop);
            UiFactory.CreateStretch(root, 26f);
            UiFactory.CreateVStack(root, 12f);

            var view = backdrop.gameObject.AddComponent<CharacterSelectView>();

            view.header = UiFactory.CreateLabel("Header", root, "PLAY WITH BOTS", 34f,
                UiFactory.Frost, TextAlignmentOptions.Center);
            view.header.fontStyle = FontStyles.Bold;
            view.header.characterSpacing = 3f;
            UiFactory.Pin((RectTransform)view.header.transform, -1f, 42f);

            view.hint = UiFactory.CreateLabel("Hint", root, string.Empty, 16f,
                UiFactory.TextDim, TextAlignmentOptions.Center);
            UiFactory.Pin((RectTransform)view.hint.transform, -1f, 24f);

            // --- one roomy fighter row -----------------------------------------
            var cardRow = UiFactory.CreateRect("CardRow", root);
            UiFactory.Pin(cardRow, -1f, 300f);
            UiFactory.CreateHStack(cardRow, 26f);

            // One card per class the config defines, so adding a fighter to the
            // enum (and to GameConfig.Get) is all it takes to offer them here.
            var roster = (CharacterId[])System.Enum.GetValues(typeof(CharacterId));
            view.cards = new Button[roster.Length];
            for (int i = 0; i < roster.Length; i++)
                view.cards[i] = Card(cardRow, GameConfig.Get(roster[i]), view);

            // --- per-mode options ----------------------------------------------
            var options = UiFactory.CreateRect("Options", root);
            UiFactory.Pin(options, -1f, 92f);
            UiFactory.CreateHStack(options, 20f);

            view.zoneGroup = Group(options, "Seg_ZONE", "ZONE SHAPE", 360f);
            view.zoneButtons = Segment(Row(view.zoneGroup, "Seg"),
                new[] { "Circle", "Square" }, (int)view.zone, i =>
                {
                    view.zone = (ZoneShape)i;
                    view.ZoneChosen?.Invoke(view.zone);
                });

            view.botGroup = Group(options, "Seg_BOTS", "OPPONENTS", 500f);
            view.botButtons = Segment(Row(view.botGroup, "Seg"),
                new[] { "1", "2", "3", "5" }, 0, i => view.BotCountChosen?.Invoke(BotCounts[i]));

            view.lanGroup = Group(options, "LanBox", "NETWORK", 740f);
            view.BuildNetworkGroup(view.lanGroup);

            view.status = UiFactory.CreateLabel("Status", root, string.Empty, 15f,
                UiFactory.Muted, TextAlignmentOptions.Center);
            UiFactory.Pin((RectTransform)view.status.transform, -1f, 22f);

            // --- actions ---------------------------------------------------------
            var actions = UiFactory.CreateRect("Actions", root);
            UiFactory.Pin(actions, -1f, 70f);
            UiFactory.CreateHStack(actions, 18f);

            var back = Action(actions, "Action_BACK", "◀  MODES", 220f,
                new Color(0.16f, 0.22f, 0.34f, 1f), new Color(0.09f, 0.13f, 0.22f, 1f));
            back.onClick.AddListener(() => view.CancelRequested?.Invoke());

            view.startButton = Action(actions, "Action_START", "START MATCH", 560f,
                new Color(0.24f, 0.55f, 0.96f, 1f), new Color(0.10f, 0.31f, 0.72f, 1f));
            view.startButton.onClick.AddListener(view.PressStart);
            view.startLabel = view.startButton.GetComponentInChildren<TextMeshProUGUI>();
            view.startPulse = view.startButton.gameObject.AddComponent<UiPulse>();
            view.startPulse.SetAmplified(true);

            view.Hide();
            return view;
        }

        /// <summary>One fighter card: role icon, name, role and the three spells.</summary>
        private static Button Card(Transform parent, CharacterDef def, CharacterSelectView view)
        {
            var img = UiFactory.CreateButton("Card_" + def.Id, parent, UiFactory.Card);
            UiFactory.Pin((RectTransform)img.transform, 400f, 300f);
            UiFactory.UseRoundedGradient(img, UiFactory.CardTop, UiFactory.CardBottom);
            UiFactory.AddOutline(img, UiFactory.Stroke, 2f);

            var accent = UiFactory.CreatePanel("Accent", img.transform, def.Color);
            UiFactory.UseRounded(accent);
            var art = (RectTransform)accent.transform;
            art.anchorMin = new Vector2(0f, 1f);
            art.anchorMax = new Vector2(1f, 1f);
            art.pivot = new Vector2(0.5f, 1f);
            art.sizeDelta = new Vector2(-16f, 5f);
            art.anchoredPosition = new Vector2(0f, -8f);

            var disc = UiFactory.CreateIcon("Disc", img.transform, def.IconEmoji, 54f);
            disc.anchorMin = disc.anchorMax = new Vector2(0.5f, 1f);
            disc.pivot = new Vector2(0.5f, 1f);
            disc.anchoredPosition = new Vector2(0f, -22f);
            disc.GetComponent<Image>().color = def.Color;

            var title = UiFactory.CreateLabel("Name", img.transform, def.DisplayName, 25f,
                Color.white, TextAlignmentOptions.Center);
            title.fontStyle = FontStyles.Bold;
            title.characterSpacing = 2f;
            var nrt = (RectTransform)title.transform;
            nrt.anchorMin = nrt.anchorMax = new Vector2(0.5f, 1f);
            nrt.pivot = new Vector2(0.5f, 1f);
            nrt.sizeDelta = new Vector2(376f, 34f);
            nrt.anchoredPosition = new Vector2(0f, -82f);

            var role = UiFactory.CreateLabel("Role", img.transform, def.Role, 14f,
                UiFactory.TextDim, TextAlignmentOptions.Center);
            var rrt = (RectTransform)role.transform;
            rrt.anchorMin = rrt.anchorMax = new Vector2(0.5f, 1f);
            rrt.pivot = new Vector2(0.5f, 1f);
            rrt.sizeDelta = new Vector2(376f, 20f);
            rrt.anchoredPosition = new Vector2(0f, -116f);

            // Spell list: key chip, the spell itself, and the numbers a player
            // wants before a match on a second line under it — three cards is not
            // wide enough for a fourth column. A telegraphed spell says so,
            // because that is the difference between a dodge and a hit.
            for (int i = 0; i < def.Abilities.Length && i < 3; i++)
            {
                var ability = def.Abilities[i];
                float y = -150f - i * 46f;

                var key = UiFactory.CreatePanel("Key" + i, img.transform,
                    new Color(0.10f, 0.20f, 0.36f, 1f));
                UiFactory.UseRounded(key);
                var krt = (RectTransform)key.transform;
                krt.anchorMin = krt.anchorMax = new Vector2(0.5f, 1f);
                krt.pivot = new Vector2(1f, 1f);
                krt.sizeDelta = new Vector2(28f, 26f);
                krt.anchoredPosition = new Vector2(-146f, y);
                var keyLabel = UiFactory.CreateLabel("KeyText", key.transform,
                    (i + 1).ToString(), 14f, UiFactory.Frost, TextAlignmentOptions.Center);
                keyLabel.fontStyle = FontStyles.Bold;
                UiFactory.CreateStretch((RectTransform)keyLabel.transform);

                var spell = UiFactory.CreateLabel("Spell" + i, img.transform, ability.DisplayName,
                    16f, UiFactory.TextPrimary, TextAlignmentOptions.MidlineLeft);
                var srt = (RectTransform)spell.transform;
                srt.anchorMin = srt.anchorMax = new Vector2(0.5f, 1f);
                srt.pivot = new Vector2(0f, 1f);
                srt.sizeDelta = new Vector2(320f, 22f);
                srt.anchoredPosition = new Vector2(-134f, y);

                string info = ability.Damage > 0f
                    ? $"{ability.Cooldown:0.#}s · {ability.Damage:0} dmg"
                    : $"{ability.Cooldown:0.#}s";
                if (ability.BuffDuration > 0f) info += $" · {ability.BuffDuration:0.#}s buff";
                if (ability.FanCount > 0) info += $" · ×{ability.FanCount}";
                if (ability.ProjectileRange > 0f) info += " · returns";
                if (ability.Telegraph > 0f) info += " · telegraphed";
                var details = UiFactory.CreateLabel("Info" + i, img.transform, info, 13f,
                    ability.Telegraph > 0f ? UiFactory.Warn : UiFactory.Muted,
                    TextAlignmentOptions.MidlineLeft);
                var irt = (RectTransform)details.transform;
                irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 1f);
                irt.pivot = new Vector2(0f, 1f);
                irt.sizeDelta = new Vector2(320f, 20f);
                irt.anchoredPosition = new Vector2(-134f, y - 24f);
            }

            var id = def.Id;
            img.GetComponent<Button>().onClick.AddListener(() => view.ChooseCharacter(id));
            return img.GetComponent<Button>();
        }

        /// <summary>A labelled column that holds one segment row.</summary>
        private static RectTransform Group(Transform parent, string name, string caption, float width)
        {
            var rect = UiFactory.CreateRect(name, parent);
            UiFactory.Pin(rect, width, 84f);
            UiFactory.CreateVStack(rect, 6f);

            var label = UiFactory.CreateLabel("Caption", rect, caption, 14f,
                UiFactory.Muted, TextAlignmentOptions.Center);
            label.characterSpacing = 2f;
            UiFactory.Pin((RectTransform)label.transform, -1f, 20f);

            var row = UiFactory.CreateRect("Seg", rect);
            UiFactory.Pin(row, -1f, 52f);
            return rect;
        }

        /// <summary>The laid-out option row inside a group.</summary>
        private static RectTransform Row(RectTransform group, string name)
        {
            var row = (RectTransform)group.Find(name);
            UiFactory.CreateHStack(row, 10f);
            return row;
        }

        /// <summary>Segmented picker with an underline on the live option.</summary>
        private static Button[] Segment(RectTransform row, string[] labels,
            int initialIndex, Action<int> onPick)
        {
            var created = new Button[labels.Length];

            for (int i = 0; i < labels.Length; i++)
            {
                bool on0 = i == initialIndex;
                var img = UiFactory.CreateButton("Opt" + i, row, UiFactory.Card);
                UiFactory.Pin((RectTransform)img.transform, 96f, 52f);
                UiFactory.UseRoundedGradient(img,
                    on0 ? new Color(0.20f, 0.42f, 0.72f, 1f) : new Color(0.075f, 0.12f, 0.21f, 1f),
                    on0 ? new Color(0.10f, 0.26f, 0.52f, 1f) : new Color(0.04f, 0.07f, 0.13f, 1f));
                UiFactory.AddOutline(img, on0 ? UiFactory.Accent : UiFactory.Stroke, 2f);

                var label = UiFactory.CreateLabel("Text", img.transform, labels[i], 18f,
                    on0 ? Color.white : UiFactory.TextDim, TextAlignmentOptions.Center);
                label.fontStyle = on0 ? FontStyles.Bold : FontStyles.Normal;
                UiFactory.CreateStretch((RectTransform)label.transform, 4f);

                var underline = UiFactory.CreatePanel("Underline", img.transform, UiFactory.Accent);
                UiFactory.UseRounded(underline);
                var urt = (RectTransform)underline.transform;
                urt.anchorMin = new Vector2(0.5f, 0f);
                urt.anchorMax = new Vector2(0.5f, 0f);
                urt.pivot = new Vector2(0.5f, 0f);
                urt.anchoredPosition = new Vector2(0f, 4f);
                urt.sizeDelta = new Vector2(70f, 4f);
                underline.gameObject.SetActive(on0);

                int captured = i;
                var btn = img.GetComponent<Button>();
                created[i] = btn;
                btn.onClick.AddListener(() =>
                {
                    onPick(captured);
                    Repaint(row, captured);
                });
            }
            return created;
        }

        /// <summary>Repaints one segment row so exactly the tapped option reads as live.</summary>
        internal static void Repaint(RectTransform row, int selected)
        {
            if (row == null) return;
            for (int k = 0; k < row.childCount; k++)
            {
                var child = row.GetChild(k);
                bool on = k == selected;
                var img = child.GetComponent<Image>();
                if (img != null)
                    UiFactory.UseRoundedGradient(img,
                        on ? new Color(0.20f, 0.42f, 0.72f, 1f) : new Color(0.075f, 0.12f, 0.21f, 1f),
                        on ? new Color(0.10f, 0.26f, 0.52f, 1f) : new Color(0.04f, 0.07f, 0.13f, 1f));
                var outline = child.GetComponent<Outline>();
                if (outline != null) outline.effectColor = on ? UiFactory.Accent : UiFactory.Stroke;
                var text = child.GetComponentInChildren<TextMeshProUGUI>();
                if (text != null)
                {
                    text.color = on ? Color.white : UiFactory.TextDim;
                    text.fontStyle = on ? FontStyles.Bold : FontStyles.Normal;
                }
                var underline = child.Find("Underline");
                if (underline != null) underline.gameObject.SetActive(on);
            }
        }

        /// <summary>Host or join, plus the box a joining phone types an address into.</summary>
        private void BuildNetworkGroup(RectTransform group)
        {
            var row = Row(group, "Seg");

            var roleRow = UiFactory.CreateRect("Role", row);
            UiFactory.Pin(roleRow, 210f, 52f);
            var roleSeg = UiFactory.CreateRect("Seg", roleRow);
            UiFactory.CreateStretch(roleSeg);
            UiFactory.CreateHStack(roleSeg, 10f);
            roleButtons = Segment(roleSeg, new[] { "HOST", "JOIN" }, 0, i =>
            {
                mode = i == 0 ? GameMode.LanHost : GameMode.LanJoin;
                ApplyMode();
            });

            var box = UiFactory.CreateRect("IpField", row);
            UiFactory.Pin(box, 330f, 46f);
            var bg = box.gameObject.AddComponent<Image>();
            bg.color = new Color(0.075f, 0.12f, 0.21f, 1f);
            UiFactory.UseRounded(bg);
            UiFactory.AddOutline(bg, UiFactory.Stroke, 2f);

            hostAddress = box.gameObject.AddComponent<TMP_InputField>();
            var textRt = UiFactory.CreateRect("Text", box);
            UiFactory.CreateStretch(textRt, 12f);
            var text = textRt.gameObject.AddComponent<TextMeshProUGUI>();
            text.fontSize = 19f;
            text.color = Color.white;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            text.raycastTarget = false;
            if (UiFactory.UiFont != null) text.font = UiFactory.UiFont;

            var holderRt = UiFactory.CreateRect("Placeholder", box);
            UiFactory.CreateStretch(holderRt, 12f);
            var holder = holderRt.gameObject.AddComponent<TextMeshProUGUI>();
            holder.text = "host address, e.g. 192.168.43.1";
            holder.fontSize = 17f;
            holder.color = UiFactory.Muted;
            holder.alignment = TextAlignmentOptions.MidlineLeft;
            holder.raycastTarget = false;
            if (UiFactory.UiFont != null) holder.font = UiFactory.UiFont;

            hostAddress.textComponent = text;
            hostAddress.placeholder = holder;
            hostAddress.targetGraphic = bg;
            hostAddress.lineType = TMP_InputField.LineType.SingleLine;
            hostAddress.contentType = TMP_InputField.ContentType.Standard;
        }

        private static Button Action(Transform parent, string name, string label, float width,
            Color top, Color bottom)
        {
            var img = UiFactory.CreateButton(name, parent, Color.white);
            UiFactory.Pin((RectTransform)img.transform, width, 62f);
            UiFactory.UseRoundedGradient(img, top, bottom);
            UiFactory.AddOutline(img, new Color(0.55f, 0.75f, 1f, 0.5f), 2f);

            var text = UiFactory.CreateLabel("Text", img.transform, label, 26f,
                Color.white, TextAlignmentOptions.Center);
            text.fontStyle = FontStyles.Bold;
            text.characterSpacing = 2f;
            UiFactory.CreateStretch((RectTransform)text.transform, 6f);
            return img.GetComponent<Button>();
        }

        // ------------------------------------------------------------------- api

        /// <summary>Shows the picker for one mode, with that mode's own options.</summary>
        public void Show(GameMode gameMode, CharacterId current, ZoneShape currentZone,
            int bots, int campaignStage)
        {
            mode = gameMode;
            pick = current;
            zone = currentZone;
            botCount = bots;
            stage = campaignStage;
            busy = false;

            int role = mode == GameMode.LanJoin ? 1 : 0;
            if (roleButtons != null) Repaint((RectTransform)roleButtons[0].transform.parent, role);

            int selected = 0;
            for (int i = 0; i < BotCounts.Length; i++)
                if (BotCounts[i] == botCount) selected = i;
            Repaint((RectTransform)botGroup.Find("Seg"), selected);
            Repaint((RectTransform)zoneGroup.Find("Seg"), (int)zone);

            if (hostAddress != null) hostAddress.text = string.Empty;
            offeredAddress = null;
            ApplyMode();
            ChooseCharacter(current);
            SetStatus(string.Empty, false);
            gameObject.SetActive(true);
        }

        public void Hide() => gameObject.SetActive(false);

        /// <summary>Applies everything that depends on which mode is showing.</summary>
        private void ApplyMode()
        {
            bool join = mode == GameMode.LanJoin;
            bool lan = mode == GameMode.LanHost || join;

            zoneGroup.gameObject.SetActive(!join);
            botGroup.gameObject.SetActive(mode == GameMode.Bot);
            lanGroup.gameObject.SetActive(lan);
            foreach (var button in zoneButtons)
                button.interactable = !join;
            if (hostAddress != null) hostAddress.gameObject.SetActive(join);

            header.text = mode == GameMode.Bot ? "PLAY WITH BOTS"
                : mode == GameMode.Campaign ? $"CAMPAIGN · STAGE {stage}"
                : join ? "LOCAL AREA NETWORK · JOIN"
                : "LOCAL AREA NETWORK · HOST";

            hint.text = mode == GameMode.Bot
                ? "Pick your fighter, set the line-up, then start."
                : mode == GameMode.Campaign
                    ? $"{stage} opponent{(stage == 1 ? "" : "s")} this stage — win to advance."
                    : join
                        ? "Type the host's address, then join — the host decides the arena."
                        : "Your phone owns the arena. The guest picks their fighter on their own screen.";

            RefreshActionLabel();
        }

        /// <summary>
        /// The one big button: the mode's own verb, or CANCEL while a network
        /// wait is up - a host waiting for a guest, or a guest waiting on a
        /// connect, must always have a way out that does not depend on finding
        /// the BACK button.
        /// </summary>
        private void RefreshActionLabel()
        {
            startLabel.text = busy ? "CANCEL"
                : mode == GameMode.Bot ? "START MATCH"
                : mode == GameMode.Campaign ? $"START STAGE {stage}"
                : mode == GameMode.LanJoin ? "JOIN BATTLE"
                : "HOST BATTLE";
        }

        /// <summary>
        /// Offers an address discovery heard, without overwriting one the player
        /// typed themselves - only an earlier offer is replaced.
        /// </summary>
        public void SetHostAddress(string address)
        {
            if (hostAddress == null || string.IsNullOrEmpty(address)) return;
            if (!string.IsNullOrEmpty(hostAddress.text) && hostAddress.text != offeredAddress) return;
            offeredAddress = address;
            if (hostAddress.text != address) hostAddress.text = address;
        }

        /// <summary>Moves the selection ring and tells the game which fighter was taken.</summary>
        public void ChooseCharacter(CharacterId id)
        {
            pick = id;
            for (int i = 0; i < cards.Length; i++)
            {
                bool on = i == (int)id;
                var outline = cards[i].GetComponent<Outline>();
                if (outline != null)
                {
                    outline.effectColor = on ? UiFactory.Accent : UiFactory.Stroke;
                    outline.effectDistance = new Vector2(on ? 4f : 2f, on ? -4f : -2f);
                }
            }
            CharacterChosen?.Invoke(id);
        }

        /// <summary>The single big action: start the match, or leave a network wait.</summary>
        public void PressStart()
        {
            if (!startButton.interactable) return;
            if (busy) { CancelRequested?.Invoke(); return; }
            startPulse?.PlayPop(0.22f);
            StartRequested?.Invoke();
        }

        /// <summary>
        /// The line under the options: what a network wait is doing, or why the
        /// last attempt failed. The button never goes dead - a wait shows
        /// CANCEL and a failure leaves the mode's own verb up for a retry.
        /// </summary>
        public void SetStatus(string text, bool waiting, bool failed = false)
        {
            string line = text ?? string.Empty;
            Color wanted = failed ? UiFactory.Bad : waiting ? UiFactory.Good : UiFactory.Muted;
            // Discovery re-offers its line every frame; only a real change is worth
            // touching the label and the button.
            if (busy == waiting && status.text == line && status.color == wanted) return;
            busy = waiting;
            status.text = line;
            status.color = wanted;
            startButton.interactable = true;
            RefreshActionLabel();
        }
    }
}
