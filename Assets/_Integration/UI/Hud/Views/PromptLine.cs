using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CollarCali.UI
{
    /// <summary>
    /// One line of prompt, written the way the game code already writes prompts -
    /// "[E]  Drop      [Q]  Throw" - and drawn the way the design draws them: every [KEY] becomes a
    /// key cap (LMB and RMB become the mouse glyph), the words between become labels, and a long
    /// run of spaces becomes the wider gap between two actions.
    ///
    /// It rebuilds only when the text changes, so callers can set it every frame.
    /// </summary>
    public class PromptLine : MonoBehaviour
    {
        const float ActionGap = 32f;

        readonly List<GameObject> _items = new List<GameObject>();
        RectTransform _row;
        string _text;
        Color _colour;
        float _size;
        bool _keysEnabled = true;

        enum Kind
        {
            Text,
            Key,
            Gap,
        }

        readonly struct Token
        {
            public readonly Kind Kind;
            public readonly string Value;

            public Token(Kind kind, string value)
            {
                Kind = kind;
                Value = value;
            }
        }

        public static PromptLine Create(Transform parent, string name)
        {
            var row = UiKit.CreateRow(parent, name, 12f, TextAnchor.MiddleCenter);
            var line = row.gameObject.AddComponent<PromptLine>();
            line._row = row;
            return line;
        }

        public string Text => _text;

        /// <summary>Sets the line. <paramref name="size"/> is the label size; keys scale with it.</summary>
        public void Set(string text, Color colour, float size = 38f, bool keysEnabled = true)
        {
            if (text == _text && colour == _colour && Mathf.Approximately(size, _size) && keysEnabled == _keysEnabled)
                return;

            _text = text;
            _colour = colour;
            _size = size;
            _keysEnabled = keysEnabled;

            foreach (var item in _items)
                UiKit.DestroyObject(item);
            _items.Clear();

            if (string.IsNullOrEmpty(text))
                return;

            var style = TextStyle.Prompt.WithSize(size);
            foreach (var token in Parse(text))
            {
                switch (token.Kind)
                {
                    case Kind.Gap:
                        _items.Add(UiKit.CreateRect("Gap", _row).Sized(ActionGap, 1f).gameObject);
                        break;

                    case Kind.Key:
                        _items.Add(CreateKey(token.Value, size));
                        break;

                    default:
                        var label = UiKit.CreateText(_row, "Label", token.Value, style);
                        label.color = colour;
                        _items.Add(label.gameObject);
                        break;
                }
            }
        }

        GameObject CreateKey(string key, float size)
        {
            string mouse = MouseSprite(key);
            if (mouse != null)
            {
                float height = size * 1.16f;
                var image = UiKit.CreateImage(_row, "Mouse " + key, mouse, Color.white).Sized(height * 44f / 64f, height);
                image.preserveAspect = true;
                if (!_keysEnabled)
                    image.color = new Color(1f, 1f, 1f, 0.45f);
                return image.gameObject;
            }

            return KeyCap.Create(_row, key, size * 1.42f, _keysEnabled).gameObject;
        }

        static string MouseSprite(string key)
        {
            switch (key.ToUpperInvariant())
            {
                case "LMB":
                case "MOUSE 1":
                case "MOUSE1":
                    return UiSprites.MouseLeft;
                case "RMB":
                case "MOUSE 2":
                case "MOUSE2":
                    return UiSprites.MouseRight;
                default:
                    return null;
            }
        }

        /// <summary>"[E]  Drop      [Q]  Throw" into Key E, Text Drop, Gap, Key Q, Text Throw.</summary>
        static List<Token> Parse(string text)
        {
            var tokens = new List<Token>();
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '[')
                {
                    int close = text.IndexOf(']', i + 1);
                    if (close > i)
                    {
                        tokens.Add(new Token(Kind.Key, text.Substring(i + 1, close - i - 1).Trim()));
                        i = close + 1;
                        continue;
                    }
                }

                int next = text.IndexOf('[', i);
                if (next < 0)
                    next = text.Length;
                AddText(tokens, text.Substring(i, next - i));
                i = next;
            }

            return tokens;
        }

        static void AddText(List<Token> tokens, string chunk)
        {
            // Four or more spaces separate two actions; fewer are just spacing inside one.
            var parts = chunk.Split(new[] { "    " }, System.StringSplitOptions.None);
            for (int p = 0; p < parts.Length; p++)
            {
                var part = parts[p].Trim();
                if (p > 0 && tokens.Count > 0 && tokens[tokens.Count - 1].Kind != Kind.Gap)
                    tokens.Add(new Token(Kind.Gap, null));
                if (part.Length > 0)
                    tokens.Add(new Token(Kind.Text, part));
            }
        }
    }
}
