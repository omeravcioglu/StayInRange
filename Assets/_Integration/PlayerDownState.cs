#if CMPSETUP_COMPLETE
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Everything that is true about a player while they are dead: a ragdoll on the floor, a body
    /// somebody can pick up and carry, and a spectator camera for the player it belongs to.
    ///
    /// Runs on every machine. FpsNetworkBridge owns the networked facts - IsDead and CarriedBy - and
    /// hands them here; this component is what those facts look like on screen and in the physics
    /// scene. Splitting it that way keeps the bridge from growing a fourth responsibility, and keeps
    /// the ragdoll out of the networking.
    ///
    /// The body position is agreed between machines through the network root, which already
    /// replicates. While a body is loose the owner drives that root from its own hips and everyone
    /// else corrects drift towards it. While a body is carried, every machine places it on the
    /// carrier's shoulder from the carrier's own replicated position, so nobody has to send the
    /// body's position at all - it is derived from something already synchronised.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(PlayerRagdoll))]
    public class PlayerDownState : MonoBehaviour
    {
        /// <summary>Where a carried body rides, relative to the carrier.</summary>
        static readonly Vector3 CarryOffset = new Vector3(0f, 1.15f, 0.45f);

        /// <summary>
        /// How far a loose body may drift from the replicated position before it is pulled back.
        /// Generous on purpose: each machine tumbles the corpse itself, and snapping on every small
        /// disagreement would look worse than the disagreement.
        /// </summary>
        const float DriftTolerance = 1.1f;

        FpsNetworkBridge _bridge;
        PlayerRagdoll _ragdoll;
        DeadBodyInteractable _interactable;
        SpectatorController _spectator;

        bool _down;
        bool _carried;

        public bool IsDown => _down;
        public PlayerRagdoll Ragdoll => _ragdoll;

        public void Bind(FpsNetworkBridge bridge, Transform body)
        {
            _bridge = bridge;
            _ragdoll = GetComponent<PlayerRagdoll>();
            _ragdoll.Bind(body);
        }

        #region Down and up

        /// <summary>
        /// Called on every machine the first time this player is seen to be dead.
        ///
        /// <paramref name="owner"/> is true only where the player is actually playing; that is the
        /// machine that loses its camera and gains a spectator view.
        /// </summary>
        public void EnterDown(bool owner, Vector3 velocity)
        {
            if (_down)
                return;
            _down = true;

            _ragdoll.Activate(velocity);
            // Bones off the interact mask, so they cannot swallow the prompt that belongs to the
            // body. The hips keep their own layer because that is where the prompt lives.
            _ragdoll.ApplyBoneLayer(LayerMask.NameToLayer("Player"));

            EnsureInteractable();

            if (!owner)
                return;

            _spectator = SpectatorController.Ensure();
            _spectator.Begin(_bridge);
        }

        /// <summary>Called on every machine when the player is revived.</summary>
        public void ExitDown(bool owner)
        {
            if (!_down)
                return;
            _down = false;
            _carried = false;

            _ragdoll.SetCarried(false);
            _ragdoll.Deactivate();

            if (_interactable != null)
            {
                Destroy(_interactable.gameObject);
                _interactable = null;
            }

            if (!owner)
                return;

            if (_spectator != null)
                _spectator.End();
            _spectator = null;
        }

        #endregion

        #region Per-frame placement

        /// <summary>
        /// Keeps the body where everyone agrees it is.
        ///
        /// Driven from the bridge's LateUpdate rather than its own, so it always runs after whatever
        /// moved the root this frame.
        /// </summary>
        public void Tick(bool owner)
        {
            if (!_down)
                return;

            var carrier = _bridge.ResolveCarrier();
            bool carried = carrier != null;

            if (carried != _carried)
            {
                _carried = carried;
                _ragdoll.SetCarried(carried);
            }

            if (carried)
            {
                // Derived on every machine from the carrier's own replicated transform, so the body
                // needs no position of its own while it is being carried.
                var yaw = Quaternion.Euler(0f, carrier.GetGameplayYaw(), 0f);
                _ragdoll.MoveTo(carrier.GetNetworkAnchorPosition() + yaw * CarryOffset);
                return;
            }

            if (owner)
                return;

            // Proxy: each machine tumbles its own corpse, so let it, and only correct when the two
            // have drifted far enough apart to matter.
            var replicated = _bridge.transform.position;
            if ((replicated - _ragdoll.BodyPosition).sqrMagnitude > DriftTolerance * DriftTolerance)
                _ragdoll.MoveTo(replicated);
        }

        /// <summary>
        /// The owner's root follows its own hips, which is what tells every other machine - and the
        /// collar - where the body ended up. Pinning around the write stops the root from dragging
        /// the ragdoll along with it.
        /// </summary>
        public void DriveRootFromBody(Transform root)
        {
            if (!_down || _ragdoll == null)
                return;

            _ragdoll.PinWorldPose();
            root.position = _ragdoll.BodyPosition;
            _ragdoll.RestoreWorldPose();
        }

        /// <summary>Moves a downed body somewhere, used by revives and team-failure teleports.</summary>
        public void TeleportBody(Vector3 position)
        {
            if (_down)
                _ragdoll.MoveTo(position);
        }

        #endregion

        #region Interact target

        /// <summary>
        /// Builds the thing survivors actually aim at.
        ///
        /// It has to be its own object on the Interactable layer with its own collider, because
        /// Cowsins resolves an interaction by looking for an Interactable on the exact collider its
        /// ray hit. Parenting it to the hips means it rides the body wherever the ragdoll ends up.
        /// </summary>
        void EnsureInteractable()
        {
            if (_interactable != null)
                return;

            var anchor = _ragdoll.Hips != null ? _ragdoll.Hips : transform;

            var go = new GameObject("BodyInteract");
            go.transform.SetParent(anchor, false);
            go.transform.localPosition = Vector3.zero;

            int layer = LayerMask.NameToLayer("Interactable");
            if (layer >= 0)
                go.layer = layer;

            // Sized in world metres and divided back out of the bone scale, which on this rig is
            // 100 - the same trap the ragdoll colliders have to avoid.
            float scale = Mathf.Max(0.0001f, Mathf.Abs(anchor.lossyScale.x));
            var sphere = go.AddComponent<SphereCollider>();
            sphere.radius = 0.55f / scale;

            // A TRIGGER, and that is not a detail. This object is parented to a bone that has a
            // Rigidbody, so a solid collider here would be absorbed into that bone's compound shape
            // and the corpse would end up resting on a half-metre ball at its hips. A trigger is
            // ignored by the physics solver but still found by the interact ray, because this
            // project leaves Physics.queriesHitTriggers on and Cowsins casts with the global default.
            sphere.isTrigger = true;

            _interactable = go.AddComponent<DeadBodyInteractable>();
            _interactable.Bind(_bridge);
        }

        #endregion
    }
}
#endif
