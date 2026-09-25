using EmeraldAI;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Lets Cowsins hits damage Emerald AI and shows a number even if Emerald combat text is missing.
    /// </summary>
    public class CowsinsHitsEmeraldHealth : MonoBehaviour, cowsins.IDamageable
    {
        EmeraldHealth _health;

        bool _applyingNetworked;

        void Awake()
        {
            ResolveHealth();
        }

        public void Damage(float damage, bool isHeadshot)
        {
            if (_applyingNetworked)
            {
                ApplyLocal(damage, isHeadshot);
                return;
            }

#if CMPSETUP_COMPLETE
            var actor = NetworkWorldActor.FindFor(gameObject);
            if (actor != null && actor.Object != null && actor.Object.IsValid)
            {
                actor.RequestDamage(damage, isHeadshot);
                return;
            }
#endif

            ApplyLocal(damage, isHeadshot);
        }

        public void ApplyNetworkedDamage(float damage, bool isHeadshot)
        {
            _applyingNetworked = true;
            try
            {
                ApplyLocal(damage, isHeadshot);
            }
            finally
            {
                _applyingNetworked = false;
            }
        }

        void ApplyLocal(float damage, bool isHeadshot)
        {
            ResolveHealth();
            if (_health == null)
                return;

            int max = Mathf.Max(1, _health.StartingHealth);
            // Player weapons in this project deal ~2 per pistol shot; enemies were at 30 HP (15 shots).
            // Always take three bullets to kill.
            int amount = Mathf.Max(1, Mathf.CeilToInt(max / 3f));
            if (isHeadshot)
                amount = Mathf.Max(amount, Mathf.CeilToInt(max / 2f));
            int before = _health.Health;

            Scene2DamagePopup.Show(_health.transform.position, amount);

            try
            {
                _health.Damage(amount, null, 50, isHeadshot);
            }
            catch (System.Exception)
            {
                // Emerald combat text or death setup can throw; still apply HP below.
            }

            if (_health.Health == before)
                _health.Health = before - amount;

            if (_health.Health <= 0)
            {
                _health.Health = 0;
                var system = _health.GetComponent<EmeraldSystem>();
                if (system != null && system.AnimationComponent != null && !system.AnimationComponent.IsDead)
                {
                    try
                    {
                        _health.KillAI();
                    }
                    catch (System.Exception)
                    {
                    }
                }

                Scene2EnemyDeathFall.Force(_health.gameObject);
            }
        }

        void ResolveHealth()
        {
            if (_health != null)
                return;
            _health = GetComponent<EmeraldHealth>() ?? GetComponentInParent<EmeraldHealth>();
        }
    }
}
