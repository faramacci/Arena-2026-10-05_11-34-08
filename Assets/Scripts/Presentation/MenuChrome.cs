using UnityEngine;

namespace Arena.Presentation
{
    /// <summary>
    /// The dark backdrop every full-screen menu shares: a vertical wash, a few
    /// drifting glows and a horizon band. Purely decorative — every layer is
    /// raycast-transparent so buttons built on top stay tappable.
    /// </summary>
    internal static class MenuChrome
    {
        /// <summary>Root panel for one screen; the caller parents its content here.</summary>
        public static RectTransform Backdrop(Transform canvas, string name)
        {
            var panel = UiFactory.CreatePanel(name, canvas, new Color(0.015f, 0.03f, 0.07f, 1f));
            var rt = (RectTransform)panel.transform;
            UiFactory.CreateStretch(rt);

            var wash = UiFactory.CreateGradient("AtmosWash", panel.transform,
                new Color(0.06f, 0.12f, 0.24f, 1f), new Color(0.012f, 0.02f, 0.05f, 1f));
            UiFactory.CreateStretch((RectTransform)wash.transform);

            AddBlob(panel.transform, "GlowCyan", new Color(0.28f, 0.62f, 1f, 0.28f),
                new Vector2(-380f, 220f), 900f, 34f);
            AddBlob(panel.transform, "GlowViolet", new Color(0.55f, 0.36f, 1f, 0.20f),
                new Vector2(440f, -200f), 780f, 41f);

            var horizon = UiFactory.CreatePanel("Horizon", panel.transform,
                new Color(0.10f, 0.20f, 0.38f, 0.35f));
            var hrt = (RectTransform)horizon.transform;
            hrt.anchorMin = new Vector2(0f, 0f);
            hrt.anchorMax = new Vector2(1f, 0f);
            hrt.pivot = new Vector2(0.5f, 0f);
            hrt.sizeDelta = new Vector2(0f, 3f);
            hrt.anchoredPosition = new Vector2(0f, 96f);

            return rt;
        }

        private static void AddBlob(Transform parent, string name, Color color,
            Vector2 anchoredPos, float size, float speed)
        {
            var glow = UiFactory.CreateGlow(name, parent, color, size);
            var rt = (RectTransform)glow.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            var drift = glow.gameObject.AddComponent<UiDrift>();
            drift.Configure(26f, speed, 0.6f, 1f);
        }
    }
}
