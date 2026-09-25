using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace CollarCali
{
    /// <summary>
    /// Cowsins ships a Built-in Main Camera + WeaponCamera pair. On URP both
    /// become Base cameras, so WeaponCamera paints over the world. Stack it as
    /// Overlay and keep weapons-only culling. Does not restyle the HUD.
    /// Scene 1 cameras stay on the FlatKit renderer; every other camera uses 0.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    public sealed class CowsinsUrpCameraStack : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Register()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AfterSceneLoad()
        {
            TryAttach(SceneManager.GetActiveScene());
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            TryAttach(scene);
        }

        public static void TryAttach(Scene scene)
        {
            // Lobby is UI-only and drives its own camera; stacking Cowsins overlays onto
            // it just fights LobbyController for the same settings.
            if (!scene.IsValid() || scene.name == "1" || scene.name == "Lobby")
                return;

            foreach (var existing in FindObjectsByType<CowsinsUrpCameraStack>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (existing != null && existing.gameObject.scene == scene)
                {
                    existing.Apply();
                    return;
                }
            }

            var go = new GameObject("CowsinsUrpCameraStack");
            SceneManager.MoveGameObjectToScene(go, scene);
            go.AddComponent<CowsinsUrpCameraStack>();
        }

        int _lastCameraState;

        void Awake()
        {
            Apply();
        }

        void LateUpdate()
        {
            var cameras = Camera.allCameras;
            int state = cameras.Length;
            for (int i = 0; i < cameras.Length; i++)
            {
                var cam = cameras[i];
                if (cam != null && cam.isActiveAndEnabled)
                    state = unchecked(state * 31 + cam.GetInstanceID());
            }

            if (state == _lastCameraState)
                return;
            _lastCameraState = state;
            Apply();
        }

        public void Apply()
        {
            var cameras = Camera.allCameras;
            for (int i = 0; i < cameras.Length; i++)
            {
                var cam = cameras[i];
                if (cam == null || !cam.isActiveAndEnabled)
                    continue;
                if (cam.cameraType != CameraType.Game)
                    continue;
                if (cam.gameObject.scene.name == "1")
                    continue;

                int rendererIndex = 0;
                ForceCleanRenderer(cam, rendererIndex);
                StackIfWeaponCamera(cam, rendererIndex);
            }
        }

        static void ForceCleanRenderer(Camera cam, int rendererIndex)
        {
            var data = cam.GetUniversalAdditionalCameraData();
            data.SetRenderer(rendererIndex);
        }

        static void StackIfWeaponCamera(Camera weaponCam, int rendererIndex)
        {
            if (weaponCam.name != "WeaponCamera")
                return;

            Camera baseCam = null;
            var parent = weaponCam.transform.parent;
            if (parent != null)
                baseCam = parent.GetComponent<Camera>();
            if (baseCam == null)
                baseCam = Camera.main;
            if (baseCam == null || baseCam == weaponCam)
                return;
            if (baseCam.gameObject.scene.name == "1")
                return;

            DisablePostProcessLayer(baseCam);
            DisablePostProcessLayer(weaponCam);
            UseNormalProjection(baseCam);
            UseNormalProjection(weaponCam);

            baseCam.rect = new Rect(0f, 0f, 1f, 1f);
            weaponCam.rect = new Rect(0f, 0f, 1f, 1f);
            weaponCam.fieldOfView = baseCam.fieldOfView;
            weaponCam.nearClipPlane = Mathf.Min(weaponCam.nearClipPlane, 0.01f);
            weaponCam.clearFlags = CameraClearFlags.Depth;
            weaponCam.backgroundColor = new Color(0f, 0f, 0f, 0f);

            int weaponsLayer = LayerMask.NameToLayer("Weapons");
            if (weaponsLayer >= 0)
                weaponCam.cullingMask = 1 << weaponsLayer;

            var baseData = baseCam.GetUniversalAdditionalCameraData();
            var overlayData = weaponCam.GetUniversalAdditionalCameraData();

            baseData.renderType = CameraRenderType.Base;
            baseData.SetRenderer(rendererIndex);
            overlayData.renderType = CameraRenderType.Overlay;
            overlayData.SetRenderer(rendererIndex);
            overlayData.renderPostProcessing = false;

            if (!baseData.cameraStack.Contains(weaponCam))
                baseData.cameraStack.Add(weaponCam);
        }

        static void UseNormalProjection(Camera cam)
        {
            cam.ResetProjectionMatrix();
            cam.usePhysicalProperties = false;
            cam.ResetAspect();
            cam.gateFit = Camera.GateFitMode.Vertical;
        }

        static void DisablePostProcessLayer(Camera cam)
        {
            var behaviours = cam.GetComponents<Behaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour != null && behaviour.GetType().Name == "PostProcessLayer")
                    behaviour.enabled = false;
            }
        }
    }
}
