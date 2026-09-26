#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Puts the game on one pair of fonts: Knewave for display, Outfit for body text.
    ///
    /// Run <b>Tools ▸ CollarCali ▸ Apply Game Fonts</b>.
    ///
    /// WHY THIS IS A TOOL AND NOT A FIND-AND-REPLACE: every TMP_Text references a font asset AND a
    /// material built from that font's atlas. Swapping the font guid in the YAML leaves the old
    /// material behind and the text renders from the wrong atlas - garbled glyphs, or nothing at all.
    /// Assigning through TMP_Text.font lets TMP repoint the material with it.
    ///
    /// WHICH TEXT BECOMES WHICH: mapped from what each component already uses rather than guessed
    /// from object names. Cowsins already separated display text (ZacbelX) from body text
    /// (Outfit/OpenSans/LiberationSans), so that existing distinction is reused - anything on ZacbelX
    /// becomes Knewave, everything else becomes Outfit. It restyles the game without inventing a new
    /// opinion about which label is a heading.
    /// </summary>
    public static class GameFontBuilder
    {
        const string KnewaveTtfPath = "Assets/Fonts/Knewave/Knewave-Regular.ttf";
        const string DisplayFontAssetPath = "Assets/_Integration/Resources/Knewave SDF.asset";
        const string FontSetPath = "Assets/_Integration/Resources/GameFontSet.asset";

        // Outfit-SemiBold SDF, already in the project as the body face.
        const string BodyFontAssetGuid = "ab71b4296a7219543a3eda0f04eaabde";
        const string BodyFontAssetFallbackPath =
            "Assets/Cowsins/UI/Typography/Outfit_Complete/Outfit-SemiBold SDF.asset";
        const string BodyTtfPath =
            "Assets/Cowsins/UI/Typography/Outfit_Complete/Fonts/OTF/Outfit-SemiBold.otf";

        /// <summary>Text on these becomes the display face; everything else becomes body.</summary>
        static readonly string[] DisplayFontAssetNames = { "ZacbelX-Bold", "ZacbelX-Bold-Outline" };

        /// <summary>Folders whose prefabs get retyped. Demo content is deliberately left alone.</summary>
        static readonly string[] PrefabFolders =
        {
            "Assets/_Integration",
            "Assets/LabPrefabs",
            "Assets/Cowsins/Prefabs",
            "Assets/Clean Multiplayer Pro",
        };

        [MenuItem("Tools/CollarCali/Apply Game Fonts")]
        public static void Apply()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            if (!EditorUtility.DisplayDialog("Apply Game Fonts",
                    "Retypes every TMP text in the project's prefabs and build scenes:\n\n" +
                    "    display  ->  Knewave\n" +
                    "    body     ->  Outfit\n\n" +
                    "Scenes are opened and saved one at a time. Demo scenes are not touched.",
                    "Apply", "Cancel"))
                return;

            var log = new System.Text.StringBuilder("[GameFontBuilder]\n");

            var display = EnsureDisplayFontAsset(log);
            var body = LoadBodyFontAsset(log);
            if (display == null || body == null)
            {
                Debug.LogError(log.ToString());
                return;
            }

            BuildFontSet(display, body, log);
            SetTmpDefault(body, log);

            int prefabs = RetypePrefabs(display, body, log);
            int scenes = RetypeScenes(display, body, log);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            log.AppendLine($"Done. {prefabs} prefab(s) and {scenes} scene(s) updated.");
            Debug.Log(log.ToString());
        }

        #region Font assets

        /// <summary>
        /// Generates the Knewave SDF asset from the TTF if it does not exist yet.
        ///
        /// The material and atlas texture have to be added to the asset file as sub-objects. A font
        /// asset created in script keeps them as loose objects otherwise, and every reference to it
        /// breaks the moment the editor reloads.
        /// </summary>
        static TMP_FontAsset EnsureDisplayFontAsset(System.Text.StringBuilder log)
        {
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(DisplayFontAssetPath);
            if (existing != null)
            {
                log.AppendLine("  Display font asset already present.");
                return existing;
            }

            var ttf = AssetDatabase.LoadAssetAtPath<Font>(KnewaveTtfPath);
            if (ttf == null)
            {
                log.AppendLine("  ! Knewave TTF not found at " + KnewaveTtfPath);
                return null;
            }

            var fontAsset = TMP_FontAsset.CreateFontAsset(ttf);
            if (fontAsset == null)
            {
                log.AppendLine("  ! TMP could not build a font asset from the TTF.");
                return null;
            }

            fontAsset.name = "Knewave SDF";
            System.IO.Directory.CreateDirectory("Assets/_Integration/Resources");
            AssetDatabase.CreateAsset(fontAsset, DisplayFontAssetPath);

            if (fontAsset.material != null)
            {
                fontAsset.material.name = "Knewave SDF Material";
                AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
            }

            if (fontAsset.atlasTextures != null)
            {
                for (int i = 0; i < fontAsset.atlasTextures.Length; i++)
                {
                    if (fontAsset.atlasTextures[i] == null)
                        continue;
                    fontAsset.atlasTextures[i].name = "Knewave Atlas " + i;
                    AssetDatabase.AddObjectToAsset(fontAsset.atlasTextures[i], fontAsset);
                }
            }

            EditorUtility.SetDirty(fontAsset);
            AssetDatabase.SaveAssets();
            log.AppendLine("  Built " + DisplayFontAssetPath);
            return AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(DisplayFontAssetPath);
        }

        static TMP_FontAsset LoadBodyFontAsset(System.Text.StringBuilder log)
        {
            var path = AssetDatabase.GUIDToAssetPath(BodyFontAssetGuid);
            var asset = string.IsNullOrEmpty(path)
                ? null
                : AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(path);

            if (asset == null)
                asset = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(BodyFontAssetFallbackPath);

            if (asset == null)
                log.AppendLine("  ! Body font asset (Outfit SDF) not found.");
            else
                log.AppendLine("  Body font: " + asset.name);

            return asset;
        }

        static void BuildFontSet(TMP_FontAsset display, TMP_FontAsset body,
            System.Text.StringBuilder log)
        {
            var set = AssetDatabase.LoadAssetAtPath<GameFontSet>(FontSetPath);
            if (set == null)
            {
                set = ScriptableObject.CreateInstance<GameFontSet>();
                AssetDatabase.CreateAsset(set, FontSetPath);
            }

            set.display = display;
            set.body = body;
            set.legacyDisplay = AssetDatabase.LoadAssetAtPath<Font>(KnewaveTtfPath);
            set.legacyBody = AssetDatabase.LoadAssetAtPath<Font>(BodyTtfPath);

            EditorUtility.SetDirty(set);
            log.AppendLine("  Font set written to " + FontSetPath);
        }

        /// <summary>
        /// Points TMP's default at the body face, so any text left on the default - 67 components at
        /// last count - follows along instead of staying on LiberationSans.
        /// </summary>
        static void SetTmpDefault(TMP_FontAsset body, System.Text.StringBuilder log)
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
            log.AppendLine("  TMP default font set to " + body.name);
        }

        #endregion

        #region Retyping

        static int RetypePrefabs(TMP_FontAsset display, TMP_FontAsset body,
            System.Text.StringBuilder log)
        {
            var folders = PrefabFolders.Where(AssetDatabase.IsValidFolder).ToArray();
            if (folders.Length == 0)
                return 0;

            int touched = 0;
            int components = 0;

            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", folders))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    int changed = Retype(contents.GetComponentsInChildren<TMP_Text>(true),
                        display, body);
                    if (changed > 0)
                    {
                        PrefabUtility.SaveAsPrefabAsset(contents, path);
                        touched++;
                        components += changed;
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }

            log.AppendLine($"  Prefabs: {components} text component(s) across {touched} prefab(s).");
            return touched;
        }

        /// <summary>
        /// Only the scenes that ship, plus Hospitalstart. Retyping the Malbers and Cowsins demo
        /// scenes would be a lot of churn in files the game never loads.
        /// </summary>
        static int RetypeScenes(TMP_FontAsset display, TMP_FontAsset body,
            System.Text.StringBuilder log)
        {
            var paths = new List<string>();
            foreach (var scene in EditorBuildSettings.scenes)
            {
                if (scene != null && !string.IsNullOrEmpty(scene.path))
                    paths.Add(scene.path);
            }

            foreach (var extra in new[] { "Assets/Scenes/Hospitalstart.unity" })
            {
                if (!paths.Contains(extra) && System.IO.File.Exists(extra))
                    paths.Add(extra);
            }

            int touched = 0;
            int components = 0;

            foreach (var path in paths)
            {
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                int changed = 0;

                foreach (var root in scene.GetRootGameObjects())
                    changed += Retype(root.GetComponentsInChildren<TMP_Text>(true), display, body);

                if (changed > 0)
                {
                    EditorSceneManager.MarkSceneDirty(scene);
                    EditorSceneManager.SaveScene(scene);
                    touched++;
                    components += changed;
                }

                log.AppendLine($"    {System.IO.Path.GetFileName(path)}: {changed} component(s)");
            }

            log.AppendLine($"  Scenes: {components} text component(s) across {touched} scene(s).");
            return touched;
        }

        static int Retype(IEnumerable<TMP_Text> texts, TMP_FontAsset display, TMP_FontAsset body)
        {
            int changed = 0;

            foreach (var text in texts)
            {
                if (text == null)
                    continue;

                var wanted = IsDisplay(text.font) ? display : body;
                if (text.font == wanted)
                    continue;

                // Through the property, not the serialized field: this is what makes TMP repoint the
                // material at the new atlas as well.
                text.font = wanted;
                EditorUtility.SetDirty(text);
                changed++;
            }

            return changed;
        }

        static bool IsDisplay(TMP_FontAsset current)
        {
            if (current == null)
                return false;

            foreach (var name in DisplayFontAssetNames)
            {
                if (current.name.StartsWith(name, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        #endregion
    }
}
#endif
