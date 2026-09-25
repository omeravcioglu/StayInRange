using cowsins;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CollarCali
{
    /// <summary>
    /// Spotlight that sits in front of the current gun.
    /// Toggle with Cowsins ToggleFlashLight (default T).
    /// </summary>
    public class WeaponFlashlight : MonoBehaviour
    {
        public const string RigName = "WeaponFlashlightRig";
        const string SettingsResource = "WeaponFlashlightSettings";

        static WeaponFlashlightSettings _settings;

        WeaponController _weapon;
        DualPlayerController _dual;
        Transform _rig;
        Light _light;
        Light _fill;
        Transform _mountedOn;
        bool _on;
        int _lastToggleFrame = -1;

        public bool IsOn => _on;

        public static WeaponFlashlightSettings Settings
        {
            get
            {
                if (_settings == null)
                    _settings = Resources.Load<WeaponFlashlightSettings>(SettingsResource);
                if (_settings == null)
                    _settings = ScriptableObject.CreateInstance<WeaponFlashlightSettings>();
                return _settings;
            }
        }

        public void Bind(WeaponController weapon, DualPlayerController dual)
        {
            if (weapon != null)
                _weapon = weapon;
            if (dual != null)
                _dual = dual;
        }

        public void SetOn(bool on)
        {
            _on = on;
            if (_light != null)
                _light.enabled = on;
            if (_fill != null)
                _fill.enabled = on;
            if (_rig != null)
                _rig.gameObject.SetActive(true);
        }

        public void Toggle()
        {
            if (Time.frameCount == _lastToggleFrame)
                return;
            _lastToggleFrame = Time.frameCount;
            SetOn(!_on);

            // In Toggle rather than SetOn: SetOn is also how the state is applied on remote copies
            // and at startup, neither of which is someone pressing the key.
            GameSfx.Play2D(SfxId.FlashlightToggle);
        }

        void Awake()
        {
            _weapon = GetComponent<WeaponController>()
                      ?? GetComponentInChildren<WeaponController>(true);
            _dual = GetComponent<DualPlayerController>()
                    ?? GetComponentInParent<DualPlayerController>();
            _on = Settings.startOn;
        }

        void LateUpdate()
        {
#if CMPSETUP_COMPLETE
            var bridge = GetComponent<FpsNetworkBridge>();
            if (bridge != null && bridge.Object != null && !bridge.IsLocalOwner)
                return;
#endif
            if (WasTogglePressed())
                Toggle();

            EnsureRig();
            FollowMount();
            ApplyBeam();
        }

        bool WasTogglePressed()
        {
            if (InputManager.inputActions != null)
            {
                var action = InputManager.inputActions.GameControls.ToggleFlashLight;
                if (action != null && action.enabled && action.WasPressedThisFrame())
                    return true;
            }

            var keyboard = Keyboard.current;
            return keyboard != null && keyboard.tKey.wasPressedThisFrame;
        }

        void FollowMount()
        {
            var mount = ResolveCamera();
            if (mount == null || _rig == null)
                return;

            if (_mountedOn != mount)
            {
                _mountedOn = mount;
                _rig.SetParent(mount, false);
                _rig.localScale = Vector3.one;
            }

            _rig.localPosition = Settings.cameraLocalPosition;
            _rig.localRotation = Quaternion.identity;
        }

        Transform ResolveCamera()
        {
            if (_weapon == null)
            {
                _weapon = GetComponent<WeaponController>()
                          ?? GetComponentInChildren<WeaponController>(true);
            }
            if (_dual == null)
                _dual = GetComponent<DualPlayerController>()
                        ?? GetComponentInParent<DualPlayerController>();

            if (_weapon != null && _weapon.MainCamera != null && _weapon.MainCamera.isActiveAndEnabled)
                return _weapon.MainCamera.transform;

            var cam = Camera.main;
            if (cam != null && cam.enabled)
                return cam.transform;

            if (_dual != null && _dual.SteveRoot != null)
                return _dual.SteveRoot.transform;

            return null;
        }

        void ApplyBeam()
        {
            if (_light == null)
                return;

            var settings = Settings;
            var fade = CloseFade(settings);
            _light.enabled = _on;
            _light.range = settings.range;
            _light.spotAngle = settings.spotAngle;
            _light.innerSpotAngle = settings.innerSpotAngle;
            _light.color = settings.color;
            _light.intensity = _on ? settings.intensity * fade : 0f;
            _light.cullingMask = WorldLightMask;

            if (_fill != null)
            {
                _fill.enabled = _on;
                _fill.range = settings.fillRange;
                _fill.color = settings.color;
                _fill.intensity = _on ? settings.fillIntensity * fade : 0f;
                _fill.cullingMask = WorldLightMask;
            }
        }

        float CloseFade(WeaponFlashlightSettings settings)
        {
            if (settings.closeFadeDistance <= 0.01f || _mountedOn == null)
                return 1f;

            var origin = _mountedOn.position + _mountedOn.forward * 0.35f;
            if (!Physics.Raycast(origin, _mountedOn.forward, out var hit, settings.closeFadeDistance, ~0, QueryTriggerInteraction.Ignore))
                return 1f;

            var t = Mathf.SmoothStep(0f, 1f, hit.distance / settings.closeFadeDistance);
            return Mathf.Lerp(settings.closeMinScale, 1f, t);
        }

        void EnsureRig()
        {
            if (_rig != null)
                return;
            _rig = CreateRig(transform, Settings.cameraLocalPosition).transform;
            CacheLights(_rig.gameObject);
            SetOn(_on);
        }

        public static GameObject CreateRig(Transform parent, Vector3 localPosition)
        {
            var settings = Settings;
            var root = new GameObject(RigName);
            if (parent != null)
                root.transform.SetParent(parent, false);
            root.transform.localPosition = localPosition;
            root.transform.localRotation = Quaternion.identity;

            var light = root.AddComponent<Light>();
            light.type = LightType.Spot;
            light.range = settings.range;
            light.intensity = settings.intensity;
            light.spotAngle = settings.spotAngle;
            light.innerSpotAngle = settings.innerSpotAngle;
            light.color = settings.color;
            light.shadows = LightShadows.None;
            light.renderMode = LightRenderMode.ForcePixel;
            light.cullingMask = WorldLightMask;

            var fillGo = new GameObject("Fill");
            fillGo.transform.SetParent(root.transform, false);
            var fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Point;
            fill.range = settings.fillRange;
            fill.intensity = settings.fillIntensity;
            fill.color = settings.color;
            fill.shadows = LightShadows.None;
            fill.renderMode = LightRenderMode.ForcePixel;
            fill.cullingMask = WorldLightMask;
            return root;
        }

        static readonly string[] HiddenLayers =
        {
            "Weapons", "Player", "Effects", "TransparentFX",
            "UI", "UITop", "PostProcessing", "Ignore Raycast"
        };

        static int WorldLightMask
        {
            get
            {
                var mask = ~0;
                for (int i = 0; i < HiddenLayers.Length; i++)
                {
                    var layer = LayerMask.NameToLayer(HiddenLayers[i]);
                    if (layer >= 0)
                        mask &= ~(1 << layer);
                }
                return mask;
            }
        }

        void CacheLights(GameObject rig)
        {
            _light = null;
            _fill = null;
            if (rig == null)
                return;

            var lights = rig.GetComponentsInChildren<Light>(true);
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i].type == LightType.Spot && _light == null)
                    _light = lights[i];
                else if (lights[i].type == LightType.Point)
                    _fill = lights[i];
            }
        }
    }
}
