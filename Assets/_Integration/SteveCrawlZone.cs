using MalbersAnimations;
using MalbersAnimations.Controller;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Holds Steve in the Crouch stance while he is inside the volume.
    /// Vent tunnels are 1.5 m tall and his standing capsule does not fit.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class SteveCrawlZone : MonoBehaviour
    {
        [SerializeField] StanceID crouchStance;

        void Reset()
        {
            GetComponent<Collider>().isTrigger = true;
        }

        void OnTriggerEnter(Collider other)
        {
            var animal = Resolve(other);
            if (animal != null)
                animal.Stance_SetPersistent(crouchStance);
        }

        void OnTriggerExit(Collider other)
        {
            var animal = Resolve(other);
            if (animal != null)
                animal.Stance_ResetPersistent(crouchStance);
        }

        MAnimal Resolve(Collider other)
        {
            if (crouchStance == null)
                return null;

            // Ignore Malbers radius / attack / interact volumes.
            if (other.isTrigger)
                return null;

            var animal = other.GetComponentInParent<MAnimal>();
            if (animal == null)
                return null;

            return other == animal.MainCollider ? animal : null;
        }
    }
}
