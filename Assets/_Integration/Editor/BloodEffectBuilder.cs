#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Wraps the RVFX Blood Effects Pack into the two prefabs the game spawns.
    ///
    /// It uses the pack's 1_URP variant, whose materials are already built on the pack's own
    /// BloodFX_PBR_URP shader graph - so unlike the Malbers particles this replaced, nothing needs
    /// converting and nothing renders magenta. The wrappers nest the pack prefabs rather than
    /// copying them, so tweaks made inside RVFX still flow through.
    /// </summary>
    public static class BloodEffectBuilder
    {
        const string PackRoot = "Assets/RVFX/BloodEffectsPack/1_URP/";

        /// <summary>Per-hit spray. Smaller and quicker than the death effect.</summary>
        const string HitSplash = PackRoot + "Blood/Splash/Blood_Splash_02_URP.prefab";

        /// <summary>Death. The WithGut variant is the one that throws chunks.</summary>
        const string DeathSplash = PackRoot + "Blood/Splash/Blood_Splash_01_WithGut_URP.prefab";

        /// <summary>
        /// Mesh decal, deliberately NOT the Decal_Projector variant: URP decal projectors need the
        /// Decal Renderer Feature on the renderer asset, and neither URP-Default-Renderer nor
        /// URP-Scene1-Renderer in this project has it. The mesh version renders regardless.
        /// </summary>
        const string GroundDecal = PackRoot + "Blood/Decal/BloodDecal_01_Static_URP.prefab";

        const string OutputRoot = "Assets/_Integration/Resources/";
        const string MaterialFolder = "Assets/_Integration/Materials";
        const string HitPrefab = OutputRoot + "ZombieBlood.prefab";
        const string DeathPrefab = OutputRoot + "ZombieBloodDeath.prefab";

        /// <summary>
        /// Standalone, NOT nested inside the death effect. As a child it inherited the splash's
        /// transform and had to be re-parented at runtime, which is how it ended up hanging at
        /// chest height. ZombieBloodFx now raycasts first and spawns this straight onto the floor.
        /// </summary>
        const string DecalPrefab = OutputRoot + "ZombieBloodDecal.prefab";

        /// <summary>Left over from the Malbers-based version this replaced.</summary>
        static readonly string[] ObsoleteAssets =
        {
            "Assets/_Integration/Materials/Blood_Hit_Particle_03_Blood.mat",
            "Assets/_Integration/Materials/Blood_Ring.mat",
        };

        const string EnemyLayerName = "Enemy";

        static readonly string[] PlayerPrefabFolders =
        {
            "Assets/LabPrefabs",
            "Assets/_Integration/Prefabs",
            "Assets/_Integration/Resources",
        };

        [MenuItem("Tools/CollarCali/Build Blood Effects")]
        public static void Build()
        {
            var hitSource = Require(HitSplash);
            var deathSource = Require(DeathSplash);
            var decalSource = AssetDatabase.LoadAssetAtPath<GameObject>(GroundDecal);

            if (hitSource == null || deathSource == null)
                return;

            CleanUpMalbersLeftovers();

            BuildWrapper(HitPrefab, "ZombieBlood", hitSource, null);
            BuildWrapper(DeathPrefab, "ZombieBloodDeath", deathSource, null);
            BuildDecal(decalSource);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            VerifyMaterials();
            AssignToEnemyImpactSlot();

            Debug.Log("[CollarCali] Blood effects rebuilt from the RVFX pack.");
        }

        static GameObject Require(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null)
            {
                Debug.LogError("[CollarCali] Missing RVFX prefab: " + path +
                               "\nExtract BloodEffectPack_URP.unitypackage into the project first.");
            }
            return asset;
        }

        #region Wrapper prefabs

        /// <summary>
        /// One empty root holding the pack effect, plus optionally a ground decal dropped to the
        /// floor by ZombieBloodFx at spawn time.
        /// </summary>
        static void BuildWrapper(string path, string name, GameObject effect, GameObject decal)
        {
            var root = new GameObject(name);

            try
            {
                var splash = (GameObject)PrefabUtility.InstantiatePrefab(effect);
                splash.transform.SetParent(root.transform, false);
                splash.name = "Splash";

                TuneGutBurst(root, splash);


                AssetDatabase.DeleteAsset(path);
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// The pack's decal shader graph is single-sided (RenderFace Front), so a decal laid the
        /// wrong way up is invisible rather than merely mirrored. ZombieBloodFx orients it
        /// correctly, but a double-sided copy means a surface normal we did not anticipate can
        /// never silently blank the effect.
        /// </summary>
        static void MakeDecalDoubleSided(GameObject decalInstance)
        {
            foreach (var renderer in decalInstance.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    var original = materials[i];
                    if (original == null)
                        continue;
                    materials[i] = DoubleSidedCopy(original);
                }
                renderer.sharedMaterials = materials;
            }
        }

        static Material DoubleSidedCopy(Material original)
        {
            if (!AssetDatabase.IsValidFolder(MaterialFolder))
                AssetDatabase.CreateFolder("Assets/_Integration", "Materials");

            string path = MaterialFolder + "/" + SafeName(original.name) + "_TwoSided.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
                return existing;

            var copy = new Material(original) { name = original.name + "_TwoSided" };
            // URP RenderFace: Both = 0, Back = 1, Front = 2. _Cull mirrors it for the built pass.
            if (copy.HasProperty("_RenderFace"))
                copy.SetFloat("_RenderFace", 0f);
            if (copy.HasProperty("_Cull"))
                copy.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            copy.doubleSidedGI = true;

            AssetDatabase.CreateAsset(copy, path);
            return copy;
        }

        static string SafeName(string value)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');
            return value.Trim();
        }

        /// <summary>
        /// The pack ships its gut chunks at 0.15-0.23 units across a 0.23 radius - at gameplay
        /// distance that is a few invisible specks. Scaled up and thrown wider so a kill actually
        /// reads, with a cleanup component so the chunks do not pile up across a horde.
        /// </summary>
        static void TuneGutBurst(GameObject root, GameObject splash)
        {
            bool found = false;

            foreach (var component in splash.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component.GetType().Name != "EffectController")
                    continue;

                var so = new SerializedObject(component);
                SetIfPresent(so, "spawnNumber", 9);
                SetIfPresent(so, "spawnRadius", 0.4f);
                SetIfPresent(so, "minScale", 0.45f);
                SetIfPresent(so, "maxScale", 0.85f);
                SetIfPresent(so, "minForce", 60f);
                SetIfPresent(so, "maxForce", 260f);
                so.ApplyModifiedPropertiesWithoutUndo();
                found = true;
            }

            if (!found)
            {
                Debug.LogWarning("[CollarCali] No EffectController inside " + splash.name +
                                 " - the death effect will have no gut chunks.");
                return;
            }

            if (root.GetComponent<ZombieGutCleanup>() == null)
                root.AddComponent<ZombieGutCleanup>();
        }

        static void SetIfPresent(SerializedObject so, string field, float value)
        {
            var property = so.FindProperty(field);
            if (property == null)
                return;
            if (property.propertyType == SerializedPropertyType.Integer)
                property.intValue = Mathf.RoundToInt(value);
            else
                property.floatValue = value;
        }

        /// <summary>
        /// The blood pool, alone in its own prefab with an identity transform so the caller owns
        /// the pose completely.
        /// </summary>
        static void BuildDecal(GameObject decal)
        {
            if (decal == null)
            {
                Debug.LogWarning("[CollarCali] No decal prefab found; deaths will leave no pool.");
                return;
            }

            var root = (GameObject)PrefabUtility.InstantiatePrefab(decal);

            try
            {
                root.name = "ZombieBloodDecal";
                root.transform.position = Vector3.zero;
                root.transform.rotation = Quaternion.identity;
                // The pack's decal is a 1x1 quad - far too small to read as a pool of blood.
                root.transform.localScale = Vector3.one * 1.8f;

                MakeDecalDoubleSided(root);

                AssetDatabase.DeleteAsset(DecalPrefab);
                PrefabUtility.SaveAsPrefabAsset(root, DecalPrefab);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        static void CleanUpMalbersLeftovers()
        {
            foreach (var path in ObsoleteAssets)
            {
                if (AssetDatabase.LoadAssetAtPath<Object>(path) != null)
                    AssetDatabase.DeleteAsset(path);
            }
        }

        #endregion

        #region Validation

        /// <summary>
        /// Catches the one failure mode that is invisible until you shoot something: a material
        /// whose shader failed to resolve renders bright magenta in play mode.
        /// </summary>
        static void VerifyMaterials()
        {
            var broken = new List<string>();

            foreach (var path in new[] { HitPrefab, DeathPrefab, DecalPrefab })
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null)
                    continue;

                foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (material == null)
                        {
                            broken.Add(renderer.name + " has an empty material slot");
                            continue;
                        }

                        var shader = material.shader;
                        if (shader == null || shader.name == "Hidden/InternalErrorShader")
                            broken.Add(material.name + " has no usable shader");
                    }
                }
            }

            if (broken.Count == 0)
            {
                Debug.Log("[CollarCali] Blood materials verified - all shaders resolved.");
                return;
            }

            Debug.LogError("[CollarCali] Blood material problems:\n - " + string.Join("\n - ", broken));
        }

        #endregion

        #region Cowsins impact list

        /// <summary>
        /// Points the Cowsins Enemy-layer impact at the hit splash, so bullets on anything on the
        /// Enemy layer - zombies, Emerald AI, the creep - spray blood instead of grey smoke.
        /// </summary>
        static void AssignToEnemyImpactSlot()
        {
            var blood = AssetDatabase.LoadAssetAtPath<GameObject>(HitPrefab);
            if (blood == null)
                return;

            var folders = new List<string>();
            foreach (var folder in PlayerPrefabFolders)
            {
                if (AssetDatabase.IsValidFolder(folder))
                    folders.Add(folder);
            }

            if (folders.Count == 0)
                return;

            int updated = 0;

            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", folders.ToArray()))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (asset == null || asset.GetComponentInChildren<cowsins.WeaponController>(true) == null)
                    continue;

                var contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    bool changed = false;

                    foreach (var controller in contents.GetComponentsInChildren<cowsins.WeaponController>(true))
                    {
                        var impacts = controller?.settings?.impactEffects?.impacts;
                        if (impacts == null)
                            continue;

                        for (int i = 0; i < impacts.Count; i++)
                        {
                            if (impacts[i] == null || impacts[i].layerName != EnemyLayerName)
                                continue;
                            if (impacts[i].impact == blood)
                                continue;

                            impacts[i].impact = blood;
                            changed = true;
                        }
                    }

                    if (changed)
                    {
                        PrefabUtility.SaveAsPrefabAsset(contents, path);
                        updated++;
                    }
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(contents);
                }
            }

            Debug.Log("[CollarCali] Blood assigned to the Enemy impact slot on " + updated + " prefab(s).");
        }

        #endregion
    }
}
#endif
