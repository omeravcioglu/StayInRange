using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// A keyboard key as the design draws it: a sketchy dark cap with a thick bottom stroke and the
    /// key's name in Coming Soon.
    ///
    /// Long names (SPACE, MOUSE 1) stretch the cap sideways the way the canvas does, and a key that
    /// is fixed - or an action that cannot happen right now - greys out rather than disappearing, so
    /// the player still learns which key it would be.
    /// </summary>
    public class KeyCap : MonoBehaviour
    {
        static readonly TextStyle LetterStyle =
            new TextStyle(FontRole.Body, 28f, ColorRole.White, Ink.None, 0f, TextAlignmentOptions.Center);

        Image _cap;
        TextMeshProUGUI _label;
        LayoutElement _layout;
        RectTransform _rect;
        float _height;

        public string Key { get; private set; } = string.Empty;
        public bool IsEnabled { get; private set; } = true;

        public static KeyCap Create(Transform parent, string key, float height, bool enabled = true)
        {
            var rect = UiKit.CreateRect("Key " + key, parent);
            var cap = rect.gameObject.AddComponent<KeyCap>();
            cap.Build(height);
            cap.Set(key, enabled);
            return cap;
        }

        void Build(float height)
        {
            _height = height;
            _rect = (RectTransform)transform;
            _layout = gameObject.AddComponent<LayoutElement>();

            _cap = UiKit.CreateImage(transform, "Cap", UiSprites.KeyCap, Color.white, sliced: true);
            _cap.rectTransform.Fill();

            _label = UiKit.CreateText(transform, "Label", string.Empty, LetterStyle);
            // The thick bottom stroke makes the cap's optical middle sit higher than its real one.
            _label.rectTransform.Fill();
            _label.rectTransform.offsetMin = new Vector2(0f, height * 0.1f);
        }

        public void Set(string key, bool enabled = true)
        {
            Key = key ?? string.Empty;
            IsEnabled = enabled;

            var sprite = UiKit.GetSprite(enabled ? UiSprites.KeyCap : UiSprites.KeyCapOff);
            _cap.sprite = sprite;
            _cap.type = sprite != null && sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
            // Without the theme built there is no cap drawing; a dark square still reads as a key.
            _cap.color = sprite != null ? Color.white : new Color(0.04f, 0.04f, 0.05f, 0.6f);

            bool wide = Key.Length > 1;
            _label.fontSize = _height * (wide ? 0.36f : 0.52f);
            _label.color = enabled ? UiTheme.Active.white : UiTheme.Rgb(0x6E7876);
            _label.text = Key;

            float width = _height;
            if (wide)
                width = Mathf.Max(_height * 1.4f, _label.GetPreferredValues(Key).x + _height * 0.6f);

            _rect.sizeDelta = new Vector2(width, _height);
            _layout.minWidth = _layout.preferredWidth = width;
            _layout.minHeight = _layout.preferredHeight = _height;
        }
    }
}
