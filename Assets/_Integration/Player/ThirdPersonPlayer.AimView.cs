using System.Collections;
using System.Collections.Generic;
using MalbersAnimations;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.Rendering;

namespace CollarCali
{
    /// <summary>
    /// The throw aim view: while a throw is aimed, the view moves into the character's eyes, and back
    /// over the shoulder afterwards. Ported from DualPlayerController's third-person half.
    ///
    /// Only the camera changes. Malbers stays in control the whole time - no teleport, nothing for the
    /// network to see beyond the character turning to face the aim. The view is a Cinemachine camera
    /// parked on the eyes, so the brain blends position, rotation and field of view in and out.
    /// </summary>
    public partial class ThirdPersonPlayer
    {
        /// <summary>Above every camera in the Malbers rig (its highest, LockOn, is 20).</summary>
        const int AimEyePriority = 900;

        CinemachineCamera _aimEyeCamera;
        Coroutine _aimViewRoutine;
        bool _aimViewRequested;
        bool _aimEyeLive;
        float _aimYaw;
        float _aimPitch;
        float _aimEyeY;
        bool _aimEyeYValid;
        float _aimFieldOfView = 60f;
        bool _aimStrafeSet;
        bool _aimSavedStrafe;
        bool _aimSavedCanStrafe;
        ThirdPersonFollowTarget[] _lookSources;
        readonly List<Renderer> _shadowOnlyRenderers = new List<Renderer>();
        readonly List<ShadowCastingMode> _shadowModes = new List<ShadowCastingMode>();

        CinemachineBlendDefinition _savedBlend;
        CinemachineBlenderSettings _savedCustomBlends;
        bool _blendSaved;

        /// <summary>True while the view is in the character's eyes for a throw, or on its way back.</summary>
        public bool IsAimViewLive => _aimEyeLive;

        /// <summary>
        /// Moves the view into the character's eyes to aim a throw (true), and back over the shoulder
        /// afterwards (false) - after <paramref name="returnDelay"/> seconds, so a throw can be watched
        /// leaving before the camera pulls back. Safe to call every frame; only a change of request
        /// does anything.
        /// </summary>
        public void SetThirdPersonAimView(bool active, float returnDelay = 0f)
        {
            var settings = PlayerTuning.Active.cameraTransition;

            if (active)
            {
                if (_aimViewRequested || _suspended || !settings.firstPersonAimInThirdPerson || !isActiveAndEnabled)
                    return;

                var brain = Brain;
                if (brain == null || !brain.isActiveAndEnabled)
                    return;

                _aimViewRequested = true;
                if (_aimViewRoutine != null)
                    StopCoroutine(_aimViewRoutine);
                _aimViewRoutine = StartCoroutine(AimViewInRoutine(brain, settings));
                return;
            }

            if (!_aimViewRequested)
                return;

            _aimViewRequested = false;
            if (_aimViewRoutine != null)
                StopCoroutine(_aimViewRoutine);

            var outBrain = Brain;
            if (outBrain == null || !_aimEyeLive || !isActiveAndEnabled)
            {
                EndAimViewImmediate();
                return;
            }

            _aimViewRoutine = StartCoroutine(AimViewOutRoutine(outBrain, settings, Mathf.Max(0f, returnDelay)));
        }

        IEnumerator AimViewInRoutine(CinemachineBrain brain, PlayerTuning.CameraSettings settings)
        {
            if (_aimEyeCamera == null)
            {
                var go = new GameObject("CM TPS Aim Eye");
                go.transform.SetParent(cameraRig.transform, false);
                _aimEyeCamera = go.AddComponent<CinemachineCamera>();
            }

            // A fresh aim starts looking wherever the shoulder camera was looking, at the field of
            // view on screen. Re-aiming while the view is still on its way out carries on from the eye
            // camera's own direction.
            if (!_aimEyeLive)
            {
                var view = brain.transform;
                _aimYaw = view.eulerAngles.y;
                _aimPitch = Mathf.Clamp(Mathf.DeltaAngle(0f, view.eulerAngles.x),
                    settings.aimViewPitchLimits.x, settings.aimViewPitchLimits.y);
                _aimEyeYValid = false;
                var output = brain.OutputCamera;
                _aimFieldOfView = output != null ? output.fieldOfView : 60f;
            }

            _aimEyeLive = true;
            _aimEyeCamera.gameObject.SetActive(true);
            UpdateAimEye(settings, applyLook: false);

            PrepareBrainBlend(brain, settings.aimViewInSeconds);
            _aimEyeCamera.Priority.Value = AimEyePriority;

            // Facing the aim: sideways keys strafe, and teammates see the character turned towards
            // where the body is about to go. The player's character can't strafe otherwise (Malbers'
            // Can Strafe is off), so that is switched on for the aim too.
            var animal = Animal;
            if (animal != null && !_aimStrafeSet)
            {
                _aimSavedCanStrafe = animal.CanStrafe;
                _aimSavedStrafe = animal.Strafe;
                _aimStrafeSet = true;
                animal.CanStrafe = true;
                animal.Strafe = true;
            }

            float elapsed = 0f;
            bool hidden = false;
            while (true)
            {
                yield return null;
                elapsed += Time.deltaTime;
                UpdateAimEye(settings, applyLook: true);

                // Hidden only once the camera is nearly inside the head, so the character does not
                // vanish while the view is still behind it.
                if (!hidden && elapsed >= settings.aimViewInSeconds * 0.55f)
                {
                    SetBodyHiddenForAimView(true);
                    hidden = true;
                }
            }
        }

