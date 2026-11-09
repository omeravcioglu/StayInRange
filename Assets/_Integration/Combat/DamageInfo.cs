using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// One hit on a player, as PlayerVitals reports it to the HUD, sounds and moments. Only the amount
    /// and whether it was critical survive a hop across the network; the rest is filled in when the
    /// hit is applied on the machine it happened on, and left default otherwise.
    /// </summary>
    public struct DamageInfo
    {
        /// <summary>Positive amount of health to take.</summary>
        public float Amount;

        /// <summary>A critical hit (the head, or a hitbox tagged "Critical").</summary>
        public bool Critical;

        /// <summary>Who or what dealt it, when known.</summary>
        public GameObject Attacker;

        /// <summary>Where it landed in the world, or zero when unknown.</summary>
        public Vector3 Point;

        /// <summary>Which way it pushed, or zero when unknown.</summary>
        public Vector3 Direction;

        public DamageInfo(float amount, bool critical)
        {
            Amount = amount;
            Critical = critical;
            Attacker = null;
            Point = Vector3.zero;
            Direction = Vector3.zero;
        }
    }
}
