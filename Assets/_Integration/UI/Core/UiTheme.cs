using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace CollarCali.UI
{
    /// <summary>Which of the game's two faces a piece of text uses.</summary>
    public enum FontRole
    {
        /// <summary>Knewave: titles, headings, player names, big numbers.</summary>
        Display,

        /// <summary>Coming Soon: everything else - prompts, instructions, distances, labels.</summary>
        Body,
    }

    /// <summary>How heavy the black outline and shadow around text is.</summary>
    public enum Ink
    {
        None,

        /// <summary>A thin rim and a soft glow, for small print that sits in quiet corners.</summary>
        Soft,

        /// <summary>A thick rim and a hard drop, for anything that has to read over a dark, busy scene.</summary>
        Heavy,
    }

    /// <summary>The design's named colours. Resolved through the theme so they can be tuned in one place.</summary>
    public enum ColorRole
    {
        White,
        Cream,
        Faint,
        Muted,
        Dim,
        Ink,
        Safe,
        Warning,
        Danger,
        Health,
        Shield,
        Heart,
        HeartLow,
        Revive,
        ReviveDeep,
        Brush,
        Track,
    }

    /// <summary>
    /// Everything the redesigned UI looks like, in one asset: the two fonts and their outline
    /// presets, the palette, and the hand-drawn sprites.
    ///
    /// The UI is built in code, so the look lives here rather than in prefabs: change a colour or
    /// swap a sprite in the asset and every screen built from it follows. Lives in Resources for the
    /// same reason as GameFontSet - the code-built UI has no scene or prefab to hold a reference.
    ///
    /// Built by Tools/CollarCali/UI/Build Theme. Until that has run, <see cref="Active"/> hands out
    /// an in-memory theme with the design's colours and whatever fonts exist, so nothing breaks - it
    /// just renders without the hand-drawn sprites and outline presets.
    ///
    /// Player colours are deliberately not here: they are gameplay identity, owned by
    /// <see cref="PlayerColorPalette"/>, and the body tint and the HUD must never disagree.
    /// </summary>
    [CreateAssetMenu(menuName = "CollarCali/UI Theme", fileName = "UiTheme")]
    public class UiTheme : ScriptableObject
    {
        const string ResourceName = "UiTheme";

        [Serializable]
        public struct NamedSprite
        {
            public string name;
            public Sprite sprite;
        }

        [Header("Fonts")]
        [Tooltip("Knewave SDF: titles, headings, player names, big numbers.")]
        public TMP_FontAsset display;

        [Tooltip("Coming Soon SDF: every other piece of text.")]
        public TMP_FontAsset body;

        [Tooltip("Knewave with a heavy ink rim and drop shadow.")]
        public Material displayInk;

        [Tooltip("Coming Soon with a heavy ink rim and shadow, for prompts and numbers.")]
        public Material bodyInk;

        [Tooltip("Coming Soon with a thin rim and a soft glow, for small print.")]
        public Material bodySoft;

        [Header("Palette")]
        public Color white = Color.white;
        public Color cream = Rgb(0xF4F1E8);
        public Color faint = Rgb(0xC9CFCD);
        public Color muted = Rgb(0xB9C0BE);
        public Color dim = Rgb(0x5D6664);
        public Color ink = Rgb(0x0B0B0C);

        [Tooltip("Collar: a teammate under 20 m. Green is never a player colour.")]
        public Color safe = Rgb(0x7CE36A);

        [Tooltip("Collar: 20 m and up.")]
        public Color warning = Rgb(0xFFD447);

        [Tooltip("Collar: 25 m and up. Also low health and DOWN.")]
        public Color danger = Rgb(0xFF5A52);

        public Color health = Rgb(0x5BD83A);
        public Color shield = Rgb(0xBFE9FF);
        public Color heart = Rgb(0xFF5A6E);
        public Color heartLow = Rgb(0xFF2E3E);
        public Color revive = Rgb(0x59FF8C);
        public Color reviveDeep = Rgb(0x3BE070);

        [Tooltip("The red brush stroke under a focused menu item.")]
        public Color brush = Rgb(0xC8160F);

        [Tooltip("The empty part of a bar.")]
        public Color track = Rgb(0x1C211F);

        [Header("Sprites")]
        [Tooltip("Filled in by the builder from Assets/_Integration/UI/Sprites. Names are in UiSprites.")]
        public List<NamedSprite> sprites = new List<NamedSprite>();

        [Header("Loading")]
        [TextArea]
        public string[] loadingTips =
        {
            "Bodies still count for the collar. Carry your friends.",
            "Past 30 m the collar starts counting. Five seconds, then it yanks.",
            "A green light means a revive station. Bring a body.",
            "Everybody on the pad, or nobody saves.",
            "You can throw a friend. Aim first.",
            "A creep lets go if your team shoots it off you.",
        };

        static UiTheme _active;
        static bool _resolved;

        Dictionary<string, Sprite> _spriteLookup;

        /// <summary>
        /// The theme asset, or an in-memory stand-in with the design's defaults when it has not been
        /// built yet. Never null.
        /// </summary>
        public static UiTheme Active
        {
            get
            {
                if (_resolved && _active != null)
                    return _active;

                _resolved = true;
                _active = Resources.Load<UiTheme>(ResourceName);
                if (_active == null)
                {
                    Debug.LogWarning("[CollarCali] No UiTheme in Resources - the UI will draw without its sprites " +
                                     "and outline presets. Run Tools/CollarCali/UI/Build Theme.");
                    _active = CreateInstance<UiTheme>();
                    _active.hideFlags = HideFlags.DontSave;
                }

                return _active;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetState()
        {
            // Statics outlive a play session in the editor.
            _active = null;
            _resolved = false;
        }

        void OnValidate()
        {
            // A sprite renamed or swapped in the inspector must not keep answering with the old one.
            _spriteLookup = null;
        }

        /// <summary>
        /// The face for a role: this theme's own, then the older GameFontSet, then TMP's default, so
        /// text always renders even before anything has been built.
        /// </summary>
        public TMP_FontAsset GetFont(FontRole role)
        {
            var own = role == FontRole.Display ? display : body;
            if (own != null)
                return own;

            var set = GameFontSet.Load();
            var legacy = set == null ? null : role == FontRole.Display ? set.display : set.body;
            return legacy != null ? legacy : TMP_Settings.defaultFontAsset;
        }

        /// <summary>
        /// The outline preset for a face, or null when there is none - including when the theme has no
        /// font of its own for that role, since a preset only works on the atlas it was made from.
        /// </summary>
        public Material GetInkMaterial(FontRole role, Ink weight)
        {
            if (weight == Ink.None)
                return null;

            if (role == FontRole.Display)
                return display != null ? displayInk : null;

            if (body == null)
                return null;
            return weight == Ink.Soft && bodySoft != null ? bodySoft : bodyInk;
        }

        public Color GetColor(ColorRole role)
        {
            switch (role)
            {
                case ColorRole.Cream: return cream;
                case ColorRole.Faint: return faint;
                case ColorRole.Muted: return muted;
                case ColorRole.Dim: return dim;
                case ColorRole.Ink: return ink;
                case ColorRole.Safe: return safe;
                case ColorRole.Warning: return warning;
                case ColorRole.Danger: return danger;
                case ColorRole.Health: return health;
                case ColorRole.Shield: return shield;
                case ColorRole.Heart: return heart;
                case ColorRole.HeartLow: return heartLow;
                case ColorRole.Revive: return revive;
                case ColorRole.ReviveDeep: return reviveDeep;
                case ColorRole.Brush: return brush;
                case ColorRole.Track: return track;
                default: return white;
            }
        }

        /// <summary>
        /// The collar colour for a distance, on the same thresholds as TeamDistanceManager: safe under
        /// 20 m, warning from 20 m, danger from 25 m.
        /// </summary>
        public Color GetTetherColor(float metres)
        {
            if (metres >= TetherDangerMetres)
                return danger;
            return metres >= TetherWarningMetres ? warning : safe;
        }

        // Mirrors TeamDistanceManager.WarningStart / DangerStart, which live behind the multiplayer
        // define; the gallery and offline UI need them without a session.
        public const float TetherWarningMetres = 20f;
        public const float TetherDangerMetres = 25f;

        /// <summary>A sprite by name (see <see cref="UiSprites"/>), or null when the theme has not been built.</summary>
        public Sprite GetSprite(string name)
        {
            if (_spriteLookup == null)
            {
                _spriteLookup = new Dictionary<string, Sprite>(StringComparer.Ordinal);
                foreach (var entry in sprites)
                {
                    if (!string.IsNullOrEmpty(entry.name) && entry.sprite != null)
                        _spriteLookup[entry.name] = entry.sprite;
                }
            }

            return _spriteLookup.TryGetValue(name, out var sprite) ? sprite : null;
        }

        /// <summary>A colour from a 0xRRGGBB literal, so the palette reads like the design's hex values.</summary>
        public static Color Rgb(int hex, float alpha = 1f)
        {
            return new Color(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, alpha);
        }
    }
}
