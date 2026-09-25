#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// The Game scene holds roughly twelve thousand crumpled-paper prefabs in one small patch -
    /// five variants at ~2,400 each, which looks like a scatter tool that was never stopped. That
    /// is more GameObjects than the rest of the level combined. This keeps a seeded random
    /// subset and removes the rest, so the same run always keeps the same sheets.
    /// </summary>
    public static class HospitalPaperThinner
    {
        const string PaperPrefix = "SM_Crumpled_Paper_01";
        const int KeepCount = 400;
        const int Seed = 4242;

        [MenuItem("Tools/CollarCali/Thin Crumpled Paper")]
        public static void Thin()
        {
            var papers = new List<GameObject>();
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t == null || !t.name.StartsWith(PaperPrefix))
                    continue;
                // Only prefab instance roots, so a paper nested inside another prop is left alone.
                if (PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject) == null)
                    continue;
                if (PrefabUtility.GetOutermostPrefabInstanceRoot(t.gameObject) != t.gameObject)
                    continue;
                papers.Add(t.gameObject);
            }

            if (papers.Count <= KeepCount)
            {
                Debug.Log($"[CollarCali] {papers.Count} paper sheets in the scene; nothing to thin.");
                return;
            }

            if (!EditorUtility.DisplayDialog("Thin Crumpled Paper",
                    $"Found {papers.Count} crumpled-paper prefabs. Keep {KeepCount} and delete the other " +
                    $"{papers.Count - KeepCount}?\n\nThis is undoable with Ctrl+Z.",
                    "Thin", "Cancel"))
                return;

            // Sorted by position first so the pick is deterministic regardless of scene order.
            papers.Sort((a, b) =>
            {
                var pa = a.transform.position; var pb = b.transform.position;
                int c = pa.x.CompareTo(pb.x);
                if (c != 0) return c;
                c = pa.z.CompareTo(pb.z);
                return c != 0 ? c : pa.y.CompareTo(pb.y);
            });

            var rng = new System.Random(Seed);
            var keep = new HashSet<int>();
            while (keep.Count < KeepCount)
                keep.Add(rng.Next(papers.Count));

            Undo.SetCurrentGroupName("Thin Crumpled Paper");
            int group = Undo.GetCurrentGroup();

            int removed = 0;
            for (int i = 0; i < papers.Count; i++)
            {
                if (keep.Contains(i))
                    continue;
                Undo.DestroyObjectImmediate(papers[i]);
                removed++;
            }

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log($"[CollarCali] Removed {removed} crumpled-paper sheets, kept {KeepCount}.");
        }
    }
}
#endif
