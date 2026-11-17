using Fusion;
using MalbersAnimations.Controller;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// What the other players see of a third-person player: the body they see plays the same Malbers
    /// animation as the owner's character - climbing, attacks, crouching, the carry, all of it.
    ///
    /// The owner publishes its character's Animator: every parameter, each layer's state, time and
    /// weight, and every StateOn / ModeOn trigger Malbers fires (counted, so none is missed between
    /// ticks). Every machine then drives this object's "Player Render" body from that with a copy of
    /// Malbers' controller stripped of its behaviours (PlayerRigRecipe.remoteBodyController): the body
    /// only animates, while attacks, effects and the state logic stay with the owner's character. A
    /// layer that drifts onto a different state than the owner's for a moment is brought back to it.
    /// Both bodies are built on the same model and avatar, so the same parameters give the same pose.
    /// </summary>
    public partial class PlayerNetworkBridge
    {
        const int BodyParamCapacity = 48;
        const int BodyLayerCapacity = 8;

        /// <summary>How long a layer may sit on another state than the owner's before it is put back on it (s).</summary>
        const float BodyDriftGrace = 0.25f;

        static readonly int BodyStateOnHash = Animator.StringToHash("StateOn");
        static readonly int BodyModeOnHash = Animator.StringToHash("ModeOn");

        /// <summary>The owner plays a Malbers character, so the body the others see plays Malbers' animation.</summary>
        [Networked] public NetworkBool UsesMalbersBody { get; set; }

        [Networked, Capacity(BodyParamCapacity)] NetworkArray<float> BodyParams => default;
        [Networked, Capacity(BodyLayerCapacity)] NetworkArray<int> BodyStates => default;
        [Networked, Capacity(BodyLayerCapacity)] NetworkArray<float> BodyStateTimes => default;
        [Networked, Capacity(BodyLayerCapacity)] NetworkArray<float> BodyLayerWeights => default;
        [Networked] int BodyStateOnSeq { get; set; }
        [Networked] int BodyModeOnSeq { get; set; }

        MAnimal _publishedAnimal;
        AnimatorControllerParameter[] _publishedParams;
        bool _remoteBodyMalbers;
        AnimatorControllerParameter[] _remoteBodyParams;
        int _appliedStateOn;
        int _appliedModeOn;
        readonly float[] _bodyDriftSince = new float[BodyLayerCapacity];

        /// <summary>Owner: starts publishing the character's animation for the body the others see.</summary>
        void BeginPublishingBody(ThirdPersonPlayer player)
        {
            StopPublishingBody();
            var animal = player != null ? player.Animal : null;
            if (animal == null)
                return;

            _publishedAnimal = animal;
            _publishedParams = null;
            // Malbers raises every trigger through this, after it has set the Animator's own.
            animal.SetTriggerParameter += OnPublishedTrigger;
            UsesMalbersBody = true;
        }

        void StopPublishingBody()
        {
            if (_publishedAnimal != null)
                _publishedAnimal.SetTriggerParameter -= OnPublishedTrigger;
            _publishedAnimal = null;
            _publishedParams = null;
        }

        void OnPublishedTrigger(int hash)
        {
            if (Object == null || !Object.IsValid || !HasStateAuthority)
                return;
            if (hash == BodyStateOnHash)
                BodyStateOnSeq++;
            else if (hash == BodyModeOnHash)
                BodyModeOnSeq++;
        }

        /// <summary>Owner, every frame: the character's Animator as it stands now.</summary>
        void CaptureMalbersBody()
        {
            var animal = _publishedAnimal;
            var animator = animal != null ? animal.Anim : null;
            if (animator == null || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null)
                return;

            _publishedParams ??= animator.parameters;
            int count = Mathf.Min(_publishedParams.Length, BodyParamCapacity);
            for (int i = 0; i < count; i++)
            {
                var p = _publishedParams[i];
                float value = p.type switch
                {
                    AnimatorControllerParameterType.Float => animator.GetFloat(p.nameHash),
                    AnimatorControllerParameterType.Int => animator.GetInteger(p.nameHash),
                    AnimatorControllerParameterType.Bool => animator.GetBool(p.nameHash) ? 1f : 0f,
                    _ => 0f,
                };
                if (BodyParams.Get(i) != value)
                    BodyParams.Set(i, value);
            }

            int layers = Mathf.Min(animator.layerCount, BodyLayerCapacity);
            for (int l = 0; l < layers; l++)
            {
                // Where the layer is going: mid-transition, the state it is blending into.
                var info = animator.IsInTransition(l) ? animator.GetNextAnimatorStateInfo(l) : animator.GetCurrentAnimatorStateInfo(l);
                BodyStates.Set(l, info.fullPathHash);
                BodyStateTimes.Set(l, info.normalizedTime);
                BodyLayerWeights.Set(l, l == 0 ? 1f : animator.GetLayerWeight(l));
            }
        }

        /// <summary>
        /// Every machine: the body the others see takes Malbers' controller (once) and the owner's
        /// published Animator. False when that controller is missing, so the old locomotion
        /// parameters drive the body instead.
        /// </summary>
        bool ApplyMalbersBody()
        {
            var animator = _remoteAnimator;
            if (animator == null)
                return false;

            if (!_remoteBodyMalbers)
            {
                var recipe = PlayerRigRecipe.Load();
                var controller = recipe != null ? recipe.remoteBodyController : null;
                if (controller == null)
                    return false;

                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                _remoteBodyParams = animator.parameters;
                _appliedStateOn = BodyStateOnSeq;
                _appliedModeOn = BodyModeOnSeq;
                for (int l = 0; l < _bodyDriftSince.Length; l++)
                    _bodyDriftSince[l] = -1f;
                _remoteBodyMalbers = true;
            }

            // A downed player's ragdoll has switched the Animator off.
            if (!animator.isActiveAndEnabled)
                return true;

            int count = Mathf.Min(_remoteBodyParams.Length, BodyParamCapacity);
            for (int i = 0; i < count; i++)
            {
                var p = _remoteBodyParams[i];
                float value = BodyParams.Get(i);
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float:
                        animator.SetFloat(p.nameHash, value);
                        break;
                    case AnimatorControllerParameterType.Int:
                        animator.SetInteger(p.nameHash, Mathf.RoundToInt(value));
                        break;
                    case AnimatorControllerParameterType.Bool:
                        animator.SetBool(p.nameHash, value > 0.5f);
                        break;
                }
            }

            if (BodyStateOnSeq != _appliedStateOn)
            {
                _appliedStateOn = BodyStateOnSeq;
                animator.SetTrigger(BodyStateOnHash);
            }

            if (BodyModeOnSeq != _appliedModeOn)
            {
                _appliedModeOn = BodyModeOnSeq;
                animator.SetTrigger(BodyModeOnHash);
            }

            int layers = Mathf.Min(animator.layerCount, BodyLayerCapacity);
            for (int l = 1; l < layers; l++)
                animator.SetLayerWeight(l, BodyLayerWeights.Get(l));

            for (int l = 0; l < layers; l++)
            {
                int wanted = BodyStates.Get(l);
                if (wanted == 0)
                    continue;

                int playing = animator.IsInTransition(l)
                    ? animator.GetNextAnimatorStateInfo(l).fullPathHash
                    : animator.GetCurrentAnimatorStateInfo(l).fullPathHash;
                if (playing == wanted)
                {
                    _bodyDriftSince[l] = -1f;
                    continue;
                }

                if (_bodyDriftSince[l] < 0f)
                {
                    _bodyDriftSince[l] = Time.unscaledTime;
                    continue;
                }

                if (Time.unscaledTime - _bodyDriftSince[l] < BodyDriftGrace || !animator.HasState(l, wanted))
                    continue;

                animator.CrossFade(wanted, 0.1f, l, Mathf.Repeat(BodyStateTimes.Get(l), 1f));
                _bodyDriftSince[l] = -1f;
            }

            return true;
        }
    }
}
