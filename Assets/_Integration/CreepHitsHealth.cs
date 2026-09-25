using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Cowsins damageable that always dies in 20 shots, regardless of weapon damage.
    /// </summary>
    public class CreepHitsHealth : MonoBehaviour, cowsins.IDamageable
    {
        public int HitsToDie = 20;
        public int HitsLeft { get; private set; } = 20;

        public event System.Action OnDied;

        bool _dead;

        bool _applyingNetworked;

        void Awake()
        {
            HitsLeft = Mathf.Max(1, HitsToDie);
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
            if (_dead)
                return;

            HitsLeft--;
            Scene2DamagePopup.Show(transform.position + Vector3.up * 1.6f, 1);

            if (HitsLeft > 0)
                return;

            _dead = true;
            HitsLeft = 0;
            OnDied?.Invoke();
        }
    }
}
