using System.Collections.Generic;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// The selectable looks for the player character. All four are the same Meshy humanoid model
    /// wearing a different skin material, so "character selection" is really a material index that
    /// everyone agrees on. Lives in Resources so both the lobby UI and the networked player can load
    /// it without a scene reference. Built/refreshed by Tools/CollarCali/Build Character Visuals.
    /// </summary>
    [CreateAssetMenu(menuName = "CollarCali/Character Skin Library", fileName = "CharacterSkins")]
    public class CharacterSkinLibrary : ScriptableObject
    {
        [System.Serializable]
        public class Skin
        {
            public string displayName = "Character";
            public Material material;

            [Tooltip("Small colour used for the lobby roster swatch so players can tell picks apart at a glance.")]
            public Color uiTint = Color.white;
        }

        public List<Skin> skins = new List<Skin>();

        public int Count => skins != null ? skins.Count : 0;

        /// <summary>Wraps an arbitrary index into a valid slot (handles negatives from Previous()).</summary>
        public int ClampIndex(int index)
        {
            if (Count == 0)
                return 0;
            return ((index % Count) + Count) % Count;
        }

        public Skin Get(int index)
        {
            if (Count == 0)
                return null;
            return skins[ClampIndex(index)];
        }

        public string NameOf(int index)
        {
            var skin = Get(index);
            return skin != null && !string.IsNullOrEmpty(skin.displayName)
                ? skin.displayName
                : "Character " + (ClampIndex(index) + 1);
        }

        public Color TintOf(int index)
        {
            var skin = Get(index);
            return skin != null ? skin.uiTint : Color.white;
        }

        public const string ResourceName = "CharacterSkins";

        static CharacterSkinLibrary _cached;

        public static CharacterSkinLibrary Load()
        {
            if (_cached == null)
                _cached = Resources.Load<CharacterSkinLibrary>(ResourceName);
            return _cached;
        }
    }
}
