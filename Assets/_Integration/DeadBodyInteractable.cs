#if CMPSETUP_COMPLETE
using cowsins;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// The "carry body" prompt on a dead player, built on Cowsins' own Interactable so it uses the
    /// same crosshair detection, prompt UI and hold-to-interact the rest of the game already has.
    ///
    /// Created at runtime by PlayerDownState rather than authored on the player prefab, because it
    /// only exists while somebody is dead and it has to live on a collider that rides the hips.
    ///
    /// Like WeaponPickeable, this does not act locally. Interact() only ever runs on the machine of
    /// the player who pressed the button, so it sends a request to the DEAD player's own authority
    /// and lets that decide - which is what stops two survivors grabbing the same body.
    /// </summary>
    public class DeadBodyInteractable : Interactable
    {
        FpsNetworkBridge _body;

        public void Bind(FpsNetworkBridge body)
        {
            _body = body;
            interactText = "Carry body";
        }

        public override void Interact(Transform player)
        {
            if (_body == null)
                return;

            // The interacting player's own bridge, found through the session rather than through the
            // hierarchy: for the local player the Cowsins rig is detached from the network root, so
            // GetComponentInParent would come back empty.
            var carrier = NetworkCombatHooks.FindLocalBridge();
            if (carrier == null || carrier == _body)
                return;

            bool minePlease = !IsCarriedByLocalPlayer(carrier);
            _body.RequestCarry(carrier, minePlease);

            base.Interact(player);

            // Cleared immediately so the same body can be dropped again straight away. The base sets
            // it to lock one-shot pickups, which this is not.
            alreadyInteracted = false;
        }

        public override void Highlight()
        {
            base.Highlight();
            RefreshText();
        }

        /// <summary>
        /// Blocks the prompt when somebody else already has the body, rather than letting a player
        /// press it and have the request silently rejected by the authority.
        /// </summary>
        public override bool IsForbiddenInteraction(IWeaponReferenceProvider weaponController)
        {
            if (_body == null || !_body.IsDead)
                return true;

            var carrier = NetworkCombatHooks.FindLocalBridge();
            if (carrier == null)
                return true;

            return _body.IsCarried && !IsCarriedByLocalPlayer(carrier);
        }

        void RefreshText()
        {
            if (_body == null)
                return;

            var carrier = NetworkCombatHooks.FindLocalBridge();
            if (carrier != null && IsCarriedByLocalPlayer(carrier))
            {
                interactText = "Drop body";
                return;
            }

            interactText = _body.IsCarried ? "Teammate is carrying this body" : "Carry body";
        }

        bool IsCarriedByLocalPlayer(FpsNetworkBridge carrier)
        {
            return _body != null && carrier != null && carrier.Object != null &&
                   _body.CarriedBy == carrier.Object.InputAuthority;
        }
    }
}
#endif
