#if CMPSETUP_COMPLETE
using System.Collections.Generic;
using System.Reflection;
using cowsins;
using MalbersAnimations;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CollarCali
{
    /// <summary>
    /// Puts the player's look and key settings (GameSettings) onto the local player, whichever
    /// controller is live: Cowsins' camera in first person, the Malbers shoulder camera (and the aim
    /// view that copies it) in third, and the third-person keys that share a job with a first-person
    /// one - jump, sprint, interact, crouch.
    ///
    /// Re-applied when a setting changes and whenever the pieces it sets are rebuilt (the shoulder
    /// camera and Steve appear on the first switch to third person). Nothing is touched until the
    /// player has changed a look setting or a key, so an untouched install keeps the tuning the
    /// prefabs carry. Added to the local player by HudRoot.
    /// </summary>
    public class LocalPlayerSettings : MonoBehaviour
    {
        const float RescanSeconds = 1f;

        // Cowsins reads its sensitivity once, when the camera look is built; this asks it again.
        static readonly MethodInfo GatherSensitivity = typeof(CameraLookBehaviour).GetMethod(
            "GatherSensitivityValues", BindingFlags.Instance | BindingFlags.NonPublic);

        FpsNetworkBridge _bridge;
        PlayerMovement _movement;
        PlayerInput _steveInput;
        ThirdPersonFollowTarget[] _cameras = new ThirdPersonFollowTarget[0];
        readonly Dictionary<ThirdPersonFollowTarget, Vector3> _cameraBase = new Dictionary<ThirdPersonFollowTarget, Vector3>();
        // The first-person tuning the prefab came with, put back when the player resets the controls.
        PlayerMovement _movementBaseOwner;
        float _baseX, _baseY, _basePadX, _basePadY;
        bool _baseInvert, _baseInvertPad;
        bool _dirty = true;
        float _nextScan;

        DualPlayerController _dual;

        void Awake()
        {
            // Networked, the controller sits on the bridge's object; offline (Game.unity played on
            // its own) there is no bridge and it sits on PlayerMain.
            _bridge = GetComponent<FpsNetworkBridge>();
            _dual = GetComponent<DualPlayerController>();
        }

        void OnEnable() => GameSettings.Changed += MarkDirty;
        void OnDisable() => GameSettings.Changed -= MarkDirty;

        void MarkDirty() => _dirty = true;

        void LateUpdate()
        {
            if (_bridge != null ? !_bridge.IsLocalOwner : _dual == null)
                return;

            float now = Time.unscaledTime;
            if (now >= _nextScan)
            {
                _nextScan = now + RescanSeconds;
                Rescan();
            }

            if (!_dirty)
                return;
            _dirty = false;

            ApplyFirstPerson();
            ApplyThirdPerson();
            MirrorKeys();
        }

        /// <summary>Finds what the settings go onto; anything new since last time gets them at once.</summary>
        void Rescan()
        {
            var dual = _bridge != null ? _bridge.DualPlayer : _dual;
            var body = dual != null ? dual.FpsBody : null;
            var movement = body != null ? body.GetComponentInParent<PlayerMovement>(true) : null;
            if (movement == null && body != null)
                movement = body.GetComponentInChildren<PlayerMovement>(true);
            if (movement != _movement)
            {
                _movement = movement;
                _dirty = true;
            }

            var steveInput = dual != null && dual.SteveRoot != null
                ? dual.SteveRoot.GetComponentInChildren<PlayerInput>(true)
                : null;
            if (steveInput != _steveInput)
            {
                _steveInput = steveInput;
                _dirty = true;
            }

            // The shoulder cameras exist only for the local player, so every one in the scene is ours.
            var cameras = FindObjectsByType<ThirdPersonFollowTarget>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (cameras.Length != _cameras.Length || !SameSet(cameras, _cameras))
            {
                _cameras = cameras;
                _dirty = true;
            }
        }

        static bool SameSet(ThirdPersonFollowTarget[] a, ThirdPersonFollowTarget[] b)
        {
            foreach (var camera in a)
            {
                if (System.Array.IndexOf(b, camera) < 0)
                    return false;
            }

            return true;
        }

        void ApplyFirstPerson()
        {
            if (_movement == null)
                return;

            var settings = _movement.playerSettings;
            if (_movementBaseOwner != _movement)
            {
                _movementBaseOwner = _movement;
                _baseX = settings.sensitivityX;
                _baseY = settings.sensitivityY;
                _basePadX = settings.controllerSensitivityX;
                _basePadY = settings.controllerSensitivityY;
                _baseInvert = settings.invertYSensitivty;
                _baseInvertPad = settings.invertYControllerSensitivty;
            }

            if (!GameSettings.HasLookSettings)
            {
                settings.sensitivityX = _baseX;
                settings.sensitivityY = _baseY;
                settings.controllerSensitivityX = _basePadX;
                settings.controllerSensitivityY = _basePadY;
                settings.invertYSensitivty = _baseInvert;
                settings.invertYControllerSensitivty = _baseInvertPad;
                RefreshLook();
                return;
            }

            settings.sensitivityX = GameSettings.MouseSensitivityX;
            settings.sensitivityY = GameSettings.MouseSensitivityY;
            settings.controllerSensitivityX = GameSettings.ControllerSensitivity;
            settings.controllerSensitivityY = GameSettings.ControllerSensitivity;
            settings.invertYSensitivty = GameSettings.InvertY;
            settings.invertYControllerSensitivty = GameSettings.InvertY;
            RefreshLook();
        }

        void RefreshLook()
        {
            var look = _movement.cameraLookBehaviour;
            if (look != null && GatherSensitivity != null)
                GatherSensitivity.Invoke(look, null);
        }

        /// <summary>
        /// The shoulder camera's own speed, scaled by how far the player moved the slider from the
        /// default - the third-person camera was tuned separately, so its feel is kept and only
        /// sped up or slowed down.
        /// </summary>
        void ApplyThirdPerson()
        {
            if (!GameSettings.HasLookSettings)
            {
                // Reset: the cameras go back to their own tuning.
                foreach (var pair in _cameraBase)
                {
                    if (pair.Key == null)
                        continue;
                    pair.Key.XMultiplier.Value = pair.Value.x;
                    pair.Key.YMultiplier.Value = pair.Value.y;
                    pair.Key.invertY.Value = pair.Value.z > 0.5f;
                }

                _cameraBase.Clear();
                return;
            }

            float scaleX = GameSettings.MouseSensitivityX / GameSettings.DefaultMouseSensitivity;
            float scaleY = GameSettings.MouseSensitivityY / GameSettings.DefaultMouseSensitivity;
            bool invert = GameSettings.InvertY;

            foreach (var camera in _cameras)
            {
                if (camera == null)
                    continue;
                if (!_cameraBase.TryGetValue(camera, out var baseline))
                {
                    baseline = new Vector3(camera.XMultiplier.Value, camera.YMultiplier.Value, camera.invertY.Value ? 1f : 0f);
                    _cameraBase[camera] = baseline;
                }

                camera.XMultiplier.Value = baseline.x * scaleX;
                camera.YMultiplier.Value = baseline.y * scaleY;
                camera.invertY.Value = (baseline.z > 0.5f) != invert;
            }
        }

        /// <summary>
        /// Third person runs on Malbers' own key set; the keys that do the same job in both views
        /// follow the first-person binding, so a rebound jump jumps in either.
        /// </summary>
        void MirrorKeys()
        {
            var actions = _steveInput != null ? _steveInput.actions : null;
            if (actions == null)
                return;

            foreach (var line in GameSettings.KeyActions)
            {
                if (string.IsNullOrEmpty(line.ThirdPerson))
                    continue;

                var source = GameSettings.FindAction(line.Action);
                int sourceIndex = GameSettings.KeyBindingIndex(source);
                var target = actions.FindAction(line.ThirdPerson);
                int targetIndex = GameSettings.KeyBindingIndex(target);
                if (sourceIndex < 0 || targetIndex < 0 || target.bindings[targetIndex].isComposite)
                    continue;

                // Only a key the player chose is copied across; untouched, third person keeps its own.
                string chosen = source.bindings[sourceIndex].overridePath;
                if (string.IsNullOrEmpty(chosen))
                    target.RemoveBindingOverride(targetIndex);
                else
                    target.ApplyBindingOverride(targetIndex, chosen);
            }
        }
    }
}
#endif
