using cowsins;
using MalbersAnimations.Controller;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Walk through this trigger to swap DualPlayerController between FPS and Malbers TPP.
    /// Only the character body collider counts — Steve radius / attack / interact spheres are ignored.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class PlayerModeSwitchTrigger : MonoBehaviour
    {
        public enum SwitchMode
        {
            EnterThirdPerson,
            ExitToFirstPerson,
            Toggle
        }

        [SerializeField] SwitchMode mode = SwitchMode.EnterThirdPerson;
        [SerializeField] float cooldownSeconds = 1.25f;

        public void UseFpsToTpsOnly()
        {
            mode = SwitchMode.EnterThirdPerson;
        }

        float _nextAllowedTime;

        void Reset()
        {
            var col = GetComponent<Collider>();
            col.isTrigger = true;
        }

        void OnTriggerEnter(Collider other)
        {
            if (Time.time < _nextAllowedTime)
                return;

            // Ignore Malbers radius / attack / interact trigger volumes.
            if (other.isTrigger)
                return;

            var dual = ResolveDualPlayer(other);
            if (dual == null)
                return;

            if (mode == SwitchMode.Toggle)
            {
                if (dual.IsThirdPerson)
                {
                    if (!IsSteveBodyCollider(other, dual))
                        return;
                    _nextAllowedTime = Time.time + cooldownSeconds;
                    PlaySwitch(toThirdPerson: false);
                    dual.ExitToFirstPerson();
                    return;
                }

                if (!IsFpsBodyCollider(other))
                    return;
                _nextAllowedTime = Time.time + cooldownSeconds;
                PlaySwitch(toThirdPerson: true);
                dual.EnterThirdPerson();
                return;
            }

            if (mode == SwitchMode.EnterThirdPerson)
            {
                if (dual.IsThirdPerson)
                    return;
                if (!IsFpsBodyCollider(other))
                    return;

                _nextAllowedTime = Time.time + cooldownSeconds;
                PlaySwitch(toThirdPerson: true);
                dual.EnterThirdPerson();
                return;
            }

            if (!dual.IsThirdPerson)
                return;
            if (!IsSteveBodyCollider(other, dual))
                return;

            _nextAllowedTime = Time.time + cooldownSeconds;
            PlaySwitch(toThirdPerson: false);
            dual.ExitToFirstPerson();
        }

        /// <summary>
        /// Played from here rather than from inside DualPlayerController, because every path into
        /// this method has already established that the body belongs to this machine's player -
        /// the controller itself is also driven for remote copies, which must stay silent.
        /// </summary>
        static void PlaySwitch(bool toThirdPerson)
        {
            GameSfx.Play2D(toThirdPerson
                ? SfxId.ModeSwitchToThirdPerson
                : SfxId.ModeSwitchToFirstPerson);
        }

        void OnDrawGizmos()
        {
            var box = GetComponent<BoxCollider>();
            if (box == null)
                return;

            Gizmos.color = mode == SwitchMode.Toggle
                ? new Color(0.2f, 0.65f, 1f, 0.35f)
                : mode == SwitchMode.EnterThirdPerson
                    ? new Color(0.2f, 0.85f, 0.35f, 0.35f)
                    : new Color(0.9f, 0.35f, 0.2f, 0.35f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawCube(box.center, box.size);
        }

        static bool IsSteveBodyCollider(Collider other, DualPlayerController dual)
        {
            var animal = dual.Animal;
            if (animal == null)
                animal = other.GetComponentInParent<MAnimal>();
            if (animal == null)
                return false;

            // Prefer Malbers main capsule — not child radius / hit / interact colliders.
            if (animal.MainCollider != null)
                return other == animal.MainCollider;

            // Fallback: collider on the same GameObject as MAnimal / Rigidbody root.
            return other.GetComponent<MAnimal>() != null ||
                   (animal.RB != null && other.attachedRigidbody == animal.RB && other is CapsuleCollider);
        }

        static bool IsFpsBodyCollider(Collider other)
        {
            var movement = other.GetComponentInParent<PlayerMovement>();
            if (movement == null)
                return false;

            // Body capsule on the PlayerMovement object (not weapon/trigger children).
            var bodyCapsule = movement.GetComponent<CapsuleCollider>();
            if (bodyCapsule != null)
                return other == bodyCapsule;

            return other.transform == movement.transform;
        }

        static DualPlayerController ResolveDualPlayer(Collider other)
        {
            foreach (var dual in FindObjectsByType<DualPlayerController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (dual != null && dual.OwnsCollider(other))
                    return IsLocallyOwned(dual) ? dual : null;
            }

            var nested = other.GetComponentInParent<DualPlayerController>();
            if (nested != null)
                return IsLocallyOwned(nested) ? nested : null;

            return null;
        }

        static bool IsLocallyOwned(DualPlayerController dual)
        {
            if (dual == null)
                return false;
#if CMPSETUP_COMPLETE
            var bridge = dual.NetworkBridge != null
                ? dual.NetworkBridge
                : dual.GetComponent<FpsNetworkBridge>();
            if (bridge == null)
                return true;

            return bridge.IsLocalOwner;
#else
            return true;
#endif
        }
    }
}
