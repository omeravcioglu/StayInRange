#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace CollarCali.UI.EditorTools
{
    /// <summary>
    /// Builds the assets the redesigned UI draws with, and nothing else:
    /// - the Knewave and Coming Soon SDF font assets, with a fallback for letters neither face has
    ///   (Turkish Ş, Ğ and İ in player names);
    /// - TMP material presets for the design's black text rim and shadow;
    /// - sprite import settings for the hand-drawn pieces in UI/Sprites;
    /// - the UiTheme asset that points at all of it;
    /// - GameFontSet and TMP's default font, so the older code-built overlays pick up the new faces
    ///   straight away.
    ///
    /// Run <b>Tools ▸ CollarCali ▸ UI ▸ Build Theme</b>. Safe to run again: existing assets are
    /// updated in place and colours tuned in the theme asset are kept.
    ///
    /// It never touches a prefab or a scene. The old "Apply Game Fonts" tool retyped vendor prefabs
    /// and re-saved the whole Game scene; the redesign replaces that UI instead of retyping it.
    /// </summary>
    public static class UiThemeBuilder
    {
        const string SpriteFolder = "Assets/_Integration/UI/Sprites";
        // Full-screen painted art (menu backdrops): compressed, since it has no thin strokes to smear.
        const string ArtFolder = "Assets/_Integration/UI/Art";
        const string ResourcesFolder = "Assets/_Integration/Resources";
        const string ThemePath = ResourcesFolder + "/UiTheme.asset";
        const string FontSetPath = ResourcesFolder + "/GameFontSet.asset";

        const string KnewaveTtf = "Assets/Fonts/Knewave/Knewave-Regular.ttf";
        const string ComingSoonTtf = "Assets/Fonts/ComingSoon/ComingSoon-Regular.ttf";

        // Same path the old font tool used, so either tool finds the other's Knewave.
        const string KnewaveAsset = ResourcesFolder + "/Knewave SDF.asset";
        const string ComingSoonAsset = ResourcesFolder + "/ComingSoon SDF.asset";

        // TMP's dynamic Liberation Sans: covers the Latin Extended letters the two faces lack.
        const string FallbackAsset = "Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF - Fallback.asset";

        // The sprites are exported at three times the design's pixels; this maps them back to one
        // design pixel per UI unit, 9-slice borders included.
        const float SpritePixelsPerUnit = 300f;

        const int SamplingPointSize = 90;
        // Wide padding: the design's ink rim and shadow are drawn from the distance field, and a
        // rim can only be as thick as the padding the glyphs were rendered with. At TMP's usual 9
        // the rim vanished at HUD sizes.
        const int AtlasPadding = 22;
        const int AtlasSize = 2048;

        /// <summary>9-slice borders in the PNG's pixels: left, bottom, right, top.</summary>
        static readonly Dictionary<string, Vector4> Borders = new Dictionary<string, Vector4>
        {
            { UiSprites.KeyCap, new Vector4(45f, 45f, 45f, 45f) },
            { UiSprites.KeyCapOff, new Vector4(45f, 45f, 45f, 45f) },
            { UiSprites.BarFrame, new Vector4(36f, 30f, 36f, 30f) },
            { UiSprites.Brush, new Vector4(60f, 0f, 60f, 0f) },
        };

        static readonly Color InkColour = new Color(0.043f, 0.043f, 0.047f, 1f);

        [MenuItem("Tools/CollarCali/UI/Build Theme")]
        public static void Build()
        {
            var log = new StringBuilder("[UiThemeBuilder]\n");
            AssetDatabase.Refresh();
            Directory.CreateDirectory(ResourcesFolder);

            ImportSprites(log);

            var fallback = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FallbackAsset);
            if (fallback == null)
                log.AppendLine("  ! No fallback font at " + FallbackAsset + "; missing letters will show as boxes.");

            var display = EnsureFontAsset(KnewaveTtf, KnewaveAsset, "Knewave SDF", fallback, log);
            var body = EnsureFontAsset(ComingSoonTtf, ComingSoonAsset, "ComingSoon SDF", fallback, log);
            if (display == null || body == null)
            {
                Debug.LogError(log.ToString());
                return;
            }

            // Knewave: a thick rim with a hard drop underneath, the design's outlined headline look -
            // also drawn by the underlay, so the rim never eats into the letters.
            var displayInk = EnsurePreset(display, "Ink", outline: 0.06f, dilate: 0.06f,
                underlayOffset: new Vector2(0.02f, -0.16f), underlaySoftness: 0.06f, underlayDilate: 0.5f, log);
            // Coming Soon's strokes are thinner than any outline worth having, and TMP's outline eats
            // inward as well as out - it painted the letters black. So the face is only fattened a
            // little (the design strokes it), and the ink rim comes from the underlay, which sits
            // behind the glyph and grows outward only.
            var bodyInk = EnsurePreset(body, "Ink", outline: 0.04f, dilate: 0.15f,
                underlayOffset: new Vector2(0.03f, -0.03f), underlaySoftness: 0.22f, underlayDilate: 0.5f, log);
            var bodySoft = EnsurePreset(body, "Soft", outline: 0.02f, dilate: 0.1f,
                underlayOffset: new Vector2(0.02f, -0.02f), underlaySoftness: 0.3f, underlayDilate: 0.35f, log);

            var theme = EnsureTheme(log);
            theme.display = display;
            theme.body = body;
            theme.displayInk = displayInk;
            theme.bodyInk = bodyInk;
            theme.bodySoft = bodySoft;
            theme.sprites = LoadSprites(log);
            EditorUtility.SetDirty(theme);

            UpdateFontSet(display, body, log);
            SetTmpDefault(body, log);

            AssetDatabase.SaveAssets();
            log.AppendLine("Done. Theme at " + ThemePath);
            Debug.Log(log.ToString());
        }

        #region Sprites

        static void ImportSprites(StringBuilder log)
        {
            int changed = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { SpriteFolder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!(AssetImporter.GetAtPath(path) is TextureImporter importer))
                    continue;

                var name = Path.GetFileNameWithoutExtension(path);
                var border = Borders.TryGetValue(name, out var b) ? b : Vector4.zero;

                bool upToDate = importer.textureType == TextureImporterType.Sprite &&
                                importer.spriteImportMode == SpriteImportMode.Single &&
                                Mathf.Approximately(importer.spritePixelsPerUnit, SpritePixelsPerUnit) &&
                                !importer.mipmapEnabled &&
                                importer.alphaIsTransparency &&
                                importer.spriteBorder == border &&
                                importer.textureCompression == TextureImporterCompression.Uncompressed &&
                                importer.filterMode == FilterMode.Bilinear &&
                                importer.wrapMode == TextureWrapMode.Clamp;
                if (upToDate)
                    continue;

                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = SpritePixelsPerUnit;
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = true;
                importer.spriteBorder = border;
                // Uncompressed: block compression smears the thin hand-drawn strokes.
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.filterMode = FilterMode.Bilinear;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
                changed++;
            }

            log.AppendLine($"  Sprites: {changed} import setting(s) updated in {SpriteFolder}.");

            changed = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { ArtFolder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!(AssetImporter.GetAtPath(path) is TextureImporter importer))
                    continue;

                // The backdrops are opaque; the lobby's cut-out figures are not.
                bool alpha = importer.DoesSourceTextureHaveAlpha();
                bool upToDate = importer.textureType == TextureImporterType.Sprite &&
                                importer.spriteImportMode == SpriteImportMode.Single &&
                                !importer.mipmapEnabled &&
                                importer.alphaIsTransparency == alpha &&
                                importer.textureCompression == TextureImporterCompression.CompressedHQ &&
                                importer.maxTextureSize >= 2048 &&
                                importer.wrapMode == TextureWrapMode.Clamp;
                if (upToDate)
                    continue;

                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = 100f;
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = alpha;
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.maxTextureSize = 2048;
                importer.filterMode = FilterMode.Bilinear;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
                changed++;
            }

            log.AppendLine($"  Art: {changed} import setting(s) updated in {ArtFolder}.");
        }

        static List<UiTheme.NamedSprite> LoadSprites(StringBuilder log)
        {
            var list = new List<UiTheme.NamedSprite>();
            foreach (var guid in AssetDatabase.FindAssets("t:Sprite", new[] { SpriteFolder, ArtFolder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                if (sprite == null)
                    continue;
                list.Add(new UiTheme.NamedSprite { name = Path.GetFileNameWithoutExtension(path), sprite = sprite });
            }

            list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            log.AppendLine($"  Theme lists {list.Count} sprite(s).");
            return list;
        }

        #endregion

        #region Fonts

        /// <summary>
        /// A dynamic SDF font asset for a TTF, created once. The material and atlas have to be stored
        /// inside the asset file as sub-objects: left loose, every reference to them breaks the next
        /// time the editor reloads.
        /// </summary>
        static TMP_FontAsset EnsureFontAsset(string ttfPath, string assetPath, string assetName,
            TMP_FontAsset fallback, StringBuilder log)
        {
            var fontAsset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
            if (fontAsset != null && fontAsset.atlasPadding != AtlasPadding)
            {
                // Padding is baked into the atlas and every glyph, so a change means a fresh asset.
                // Everything that points at it is repointed below by this same build.
                log.AppendLine($"  Rebuilding {assetPath}: padding {fontAsset.atlasPadding} -> {AtlasPadding}");
                AssetDatabase.DeleteAsset(assetPath);
                fontAsset = null;
            }

            if (fontAsset == null)
            {
                var ttf = AssetDatabase.LoadAssetAtPath<Font>(ttfPath);
                if (ttf == null)
                {
                    log.AppendLine("  ! Font file not found: " + ttfPath);
                    return null;
                }

                fontAsset = TMP_FontAsset.CreateFontAsset(ttf, SamplingPointSize, AtlasPadding,
                    GlyphRenderMode.SDFAA, AtlasSize, AtlasSize, AtlasPopulationMode.Dynamic, true);
                if (fontAsset == null)
                {
                    log.AppendLine("  ! TMP could not build a font asset from " + ttfPath);
                    return null;
                }

                fontAsset.name = assetName;
                AssetDatabase.CreateAsset(fontAsset, assetPath);

                if (fontAsset.material != null)
                {
                    fontAsset.material.name = assetName + " Material";
                    AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
                }

                if (fontAsset.atlasTextures != null)
                {
                    for (int i = 0; i < fontAsset.atlasTextures.Length; i++)
                    {
                        if (fontAsset.atlasTextures[i] == null)
                            continue;
                        fontAsset.atlasTextures[i].name = assetName + " Atlas " + i;
                        AssetDatabase.AddObjectToAsset(fontAsset.atlasTextures[i], fontAsset);
                    }
                }

                log.AppendLine("  Built " + assetPath);
            }
            else
            {
                log.AppendLine("  Font asset present: " + assetPath);
            }

            if (fallback != null && fallback != fontAsset)
            {
                if (fontAsset.fallbackFontAssetTable == null)
                    fontAsset.fallbackFontAssetTable = new List<TMP_FontAsset>();
                if (!fontAsset.fallbackFontAssetTable.Contains(fallback))
                {
                    fontAsset.fallbackFontAssetTable.Add(fallback);
                    log.AppendLine("    fallback: " + fallback.name);
                }
            }

            EditorUtility.SetDirty(fontAsset);
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath);
        }

        /// <summary>
        /// A TMP material preset: a copy of the font's own material, sharing its atlas, with an ink rim
        /// and an underlay shadow. Named "&lt;font&gt; - &lt;suffix&gt;" next to the font so TMP's
        /// preset picker lists it too.
        /// </summary>
        static Material EnsurePreset(TMP_FontAsset font, string suffix, float outline, float dilate,
            Vector2 underlayOffset, float underlaySoftness, float underlayDilate, StringBuilder log)
        {
            var folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(font))?.Replace('\\', '/');
            var path = folder + "/" + font.name + " - " + suffix + ".mat";

            var preset = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (preset == null)
            {
                preset = new Material(font.material) { name = font.name + " - " + suffix };
                AssetDatabase.CreateAsset(preset, path);
            }
            else
            {
                // A rebuilt font has a new atlas and gradient scale; take all of them from its
                // material before applying the ink on top.
                preset.shader = font.material.shader;
                preset.CopyPropertiesFromMaterial(font.material);
            }

            preset.SetFloat("_FaceDilate", dilate);
            preset.SetColor("_OutlineColor", InkColour);
            preset.SetFloat("_OutlineWidth", outline);
            preset.EnableKeyword("OUTLINE_ON");

            preset.EnableKeyword("UNDERLAY_ON");
            preset.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.9f));
            preset.SetFloat("_UnderlayOffsetX", underlayOffset.x);
            preset.SetFloat("_UnderlayOffsetY", underlayOffset.y);
            preset.SetFloat("_UnderlaySoftness", underlaySoftness);
            preset.SetFloat("_UnderlayDilate", underlayDilate);

            EditorUtility.SetDirty(preset);
            log.AppendLine("  Preset " + path);
            return preset;
        }

        static void UpdateFontSet(TMP_FontAsset display, TMP_FontAsset body, StringBuilder log)
        {
            var set = AssetDatabase.LoadAssetAtPath<GameFontSet>(FontSetPath);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<GameFontSet>();
                AssetDatabase.CreateAsset(set, FontSetPath);
            }

            set.display = display;
            set.body = body;
            set.legacyDisplay = AssetDatabase.LoadAssetAtPath<Font>(KnewaveTtf);
            set.legacyBody = AssetDatabase.LoadAssetAtPath<Font>(ComingSoonTtf);
            EditorUtility.SetDirty(set);
            log.AppendLine("  GameFontSet: Knewave display, Coming Soon body (" + FontSetPath + ")");
        }

        /// <summary>
        /// Points TMP's default at Coming Soon, so text built in code without a font - the lobby and
        /// the old distance list among them - follows the design too.
        /// </summary>
        static void SetTmpDefault(TMP_FontAsset body, StringBuilder log)
        {
            var settings = TMP_Settings.instance;
            if (settings == null)
            {
                log.AppendLine("  ! No TMP Settings asset; default font left alone.");
                return;
            }

            var serialized = new SerializedObject(settings);
            var property = serialized.FindProperty("m_defaultFontAsset");
            if (property == null)
            {
                log.AppendLine("  ! TMP Settings has no m_defaultFontAsset field.");
                return;
            }

            property.objectReferenceValue = body;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings);
            log.AppendLine("  TMP default font: " + body.name);
        }

        #endregion

        static UiTheme EnsureTheme(StringBuilder log)
        {
            var theme = AssetDatabase.LoadAssetAtPath<UiTheme>(ThemePath);
            if (theme != null)
                return theme;

            theme = ScriptableObject.CreateInstance<UiTheme>();
            AssetDatabase.CreateAsset(theme, ThemePath);
            log.AppendLine("  Created " + ThemePath + " with the design's palette.");
            return theme;
        }
    }
}
#endif
