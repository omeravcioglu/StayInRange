#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Brings hand-posed copies of the player model in scenes to the model's new size.
    ///
    /// A copy whose bones were posed by hand keeps those bones' positions as overrides. When the
    /// model's import scale went from 1 to 0.75, the overridden bones kept their old, longer
    /// positions and stretched the copy. This scales every bone position override by the size change
    /// - never the copy's own placement, rotation or scale - so the pose looks as it did, at the new
    /// size. A copy is left alone when its overrides already match the new size, so running it twice
    /// changes nothing.
    ///
    /// Menu: Tools ▸ CollarCali ▸ Character ▸ Rescale Posed Copies (open scenes). Batch:
    ///   -executeMethod CollarCali.EditorTools.CharacterPosedCopyMigration.RunBatch -scenes "Assets/a.unity;Assets/b.unity"
    /// </summary>
    public static class CharacterPosedCopyMigration
    {
        /// <summary>The model's import scale before the size fix.</summary>
        const float FromScale = 1f;

        static float Factor => CharacterAvatarFixer.TargetScale / FromScale;

        [MenuItem(CharacterFitCheck.MenuRoot + "Rescale Posed Copies (open scenes)")]
        static void RunFromMenu()
        {
            var log = new StringBuilder();
            int changed = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;
                int n = Migrate(scene, log);
                if (n > 0)
                    EditorSceneManager.MarkSceneDirty(scene);
                changed += n;
            }

            Debug.Log("[PosedCopies] " + log);
            EditorUtility.DisplayDialog("Rescale Posed Copies",
                (changed > 0 ? changed + " copy(ies) rescaled - save the scene(s).\n\n" : "Nothing to rescale.\n\n") + log, "OK");
        }

        /// <summary>Batch: opens each scene in -scenes, rescales its posed copies and saves it if anything changed.</summary>
        public static void RunBatch()
        {
            string list = CharacterFitCheck.ArgValue("-scenes");
            if (string.IsNullOrEmpty(list))
            {
                Debug.LogError("[PosedCopies] Pass -scenes \"Assets/....unity\" (several separated by ';').");
                return;
            }

            foreach (var path in list.Split(';').Select(p => p.Trim()).Where(p => p.Length > 0))
            {
                var log = new StringBuilder();
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                int n = Migrate(scene, log);
                if (n > 0)
                    EditorSceneManager.SaveScene(scene);
                Debug.Log($"[PosedCopies] {path}: {n} copy(ies) rescaled{(n > 0 ? ", saved" : "")}.\n{log}");
            }
        }

        /// <summary>Rescales the posed copies of the player model in <paramref name="scene"/>. Returns how many changed.</summary>
        public static int Migrate(Scene scene, StringBuilder log)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(CharacterAvatarFixer.ModelGuid));
            if (model == null)
            {
                log.AppendLine("Player model not found.");
                return 0;
            }

            int changed = 0;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    var go = t.gameObject;
                    if (!PrefabUtility.IsOutermostPrefabInstanceRoot(go) ||
                        PrefabUtility.GetCorrespondingObjectFromOriginalSource(go) != model)
                        continue;

                    if (MigrateCopy(go, model, log))
                        changed++;
                }
            }

            return changed;
        }

        static bool MigrateCopy(GameObject copy, GameObject model, StringBuilder log)
        {
            string name = copy.name;
            var mods = PrefabUtility.GetPropertyModifications(copy);
            if (mods == null || mods.Length == 0)
            {
                log.AppendLine($"'{name}': no overrides.");
                return false;
            }

            // Bone position overrides: on a Transform of the model other than its root.
            var bonePositions = mods.Where(m =>
                m.target is Transform bone && bone != model.transform &&
                m.propertyPath.StartsWith("m_LocalPosition.", StringComparison.Ordinal)).ToList();
            if (bonePositions.Count == 0)
            {
                log.AppendLine($"'{name}': no bone positions overridden.");
                return false;
            }

            // Old-size overrides are 1/Factor times the model's (new) defaults; already-migrated ones match them.
            var ratios = new List<float>();
            foreach (var m in bonePositions)
            {
                float def = Component(((Transform)m.target).localPosition, m.propertyPath);
                if (Mathf.Abs(def) > 0.01f && TryParse(m.value, out float v))
                    ratios.Add(v / def);
            }

            if (ratios.Count == 0)
            {
                log.AppendLine($"'{name}': no bone long enough to tell its size; left alone.");
                return false;
            }

            ratios.Sort();
            float median = ratios[ratios.Count / 2];
            if (Mathf.Abs(median - 1f) < 0.05f)
            {
                log.AppendLine($"'{name}': already at the new size (median ratio {median:0.000}).");
                return false;
            }

            if (Mathf.Abs(median - 1f / Factor) > 0.05f)
            {
                log.AppendLine($"'{name}': bone overrides are {median:0.000}× the model's - not a size change this " +
                               "tool understands; left alone.");
                return false;
            }

            foreach (var m in bonePositions)
            {
                if (TryParse(m.value, out float v))
                    m.value = (v * Factor).ToString("R", CultureInfo.InvariantCulture);
            }

            PrefabUtility.SetPropertyModifications(copy, mods);
            log.AppendLine($"'{name}': {bonePositions.Count} bone position overrides scaled by {Factor} " +
                           $"(were {median:0.000}× the model's). Its placement, rotation and scale are unchanged.");
            return true;
        }

        static float Component(Vector3 v, string propertyPath)
        {
            switch (propertyPath[propertyPath.Length - 1])
            {
                case 'x': return v.x;
                case 'y': return v.y;
                default: return v.z;
            }
        }

        static bool TryParse(string value, out float result) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }
}
#endif
