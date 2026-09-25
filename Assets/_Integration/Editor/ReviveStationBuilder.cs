#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Builds the revive station prefab, so it can be dropped anywhere in a level.
    ///
    /// Built by script rather than by hand for the same reason as the rest of this project's
    /// prefabs: the details that make an interactable work are easy to get wrong by hand and
    /// invisible when they are wrong. In particular the root must be on the Interactable layer and
    /// carry the collider itself, because Cowsins resolves an interaction by looking for an
    /// Interactable on the exact collider its ray hit - and none of the decorative children may have
    /// a collider, or they would intercept that ray and the prompt would never appear.
    /// </summary>
    public static class ReviveStationBuilder
    {
        const string OutputPath = "Assets/_Integration/Prefabs/ReviveStation.prefab";

        [MenuItem("Tools/CollarCali/Build Revive Station")]
        public static void Build()
        {
            var root = new GameObject("ReviveStation");

            try
            {
                int interactable = LayerMask.NameToLayer("Interactable");
                if (interactable >= 0)
                    root.layer = interactable;
                else
                    Debug.LogWarning("[CollarCali] No 'Interactable' layer in this project - the " +
                                     "station will not be detected by the player's interact ray.");

                // The collider the interact ray has to hit. On the root, with the component.
                var collider = root.AddComponent<BoxCollider>();
                collider.size = new Vector3(1.6f, 0.9f, 1.6f);
                collider.center = new Vector3(0f, 0.45f, 0f);

                BuildPad(root.transform);
                var indicator = BuildIndicator(root.transform);
                var spawn = BuildSpawnPoint(root.transform);

                var station = root.AddComponent<ReviveStation>();
                station.interactText = "Revive teammate";

                // Private serialized fields, set the same way the other builders in this project do.
                var serialized = new SerializedObject(station);
                serialized.FindProperty("spawnPoint").objectReferenceValue = spawn;
                serialized.FindProperty("readyIndicator").objectReferenceValue = indicator;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                System.IO.Directory.CreateDirectory("Assets/_Integration/Prefabs");
                AssetDatabase.DeleteAsset(OutputPath);
                PrefabUtility.SaveAsPrefabAsset(root, OutputPath);
                AssetDatabase.SaveAssets();

                Debug.Log("[CollarCali] Revive station built at " + OutputPath +
                          ". Drop it in a scene wherever players should be able to revive.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>The visible pad. Its collider is removed - see the class comment.</summary>
        static void BuildPad(Transform parent)
        {
            var pad = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pad.name = "Pad";
            pad.transform.SetParent(parent, false);
            pad.transform.localScale = new Vector3(1.5f, 0.06f, 1.5f);
            StripCollider(pad);
            Paint(pad, new Color(0.16f, 0.5f, 0.32f, 1f));

            var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
            post.name = "Post";
            post.transform.SetParent(parent, false);
            post.transform.localPosition = new Vector3(0f, 0.6f, -0.55f);
            post.transform.localScale = new Vector3(0.18f, 1.2f, 0.18f);
            StripCollider(post);
            Paint(post, new Color(0.2f, 0.22f, 0.26f, 1f));
        }

        /// <summary>Lit only while a body is in range, so the station reads as ready to use.</summary>
        static GameObject BuildIndicator(Transform parent)
        {
            var indicator = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            indicator.name = "ReadyIndicator";
            indicator.transform.SetParent(parent, false);
            indicator.transform.localPosition = new Vector3(0f, 1.25f, -0.55f);
            indicator.transform.localScale = Vector3.one * 0.22f;
            StripCollider(indicator);
            Paint(indicator, new Color(0.35f, 1f, 0.55f, 1f), emissive: true);

            var light = indicator.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 6f;
            light.intensity = 2.2f;
            light.color = new Color(0.4f, 1f, 0.6f);

            indicator.SetActive(false);
            return indicator;
        }

        static Transform BuildSpawnPoint(Transform parent)
        {
            var spawn = new GameObject("ReviveSpawn");
            spawn.transform.SetParent(parent, false);
            // In front of the pad, facing back at it, so a revived player stands up looking at the
            // station rather than into it.
            spawn.transform.localPosition = new Vector3(0f, 0f, 1.1f);
            spawn.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            return spawn.transform;
        }

        static void StripCollider(GameObject go)
        {
            var collider = go.GetComponent<Collider>();
            if (collider != null)
                Object.DestroyImmediate(collider);
        }

        static void Paint(GameObject go, Color color, bool emissive = false)
        {
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                return;

            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var material = new Material(shader) { name = go.name + "Material" };
            material.color = color;
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);

            if (emissive)
            {
                material.EnableKeyword("_EMISSION");
                if (material.HasProperty("_EmissionColor"))
                    material.SetColor("_EmissionColor", color * 2.5f);
            }

            renderer.sharedMaterial = material;
        }
    }
}
#endif
