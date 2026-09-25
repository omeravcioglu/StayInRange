using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Turns a platform on for 4s and off for 2.5s. Every client uses Fusion's
    /// shared simulation clock, so all copies flip at the same time. Add this to
    /// the objects you want to blink. Do not disable this GameObject — the timer
    /// has to keep running. Leave Target empty to hide renderers/colliders here,
    /// or point Target at a child mesh.
    /// </summary>
    [AddComponentMenu("CollarCali/Networked Blinking Platform")]
    [DisallowMultipleComponent]
    public class NetworkedBlinkingPlatform : MonoBehaviour
    {
        [SerializeField, Min(0.01f)] float enabledSeconds = 4f;
        [SerializeField, Min(0.01f)] float disabledSeconds = 2.5f;

        [SerializeField, Tooltip("Object that appears and disappears. Leave empty to toggle renderers and colliders on this object. Do not point this at the GameObject that has this script.")]
        GameObject target;

        [SerializeField, Tooltip("Leave 0 so every platform with this script blinks together.")]
        float phaseOffsetSeconds;

        bool _on;
        bool _applied;
        Renderer[] _renderers;
        Collider[] _colliders;
        Light[] _lights;

        void Awake()
        {
            if (target == gameObject)
                target = null;

            CacheVisuals();
            Apply(IsOnAt(SharedTime()));
        }

        void Update()
        {
            Apply(IsOnAt(SharedTime()));
        }

        float SharedTime()
        {
#if CMPSETUP_COMPLETE
            var runner = NetworkCombatHooks.FindRunner();
            if (runner != null && runner.IsRunning)
                return (float)runner.SimulationTime;
#endif
            return Time.time;
        }

        bool IsOnAt(float time)
        {
            float cycle = enabledSeconds + disabledSeconds;
            if (cycle <= 0f)
                return true;

            float t = time + phaseOffsetSeconds;
            t %= cycle;
            if (t < 0f)
                t += cycle;

            return t < enabledSeconds;
        }

        void Apply(bool on)
        {
            if (_applied && _on == on)
                return;

            // Only for a real flip, never for the first Apply from Awake - otherwise every platform
            // in the level would announce itself on the frame the scene loads.
            if (_applied)
                GameSfx.Play(on ? SfxId.PlatformBlinkOn : SfxId.PlatformBlinkOff, transform.position);

            _applied = true;
            _on = on;

            if (target != null && target != gameObject)
            {
                if (target.activeSelf != on)
                    target.SetActive(on);
                return;
            }

            if (_renderers == null)
                CacheVisuals();

            for (int i = 0; i < _renderers.Length; i++)
            {
                if (_renderers[i] != null)
                    _renderers[i].enabled = on;
            }

            for (int i = 0; i < _colliders.Length; i++)
            {
                if (_colliders[i] != null)
                    _colliders[i].enabled = on;
            }

            for (int i = 0; i < _lights.Length; i++)
            {
                if (_lights[i] != null)
                    _lights[i].enabled = on;
            }
        }

        void CacheVisuals()
        {
            _renderers = GetComponentsInChildren<Renderer>(true);
            _colliders = GetComponentsInChildren<Collider>(true);
            _lights = GetComponentsInChildren<Light>(true);
        }
    }
}
