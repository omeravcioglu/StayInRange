using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// Builders for the pieces every redesigned screen is made of, so they are assembled the same
    /// way everywhere: canvases on the one scaler standard, text on the theme's faces and outline
    /// presets, and the hand-drawn sprites.
    ///
    /// The team's older overlays each carried private CreateRect / CreateText helpers with their own
    /// fonts and colours; this replaces them for the new UI. Sizes are in design pixels at the
    /// 1920x1080 reference, the same numbers as the canvas boards.
    /// </summary>
    public static class UiKit
    {
        public static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1080f);

        static Sprite _whiteFallback;

        /// <summary>
        /// A screen-space canvas at the reference resolution, scaled so the whole 1920x1080 layout
        /// always fits: on a screen wider or narrower than 16:9 the canvas grows in the spare
        /// direction instead of cropping the other. (Matching width and height halfway, as the
        /// vendor canvases do, took height away on a wide screen and pushed the titles at the top
        /// off it.)
        /// </summary>
        public static Canvas CreateCanvas(string name, int sortingOrder, bool interactive = false,
            Transform parent = null)
        {
            var go = new GameObject(name, typeof(RectTransform));
            if (parent != null)
                go.transform.SetParent(parent, false);

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;

            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = ReferenceResolution;
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;

            // HUD canvases take no clicks, so they never steal them from a menu underneath.
            if (interactive)
                go.AddComponent<GraphicRaycaster>();

            return canvas;
        }

        public static RectTransform CreateRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        /// <summary>
        /// Pins a rect to a point of its parent, with the pivot on the same point: anchor (0, 1) and
        /// position (24, -24) is "24 px in from the top-left corner", as the boards measure it.
        /// </summary>
        public static RectTransform Place(this RectTransform rect, Vector2 anchor, Vector2 position, Vector2 size)
        {
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return rect;
        }

        /// <summary>Stretches a rect over its parent, inset on every side.</summary>
        public static RectTransform Fill(this RectTransform rect, float inset = 0f)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
            return rect;
        }

        /// <summary>
        /// Places a rect by a board's own numbers - its top-left corner and size on the 1920x1080
        /// page - pinned to <paramref name="anchor"/> of a full-screen parent, so it stays put on other
        /// aspects: (0, 0.5) keeps a left-hand menu column on the left edge, (0.5, 0.5) keeps a centred
        /// group together. The pivot is the rect's left middle, where the boards' titles turn, or its
        /// top-left corner with <paramref name="fromTop"/> - for columns that grow downwards.
        /// </summary>
        public static RectTransform AtBoard(this RectTransform rect, Vector2 anchor, float left, float top,
            float width, float height, bool fromTop = false)
        {
            float anchorX = anchor.x * ReferenceResolution.x;
            float anchorY = (1f - anchor.y) * ReferenceResolution.y;
            rect.anchorMin = rect.anchorMax = anchor;
            rect.pivot = fromTop ? new Vector2(0f, 1f) : new Vector2(0f, 0.5f);
            float y = fromTop ? top : top + height * 0.5f;
            rect.anchoredPosition = new Vector2(left - anchorX, anchorY - y);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        /// <summary>
        /// A full-screen picture that keeps its shape: it covers the parent and crops the overflow,
        /// so the menu art is never squashed on a screen that is not 16:9.
        /// </summary>
        public static Image CreateBackdrop(Transform parent, string sprite, Color tint)
        {
            var image = CreateImage(parent, "Backdrop " + sprite, sprite, tint);
            image.rectTransform.anchorMin = image.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            image.rectTransform.sizeDelta = ReferenceResolution;
            var fitter = image.gameObject.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
            fitter.aspectRatio = ReferenceResolution.x / ReferenceResolution.y;
            return image;
        }

        /// <summary>
        /// Tilts a rect by the boards' own number: the design's rotate(-3deg) leans a title up to
        /// the right, and Unity's z rotation turns the other way, so the sign is flipped here.
        /// </summary>
        public static RectTransform Tilt(this RectTransform rect, float designDegrees)
        {
            rect.localRotation = Quaternion.Euler(0f, 0f, -designDegrees);
            return rect;
        }

        /// <summary>
        /// A label in one of the design's text styles. Rich text is off: player and room names go
        /// through here, and a name must never be parsed as markup.
        /// </summary>
        public static TextMeshProUGUI CreateText(Transform parent, string name, string text, TextStyle style)
        {
            var rect = CreateRect(name, parent);
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            label.raycastTarget = false;
            label.richText = false;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
            ApplyStyle(label, style);
            label.text = text;
            return label;
        }

        public static void ApplyStyle(TMP_Text label, TextStyle style)
        {
            var theme = UiTheme.Active;

            var font = theme.GetFont(style.Face);
            if (font != null)
            {
                // Through the property, not the field, so TMP repoints the material at the new atlas.
                label.font = font;
            }

            var preset = theme.GetInkMaterial(style.Face, style.Weight);
            if (preset != null)
            {
                label.fontSharedMaterial = preset;
            }
            else if (style.Weight != Ink.None)
            {
                // No preset until the theme is built: a per-text outline keeps it readable meanwhile,
                // at the cost of one material instance per label.
                label.outlineWidth = style.Weight == Ink.Heavy ? 0.2f : 0.12f;
                label.outlineColor = new Color32(11, 11, 12, 230);
            }

            label.fontSize = style.Size;
            label.color = theme.GetColor(style.Tint);
            label.characterSpacing = style.Spacing;
            label.alignment = style.Align;
        }

        /// <summary>
        /// A theme sprite by name. The plain white square has a runtime stand-in, because filled bars
        /// need a sprite to fill and must work before the theme is built.
        /// </summary>
        public static Sprite GetSprite(string name)
        {
            var sprite = UiTheme.Active.GetSprite(name);
            if (sprite != null || name != UiSprites.White)
                return sprite;

            if (_whiteFallback == null)
            {
                var texture = Texture2D.whiteTexture;
                _whiteFallback = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f));
                _whiteFallback.hideFlags = HideFlags.DontSave;
            }

            return _whiteFallback;
        }

        /// <summary>An image of a theme sprite. Sliced only when the sprite has 9-slice borders.</summary>
        public static Image CreateImage(Transform parent, string name, string sprite, Color color, bool sliced = false)
        {
            var rect = CreateRect(name, parent);
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = GetSprite(sprite);
            image.type = sliced && image.sprite != null && image.sprite.border != Vector4.zero
                ? Image.Type.Sliced
                : Image.Type.Simple;
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>A player's colour blob with its ink rim - how a player is shown everywhere.</summary>
        public static IconStack CreateSwatch(Transform parent, Color color, float size)
        {
            return IconStack.Create(parent, "Swatch", new Vector2(size, size), color,
                IconStack.TintLayer(UiSprites.BlobFill), IconStack.PlainLayer(UiSprites.BlobInk));
        }

        /// <summary>A two-piece icon: a black ink stroke with a tinted line drawn over it.</summary>
        public static IconStack CreateIcon(Transform parent, string name, float size, string ink, string line, Color tint)
        {
            return IconStack.Create(parent, name, new Vector2(size, size), tint,
                IconStack.PlainLayer(ink), IconStack.TintLayer(line));
        }

        /// <summary>
        /// A left-to-right row. It sizes itself to its children unless <paramref name="fitToContent"/>
        /// is off - which it must be inside another layout group, where the parent does the sizing.
        /// </summary>
        public static RectTransform CreateRow(Transform parent, string name, float spacing,
            TextAnchor alignment = TextAnchor.MiddleLeft, bool fitToContent = true)
        {
            var rect = CreateRect(name, parent);

            var layout = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.childAlignment = alignment;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            if (fitToContent)
                FitToContent(rect);
            return rect;
        }

        /// <summary>A top-to-bottom column; same sizing rule as <see cref="CreateRow"/>.</summary>
        public static RectTransform CreateColumn(Transform parent, string name, float spacing,
            TextAnchor alignment = TextAnchor.UpperLeft, bool fitToContent = true)
        {
            var rect = CreateRect(name, parent);

            var layout = rect.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.childAlignment = alignment;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            if (fitToContent)
                FitToContent(rect);
            return rect;
        }

        static void FitToContent(RectTransform rect)
        {
            var fitter = rect.gameObject.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        /// <summary>Gives a layout child a fixed size.</summary>
        public static T Sized<T>(this T component, float width, float height) where T : Component
        {
            var element = component.GetComponent<LayoutElement>();
            if (element == null)
                element = component.gameObject.AddComponent<LayoutElement>();
            element.minWidth = element.preferredWidth = width;
            element.minHeight = element.preferredHeight = height;
            ((RectTransform)component.transform).sizeDelta = new Vector2(width, height);
            return component;
        }

        /// <summary>A key and what it does, the way every prompt shows it: [E] Lift DOT.</summary>
        public static RectTransform CreateAction(Transform parent, string key, string label, TextStyle style,
            float keyHeight = 54f)
        {
            var row = CreateRow(parent, "Action " + key, 12f);
            KeyCap.Create(row, key, keyHeight);
            CreateText(row, "Label", label, style);
            return row;
        }

        /// <summary>
        /// Removes a piece of UI in play mode or in the editor (the gallery captures build UI outside
        /// play mode, where Destroy is not allowed). Hidden first, so a layout does not keep the
        /// dying object in its row for the rest of the frame.
        /// </summary>
        public static void DestroyObject(GameObject target)
        {
            if (target == null)
                return;

            target.SetActive(false);
            if (Application.isPlaying)
                Object.Destroy(target);
            else
                Object.DestroyImmediate(target);
        }

        /// <summary>
        /// Makes sure clicks reach the UI: the Game scene has no EventSystem of its own. Returns the
        /// one created, or null when one already existed (the caller then must not remove it).
        /// </summary>
        public static GameObject EnsureEventSystem()
        {
            if (UnityEngine.EventSystems.EventSystem.current != null ||
                Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() != null)
                return null;

            return new GameObject("EventSystem (UI)", typeof(UnityEngine.EventSystems.EventSystem),
                typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));
        }

        /// <summary>The charge colour, yellow to red: the same lerp BodyCarryHud has always used.</summary>
        public static Color ChargeColor(float charge01)
        {
            return Color.Lerp(new Color(1f, 0.85f, 0.25f), new Color(1f, 0.25f, 0.15f), Mathf.Clamp01(charge01));
        }
    }
}
