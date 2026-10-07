using System.Collections.Generic;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;
using TMPro;

namespace Arena.Presentation
{
    /// <summary>
    /// Programmatic uGUI construction helpers. The menu and HUD are built in
    /// code rather than authored prefabs so the whole UI survives refactors and
    /// every string goes through the same label factory.
    ///
    /// All game text is English, so labels are plain TextMeshProUGUI — no RTL
    /// shaping pass, which sidesteps the whole class of TextMesh / RTL glyph
    /// problems the Persian build used to hit.
    /// </summary>
    public static class UiFactory
    {
        public static readonly Color Panel = new Color(0.043f, 0.075f, 0.14f, 0.94f);
        public static readonly Color Card = new Color(0.055f, 0.10f, 0.19f, 1f);
        public static readonly Color Stroke = new Color(0.14f, 0.25f, 0.42f, 1f);
        public static readonly Color Accent = new Color(0.50f, 0.83f, 1f, 1f);
        public static readonly Color TextPrimary = new Color(0.87f, 0.91f, 1f, 1f);
        public static readonly Color TextDim = new Color(0.62f, 0.71f, 0.85f, 1f);
        public static readonly Color Good = new Color(0.38f, 0.90f, 0.50f, 1f);
        public static readonly Color Warn = new Color(1f, 0.78f, 0.34f, 1f);
        public static readonly Color Bad = new Color(1f, 0.37f, 0.37f, 1f);

        // Extended palette used by the upgraded menu/HUD chrome.
        public static readonly Color Violet = new Color(0.62f, 0.48f, 1f, 1f);
        public static readonly Color Gold = new Color(1f, 0.83f, 0.42f, 1f);
        public static readonly Color Frost = new Color(0.72f, 0.90f, 1f, 1f);
        public static readonly Color DeepPanel = new Color(0.028f, 0.05f, 0.10f, 0.96f);
        public static readonly Color CardTop = new Color(0.11f, 0.18f, 0.32f, 1f);
        public static readonly Color CardBottom = new Color(0.045f, 0.08f, 0.16f, 1f);
        public static readonly Color AccentDeep = new Color(0.16f, 0.44f, 0.82f, 1f);
        public static readonly Color Muted = new Color(0.45f, 0.55f, 0.70f, 1f);

        // Loaded once from Assets/Fonts/Resources so the UI never falls back
        // to whatever TMP's default is: we build a dynamic SDF atlas from the
        // bundled Vazirmatn TTF, which has full Latin coverage for the game's
        // English strings (and rasterises glyphs on demand).
        private static TMP_FontAsset cachedFont;

        public static TMP_FontAsset UiFont
        {
            get
            {
                if (cachedFont != null) return cachedFont;

                // Preferred: a hand-authored asset dropped into a Resources folder.
                cachedFont = Resources.Load<TMP_FontAsset>("Fonts/Vazirmatn SDF");

                // Otherwise: build a dynamic atlas from the Vazirmatn TTF that
                // ships in Assets/Fonts/Resources. Glyphs are rasterised on
                // demand, which keeps first-frame cost small and works in
                // players too (the TTF is packed via Resources).
                if (cachedFont == null)
                    cachedFont = BuildRuntimeFontAsset();

                // Last resort so text never disappears entirely.
                if (cachedFont == null)
                    cachedFont = TMP_Settings.defaultFontAsset;
                return cachedFont;
            }
        }

        private static TMP_FontAsset BuildRuntimeFontAsset()
        {
            Font ttf = Resources.Load<Font>("Vazirmatn-Regular");
            if (ttf == null) return null;

            TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(ttf, 90, 9,
                GlyphRenderMode.SDFAA, 2048, 2048, AtlasPopulationMode.Dynamic, true);
            if (asset != null) asset.name = "Vazirmatn SDF (runtime)";
            return asset;
        }

        // --- procedural rounded-rect sprite --------------------------------
        // The project ships no UI sprites yet, so we generate one 64px rounded
        // square at runtime and nine-slice it. One asset serves every panel,
        // tile and bar in the game.
        private static Sprite roundedSprite;

        public static Sprite RoundedSprite
        {
            get
            {
                if (roundedSprite != null) return roundedSprite;

                const int size = 64;
                const int radius = 18;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };

                float half = size * 0.5f;
                float corner = half - radius;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float ax = Mathf.Abs(x + 0.5f - half);
                        float ay = Mathf.Abs(y + 0.5f - half);
                        float ox = Mathf.Max(0f, ax - corner);
                        float oy = Mathf.Max(0f, ay - corner);
                        // Signed distance to the rounded rect edge, in pixels.
                        float d = Mathf.Sqrt(ox * ox + oy * oy) - radius;
                        float a = Mathf.Clamp01(0.5f - d);
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                    }
                }
                tex.Apply();