        IEnumerator AimViewOutRoutine(CinemachineBrain brain, PlayerTuning.CameraSettings settings, float delay)
        {
            // Still aiming from the eyes while the thrown body flies away.
            float waited = 0f;
            while (waited < delay)
            {
                yield return null;
                waited += Time.deltaTime;
                UpdateAimEye(settings, applyLook: true);
            }

            RestoreAimStrafe();
            PrepareBrainBlend(brain, settings.aimViewOutSeconds);
            // The brain blends back to the shoulder camera, which Malbers kept turned the same way as
            // the eye camera while it was live, so the view comes back facing where the throw went.
            _aimEyeCamera.Priority.Value = -1000;

            float elapsed = 0f;
            bool shown = false;
            while (elapsed < settings.aimViewOutSeconds + 0.05f)
            {
                yield return null;
                elapsed += Time.deltaTime;
                // Keeps riding with the character, but no longer takes the mouse: the shoulder camera
                // it is blending into has it now.
                UpdateAimEye(settings, applyLook: false);

                if (!shown && elapsed >= settings.aimViewOutSeconds * 0.35f)
                {
                    SetBodyHiddenForAimView(false);
                    shown = true;
                }
            }

            EndAimViewImmediate();
        }

        /// <summary>Drops the aim view at once, with no blend: on going down, or when finished.</summary>
        void EndAimViewImmediate()
        {
            _aimViewRequested = false;
            if (_aimViewRoutine != null)
            {
                StopCoroutine(_aimViewRoutine);
                _aimViewRoutine = null;
            }

            if (_aimEyeCamera != null)
            {
                _aimEyeCamera.Priority.Value = -1000;
                _aimEyeCamera.gameObject.SetActive(false);
            }

            SetBodyHiddenForAimView(false);
            RestoreAimStrafe();

            if (_aimEyeLive)
            {
                _aimEyeLive = false;
                RestoreBrainBlends();
            }
        }

        void RestoreAimStrafe()
        {
            if (!_aimStrafeSet)
                return;
            _aimStrafeSet = false;
            var animal = Animal;
            if (animal != null)
            {
                animal.Strafe = _aimSavedStrafe;
                animal.CanStrafe = _aimSavedCanStrafe;
            }
        }

        /// <summary>
        /// Parks the eye camera on the character's eyes and, when it has the view, turns it with the
        /// mouse. The look is read from the same Malbers input the shoulder camera uses, with the same
        /// sensitivity and inversion, because Malbers stops turning its own camera the moment it is
        /// not the live one.
        /// </summary>
        void UpdateAimEye(PlayerTuning.CameraSettings settings, bool applyLook)
        {
            if (_aimEyeCamera == null)
                return;

            // A menu over the game holds the eye camera still too.
            if (applyLook && !_menuOpen)
                ApplyAimLook(settings);

            var feet = transform.position;
            var animal = Animal;
            var head = animal != null && animal.Anim != null && animal.Anim.isHuman
                ? animal.Anim.GetBoneTransform(HumanBodyBones.Head)
                : null;
            float targetY = head != null ? head.position.y + 0.08f : feet.y + settings.aimViewEyeHeight;

            // Smoothed, so the head's walk bob does not shake the view.
            _aimEyeY = _aimEyeYValid
                ? Mathf.Lerp(_aimEyeY, targetY, 1f - Mathf.Exp(-12f * Time.deltaTime))
                : targetY;
            _aimEyeYValid = true;

            var yaw = Quaternion.Euler(0f, _aimYaw, 0f);
            var position = new Vector3(feet.x, _aimEyeY, feet.z) + yaw * (Vector3.forward * 0.12f);
            _aimEyeCamera.transform.SetPositionAndRotation(position, Quaternion.Euler(_aimPitch, _aimYaw, 0f));

            // PlayerTuning's 0 meant "the first-person camera's"; with no first person it keeps the
            // field of view that was on screen when the aim started.
            var lens = _aimEyeCamera.Lens;
            lens.FieldOfView = settings.aimViewFieldOfView > 0f ? settings.aimViewFieldOfView : _aimFieldOfView;
            lens.NearClipPlane = 0.05f;
            _aimEyeCamera.Lens = lens;
        }

