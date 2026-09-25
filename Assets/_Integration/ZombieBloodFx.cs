using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Spawns the RVFX blood effects assembled by BloodEffectBuilder.
    ///
    /// Per-hit blood normally comes from Cowsins' own Enemy-layer impact (which the builder points
    /// at the same hit prefab), so what this mostly handles is the death effect: the gut splash
    /// plus a ground decal seated on whatever surface the body fell on.
    /// </summary>
    public static class ZombieBloodFx
    {
        public enum Kind
        {
            Hit = 0,
            Death = 1,
            Decal = 2,
        }

        const string HitPrefabName = "ZombieBlood";
        const string DeathPrefabName = "ZombieBloodDeath";
        const string DecalPrefabName = "ZombieBloodDecal";

        /// <summary>Splash particles finish well inside this; the decal is parented out first.</summary>
        const float SplashLifetime = 4f;

        /// <summary>Decals outlive the splash so the floor stays stained after the body goes.</summary>
        const float DecalLifetime = 25f;

        /// <summary>Generous: the effect spawns at chest height and bodies die on slopes.</summary>
        const float DecalDropDistance = 6f;

        static GameObject _hit;
        static GameObject _death;
        static GameObject _decal;
        static bool _resolved;
        static int _groundMask = -1;

        /// <summary>Plays on this machine only.</summary>
        public static void Play(Vector3 position, Kind kind, float scale = 1f)
        {
            var prefab = Resolve(kind);
            if (prefab == null)
                return;

            // Ground is resolved BEFORE the splash is instantiated, and this ordering is load
            // bearing. The gut effect spawns nine rigidbody chunks from its OnEnable - which runs
            // synchronously inside Instantiate - as solid MeshColliders on the Default layer,
            // right where the downward ray passes. Raycasting afterwards hit a flying chunk of
            // gore at chest height and stuck the blood pool up there in mid-air.
            // Pre-declared rather than `out var` inside the &&: short-circuiting would leave them
            // unassigned as far as the compiler is concerned.
            var groundPoint = Vector3.zero;
            var groundRotation = Quaternion.identity;
            GameObject decalPrefab = null;

            if (kind == Kind.Death)
            {
                decalPrefab = Resolve(Kind.Decal);
                if (decalPrefab != null &&
                    !TryFindGround(position, out groundPoint, out groundRotation))
                {
                    decalPrefab = null;
                }
            }

            var instance = Object.Instantiate(prefab, position, Quaternion.identity);
            instance.transform.localScale = Vector3.one * Mathf.Max(0.01f, scale);
            Object.Destroy(instance, SplashLifetime);

            // Local play, deliberately. This method already runs once on every machine - directly on
            // whoever landed the kill, and through the blood relay everywhere else - so the gore
            // sound rides that existing broadcast instead of sending a second one of its own, and
            // lands on the exact frame the splash appears.
            //
            // Death only: the per-hit squelch is fired from ZombieHealth, because most hit blood is
            // spawned by Cowsins' own impact effect and never reaches this method at all.
            if (kind == Kind.Death)
                GameSfx.Play(SfxId.BloodBurstDeath, position);

            if (decalPrefab != null)
            {
                var decal = Object.Instantiate(decalPrefab, groundPoint, groundRotation);
                Object.Destroy(decal, DecalLifetime);
            }
        }

        /// <summary>
        /// Plays here immediately and tells everyone else to play it too. Local-first so whoever
        /// landed the kill sees it on the same frame rather than a round trip later.
        /// </summary>
        public static void PlayShared(Vector3 position, Kind kind, float scale = 1f)
        {
            Play(position, kind, scale);

#if CMPSETUP_COMPLETE
            var relay = NetworkVfxRelay.Instance;
            if (relay != null)
                relay.BroadcastBlood(position, (int)kind, scale);
#endif
        }

        /// <summary>
        /// Casts down for a surface the blood pool can sit on, ignoring anything that is not part
        /// of the level: gore chunks, corpses, and any other rigidbody debris in the way.
        /// </summary>
        static bool TryFindGround(Vector3 origin, out Vector3 point, out Quaternion rotation)
        {
            point = default;
            rotation = Quaternion.identity;

            var hits = Physics.RaycastAll(origin + Vector3.up * 1f, Vector3.down,
                DecalDropDistance, GroundMask(), QueryTriggerInteraction.Ignore);

            if (hits == null || hits.Length == 0)
            {
                // #region agent log
                AgentDebugLog.Write("Z2", "ZombieBloodFx.TryFindGround", "no_ground",
                    "{\"origin\":\"" + origin.ToString("F2") + "\"}");
                // #endregion
                return false;
            }

            RaycastHit best = default;
            float bestDistance = float.MaxValue;

            foreach (var hit in hits)
            {
                if (hit.collider == null || !IsLevelGeometry(hit.collider))
                    continue;
                if (hit.distance >= bestDistance)
                    continue;

                best = hit;
                bestDistance = hit.distance;
            }

            if (bestDistance == float.MaxValue)
            {
                // #region agent log
                AgentDebugLog.Write("Z2", "ZombieBloodFx.TryFindGround", "no_level_geometry",
                    "{\"origin\":\"" + origin.ToString("F2") + "\",\"hits\":" + hits.Length + "}");
                // #endregion
                return false;
            }

            // Lifted slightly off the surface, otherwise the quad z-fights with the floor. The
            // rotation uses MINUS the normal because a Unity quad's visible face points down its
            // local -Z and the pack's decal shader is single-sided.
            point = best.point + best.normal * 0.02f;
            rotation = Quaternion.LookRotation(-best.normal) *
                       Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
            return true;
        }

        /// <summary>
        /// Level geometry is static and solid. A rigidbody means it is debris that is still
        /// moving, and anything belonging to a zombie is a corpse, not a floor.
        /// </summary>
        static bool IsLevelGeometry(Collider collider)
        {
            if (collider.attachedRigidbody != null)
                return false;
            if (collider.GetComponentInParent<ZombieEnemy>() != null)
                return false;
            if (collider.GetComponentInParent<ZombieHealth>() != null)
                return false;
            return true;
        }

        static int GroundMask()
        {
            if (_groundMask != -1)
                return _groundMask;

            // Everything solid; enemies and players must not catch the decal.
            int ignore = LayerMask.GetMask(
                "Ignore Raycast", "TransparentFX", "UI", "UITop", "Water",
                "Weapons", "Player", "Enemy", "Animal", "Item", "PostProcessing", "Effects");
            _groundMask = ~ignore;
            return _groundMask;
        }

        static GameObject Resolve(Kind kind)
        {
            if (!_resolved)
            {
                _resolved = true;
                _hit = Load(HitPrefabName);
                _death = Load(DeathPrefabName);
                _decal = Load(DecalPrefabName);
            }

            GameObject prefab;
            switch (kind)
            {
                case Kind.Death: prefab = _death; break;
                case Kind.Decal: prefab = _decal; break;
                default: prefab = _hit; break;
            }
            if (prefab == null)
            {
                Debug.LogWarning("[CollarCali] Blood prefab missing. " +
                                 "Run Tools/CollarCali/Build Blood Effects.");
            }
            return prefab;
        }

        static GameObject Load(string name)
        {
            var prefab = Resources.Load<GameObject>(name);

#if UNITY_EDITOR
            if (prefab == null)
            {
                prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                    "Assets/_Integration/Resources/" + name + ".prefab");
            }
#endif
            return prefab;
        }
    }
}
