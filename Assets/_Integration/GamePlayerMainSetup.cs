using cowsins;
#if CMPSETUP_COMPLETE
using Fusion;
#endif
using MalbersAnimations.Controller;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali
{
    /// <summary>
    /// Game scene: wrap the existing FPS + Steve + Cameras CM3 under PlayerMain,
    /// start in first person, and keep an FPS-to-TPS switch collider.
    /// </summary>
    public static class GamePlayerMainSetup
    {
        const string SwitchName = "FPS TPS Switch";

        // Fusion loads Game.unity after startup, so RuntimeInitializeOnLoadMethod alone never
        // fires for the Menu -> Game path. Listen for the scene load as well.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == "Game")
                Setup(scene);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AfterSceneLoad()
        {
            var active = SceneManager.GetActiveScene();
            if (active.name == "Game")
                Setup(active);
        }

        static void Setup(Scene scene)
        {
            CowsinsUrpCameraStack.TryAttach(scene);

            var spawn = Object.FindFirstObjectByType<LocalDualPlayerSpawn>(FindObjectsInactive.Include);
            if (spawn != null)
                spawn.enabled = false;

            if (FusionSessionIsRunning())
            {
                // #region agent log
                AgentDebugLog.Write("B1", "GamePlayerMainSetup.Setup", "fusion_skip_offline",
                    "{\"scene\":\"" + scene.name + "\"}");
                // #endregion
                DisableSceneOfflinePlayers();
                PinMalbersStaminaHud();
                var switchOrigin = FindFpsRoot();
                EnsureToggleSwitch(switchOrigin != null ? switchOrigin.transform.position : new Vector3(0f, 0f, -35f));
                return;
            }

            // #region agent log
            AgentDebugLog.Write("B1", "GamePlayerMainSetup.Setup", "offline_wrap_playermain",
                "{\"scene\":\"" + scene.name + "\"}");
            // #endregion

            var fps = FindFpsRoot();
            var steve = FindSteveRoot();
            var cameras = FindNamedIncludingInactive("Cameras CM3");

            var main = FindNamedIncludingInactive("PlayerMain");
            if (main == null)
            {
                main = new GameObject("PlayerMain");
                SceneManager.MoveGameObjectToScene(main, SceneManager.GetActiveScene());
                if (fps != null)
                    main.transform.SetPositionAndRotation(fps.transform.position, fps.transform.rotation);
            }

            var dual = main.GetComponent<DualPlayerController>();
            if (dual == null)
                dual = main.AddComponent<DualPlayerController>();

            dual.WireExisting(fps, steve, cameras);
            PinMalbersStaminaHud();
            if (steve != null)
                steve.SetActive(false);
            if (cameras != null)
                cameras.SetActive(false);
            if (fps != null)
                fps.SetActive(true);

            DisableExtraFps(main);
            EnsureToggleSwitch(fps != null ? fps.transform.position : main.transform.position);

            // Offline (pressing Play directly on Game.unity) there is no network spawn, so the
            // player used to start at wherever the FPS object happened to sit in the scene - which
            // meant an accidental nudge in the editor moved the start point. Snap to the same
            // TeamSpawnPoints the networked path uses, so the authored spawn markers are always the
            // source of truth.
            MoveOfflinePlayerToSpawn(main, fps);

            // #region agent log
            AgentDebugLog.LogPlayerSetup(
                "H2",
                "GamePlayerMainSetup.AfterSceneLoad",
                "offline_direct_game",
                fps,
                steve,
                networkMode: false);
            // #endregion
        }

        static void MoveOfflinePlayerToSpawn(GameObject main, GameObject fps)
        {
            var reg = TeamSpawnPoints.Find();
            if (reg == null || !reg.TryGetSpawn(0, out var pos, out var yaw))
                return;   // no spawn markers wired: keep the authored scene position

            var rot = Quaternion.Euler(0f, yaw, 0f);

            if (main != null)
                main.transform.SetPositionAndRotation(pos, rot);

            if (fps != null)
            {
                fps.transform.SetPositionAndRotation(pos, rot);
                // The Cowsins controller is a rigidbody; move it and kill any carried velocity so
                // it does not slide off the mark the instant physics resumes.
                var rb = fps.GetComponentInChildren<Rigidbody>();
                if (rb != null)
                {
                    rb.position = pos;
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }

            // #region agent log
            AgentDebugLog.Write("B1", "GamePlayerMainSetup.MoveOfflinePlayerToSpawn", "offline_spawn",
                "{\"pos\":\"" + pos.ToString() + "\",\"yaw\":" + yaw + "}");
            // #endregion
        }

        static bool FusionSessionIsRunning()
        {
#if CMPSETUP_COMPLETE
            foreach (var runner in NetworkRunner.Instances)
            {
                if (runner != null && runner.IsRunning)
                    return true;
            }

            if (AvocadoShark.FusionConnection.Instance != null)
                return true;
#endif
            return false;
        }

        static void DisableSceneOfflinePlayers()
        {
            int disabled = 0;

            // Match by component, not by name: the scene player is a renamed prefab variant
            // ("MovementCowsinsFPSController Variant" / "Steve Player (Holsters)").
            foreach (var movement in Object.FindObjectsByType<PlayerMovement>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (movement == null)
                    continue;
                var root = ClimbFpsController(movement.transform);
                if (root == null || IsOwnedByNetworkPlayer(root))
                    continue;
                root.SetActive(false);
                disabled++;
            }

            foreach (var animal in Object.FindObjectsByType<MAnimal>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (animal == null)
                    continue;
                var root = ClimbSteveRoot(animal.transform);
                if (root == null || IsOwnedByNetworkPlayer(root))
                    continue;
                root.SetActive(false);
                disabled++;
            }

            foreach (var name in new[] { "Cameras CM3", "PlayerMain" })
            {
                var go = FindNamedIncludingInactive(name);
                if (go == null || IsOwnedByNetworkPlayer(go))
                    continue;
                go.SetActive(false);
                disabled++;
            }

            // #region agent log
            AgentDebugLog.Write("B1", "GamePlayerMainSetup.DisableSceneOfflinePlayers", "disabled_offline",
                "{\"disabled\":" + disabled + "}");
            // #endregion
        }

        static GameObject ClimbSteveRoot(Transform t)
        {
            while (t != null)
            {
                if (t.GetComponent<DualPlayerController>() != null || t.name == "PlayerMain")
                    return null;
                if (t.name.IndexOf("Steve", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return t.gameObject;
                t = t.parent;
            }

            return null;
        }

        static bool IsOwnedByNetworkPlayer(GameObject go)
        {
            if (go == null)
                return false;
#if CMPSETUP_COMPLETE
            if (go.GetComponentInParent<FpsNetworkBridge>() != null)
                return true;
#endif

            foreach (var dual in Object.FindObjectsByType<DualPlayerController>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (dual == null || !dual.IsNetworkMode)
                    continue;
                if (go == dual.gameObject)
                    return true;
                if (dual.FpsBody != null &&
                    (go.transform == dual.FpsBody || go.transform.IsChildOf(dual.FpsBody) ||
                     dual.FpsBody.IsChildOf(go.transform)))
                    return true;
                if (dual.SteveRoot != null &&
                    (go == dual.SteveRoot || go.transform.IsChildOf(dual.SteveRoot.transform)))
                    return true;
            }

            return false;
        }

        static void DisableExtraFps(GameObject main)
        {
            if (main == null)
                return;

            var nested = main.GetComponentInChildren<PlayerMovement>(true);
            if (nested == null)
                return;

            foreach (var movement in Object.FindObjectsByType<PlayerMovement>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (movement == null || movement.transform.IsChildOf(main.transform))
                    continue;

                var root = ClimbFpsController(movement.transform);
                if (root != null && root != main)
                    root.SetActive(false);
            }
        }

        static GameObject FindNamedIncludingInactive(string name)
        {
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t != null && t.name == name)
                    return t.gameObject;
            }

            return null;
        }

        static void PinMalbersStaminaHud()
        {
            var slider = FindNamedIncludingInactive("Slider Stamina UI v2");
            ApplyStaminaHudPin(slider);

            // #region agent log
            LogStaminaHudState("pin_immediate", slider);
            // #endregion

            // Malbers re-enables UIFollowTransform from its own Start, which drags the slider back to
            // a world-space follow position and off screen, so pinning once at scene load never held.
            MalbersStaminaHudKeeper.Schedule(slider);
        }

        static void ApplyStaminaHudPin(GameObject slider)
        {
            foreach (var follow in Object.FindObjectsByType<MalbersAnimations.UI.UIFollowTransform>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (follow != null)
                    follow.enabled = false;
            }

            if (slider == null)
                return;

            var rect = slider.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.anchorMin = new Vector2(0.5f, 1f);
                rect.anchorMax = new Vector2(0.5f, 1f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = new Vector2(0f, -225f);
                rect.localRotation = Quaternion.identity;
                rect.localScale = Vector3.one;
            }

            foreach (var child in slider.GetComponentsInChildren<RectTransform>(true))
            {
                if (child == null || child.gameObject == slider)
                    continue;
                child.anchoredPosition = Vector2.zero;
                child.localPosition = Vector3.zero;
            }

            slider.SetActive(true);
        }

        /// <summary>
        /// Re-asserts the pin for a few seconds so Malbers' own initialization cannot claim the HUD
        /// back after the scene-load pass has run.
        /// </summary>
        sealed class MalbersStaminaHudKeeper : MonoBehaviour
        {
            const float HoldSeconds = 5f;
            const float ReapplyInterval = 0.25f;

            GameObject _slider;
            float _deadline;
            float _nextApply;
            // #region agent log
            float _lateLogAt;
            bool _lateLogged;
            // #endregion

            public static void Schedule(GameObject slider)
            {
                var keeper = FindFirstObjectByType<MalbersStaminaHudKeeper>();
                if (keeper == null)
                {
                    var host = new GameObject("MalbersStaminaHudKeeper");
                    keeper = host.AddComponent<MalbersStaminaHudKeeper>();
                    // #region agent log
                    keeper._lateLogAt = Time.realtimeSinceStartup + 2f;
                    // #endregion
                }

                keeper._slider = slider;
                keeper._deadline = Time.realtimeSinceStartup + HoldSeconds;
                keeper._nextApply = 0f;
            }

            void Update()
            {
                if (Time.realtimeSinceStartup >= _nextApply)
                {
                    _nextApply = Time.realtimeSinceStartup + ReapplyInterval;
                    ApplyStaminaHudPin(_slider);
                }

                // #region agent log
                if (!_lateLogged && Time.realtimeSinceStartup >= _lateLogAt)
                {
                    _lateLogged = true;
                    LogStaminaHudState("pin_after_2s", _slider);
                }
                // #endregion

                if (Time.realtimeSinceStartup >= _deadline)
                    Destroy(gameObject);
            }
        }

        // #region agent log
        internal static void LogStaminaHudState(string stage, GameObject slider)
        {
            if (slider == null)
            {
                AgentDebugLog.Write("S1", "GamePlayerMainSetup.StaminaHud", stage, "{\"slider\":\"null\"}");
                return;
            }

            var rect = slider.GetComponent<RectTransform>();
            var canvas = slider.GetComponentInParent<Canvas>();
            var path = slider.name;
            for (var t = slider.transform.parent; t != null; t = t.parent)
                path = t.name + "/" + path;

            int follows = 0;
            foreach (var f in Object.FindObjectsByType<MalbersAnimations.UI.UIFollowTransform>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (f != null && f.enabled)
                    follows++;
            }

            AgentDebugLog.Write("S1", "GamePlayerMainSetup.StaminaHud", stage,
                "{\"path\":\"" + path +
                "\",\"activeSelf\":" + (slider.activeSelf ? "true" : "false") +
                ",\"activeInHierarchy\":" + (slider.activeInHierarchy ? "true" : "false") +
                ",\"anchoredPos\":\"" + (rect != null ? rect.anchoredPosition.ToString() : "n/a") +
                "\",\"sizeDelta\":\"" + (rect != null ? rect.sizeDelta.ToString() : "n/a") +
                "\",\"lossyScale\":\"" + slider.transform.lossyScale.ToString() +
                "\",\"canvas\":\"" + (canvas != null ? canvas.name : "null") +
                "\",\"canvasEnabled\":" + (canvas != null && canvas.enabled ? "true" : "false") +
                ",\"canvasActive\":" + (canvas != null && canvas.gameObject.activeInHierarchy ? "true" : "false") +
                ",\"renderMode\":\"" + (canvas != null ? canvas.renderMode.ToString() : "null") +
                "\",\"followsStillEnabled\":" + follows + "}");
        }

        // #endregion

        static GameObject FindFpsRoot()
        {
            var movement = Object.FindFirstObjectByType<PlayerMovement>(FindObjectsInactive.Include);
            if (movement == null)
                return null;

            return ClimbFpsController(movement.transform);
        }

        static GameObject ClimbFpsController(Transform t)
        {
            while (t.parent != null &&
                   t.parent.GetComponent<DualPlayerController>() == null &&
                   t.parent.name != "PlayerMain")
            {
                t = t.parent;
            }

            return t.gameObject;
        }

        static GameObject FindSteveRoot()
        {
            foreach (var animal in Object.FindObjectsByType<MAnimal>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (animal == null)
                    continue;

                var t = animal.transform;
                while (t != null)
                {
                    if (t.GetComponent<DualPlayerController>() != null || t.name == "PlayerMain")
                        break;
                    if (t.name.IndexOf("Steve", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        return t.gameObject;
                    t = t.parent;
                }
            }

            return null;
        }

        static void EnsureToggleSwitch(Vector3 fpsPosition)
        {
            var existing = GameObject.Find(SwitchName);
            if (existing != null)
            {
                var trigger = existing.GetComponent<PlayerModeSwitchTrigger>();
                if (trigger == null)
                    trigger = existing.AddComponent<PlayerModeSwitchTrigger>();
                trigger.UseFpsToTpsOnly();
                return;
            }

            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = SwitchName;
            go.transform.position = fpsPosition + new Vector3(0f, 1.5f, 5f);
            go.transform.localScale = new Vector3(4f, 3f, 3f);

            var col = go.GetComponent<Collider>();
            col.isTrigger = true;

            var rend = go.GetComponent<Renderer>();
            if (rend != null)
                rend.enabled = false;

            go.AddComponent<PlayerModeSwitchTrigger>().UseFpsToTpsOnly();
            SceneManager.MoveGameObjectToScene(go, SceneManager.GetActiveScene());
        }
    }
}
