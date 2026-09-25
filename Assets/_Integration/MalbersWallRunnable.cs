using System.Collections.Generic;
using MalbersAnimations.Controller;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Drop this on a wall and the Malbers character can wall-run it.
    ///
    /// Wall running is filtered differently from climbing, which is worth knowing before copying
    /// setups between the two. Climb compares a physics material; WallRun compares a TAG:
    ///
    ///     if (WallTag.Empty || WallFound.CompareTag(WallTag))   // WallRun.cs, WallRunVertical.cs
    ///
    /// with WallFound being WallHit.transform - the transform of the collider the ray hit. That
    /// detail is the one that catches people out: on an imported model whose collider sits on a
    /// child mesh, the TAG HAS TO BE ON THAT CHILD. Tagging the root does nothing, because the root
    /// is not what the ray hit. The same goes for the layer, since a raycast mask tests the
    /// collider's own object too. This component therefore tags and re-layers every collider it
    /// finds rather than just the object it is sitting on.
    ///
    /// So a wall-runnable object is:
    ///
    ///   1. a collider - kept non-trigger here, because whether a ray sees a trigger depends on the
    ///      project-wide Physics.queriesHitTriggers setting, and quietly depending on that is how
    ///      this breaks for someone else later
    ///   2. that collider's object tagged with the state's Wall Tag, which ships as "WallRun"
    ///   3. that collider's object on a layer inside the state's layer mask, which ships as Default
    ///
    /// Two things this component cannot do anything about, because they are about the character
    /// rather than the wall: the animal has to be at least StartHeight off the ground (0.2 on the
    /// shipped human state, so effectively airborne - you jump into a wall run, you cannot start one
    /// standing still), and it has to be moving alongside the wall for the side rays to hit it.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("CollarCali/Malbers Wall Runnable")]
    public class MalbersWallRunnable : MonoBehaviour
    {
        [Tooltip("Tag the wall-run states filter by. Filled in automatically from the WallRun states " +
                 "in the project.")]
        [SerializeField] string wallTag = "WallRun";

        [Tooltip("Also apply to colliders on child objects. Leave on for imported models, where the " +
                 "collider usually sits on a child mesh rather than the root.")]
        [SerializeField] bool includeChildren = true;

        [Tooltip("Add a BoxCollider if this object has no usable collider at all.")]
        [SerializeField] bool addColliderIfMissing = true;

        [Tooltip("Move colliders onto a layer the wall-run states actually cast against.")]
        [SerializeField] bool fixLayer = true;

        [SerializeField, HideInInspector] int wallLayerMask = 1;

        void Reset()
        {
            ResolveFromWallRunStates();
            Apply();
        }

        void OnValidate()
        {
            // No asset searching here - OnValidate also runs during import. Applying an
            // already-resolved tag is cheap and safe.
            if (!string.IsNullOrEmpty(wallTag))
                Apply();
        }

        void Awake()
        {
            if (!Application.isPlaying)
                return;

            Apply();
        }

        #region Applying

        [ContextMenu("Make Wall-Runnable Now")]
        public void MakeWallRunnableNow()
        {
            ResolveFromWallRunStates();
            Apply();
            Diagnose();
        }

        public void Apply()
        {
            if (string.IsNullOrEmpty(wallTag) || !TagExists(wallTag))
                return;

            var colliders = CollectColliders();

            if (colliders.Count == 0 && addColliderIfMissing)
                colliders.Add(gameObject.AddComponent<BoxCollider>());

            foreach (var collider in colliders)
            {
                // Tag and layer go on the collider's own object, not on this one. See the class
                // comment: WallHit.transform is what gets compared, and a raycast mask tests the
                // layer of the collider it hit.
                var target = collider.gameObject;

                if (!target.CompareTag(wallTag))
                {
                    target.tag = wallTag;
#if UNITY_EDITOR
                    UnityEditor.EditorUtility.SetDirty(target);
#endif
                }

                if (fixLayer && !LayerIsWallRunnable(target.layer))
                {
                    int layer = LowestLayerIn(wallLayerMask);
                    if (layer >= 0)
                    {
                        target.layer = layer;
#if UNITY_EDITOR
                        UnityEditor.EditorUtility.SetDirty(target);
#endif
                    }
                }
            }
        }

        List<Collider> CollectColliders()
        {
            var found = new List<Collider>();
            var candidates = includeChildren
                ? GetComponentsInChildren<Collider>(true)
                : GetComponents<Collider>();

            foreach (var collider in candidates)
            {
                if (collider != null && !collider.isTrigger)
                    found.Add(collider);
            }

            return found;
        }

        bool LayerIsWallRunnable(int layer) => (wallLayerMask & (1 << layer)) != 0;

        static int LowestLayerIn(int mask)
        {
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) != 0)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Assigning a tag the project does not define throws, so it is checked rather than assumed.
        /// Creating it is deliberately left alone: silently editing a project's tag list from a
        /// component is a worse surprise than being told to add one line in the inspector.
        /// </summary>
        static bool TagExists(string tag)
        {
#if UNITY_EDITOR
            foreach (var existing in UnityEditorInternal.InternalEditorUtility.tags)
            {
                if (existing == tag)
                    return true;
            }
            return false;
#else
            return true;
#endif
        }

        #endregion

        #region Diagnosis

        [ContextMenu("Diagnose Wall Run")]
        public void Diagnose()
        {
            var problems = new List<string>();

            if (string.IsNullOrEmpty(wallTag))
                problems.Add("no wall tag resolved - is there a WallRun state asset in the project?");
            else if (!TagExists(wallTag))
                problems.Add("the tag '" + wallTag + "' is not defined in this project. Add it under " +
                             "Edit > Project Settings > Tags and Layers, then press Make Wall-Runnable Now");

            var colliders = CollectColliders();
            if (colliders.Count == 0)
            {
                bool hasTrigger = false;
                foreach (var collider in includeChildren
                             ? GetComponentsInChildren<Collider>(true)
                             : GetComponents<Collider>())
                {
                    if (collider != null && collider.isTrigger)
                        hasTrigger = true;
                }

                problems.Add(hasTrigger
                    ? "the only collider here is a trigger; use a solid collider for a wall"
                    : "no collider at all, so the side rays have nothing to hit");
            }
            else if (!string.IsNullOrEmpty(wallTag) && TagExists(wallTag))
            {
                foreach (var collider in colliders)
                {
                    var target = collider.gameObject;
                    if (!target.CompareTag(wallTag))
                        problems.Add(target.name + " is tagged '" + target.tag + "' instead of '" +
                                     wallTag + "'");
                    if (!LayerIsWallRunnable(target.layer))
                        problems.Add(target.name + " is on layer '" +
                                     LayerMask.LayerToName(target.layer) +
                                     "', which the wall-run states do not cast against");
                }
            }

            if (problems.Count == 0)
            {
                Debug.Log("[CollarCali] " + name + " is wall-runnable: " + colliders.Count +
                          " collider(s) tagged '" + wallTag + "'. Remember the character has to be " +
                          "airborne and moving alongside it.", this);
                return;
            }

            Debug.LogWarning("[CollarCali] " + name + " is NOT wall-runnable:\n - " +
                             string.Join("\n - ", problems), this);
        }

        #endregion

        #region Editor resolution

        /// <summary>
        /// Reads the tag and layers off the wall-run state assets instead of hardcoding "WallRun",
        /// so a project that renamed the tag still works.
        ///
        /// Both state types are consulted because they filter independently and use differently
        /// named layer fields - WallRun calls it Layer, WallRunVertical calls it WallLayer - and a
        /// wall should be runnable both ways.
        /// </summary>
        void ResolveFromWallRunStates()
        {
#if UNITY_EDITOR
            var tags = new List<string>();
            int mask = 0;

            foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:WallRun"))
            {
                var state = UnityEditor.AssetDatabase.LoadAssetAtPath<WallRun>(
                    UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
                if (state == null)
                    continue;
                mask |= state.Layer.Value;
                AddTag(tags, state.WallTag.Value);
            }

            foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:WallRunVertical"))
            {
                var state = UnityEditor.AssetDatabase.LoadAssetAtPath<WallRunVertical>(
                    UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
                if (state == null)
                    continue;
                mask |= state.WallLayer.Value;
                AddTag(tags, state.WallTag.Value);
            }

            if (tags.Count == 0)
            {
                Debug.LogWarning("[CollarCali] No WallRun state asset found; keeping the tag '" +
                                 wallTag + "'.", this);
            }
            else
            {
                if (tags.Count > 1)
                {
                    Debug.LogWarning("[CollarCali] Wall-run states disagree on the wall tag (" +
                                     string.Join(", ", tags) + "); using '" + tags[0] + "'.", this);
                }
                wallTag = tags[0];
            }

            wallLayerMask = mask != 0 ? mask : 1;
#endif
        }

        static void AddTag(List<string> into, string tag)
        {
            if (!string.IsNullOrEmpty(tag) && !into.Contains(tag))
                into.Add(tag);
        }

        #endregion
    }
}