        void ApplyAimLook(PlayerTuning.CameraSettings settings)
        {
            var source = LookSource();
            if (source == null)
                return;

            var look = source.look.Value;
            if (look.sqrMagnitude < 0.00001f)
                return;

            // The same arithmetic ThirdPersonFollowTarget uses, so the eyes turn exactly as fast as the
            // shoulder camera did.
            float multiplier = source.UsingMouse.Value ? 1f : Time.deltaTime * source.GamepadMult.Value;
            _aimYaw += look.x * (source.invertX.Value ? -1f : 1f) * source.XMultiplier.Value * multiplier;
            _aimPitch += look.y * (source.invertY.Value ? 1f : -1f) * source.YMultiplier.Value * multiplier;
            _aimPitch = Mathf.Clamp(_aimPitch, settings.aimViewPitchLimits.x, settings.aimViewPitchLimits.y);
        }

        /// <summary>
        /// The rig's look input. It reaches one camera's follow target at a time, so whichever is
        /// receiving a value is the one to read.
        /// </summary>
        ThirdPersonFollowTarget LookSource()
        {
            if (cameraRig == null)
                return null;
            if (_lookSources == null || _lookSources.Length == 0)
                _lookSources = cameraRig.GetComponentsInChildren<ThirdPersonFollowTarget>(true);

            ThirdPersonFollowTarget best = null;
            float bestMagnitude = -1f;
            foreach (var source in _lookSources)
            {
                if (source == null)
                    continue;
                float magnitude = source.look.Value.sqrMagnitude;
                if (magnitude > bestMagnitude)
                {
                    bestMagnitude = magnitude;
                    best = source;
                }
            }

            return best;
        }

        /// <summary>
        /// Takes the body out of the aim view without losing its shadow: renderers switch to shadows
        /// only, the way a first-person body usually works, rather than switching off.
        /// </summary>
        void SetBodyHiddenForAimView(bool hidden)
        {
            if (!hidden)
            {
                for (int i = 0; i < _shadowOnlyRenderers.Count; i++)
                {
                    if (_shadowOnlyRenderers[i] != null)
                        _shadowOnlyRenderers[i].shadowCastingMode = _shadowModes[i];
                }

                _shadowOnlyRenderers.Clear();
                _shadowModes.Clear();
                return;
            }

            if (_shadowOnlyRenderers.Count > 0)
                return;

            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !renderer.enabled || renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly)
                    continue;
                _shadowOnlyRenderers.Add(renderer);
                _shadowModes.Add(renderer.shadowCastingMode);
                renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            }
        }

        /// <summary>
        /// Makes the brain's next blend take <paramref name="seconds"/> with the tuned easing, keeping
        /// the rig's own settings to put back afterwards. Custom blends are lifted meanwhile: a rig's
        /// "any camera" entry would otherwise override this blend.
        /// </summary>
        void PrepareBrainBlend(CinemachineBrain brain, float seconds)
        {
            if (!_blendSaved)
            {
                _savedBlend = brain.DefaultBlend;
                _savedCustomBlends = brain.CustomBlends;
                _blendSaved = true;
            }

            brain.CustomBlends = null;
            brain.DefaultBlend = new CinemachineBlendDefinition(PlayerTuning.Active.cameraTransition.blendStyle, seconds);
        }

        void RestoreBrainBlends()
        {
            if (!_blendSaved)
                return;

            var brain = Brain;
            if (brain != null)
            {
                brain.DefaultBlend = _savedBlend;
                brain.CustomBlends = _savedCustomBlends;
            }

            _blendSaved = false;
        }
    }
}