                roundedSprite = Sprite.Create(tex, new Rect(0f, 0f, size, size),
                    new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                    new Vector4(radius, radius, radius, radius));
                roundedSprite.name = "Ui_Rounded";
                return roundedSprite;
            }
        }

        /// <summary>Apply the shared rounded sprite as a nine-sliced image.</summary>
        public static Image UseRounded(Image img)
        {
            img.sprite = RoundedSprite;
            img.type = Image.Type.Sliced;
            return img;
        }

        public static Canvas CreateCanvas(string name, int sortOrder)
        {
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortOrder;

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            // Reference is the prototype's 1280x720 so layouts keep their
            // proportions on tall phones; match height so 16:9 and 20:9 agree.
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f;
            return canvas;
        }

        public static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        public static RectTransform CreateStretch(RectTransform rt, float pad = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(pad, pad);
            rt.offsetMax = new Vector2(-pad, -pad);
            return rt;
        }

        public static Image CreatePanel(string name, Transform parent, Color color)
        {
            var rt = CreateRect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        public static Image CreateButton(string name, Transform parent, Color color)
        {
            var rt = CreateRect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = true;
            rt.gameObject.AddComponent<Button>();
            return img;
        }

        /// <summary>
        /// Plain LTR label. Every string in the game is English, so no RTL
        /// component or shaping pass is involved.
        /// </summary>
        public static TextMeshProUGUI CreateLabel(string name, Transform parent, string text,
            float size, Color color, TextAlignmentOptions align = TextAlignmentOptions.Center)
        {
            var rt = CreateRect(name, parent);
            var label = rt.gameObject.AddComponent<TextMeshProUGUI>();
            Configure(label, text, size, color, align);
            return label;
        }

        public static TextMeshProUGUI CreateLabel(string name, Transform parent, string text,
            float size, Color color, TextAlignmentOptions align, Vector2 anchors, Vector2 pivot,
            Vector2 anchoredPos, Vector2 sizeDelta)
        {
            var rt = CreateRect(name, parent);
            var label = rt.gameObject.AddComponent<TextMeshProUGUI>();
            Configure(label, text, size, color, align);
            rt.anchorMin = anchors;
            rt.anchorMax = anchors;
            rt.pivot = pivot;
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = sizeDelta;
            return label;
        }

        private static void Configure(TextMeshProUGUI label, string text,
            float size, Color color, TextAlignmentOptions align)
        {
            label.text = text;
            label.fontSize = size;
            label.color = color;
            label.alignment = align;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.overflowMode = TextOverflowModes.Overflow;
            label.raycastTarget = false;
            if (UiFont != null) label.font = UiFont;
        }

        /// <summary>
        /// Coloured disc carrying a short text glyph. It lives on a child object
        /// because Image and TMP both write to CanvasRenderer, not on the disc.
        /// </summary>
        public static RectTransform CreateIcon(string name, Transform parent, string emoji, float size)
        {
            var rt = CreateRect(name, parent);
            rt.sizeDelta = new Vector2(size, size);
            var disc = rt.gameObject.AddComponent<Image>();
            disc.color = new Color(0.10f, 0.20f, 0.36f, 1f);
            disc.raycastTarget = false;

            var label = CreateRect("IconText", rt);
            CreateStretch(label);
            var t = label.gameObject.AddComponent<TextMeshProUGUI>();
            t.text = emoji;
            t.fontSize = size * 0.62f;
            t.alignment = TextAlignmentOptions.Center;
            t.raycastTarget = false;
            if (UiFont != null) t.font = UiFont;
            return rt;
        }

        /// <summary>Vertical stack with even spacing — used by both menu and HUD.</summary>
        public static VerticalLayoutGroup CreateVStack(RectTransform rt, float spacing, int pad = 0)
        {
            var g = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            g.spacing = spacing;
            g.padding = new RectOffset(pad, pad, pad, pad);
            g.childAlignment = TextAnchor.MiddleCenter;
            g.childControlWidth = true;
            g.childControlHeight = true;
            g.childForceExpandWidth = true;
            g.childForceExpandHeight = false;
            return g;
        }

        public static HorizontalLayoutGroup CreateHStack(RectTransform rt, float spacing, int pad = 0)
        {
            var g = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            g.spacing = spacing;
            g.padding = new RectOffset(pad, pad, pad, pad);
            g.childAlignment = TextAnchor.MiddleCenter;
            g.childControlWidth = true;
            g.childControlHeight = true;
            g.childForceExpandWidth = false;
            g.childForceExpandHeight = false;
            return g;
        }

        public static LayoutElement Pin(RectTransform rt, float width, float height)
        {
            var le = rt.gameObject.AddComponent<LayoutElement>();
            if (width >= 0) le.preferredWidth = width;
            if (height >= 0) le.preferredHeight = height;
            return le;
        }

        /// <summary>Full-screen dim behind a modal.</summary>
        public static Image CreateModalBackdrop(string name, Transform parent)
        {
            var img = CreatePanel(name, parent, new Color(0.02f, 0.04f, 0.09f, 0.88f));
            CreateStretch((RectTransform)img.transform);
            img.raycastTarget = true;
            return img;
        }

        // =================================================================
        //  Extended toolkit (UI v2)
        //  Everything below is additive; the originals above keep working so
        //  existing call sites and the regression harness stay untouched.
        // =================================================================

        // --- generated sprites -------------------------------------------

        /// <summary>
        /// Soft radial falloff disc, white so callers tint it. Regenerated once
        /// and shared by every glow, vignette and ground-marker halo.
        /// </summary>
        private static Sprite glowSprite;
        public static Sprite GlowSprite
        {
            get
            {
                if (glowSprite != null) return glowSprite;

                const int size = 128;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };

                float half = size * 0.5f;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float dx = (x + 0.5f - half) / half;
                        float dy = (y + 0.5f - half) / half;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        float a = Mathf.Clamp01(1f - r);
                        a = a * a * (3f - 2f * a);   // smoothstep
                        a = Mathf.Pow(a, 1.6f);        // push the core up, keep the rim soft
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                    }
                }
                tex.Apply();

                glowSprite = Sprite.Create(tex, new Rect(0f, 0f, size, size),
                    new Vector2(0.5f, 0.5f), 100f);
                glowSprite.name = "Ui_Glow";
                return glowSprite;
            }
        }

        /// <summary>
        /// Hard-edged antialiased circle, white so callers tint it. Round skill
        /// dials need a real disc: a nine-sliced rounded rectangle stays square
        /// no matter how round its corners are.
        /// </summary>
        private static Sprite discSprite;
        public static Sprite DiscSprite
        {
            get
            {
                if (discSprite != null) return discSprite;

                const int size = 128;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };

                float half = size * 0.5f;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float dx = (x + 0.5f - half) / half;
                        float dy = (y + 0.5f - half) / half;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        // A single pixel of feather: crisp rim, no stair-stepping.
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01((1f - r) * half)));
                    }
                }
                tex.Apply();

                discSprite = Sprite.Create(tex, new Rect(0f, 0f, size, size),
                    new Vector2(0.5f, 0.5f), 100f);
                discSprite.name = "Ui_Disc";
                return discSprite;
            }
        }

        /// <summary>Flat disc, sized to fill its rect.</summary>
        public static Image UseDisc(Image img, Color color)
        {
            img.sprite = DiscSprite;
            img.type = Image.Type.Simple;
            img.color = color;
            return img;
        }

        /// <summary>Thin antialiased ring, used for selection halos and joystick rims.</summary>
        private static Sprite ringSprite;
        public static Sprite RingSprite
        {
            get
            {
                if (ringSprite != null) return ringSprite;

                const int size = 128;
                const float ringAt = 0.86f;
                const float ringHalf = 0.09f;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp
                };

                float half = size * 0.5f;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float dx = (x + 0.5f - half) / half;
                        float dy = (y + 0.5f - half) / half;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        float a = Mathf.Clamp01(1f - Mathf.Abs(r - ringAt) / ringHalf);
                        a = a * a * (3f - 2f * a);
                        tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                    }
                }
                tex.Apply();

                ringSprite = Sprite.Create(tex, new Rect(0f, 0f, size, size),
                    new Vector2(0.5f, 0.5f), 100f);
                ringSprite.name = "Ui_Ring";
                return ringSprite;
            }
        }

        // A tiny colour-keyed cache: the menu uses only a handful of distinct
        // gradients, so a linear scan beats allocating a dictionary per call.
        private static readonly List<Sprite> gradientSprites = new List<Sprite>();
        private static readonly List<Color> gradientTops = new List<Color>();
        private static readonly List<Color> gradientBottoms = new List<Color>();

        /// <summary>
        /// Nine-sliced rounded rectangle with a baked vertical colour gradient.
        /// Corner radius matches <see cref="RoundedSprite"/> so the two can be
        /// mixed without the silhouettes disagreeing.
        /// </summary>
        public static Sprite RoundedGradientSprite(Color top, Color bottom)
        {
            // A destroyed Sprite means a cache entry survived from an earlier
            // Play session (the editor can keep statics alive when domain reload
            // is disabled). Returning one would hand the Image a null sprite, so
            // purge dead entries first and rebuild them on demand.
            for (int i = gradientSprites.Count - 1; i >= 0; i--)
            {
                if (gradientSprites[i] == null)
                {
                    gradientSprites.RemoveAt(i);
                    gradientTops.RemoveAt(i);
                    gradientBottoms.RemoveAt(i);
                }
            }

            for (int i = 0; i < gradientSprites.Count; i++)
                if (gradientTops[i] == top && gradientBottoms[i] == bottom)
                    return gradientSprites[i];

            const int size = 64;
            const int radius = 18;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            float half = size * 0.5f;
            float corner = half - radius;
            for (int y = 0; y < size; y++)
            {
                Color row = Color.Lerp(bottom, top, y / (size - 1f));
                for (int x = 0; x < size; x++)
                {
                    float ax = Mathf.Abs(x + 0.5f - half);
                    float ay = Mathf.Abs(y + 0.5f - half);
                    float ox = Mathf.Max(0f, ax - corner);
                    float oy = Mathf.Max(0f, ay - corner);
                    float d = Mathf.Sqrt(ox * ox + oy * oy) - radius;
                    float a = Mathf.Clamp01(0.5f - d);
                    tex.SetPixel(x, y, new Color(row.r, row.g, row.b, row.a * a));
                }
            }
            tex.Apply();

            var sprite = Sprite.Create(tex, new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                new Vector4(radius, radius, radius, radius));
            sprite.name = "Ui_RoundedGradient";

            gradientSprites.Add(sprite);
            gradientTops.Add(top);
            gradientBottoms.Add(bottom);
            return sprite;
        }

        /// <summary>
        /// Rounded + vertical gradient, applied as a nine-sliced image. The
        /// gradient carries the colour, so the tint is reset to white — otherwise
        /// a caller's flat colour multiplies the ramp and muddies it.
        /// </summary>
        public static Image UseRoundedGradient(Image img, Color top, Color bottom)
        {
            img.sprite = RoundedGradientSprite(top, bottom);
            img.type = Image.Type.Sliced;
            img.color = Color.white;
            return img;
        }

        // --- composite builders ------------------------------------------

        /// <summary>Rounded panel with a vertical top→bottom gradient.</summary>
        public static Image CreateGradient(string name, Transform parent, Color top, Color bottom)
        {
            var img = CreatePanel(name, parent, Color.white);
            return UseRoundedGradient(img, top, bottom);
        }

        /// <summary>Soft radial glow, centred on its rect. Tinted by <paramref name="color"/>.</summary>
        public static Image CreateGlow(string name, Transform parent, Color color, float size)
        {
            var img = CreatePanel(name, parent, color);
            img.sprite = GlowSprite;
            img.type = Image.Type.Simple;
            ((RectTransform)img.transform).sizeDelta = new Vector2(size, size);
            return img;
        }

        /// <summary>Thin ring outline, sized to a square rect.</summary>
        public static Image CreateRing(string name, Transform parent, Color color, float size)
        {
            var img = CreatePanel(name, parent, color);
            img.sprite = RingSprite;
            img.type = Image.Type.Simple;
            ((RectTransform)img.transform).sizeDelta = new Vector2(size, size);
            return img;
        }

        /// <summary>Adds (or reuses) a coloured outline on a graphic.</summary>
        public static Outline AddOutline(Graphic graphic, Color color, float distance = 2f)
        {
            var outline = graphic.GetComponent<Outline>();
            if (outline == null) outline = graphic.gameObject.AddComponent<Outline>();
            outline.effectColor = color;
            outline.effectDistance = new Vector2(distance, -distance);
            return outline;
        }

        /// <summary>Adds (or reuses) a drop shadow on a graphic.</summary>
        public static Shadow AddShadow(Graphic graphic, Color color, Vector2 offset)
        {
            var shadow = graphic.GetComponent<Shadow>();
            if (shadow == null) shadow = graphic.gameObject.AddComponent<Shadow>();
            shadow.effectColor = color;
            shadow.effectDistance = offset;
            return shadow;
        }

        /// <summary>Guarantees a CanvasGroup for fade-based transitions.</summary>
        public static CanvasGroup EnsureCanvasGroup(GameObject go)
        {
            var group = go.GetComponent<CanvasGroup>();
            if (group == null) group = go.AddComponent<CanvasGroup>();
            return group;
        }
    }
}
