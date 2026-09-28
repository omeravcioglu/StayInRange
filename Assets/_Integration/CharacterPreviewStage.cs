using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace CollarCali
{
    /// <summary>
    /// The player's character, live, painted in a player colour and turning slowly - rendered into a
    /// texture the lobby shows on its turntable.
    ///
    /// It shows Resources/CharacterPreview, which Tools ▸ CollarCali ▸ Build Character Visuals makes
    /// from the same model every player wears in the game, so the lobby always matches the game: swap
    /// the character there and the lobby follows. The colour goes on by the game's own rule
    /// (CharacterSelection.ApplyPlayerLook).
    ///
    /// Everything lives on its own layer, far from the scene, with its own camera and lights limited
    /// to that layer: nothing in the scene can appear in the preview, and the preview's lights cannot
    /// touch the scene.
    /// </summary>
    public class CharacterPreviewStage : MonoBehaviour
    {
        const string PreviewResource = "CharacterPreview";

        /// <summary>
        /// An unnamed layer, used only by this stage. If layer 29 is ever given a name and a purpose
        /// in Tags and Layers, move this to another free one.
        /// </summary>
        const int StageLayer = 29;

        static readonly Vector3 StagePosition = new Vector3(0f, -500f, 0f);

        const float FieldOfView = 26f;
        const float SpinDegreesPerSecond = 22f;

        /// <summary>
        /// Starts a quarter-turn off facing the camera (which looks back along -Z at a model facing
        /// +Z): a flat front view reads as a sprite.
        /// </summary>
        const float StartYaw = 25f;

        Camera _camera;
        RenderTexture _texture;
        Transform _spin;
        GameObject _model;
        int _colour = -1;

        public RenderTexture Texture => _texture;

        /// <summary>
        /// Builds a stage rendering at <paramref name="width"/> by <paramref name="height"/>, or returns
        /// null when there is no preview model to show - callers keep their fallback in that case.
        /// </summary>
        public static CharacterPreviewStage Create(int width, int height)
        {
            var prefab = Resources.Load<GameObject>(PreviewResource);
            if (prefab == null)
                return null;

            var root = new GameObject("CharacterPreviewStage");
            root.transform.position = StagePosition;
            var stage = root.AddComponent<CharacterPreviewStage>();
            stage.Build(prefab, width, height);
            return stage;
        }

        /// <summary>Paints the character in a player colour. Repeated calls with the same colour do nothing.</summary>
        public void SetColour(int colourIndex)
        {
            if (colourIndex == _colour || _model == null)
                return;
            _colour = colourIndex;
            CharacterSelection.ApplyPlayerLook(_model, colourIndex);
        }

        void Build(GameObject prefab, int width, int height)
        {
            _spin = new GameObject("Turntable").transform;
            _spin.SetParent(transform, false);
            _spin.localRotation = Quaternion.Euler(0f, StartYaw, 0f);

            _model = Instantiate(prefab, _spin);
            _model.name = "Character";
            _model.transform.localPosition = Vector3.zero;
            _model.transform.localRotation = Quaternion.identity;
            SetLayer(_model.transform, StageLayer);
            PrepareModel(_model);

            _texture = new RenderTexture(Mathf.Max(64, width), Mathf.Max(64, height), 24,
                RenderTextureFormat.ARGB32)
            {
                name = "CharacterPreview",
                antiAliasing = 4,
            };
            _texture.Create();

            BuildCamera();
            BuildLights();
            Frame();
        }

        /// <summary>
        /// A preview is a picture, not a player: no physics, no scripts, and an idle rather than a
        /// fall. The third-person controller it borrows plays its airborne pose unless it is told the
        /// feet are on the ground.
        /// </summary>
        static void PrepareModel(GameObject model)
        {
            foreach (var collider in model.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;
            foreach (var body in model.GetComponentsInChildren<Rigidbody>(true))
                body.isKinematic = true;

            var animator = model.GetComponentInChildren<Animator>(true);
            if (animator == null)
                return;

            animator.applyRootMotion = false;
            // Animated even though no gameplay camera looks at it, and through a paused time scale.
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.updateMode = AnimatorUpdateMode.UnscaledTime;

            foreach (var parameter in animator.parameters)
            {
                switch (parameter.name)
                {
                    case "Grounded" when parameter.type == AnimatorControllerParameterType.Bool:
                        animator.SetBool(parameter.nameHash, true);
                        break;
                    case "FreeFall" when parameter.type == AnimatorControllerParameterType.Bool:
                    case "Jump" when parameter.type == AnimatorControllerParameterType.Bool:
                        animator.SetBool(parameter.nameHash, false);
                        break;
                    case "MotionSpeed" when parameter.type == AnimatorControllerParameterType.Float:
                        animator.SetFloat(parameter.nameHash, 1f);
                        break;
                }
            }
        }

        void BuildCamera()
        {
            var go = new GameObject("PreviewCamera");
            go.transform.SetParent(transform, false);
            _camera = go.AddComponent<Camera>();
            _camera.cullingMask = 1 << StageLayer;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            // Transparent, so the lobby's own backdrop and turntable art show around the character.
            _camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _camera.fieldOfView = FieldOfView;
            _camera.nearClipPlane = 0.05f;
            _camera.farClipPlane = 30f;
            _camera.targetTexture = _texture;
            _camera.depth = -50f;
            // Off, or URP renders through its HDR colour buffer - a format with no alpha channel -
            // and the transparent background comes out black.
            _camera.allowHDR = false;
            _camera.allowMSAA = true;

            var data = _camera.GetUniversalAdditionalCameraData();
            // Post-processing and a renderer's full-screen passes would write an opaque background
            // over the alpha the lobby needs.
            data.renderPostProcessing = false;
            data.antialiasing = AntialiasingMode.None;
            data.renderShadows = false;
            data.requiresDepthTexture = false;
            data.requiresColorTexture = false;
        }

        void BuildLights()
        {
            // Key light from the front left, a cooler rim from behind: enough shape to read as a
            // figure against the dark corridor art.
            AddLight("Key", Quaternion.Euler(35f, 145f, 0f), new Color(1f, 0.96f, 0.9f), 1.25f);
            AddLight("Rim", Quaternion.Euler(20f, -30f, 0f), new Color(0.7f, 0.8f, 1f), 0.7f);
        }

        void AddLight(string name, Quaternion rotation, Color colour, float intensity)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.rotation = rotation;
            var light = go.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = colour;
            light.intensity = intensity;
            light.shadows = LightShadows.None;
            // Only the stage: these must not light the scene the lobby sits in.
            light.cullingMask = 1 << StageLayer;
        }

        /// <summary>
        /// Points the camera at the character and backs off until the whole figure fits with a little
        /// room above the head, whatever size or scale the model was imported at.
        /// </summary>
        void Frame()
        {
            var bounds = new Bounds(_model.transform.position + Vector3.up, Vector3.one * 0.1f);
            bool any = false;
            foreach (var renderer in _model.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null)
                    continue;
                if (!any)
                {
                    bounds = renderer.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            float height = Mathf.Max(0.5f, bounds.size.y);
            float halfFov = FieldOfView * 0.5f * Mathf.Deg2Rad;
            float distance = height * 0.56f / Mathf.Tan(halfFov);

            var target = new Vector3(transform.position.x, bounds.center.y, transform.position.z);
            _camera.transform.position = target + new Vector3(0f, height * 0.04f, distance);
            _camera.transform.rotation = Quaternion.LookRotation(target - _camera.transform.position);
        }

        void Update()
        {
            if (_spin != null)
                _spin.Rotate(0f, SpinDegreesPerSecond * Time.unscaledDeltaTime, 0f, Space.Self);
        }

        void OnDestroy()
        {
            if (_camera != null)
                _camera.targetTexture = null;
            if (_texture != null)
            {
                _texture.Release();
                Destroy(_texture);
            }
        }

        static void SetLayer(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            for (int i = 0; i < root.childCount; i++)
                SetLayer(root.GetChild(i), layer);
        }
    }
}
