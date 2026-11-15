using UnityEngine;

namespace CollarCali.Combat
{
    /// <summary>
    /// Game Creator's melee only hurts colliders marked TeamCanHit. Until enemies mark their own
    /// hitboxes when they spawn (the enemy combat bootstrap, phase 4 of the third-person plan), this
    /// marks the trigger hitboxes of every zombie in the scene, spawned ones included. Hitboxes
    /// already marked are skipped, so it is cheap to repeat a couple of times a second.
    /// </summary>
    public static class MeleeHitboxMarker
    {
        public static void MarkZombies()
        {
            foreach (var zombie in Object.FindObjectsByType<ZombieHealth>(FindObjectsSortMode.None))
            {
                foreach (var hitbox in zombie.GetComponentsInChildren<Collider>(true))
                {
                    if (hitbox.isTrigger && hitbox.GetComponent<TeamCanHit>() == null)
                        hitbox.gameObject.AddComponent<TeamCanHit>();
                }
            }
        }
    }
}
