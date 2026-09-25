#if CMPSETUP_COMPLETE
using AvocadoShark;
using cowsins;
using Fusion;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Static helpers used by light Cowsins hooks for networked combat/pickups.
    /// </summary>
    public static class NetworkCombatHooks
    {
        public static NetworkObject NetworkWeaponPickupPrefab;
        public static NetworkObject TeamLinkPrefab;
        public static NetworkObject WorldActorPrefab;

        public static FpsNetworkBridge FindLocalBridge()
        {
            if (FusionConnection.Instance != null &&
                FusionConnection.Instance.TryGetLocalPlayerComponent(out FpsNetworkBridge bridge))
                return bridge;

            foreach (var b in Object.FindObjectsByType<FpsNetworkBridge>(FindObjectsSortMode.None))
            {
                if (b != null && b.IsLocalOwner)
                    return b;
            }
            return null;
        }

        public static NetworkRunner FindRunner()
        {
            if (FusionConnection.Instance != null && FusionConnection.Instance.Runner != null)
                return FusionConnection.Instance.Runner;

            foreach (var runner in NetworkRunner.Instances)
            {
                if (runner != null && runner.IsRunning)
                    return runner;
            }
            return null;
        }

        public static void NotifyLocalShot()
        {
            FindLocalBridge()?.NotifyShot();
        }

        public static void NotifyExplosion(Vector3 position, GameObject explosionVfx)
        {
            if (NetworkVfxRelay.Instance != null)
            {
                NetworkVfxRelay.Instance.BroadcastExplosion(position, explosionVfx);
                return;
            }

            FindLocalBridge()?.NotifyExplosion(position, explosionVfx);
        }

        public static void NotifyBarrelExplode(Vector3 position)
        {
            if (NetworkVfxRelay.Instance != null)
            {
                NetworkVfxRelay.Instance.BroadcastBarrelExplode(position);
                return;
            }

            FindLocalBridge()?.NotifyBarrelExplode(position);
        }

        public static bool TryNetworkWeaponPickupInteract(WeaponPickeable pickeable, Transform player)
        {
            var net = pickeable != null ? pickeable.GetComponent<NetworkWeaponPickup>() : null;
            if (net == null)
                return false;
            return net.TryNetworkPickup(player);
        }

        public static bool TrySpawnNetworkWeaponDrop(
            Vector3 position,
            Quaternion rotation,
            Weapon_SO weapon,
            int currentBullets,
            int totalBullets,
            out WeaponPickeable localPickeable)
        {
            localPickeable = null;
            var runner = FindRunner();
            if (runner == null || NetworkWeaponPickupPrefab == null || weapon == null)
                return false;

            var pickup = NetworkWeaponPickup.SpawnDrop(
                runner, NetworkWeaponPickupPrefab, position, rotation, weapon, currentBullets, totalBullets);
            if (pickup == null)
                return false;

            localPickeable = pickup.GetComponent<WeaponPickeable>();
            return localPickeable != null;
        }
    }
}
#endif
