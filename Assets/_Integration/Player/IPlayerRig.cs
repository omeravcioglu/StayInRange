using System.Collections.Generic;
using MalbersAnimations.Controller;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// The local player's controller as the rest of the game sees it: telekinesis, the HUD, pause,
    /// the network bridge, enemies that grab. DualPlayerController implements it today and the
    /// third-person-only controller will tomorrow, so those systems can move over to this interface
    /// first and not care which one is underneath.
    /// </summary>
    public interface IPlayerRig
    {
        /// <summary>The Malbers character, when the rig has one.</summary>
        MAnimal Animal { get; }

        /// <summary>The visible third-person body's root.</summary>
        GameObject Body { get; }

        bool IsThirdPerson { get; }

        /// <summary>True while dead: nothing moves and mode changes are ignored.</summary>
        bool IsSuspended { get; }

        /// <summary>On a ladder or climbing a wall right now.</summary>
        bool IsClimbing { get; }

        /// <summary>True while the third-person view is in the character's eyes for aiming a throw.</summary>
        bool IsAimViewLive { get; }

        bool IsMenuOpen { get; }

        void SetThirdPersonAimView(bool active, float returnDelay = 0f);

        /// <summary>Where a throw starts from and the direction the player is aiming.</summary>
        bool TryGetAim(out Vector3 origin, out Vector3 forward);

        /// <summary>The ray through the middle of the screen.</summary>
        bool TryGetCrosshairRay(out Ray ray);

        Vector3 GetBodyVelocity();

        /// <summary>Adds the colliders of the player's own body, so nothing the player holds collides with them.</summary>
        void CollectOwnColliders(List<Collider> into);

        bool OwnsCollider(Collider other);

        /// <summary>Holding a body or prop: slower, and the keys the throw uses can't attack.</summary>
        void SetCarrying(bool carrying, float speedMultiplier);

        /// <summary>The interact key belongs to telekinesis for now, not to climbing or doors.</summary>
        void SetInteractClaimed(bool claimed);

        void SetMenuOpen(bool open);

        void EnterDowned();

        void ExitDowned();

        void TeleportGameplay(Vector3 position, float yawDegrees);

        /// <summary>The player's feet in the world.</summary>
        Vector3 GetGameplayWorldPosition();

        float GetGameplayYaw();
    }

    /// <summary>Finds the local player's rig without knowing which controller it is.</summary>
    public static class PlayerRigs
    {
        static readonly List<IPlayerRig> Registered = new List<IPlayerRig>();

        /// <summary>The local player's rig, or null. Only the local player has one.</summary>
        public static IPlayerRig Local
        {
            get
            {
                for (int i = Registered.Count - 1; i >= 0; i--)
                {
                    if (Registered[i] is Object unityObject && unityObject == null)
                        Registered.RemoveAt(i);
                }

                return Registered.Count > 0 ? Registered[Registered.Count - 1] : null;
            }
        }

        /// <summary>Called by a rig when it becomes active.</summary>
        public static void Register(IPlayerRig rig)
        {
            if (rig != null && !Registered.Contains(rig))
                Registered.Add(rig);
        }

        /// <summary>Called by a rig when it stops being active.</summary>
        public static void Unregister(IPlayerRig rig)
        {
            Registered.Remove(rig);
        }
    }
}
