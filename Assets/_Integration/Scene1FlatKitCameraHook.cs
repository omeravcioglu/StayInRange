using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace CollarCali
{
    /// <summary>
    /// Scene 1 play cameras come from Cinemachine/CM3, not the disabled Main Camera.
    /// Point those cameras at the FlatKit URP renderer (index 1). Other scenes stay on 0.
    /// </summary>
    public sealed class Scene1FlatKitCameraHook : MonoBehaviour
    {
        const int Scene1RendererIndex = 1;

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

        static void TryAttach(Scene scene)
        {
            if (!scene.IsValid() || scene.name != "1")
                return;

            foreach (var existing in FindObjectsByType<Scene1FlatKitCameraHook>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (existing != null && existing.gameObject.scene == scene)
                {
                    Scene1FlatKitLook.TryAttach(scene);
                    return;
                }
            }

            var go = new GameObject("Scene1FlatKitCameraHook");
            SceneManager.MoveGameObjectToScene(go, scene);
            go.AddComponent<Scene1FlatKitCameraHook>();
            Scene1FlatKitLook.TryAttach(scene);
        }

        static bool Scene2IsLoaded()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.name == "2")
                    return true;
            }
            return false;
        }

        void LateUpdate()
        {
            if (Scene2IsLoaded())
                return;

            var cameras = Camera.allCameras;
            for (int i = 0; i < cameras.Length; i++)
            {
                var cam = cameras[i];
                if (cam == null || !cam.isActiveAndEnabled)
                    continue;
                if (cam.cameraType == CameraType.SceneView || cam.cameraType == CameraType.Preview)
                    continue;
                if (cam.gameObject.scene.name != "1")
                    continue;

                var data = cam.GetUniversalAdditionalCameraData();
                if (data.renderType == CameraRenderType.Overlay)
                    continue;
                data.SetRenderer(Scene1RendererIndex);
            }
        }
    }
}
