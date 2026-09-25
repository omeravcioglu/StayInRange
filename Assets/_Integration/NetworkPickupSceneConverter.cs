#if CMPSETUP_COMPLETE
using cowsins;
using Fusion;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali
{
    /// <summary>
    /// Converts scene-placed WeaponPickeables into networked pickups once Fusion is running.
    /// </summary>
    public static class NetworkPickupSceneConverter
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Init()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name != "Game")
                return;
            // Delay until runner + prefab exist.
            var host = new GameObject("NetworkPickupSceneConverter");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<ConverterRunner>();
        }

        class ConverterRunner : MonoBehaviour
        {
            float _deadline;

            void Start()
            {
                _deadline = Time.realtimeSinceStartup + 8f;
            }

            void Update()
            {
                NetworkCombatBootstrap.EnsurePickupPrefabAssigned();
                var runner = NetworkCombatHooks.FindRunner();
                if (runner == null || !runner.IsRunning || NetworkCombatHooks.NetworkWeaponPickupPrefab == null)
                {
                    if (Time.realtimeSinceStartup > _deadline)
                        Destroy(gameObject);
                    return;
                }

                if (!runner.IsSharedModeMasterClient && !runner.IsSceneAuthority)
                {
                    Destroy(gameObject);
                    return;
                }

                ConvertScenePickups(runner);
                Destroy(gameObject);
            }
        }

        static void ConvertScenePickups(NetworkRunner runner)
        {
            var pickups = Object.FindObjectsByType<WeaponPickeable>(FindObjectsSortMode.None);
            int converted = 0;
            int skippedNull = 0;
            int skippedNet = 0;
            foreach (var pick in pickups)
            {
                if (pick == null || pick.GetComponent<NetworkWeaponPickup>() != null)
                {
                    skippedNet++;
                    continue;
                }
                if (pick.weapon == null)
                {
                    skippedNull++;
                    // #region agent log
                    CollarCali.AgentDebugLog.Write("W2", "NetworkPickupSceneConverter", "skip_null_weapon",
                        "{\"go\":\"" + pick.name + "\"}");
                    // #endregion
                    continue;
                }

                var pos = pick.transform.position;
                var rot = pick.transform.rotation;
                var weapon = pick.weapon;
                Object.Destroy(pick.gameObject);

                NetworkWeaponPickup.SpawnDrop(
                    runner,
                    NetworkCombatHooks.NetworkWeaponPickupPrefab,
                    pos,
                    rot,
                    weapon,
                    weapon.magazineSize,
                    weapon.totalMagazines * weapon.magazineSize);
                converted++;
            }

            // #region agent log
            CollarCali.AgentDebugLog.Write("W2", "NetworkPickupSceneConverter", "convert_summary",
                "{\"converted\":" + converted +
                ",\"skippedNull\":" + skippedNull +
                ",\"skippedNet\":" + skippedNet +
                ",\"prefabOk\":" + (NetworkCombatHooks.NetworkWeaponPickupPrefab != null ? "true" : "false") + "}");
            // #endregion
        }
    }
}
#endif
