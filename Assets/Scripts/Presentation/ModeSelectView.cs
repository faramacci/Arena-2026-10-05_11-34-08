using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Arena.Core;

namespace Arena.Presentation
{
    /// <summary>
    /// The first thing the player sees: how do you want to play? Three big,
    /// unhurried tiles — campaign, bots, local network — and nothing else. Every
    /// option that used to crowd this screen (fighter, zone, bot count, IP)
    /// lives on the fighter screen that follows the choice.
    /// </summary>
    public class ModeSelectView : MonoBehaviour
    {
        public event Action<GameMode> ModeChosen;

        private readonly Button[] tiles = new Button[3];
        private TextMeshProUGUI stageLabel;
        private TextMeshProUGUI notice;

        public static ModeSelectView Build(Transform canvas)
        {
            var backdrop = MenuChrome.Backdrop(canvas, "ModeBackdrop");
            var root = UiFactory.CreateRect("ModeContent", backdrop);
            UiFactory.CreateStretch(root, 40f);
            UiFactory.CreateVStack(root, 14f);

            var view = backdrop.gameObject.AddComponent<ModeSelectView>();

            var title = UiFactory.CreateLabel("Title", root, "FROZEN ARENA", 58f,
                UiFactory.Accent, TextAlignmentOptions.Center);
            title.fontStyle = FontStyles.Bold;
            title.characterSpacing = 8f;
            UiFactory.Pin((RectTransform)title.transform, -1f, 64f);
            var pulse = title.gameObject.AddComponent<UiPulse>();
            pulse.SetAmplified(true);

            var subtitle = UiFactory.CreateLabel("Subtitle", root,
                "Choose how you want to play", 19f, UiFactory.TextDim, TextAlignmentOptions.Center);
            UiFactory.Pin((RectTransform)subtitle.transform, -1f, 30f);

            view.tiles[0] = Tile(root, "Tile_Campaign", "CAMPAIGN",
                "Fight your way up the arena — every stage adds another opponent",
                UiFactory.Frost);
            view.tiles[1] = Tile(root, "Tile_Bots", "PLAY WITH BOTS",
                "Pick how many computers you want in the arena",
                UiFactory.Accent);
            view.tiles[2] = Tile(root, "Tile_Lan", "LOCAL AREA NETWORK",
                "Host or join over hotspot / Wi-Fi · one leader owns the lobby",
                UiFactory.AccentDeep);

            view.stageLabel = UiFactory.CreateLabel("Stage", root, string.Empty, 16f,
                UiFactory.Warn, TextAlignmentOptions.Center);
            UiFactory.Pin((RectTransform)view.stageLabel.transform, -1f, 24f);

            view.notice = UiFactory.CreateLabel("Notice", root, string.Empty, 16f,
                UiFactory.Bad, TextAlignmentOptions.Center);
            UiFactory.Pin((RectTransform)view.notice.transform, -1f, 24f);

            var hint = UiFactory.CreateLabel("Hint", root,
                "Pick your fighter and the zone, then start the match.",
                15f, UiFactory.Muted, TextAlignmentOptions.Center);
            UiFactory.Pin((RectTransform)hint.transform, -1f, 22f);

            view.tiles[0].onClick.AddListener(() => view.Choose(GameMode.Campaign));
            view.tiles[1].onClick.AddListener(() => view.Choose(GameMode.Bot));
            view.tiles[2].onClick.AddListener(() => view.Choose(GameMode.LanHost));
            return view;
        }

        /// <summary>Big single-line card: title on the left, the promise under it.</summary>
        private static Button Tile(Transform parent, string name, string title, string blurb, Color accent)
        {
            var img = UiFactory.CreateButton(name, parent, UiFactory.Card);
            UiFactory.Pin((RectTransform)img.transform, -1f, 116f);
            UiFactory.UseRoundedGradient(img, UiFactory.CardTop, UiFactory.CardBottom);
            UiFactory.AddOutline(img, UiFactory.Stroke, 2f);

            var label = UiFactory.CreateLabel(title + "Label", img.transform, title, 34f,
                Color.white, TextAlignmentOptions.Center);
            label.fontStyle = FontStyles.Bold;
            label.characterSpacing = 4f;
            var lrt = (RectTransform)label.transform;
            lrt.anchorMin = new Vector2(0.5f, 0.5f);
            lrt.anchorMax = new Vector2(0.5f, 0.5f);
            lrt.pivot = new Vector2(0.5f, 0.5f);
            lrt.sizeDelta = new Vector2(900f, 46f);
            lrt.anchoredPosition = new Vector2(0f, 18f);

            var sub = UiFactory.CreateLabel(title + "Blurb", img.transform, blurb, 16f,
                UiFactory.TextDim, TextAlignmentOptions.Center);
            var srt = (RectTransform)sub.transform;
            srt.anchorMin = srt.anchorMax = new Vector2(0.5f, 0.5f);
            srt.pivot = new Vector2(0.5f, 0.5f);
            srt.sizeDelta = new Vector2(900f, 26f);
            srt.anchoredPosition = new Vector2(0f, -16f);

            var bar = UiFactory.CreatePanel(title + "Accent", img.transform, accent);
            UiFactory.UseRounded(bar);
            var brt = (RectTransform)bar.transform;
            brt.anchorMin = new Vector2(0f, 0f);
            brt.anchorMax = new Vector2(0f, 1f);
            brt.pivot = new Vector2(0f, 0.5f);
            brt.sizeDelta = new Vector2(6f, -16f);
            brt.anchoredPosition = new Vector2(8f, 0f);

            return img.GetComponent<Button>();
        }

        private void Choose(GameMode mode)
        {
            ModeChosen?.Invoke(mode);
        }

        /// <summary>Campaign progress line, drawn under the tiles.</summary>
        public void SetCampaignStage(int stage, int total, bool cleared)
        {
            if (stageLabel == null) return;
            stageLabel.text = cleared
                ? $"Campaign cleared — stage {total} of {total} beaten, replaying from stage 1"
                : $"Campaign progress: stage {stage} of {total}";
        }

        /// <summary>One-line message for whatever just happened (a dropped session, an address error).</summary>
        public void SetNotice(string text)
        {
            if (notice != null) notice.text = text ?? string.Empty;
        }

        public void Show()
        {
            foreach (var tile in tiles)
                if (tile != null) tile.interactable = true;
            SetNotice(string.Empty);
            gameObject.SetActive(true);
        }

        public void Hide() => gameObject.SetActive(false);
    }
}
