using System.Collections.Generic;
using MalbersAnimations.Controller;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Drop this on an object and the Malbers character can climb it.
    ///
    /// Why copying the components off one of Malbers' own climbable objects does nothing: there are
    /// no components involved. The Climb state finds surfaces by sphere-casting and then comparing
    /// the collider's PHYSICS MATERIAL against one specific asset:
    ///
    ///     var valid = HitChest.collider.sharedMaterial == Surface;   // Climb.cs
    ///
    /// So a climbable object is only ever three things, and all three have to be true:
    ///
    ///   1. a collider that is NOT a trigger - the state casts with QueryTriggerInteraction.Ignore,
    ///      so a trigger collider is invisible to it
    ///   2. that collider's material is the exact PhysicsMaterial asset the Climb state points at.
    ///      Reference equality, not a name or a copy: a duplicate of Climbable will never match
    ///   3. the object's layer is inside the state's Climb Layer mask, which ships as just Default.
    ///      A wall moved to some other layer is never even hit by the cast
    ///
    /// This component enforces all three and complains loudly about what it could not fix. It does
    /// its work at edit time, so what ends up saved in the scene is a normal material assignment -
    /// nothing has to run at play time for the object to be climbable.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("CollarCali/Malbers Climbable")]
    public class MalbersClimbable : MonoBehaviour
    {
        [Tooltip("The physics material the Climb state compares against. Filled in automatically " +
                 "from the Climb state in the project - leave it alone unless you know it is wrong.")]
        [SerializeField] PhysicsMaterial climbableMaterial;

        [Tooltip("Also apply to colliders on child objects. Leave on for imported models, where the " +
                 "collider usually sits on a child mesh rather than the root.")]
        [SerializeField] bool includeChildren = true;

        [Tooltip("Add a BoxCollider if this object has no usable collider at all.")]
        [SerializeField] bool addColliderIfMissing = true;

        [Tooltip("Move the object onto a layer the Climb state actually casts against. " +
                 "Turn this off if the layer matters for something else, and widen the state's " +
                 "Climb Layer mask instead.")]
        [SerializeField] bool fixLayer = true;

        /// <summary>Cached so the runtime check can report the mask it validated against.</summary>
        [SerializeField, HideInInspector] int climbLayerMask = 1;

        void Reset()
        {
            // Runs the moment the component is added, which is what makes "attach it and it is
            // climbable" true rather than something you have to remember to press afterwards.
            ResolveFromClimbState();
            Apply();
        }

        void OnValidate()
        {
            // Deliberately does not go looking for assets: OnValidate also fires during asset
            // import, and searching the project from there is asking for trouble. Re-applying an
            // already-resolved material is cheap and safe.
            if (climbableMaterial != null)
                Apply();
        }

        void Awake()
        {
            // Safety net for objects spawned at runtime, where Reset never ran. In a build the
            // material reference is already serialised, so this is normally a no-op.
            if (!Application.isPlaying)
                return;

            if (climbableMaterial == null)
            {
                Debug.LogWarning("[CollarCali] " + name + " has no climbable material assigned, so " +
                                 "it will not be climbable. Open it in the editor and use " +
                                 "Make Climbable Now on the component.", this);
                return;
            }

            Apply();
        }

        #region Applying

        [ContextMenu("Make Climbable Now")]
        public void MakeClimbableNow()
        {
            ResolveFromClimbState();
            Apply();
            Diagnose();
        }

        /// <summary>
        /// Assigns the material to every usable collider, and moves the object onto a layer the
        /// Climb state can see.
        /// </summary>
        public void Apply()
        {
            if (climbableMaterial == null)
                return;

            var colliders = CollectColliders();

            if (colliders.Count == 0 && addColliderIfMissing)
            {
                var box = gameObject.AddComponent<BoxCollider>();
                colliders.Add(box);
            }

            foreach (var collider in colliders)
            {
                if (collider.sharedMaterial != climbableMaterial)
                {
                    collider.sharedMaterial = climbableMaterial;
#if UNITY_EDITOR
                    UnityEditor.EditorUtility.SetDirty(collider);
#endif
                }

                // Per collider, not just on this object: a cast mask tests the layer of the collider
                // it hit, so a child mesh left on some other layer is never hit however the root is
                // configured.
                var target = collider.gameObject;
                if (fixLayer && !LayerIsClimbable(target.layer))
                {
                    int layer = LowestLayerIn(climbLayerMask);
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

        /// <summary>
        /// Non-trigger colliders only. A trigger is skipped rather than silently converted: turning
        /// someone's trigger volume into a solid wall is a much worse surprise than this object not
        /// being climbable.
        /// </summary>
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

        bool LayerIsClimbable(int layer)
        {
            return (climbLayerMask & (1 << layer)) != 0;
        }

        static int LowestLayerIn(int mask)
        {
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) != 0)
                    return i;
            }
            return -1;
        }

        #endregion

        #region Diagnosis

        /// <summary>
        /// Says exactly why this object is or is not climbable. Worth running when something looks
        /// set up correctly and still cannot be climbed.
        /// </summary>
        [ContextMenu("Diagnose Climbability")]
        public void Diagnose()
        {
            var problems = new List<string>();

            if (climbableMaterial == null)
            {
                problems.Add("no climbable PhysicsMaterial resolved - is there a Climb state asset " +
                             "in the project?");
            }

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
                    ? "the only collider here is a trigger, and the Climb state ignores triggers"
                    : "no collider at all, so there is nothing for the climb cast to hit");
            }
            else if (climbableMaterial != null)
            {
                foreach (var collider in colliders)
                {
                    if (collider.sharedMaterial != climbableMaterial)
                    {
                        problems.Add(collider.name + " still has " +
                                     (collider.sharedMaterial == null
                                         ? "no physics material"
                                         : "the material '" + collider.sharedMaterial.name + "'"));
                    }

                    var target = collider.gameObject;
                    if (!LayerIsClimbable(target.layer))
                    {
                        problems.Add(target.name + " is on layer '" +
                                     LayerMask.LayerToName(target.layer) +
                                     "', which is not in the Climb state's Climb Layer mask, so the " +
                                     "cast never reaches it");
                    }
                }
            }

            if (problems.Count == 0)
            {
                Debug.Log("[CollarCali] " + name + " is climbable: " + colliders.Count +
                          " collider(s) using '" + climbableMaterial.name + "' on layer '" +
                          LayerMask.LayerToName(gameObject.layer) + "'.", this);
                return;
            }

            Debug.LogWarning("[CollarCali] " + name + " is NOT climbable:\n - " +
                             string.Join("\n - ", problems), this);
        }

        #endregion

        #region Editor resolution

        /// <summary>
        /// Reads the material and layer mask off the Climb state assets in the project, rather than
        /// hardcoding a path to Malbers' Climbable material.
        ///
        /// That is the whole point: the check is reference equality, so the only material guaranteed
        /// to work is the one the state itself is pointing at. If the project has several Climb
        /// states disagreeing about it, that is worth saying out loud instead of picking one.
        /// </summary>
        void ResolveFromClimbState()
        {
#if UNITY_EDITOR
            var materials = new List<PhysicsMaterial>();
            int mask = 0;

            foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:Climb"))
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                var climb = UnityEditor.AssetDatabase.LoadAssetAtPath<Climb>(path);
                if (climb == null || climb.Surface == null)
                    continue;

                if (!materials.Contains(climb.Surface))
                    materials.Add(climb.Surface);
                mask |= climb.ClimbLayer.Value;
            }

            if (materials.Count == 0)
            {
                Debug.LogWarning("[CollarCali] No Climb state asset with a Surface material found. " +
                                 "Assign the climbable material by hand.", this);
                return;
            }

            if (materials.Count > 1)
            {
                Debug.LogWarning("[CollarCali] Several Climb states use different surface materials; " +
                                 "using '" + materials[0].name + "'. If the wrong character cannot " +
                                 "climb this, that is why.", this);
            }

            climbableMaterial = materials[0];
            // Falls back to Default rather than 0: a mask of nothing would make every layer look
            // wrong and send Apply chasing a layer that does not exist.
            climbLayerMask = mask != 0 ? mask : 1;
#endif
        }

        #endregion
    }
}
