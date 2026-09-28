using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// The one place the locally chosen character skin lives. Saved in PlayerPrefs so the pick made
    /// in the lobby survives the scene load into Game (the networked player reads it back the instant
    /// it spawns) and is remembered between sessions. The index is replicated per player through
    /// FpsNetworkBridge.CharacterIndex, so everyone sees everyone else's pick.
    /// </summary>
    public static class CharacterSelection
    {
        const string Key = "CollarCali.CharacterSkin";

        public static int Count
        {
            get
            {
                var lib = CharacterSkinLibrary.Load();
                return lib != null ? lib.Count : 0;
            }
        }

        public static int SelectedIndex
        {
            get
            {
                int raw = PlayerPrefs.GetInt(Key, 0);
                var lib = CharacterSkinLibrary.Load();
                return lib != null ? lib.ClampIndex(raw) : Mathf.Max(0, raw);
            }
            set
            {
                var lib = CharacterSkinLibrary.Load();
                int clamped = lib != null ? lib.ClampIndex(value) : Mathf.Max(0, value);
                PlayerPrefs.SetInt(Key, clamped);
                PlayerPrefs.Save();
            }
        }

        /// <summary>
        /// Points every skinned mesh under <paramref name="visualRoot"/> at the chosen skin material.
        /// Uses shared materials so nothing is instanced per player - each player's own renderers just
        /// reference whichever skin asset they picked, which keeps two players on different skins from
        /// bleeding into one another.
        /// </summary>
        public static void Apply(GameObject visualRoot, int index)
        {
            if (visualRoot == null)
                return;

            var lib = CharacterSkinLibrary.Load();
            if (lib == null || lib.Count == 0)
                return;

            var skin = lib.Get(index);
            if (skin == null || skin.material == null)
                return;

            foreach (var smr in visualRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null)
                    continue;

                var mats = smr.sharedMaterials;
                if (mats == null || mats.Length == 0)
                {
                    smr.sharedMaterial = skin.material;
                    continue;
                }

                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != skin.material)
                    {
                        mats[i] = skin.material;
                        changed = true;
                    }
                }

                if (changed)
                    smr.sharedMaterials = mats;
            }
        }

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        /// <summary>
        /// Dresses a body the way a player in <paramref name="colourIndex"/> looks in the game, by the
        /// same rule FpsNetworkBridge.ApplyIdentity uses: with a skin per colour (the shared body
        /// recoloured four ways) the body wears that colour's skin; with a single skin (one character
        /// model) it wears that skin painted in the player colour.
        ///
        /// Painted through a property block rather than instanced materials, so a preview that changes
        /// colour every click leaves no material copies behind. For previews - the lobby's turntable -
        /// not for networked bodies, which keep FpsNetworkBridge's own path.
        /// </summary>
        public static void ApplyPlayerLook(GameObject visualRoot, int colourIndex)
        {
            if (visualRoot == null)
                return;

            var lib = CharacterSkinLibrary.Load();
            bool perColour = lib != null && lib.Count >= PlayerColorPalette.Count;
            Apply(visualRoot, perColour ? colourIndex : 0);

            var block = new MaterialPropertyBlock();
            var tint = PlayerColorPalette.Get(colourIndex);
            foreach (var renderer in visualRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null)
                    continue;

                if (perColour)
                {
                    // The skin carries the colour; clear any tint an earlier pick left behind.
                    renderer.SetPropertyBlock(null);
                    continue;
                }

                renderer.GetPropertyBlock(block);
                block.SetColor(BaseColorId, tint);
                block.SetColor(ColorId, tint);
                renderer.SetPropertyBlock(block);
            }
        }
    }
}
