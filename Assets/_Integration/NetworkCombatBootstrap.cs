#if CMPSETUP_COMPLETE
using Fusion;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Holds a hard reference to NetworkWeaponPickup so builds include the Fusion prefab.
    /// </summary>
    public class NetworkCombatBootstrap : MonoBehaviour
    {
        [SerializeField] NetworkObject networkWeaponPickupPrefab;

        static NetworkCombatBootstrap _instance;

        void Awake()
        {
            _instance = this;
            EnsurePickupPrefabAssigned();
        }

        public static void EnsurePickupPrefabAssigned()
        {
            if (NetworkCombatHooks.NetworkWeaponPickupPrefab != null)
                return;

            if (_instance != null && _instance.networkWeaponPickupPrefab != null)
            {
                NetworkCombatHooks.NetworkWeaponPickupPrefab = _instance.networkWeaponPickupPrefab;
                return;
            }

            foreach (var bootstrap in Object.FindObjectsByType<NetworkCombatBootstrap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (bootstrap != null && bootstrap.networkWeaponPickupPrefab != null)
                {
                    NetworkCombatHooks.NetworkWeaponPickupPrefab = bootstrap.networkWeaponPickupPrefab;
                    return;
                }
            }

#if UNITY_EDITOR
            var go = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/_Integration/Prefabs/NetworkWeaponPickup.prefab");
            if (go != null)
                NetworkCombatHooks.NetworkWeaponPickupPrefab = go.GetComponent<NetworkObject>();
#endif
        }
    }
}
#endif
