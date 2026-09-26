using TMPro;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// The game's two fonts, in one place.
    ///
    /// Display is the characterful one used for titles and stingers; body is the readable one used
    /// for anything small - ammo, distances, prompts. A brush face like Knewave is unreadable at HUD
    /// sizes, which is the whole reason for keeping two.
    ///
    /// Lives in Resources so the runtime UI that is built in code - the wipe card, the grabbed
    /// overlay, the spectator label, the world markers - can load the same fonts the editor tool
    /// assigned to every prefab and scene. Referencing the fonts from here is also what pulls them
    /// into a build, the same way GameSfxLibrary pulls in the audio clips.
    ///
    /// Built by Tools/CollarCali/Apply Game Fonts.
    /// </summary>
    [CreateAssetMenu(menuName = "CollarCali/Game Font Set", fileName = "GameFontSet")]
    public class GameFontSet : ScriptableObject
    {
        const string ResourceName = "GameFontSet";

        [Header("TextMeshPro")]
        [Tooltip("Titles, headings, YOU DIED - anything large and characterful.")]
        public TMP_FontAsset display;

        [Tooltip("HUD, prompts, numbers - anything that has to stay legible when small.")]
        public TMP_FontAsset body;

        [Header("Legacy UI.Text")]
        [Tooltip("The same display face as a plain font, for the overlays built with UnityEngine.UI.Text.")]
        public Font legacyDisplay;

        [Tooltip("The same body face as a plain font.")]
        public Font legacyBody;

        static GameFontSet _instance;
        static bool _resolved;

        /// <summary>
        /// The set, or null when it has not been built yet.
        ///
        /// Callers are expected to cope with null: the UI falls back to Unity's built-in font so a
        /// missing font set costs you the styling, not the text.
        /// </summary>
        public static GameFontSet Load()
        {
            if (_resolved)
                return _instance;

            _resolved = true;
            _instance = Resources.Load<GameFontSet>(ResourceName);
            if (_instance == null)
            {
                Debug.LogWarning("[CollarCali] No GameFontSet in Resources - UI built in code will use " +
                                 "Unity's built-in font. Run Tools/CollarCali/Apply Game Fonts.");
            }

            return _instance;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetState()
        {
            // Statics outlive a play session in the editor.
            _instance = null;
            _resolved = false;
        }

        /// <summary>The display face for legacy Text, falling back to the built-in font.</summary>
        public static Font LegacyDisplayOrDefault()
        {
            var set = Load();
            if (set != null && set.legacyDisplay != null)
                return set.legacyDisplay;
            return BuiltinFallback();
        }

        /// <summary>The body face for legacy Text, falling back to the built-in font.</summary>
        public static Font LegacyBodyOrDefault()
        {
            var set = Load();
            if (set != null && set.legacyBody != null)
                return set.legacyBody;
            return BuiltinFallback();
        }

        static Font BuiltinFallback()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return font;
        }
    }
}
