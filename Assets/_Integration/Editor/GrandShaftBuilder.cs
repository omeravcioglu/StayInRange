#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using MalbersAnimations.Controller;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali.Editor
{
    /// <summary>
    /// Generates the 70 m Grand Shaft: a huge white tower in the style of the parkour
    /// playground, with colorful tiered platforms, ladders, wall runs, climbables,
    /// moving platforms, spring pads, jump pads and traps. Steve-only, gated by
    /// mode-switch triggers like the Lab Shaft.
    /// </summary>
    public static class GrandShaftBuilder
    {
        const string OutputDir = "Assets/_Integration/Prefabs";
        const string OutputPath = OutputDir + "/GrandShaft.prefab";
        const string MaterialDir = "Assets/_Integration/Materials";
        const string GameScenePath = "Assets/Scenes/Game.unity";
        const string RootName = "GrandShaft";

        const string LadderPath = "Assets/PlatformPrefabs/Ladder (02) Variant.prefab";
        const string WallRunPath = "Assets/PlatformPrefabs/Wall Run (2).prefab";
        const string ClimbablePath = "Assets/PlatformPrefabs/Climbable (1).prefab";
        const string SpringPadPath = "Assets/PlatformPrefabs/Zone Spring Pad Variant.prefab";
        const string MovingPlatformPath = "Assets/PlatformPrefabs/PlatForm Move Up Down.prefab";
        const string TrapDir = "Assets/AK Studio Art/Traps Pack/Prefabs/Worn White/";

        /// <summary>Where the shaft lands in the Game scene, west of the Lab Shaft.</summary>
        public static readonly Vector3 ShaftOrigin = new(-60f, 0f, -35f);

        const float Interior = 16f;
        const float Half = Interior * 0.5f;
        const float WallThickness = 0.5f;
        const float WallTop = 74f;
        const float TierStep = 5f;
        const int Tiers = 14;                 // y = 5 .. 70
        const float PlatformDepth = 4f;
        const float PlatformThickness = 0.4f;
        const float RailHeight = 1.1f;
        const float RailGapWidth = 2.5f;
        const float DoorWidth = 2.5f;
        const float DoorHeight = 3f;

        /// <summary>Rise per jump pad. Raise it for longer hops, lower it if Steve comes up short.</summary>
        public static float StepRise = TierStep / 4f;

        static Material _wallMat;
        static Material _floorMat;
        static Material _accentMat;
        static Material _padMat;
        static Material[] _tileMats;

        [MenuItem("Tools/CollarCali/Build Grand Shaft (70m)")]
        public static void BuildFromMenu()
        {
            var prefab = BuildPrefab();
            if (prefab != null)
                PlaceInGameScene(prefab, openScene: false);
            else
                BuildIntoScene(openScene: false);
        }

        public static GameObject BuildPrefab()
        {
            if (!Directory.Exists(OutputDir))
                Directory.CreateDirectory(OutputDir);
            if (!Directory.Exists(MaterialDir))
                Directory.CreateDirectory(MaterialDir);

            EnsureMaterials();

            var preview = EditorSceneManager.NewPreviewScene();
            bool success = false;
            try
            {
                var root = new GameObject(RootName);
                SceneManager.MoveGameObjectToScene(root, preview);
                Populate(root.transform);
                PrefabUtility.SaveAsPrefabAsset(root, OutputPath, out success);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }

            if (!success)
            {
                Debug.LogWarning("[GrandShaft] SaveAsPrefabAsset failed; building the shaft into the scene instead.");
                return null;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[GrandShaft] Prefab saved to " + OutputPath);
            return AssetDatabase.LoadAssetAtPath<GameObject>(OutputPath);
        }

        static void Populate(Transform root)
        {
            BuildShell(root);
            BuildLighting(root);
            BuildPlatforms(root);
            BuildStepPads(root);
            PlaceProps(root);
            PlaceTraps(root);
            BuildModeTriggers(root);
        }

        public static void PlaceInGameScene(GameObject prefab, bool openScene)
        {
            if (!TryResolveGameScene(openScene, out var scene))
                return;

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = RootName;
            instance.transform.position = ShaftOrigin;

            SaveGameScene(scene);
        }

        static void BuildIntoScene(bool openScene)
        {
            if (!TryResolveGameScene(openScene, out var scene))
                return;

            EnsureMaterials();

            var root = new GameObject(RootName);
            root.transform.position = ShaftOrigin;
            Populate(root.transform);

            SaveGameScene(scene);
        }

        static bool TryResolveGameScene(bool openScene, out Scene scene)
        {
            scene = openScene
                ? EditorSceneManager.OpenScene(GameScenePath, OpenSceneMode.Single)
                : SceneManager.GetActiveScene();

            if (!scene.IsValid() || scene.path != GameScenePath)
                return false;

            return GameObject.Find(RootName) == null;
        }

        static void SaveGameScene(Scene scene)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[GrandShaft] Shaft placed in Game at {ShaftOrigin}.");
        }

        #region Tier math

        static float TierY(int tier) => tier * TierStep;

        /// <summary>Odd tiers sit on the north (+z) side, even tiers on the south.</summary>
        static bool IsNorth(int tier) => (tier % 2) == 1;

        static float PlatformCenterZ(int tier) =>
            IsNorth(tier) ? Half - PlatformDepth * 0.5f : -(Half - PlatformDepth * 0.5f);

        static float PlatformInnerZ(int tier) =>
            IsNorth(tier) ? Half - PlatformDepth : -(Half - PlatformDepth);

        static float PlatformTopY(int tier) => TierY(tier) + PlatformThickness * 0.5f;

        static float RailGapX(int tier) => (tier % 2 == 0) ? 4f : -4f;

        /// <summary>Traps live on the opposite side of the jump lane.</summary>
        static float TrapX(int tier) => RailGapX(tier) > 0f ? -4.5f : 4.5f;

        #endregion

        #region Shell

        static void BuildShell(Transform root)
        {
            var shell = Group(root, "Shell");
            float outer = Half + WallThickness;

            Solid(shell, "Floor", new Vector3(0f, -WallThickness * 0.5f, 0f),
                new Vector3(outer * 2f, WallThickness, outer * 2f), _floorMat);

            Solid(shell, "Roof", new Vector3(0f, WallTop + WallThickness * 0.5f, 0f),
                new Vector3(outer * 2f, WallThickness, outer * 2f), _wallMat);

            var groundDoor = new Rect(-DoorWidth * 0.5f, 0f, DoorWidth, DoorHeight);
            var summitDoor = new Rect(-DoorWidth * 0.5f, PlatformTopY(Tiers), DoorWidth, DoorHeight);

            BuildWall(shell, "Wall North", true, Half + WallThickness * 0.5f, -outer, outer, null);
            BuildWall(shell, "Wall South", true, -(Half + WallThickness * 0.5f), -outer, outer,
                new List<Rect> { summitDoor });
            BuildWall(shell, "Wall East", false, Half + WallThickness * 0.5f, -outer, outer, null);
            BuildWall(shell, "Wall West", false, -(Half + WallThickness * 0.5f), -outer, outer,
                new List<Rect> { groundDoor });

            // Apron outside the ground door so the approach is solid whatever the terrain does.
            Solid(shell, "Door Apron", new Vector3(-(outer + 1.5f), -0.15f, 0f),
                new Vector3(3f, 0.3f, DoorWidth + 1.5f), _floorMat);

            // Balcony outside the summit door: the reward for topping out 70 m.
            Solid(shell, "Summit Balcony",
                new Vector3(0f, PlatformTopY(Tiers) - 0.15f, -(outer + 1.5f)),
                new Vector3(5f, 0.3f, 3f), _floorMat);
        }

        static void BuildWall(Transform parent, string name, bool spansX, float normalCoord,
            float uMin, float uMax, List<Rect> holes)
        {
            var wall = Group(parent, name);

            void Segment(float u0, float u1, float v0, float v1)
            {
                float uSize = u1 - u0;
                float vSize = v1 - v0;
                if (uSize <= 0.001f || vSize <= 0.001f)
                    return;

                float uCenter = (u0 + u1) * 0.5f;
                float vCenter = (v0 + v1) * 0.5f;

                var pos = spansX
                    ? new Vector3(uCenter, vCenter, normalCoord)
                    : new Vector3(normalCoord, vCenter, uCenter);
                var size = spansX
                    ? new Vector3(uSize, vSize, WallThickness)
                    : new Vector3(WallThickness, vSize, uSize);

                Solid(wall, "Segment", pos, size, _wallMat);
            }

            if (holes == null || holes.Count == 0)
            {
                Segment(uMin, uMax, 0f, WallTop);
                return;
            }

            holes.Sort((a, b) => a.yMin.CompareTo(b.yMin));

            float cursor = 0f;
            foreach (var hole in holes)
            {
                if (hole.yMin > cursor)
                    Segment(uMin, uMax, cursor, hole.yMin);

                Segment(uMin, hole.xMin, hole.yMin, hole.yMax);
                Segment(hole.xMax, uMax, hole.yMin, hole.yMax);
                cursor = hole.yMax;
            }

            if (cursor < WallTop)
                Segment(uMin, uMax, cursor, WallTop);
        }

        /// <summary>The shaft is sealed, so it needs its own lights to be playable.</summary>
        static void BuildLighting(Transform root)
        {
            var group = Group(root, "Lighting");

            for (float y = 2.5f; y < WallTop; y += 10f)
            {
                var go = new GameObject($"Lamp y={y:0}");
                go.transform.SetParent(group, false);
                go.transform.localPosition = new Vector3(0f, y, 0f);

                var light = go.AddComponent<Light>();
                light.type = LightType.Point;
                light.range = 22f;
                light.intensity = 1.6f;
                light.color = Color.white;
                light.shadows = LightShadows.None;
            }
        }

        #endregion

        #region Platforms and railings

        static void BuildPlatforms(Transform root)
        {
            var group = Group(root, "Platforms");

            for (int tier = 1; tier <= Tiers; tier++)
            {
                var tierGroup = Group(group, $"Tier {tier} (y={TierY(tier):0})");

                Solid(tierGroup, "Platform",
                    new Vector3(0f, TierY(tier), PlatformCenterZ(tier)),
                    new Vector3(Interior, PlatformThickness, PlatformDepth),
                    _tileMats[(tier - 1) % _tileMats.Length]);

                BuildRailing(tierGroup, tier);
            }
        }

        /// <summary>
        /// Extra railing gaps where a ladder, climb wall or catwalk meets this platform,
        /// so the props are not fenced off.
        /// </summary>
        static float[] ExtraGapX(int tier) => tier switch
        {
            1 => new[] { 6.5f },
            3 => new[] { 6.5f },
            4 => new[] { -6.5f },
            6 => new[] { -6.5f },
            8 => new[] { 0f },            // spring pad launch lane
            9 => new[] { 6.5f },
            11 => new[] { 6.5f, -7.3f },  // ladder C arrival + climb west base
            12 => new[] { -6.9f },        // climb west catwalk landing
            13 => new[] { 7.3f },         // climb east base
            14 => new[] { 6.9f },         // climb east catwalk landing
            _ => null,
        };

        static void BuildRailing(Transform parent, int tier)
        {
            var group = Group(parent, "Railing");

            float inward = IsNorth(tier) ? -1f : 1f;
            float railZ = PlatformInnerZ(tier) + inward * 0.12f;
            float baseY = PlatformTopY(tier);

            var gaps = new List<Vector2>
            {
                new(RailGapX(tier) - RailGapWidth * 0.5f, RailGapX(tier) + RailGapWidth * 0.5f)
            };

            var extras = ExtraGapX(tier);
            if (extras != null)
            {
                foreach (float x in extras)
                    gaps.Add(new Vector2(x - RailGapWidth * 0.5f, x + RailGapWidth * 0.5f));
            }

            gaps.Sort((a, b) => a.x.CompareTo(b.x));

            float cursor = -Half;
            foreach (var gap in gaps)
            {
                EmitRailRun(group, cursor, gap.x, railZ, baseY);
                cursor = Mathf.Max(cursor, gap.y);
            }
            EmitRailRun(group, cursor, Half, railZ, baseY);
        }

        static void EmitRailRun(Transform parent, float xStart, float xEnd, float z, float baseY)
        {
            float length = xEnd - xStart;
            if (length <= 0.15f)
                return;

            float xCenter = (xStart + xEnd) * 0.5f;

            Solid(parent, "Rail Top", new Vector3(xCenter, baseY + RailHeight, z),
                new Vector3(length, 0.08f, 0.08f), _accentMat, collider: false);

            Solid(parent, "Rail Mid", new Vector3(xCenter, baseY + RailHeight * 0.5f, z),
                new Vector3(length, 0.06f, 0.06f), _accentMat, collider: false);

            int posts = Mathf.Max(2, Mathf.RoundToInt(length / 2f) + 1);
            for (int i = 0; i < posts; i++)
            {
                float t = i / (float)(posts - 1);
                float x = Mathf.Lerp(xStart + 0.05f, xEnd - 0.05f, t);
                Solid(parent, "Post", new Vector3(x, baseY + RailHeight * 0.5f, z),
                    new Vector3(0.08f, RailHeight, 0.08f), _accentMat, collider: false);
            }

            Blocker(parent, "Rail Blocker", new Vector3(xCenter, baseY + RailHeight * 0.5f, z),
                new Vector3(length, RailHeight, 0.12f));
        }

        #endregion

        #region Step pads

        static void BuildStepPads(Transform root)
        {
            var group = Group(root, "Step Pads");
            int pads = Mathf.Max(1, Mathf.CeilToInt(TierStep / StepRise) - 1);

            for (int tier = 0; tier < Tiers; tier++)
            {
                int next = tier + 1;

                float fromY = tier == 0 ? 0f : PlatformTopY(tier);
                float toY = PlatformTopY(next);
                float fromZ = tier == 0 ? 0f : PlatformInnerZ(tier);
                float toZ = PlatformInnerZ(next);
                float x = RailGapX(next);

                for (int i = 1; i <= pads; i++)
                {
                    float t = i / (float)(pads + 1);

                    // Small stagger so the climb zig-zags instead of being one straight hop line.
                    float xOffset = (i % 2 == 0) ? 0.9f : -0.9f;

                    Solid(group, $"Pad {next}-{i}",
                        new Vector3(x + xOffset, Mathf.Lerp(fromY, toY, t), Mathf.Lerp(fromZ, toZ, t)),
                        new Vector3(1.8f, 0.3f, 1.8f),
                        _padMat);
                }
            }
        }

        #endregion

        #region Parkour props

        static void PlaceProps(Transform root)
        {
            var group = Group(root, "Props");

            // Ladders skip a tier, so they connect same-side platforms 10 m apart.
            PlaceLadder(group, "Ladder A", 1, 6.5f);
            PlaceLadder(group, "Ladder B", 4, -6.5f);
            PlaceLadder(group, "Ladder C", 9, 6.5f);

            // Wall run slabs across the mid bands.
            PlaceWallRun(group, "Wall Run East",
                new Vector3(Half - 0.3f, TierY(6) + TierStep * 0.5f, 0f),
                new Vector3(0.4f, TierStep, Interior - 2f));
            PlaceWallRun(group, "Wall Run West",
                new Vector3(-(Half - 0.3f), TierY(7) + TierStep * 0.5f, 0f),
                new Vector3(0.4f, TierStep, Interior - 2f));

            // Long thin wall-run beams on the solid walls, like the playground's purple rails.
            PlaceWallRun(group, "Beam North Low",
                new Vector3(0f, TierY(4) + 2f, Half - 0.3f),
                new Vector3(Interior - 2f, 0.8f, 0.4f));
            PlaceWallRun(group, "Beam South Mid",
                new Vector3(0f, TierY(9) + 2f, -(Half - 0.3f)),
                new Vector3(Interior - 2f, 0.8f, 0.4f));
            PlaceWallRun(group, "Beam North High",
                new Vector3(0f, TierY(12) + 2f, Half - 0.3f),
                new Vector3(Interior - 2f, 0.8f, 0.4f));

            // Spring pad launch on tier 8.
            PlaceProp(group, SpringPadPath, "Spring Pad",
                new Vector3(0f, PlatformTopY(8) + 0.05f, PlatformCenterZ(8)),
                Quaternion.identity);

            // Free climbs near the top, each topping out onto a catwalk to the next platform.
            PlaceClimbable(group, "Climb West", 11, -(Half - 0.45f), Quaternion.Euler(0f, 90f, 0f));
            PlaceClimbable(group, "Climb East", 13, Half - 0.45f, Quaternion.Euler(0f, -90f, 0f));

            // Moving platforms bridging the core.
            PlaceProp(group, MovingPlatformPath, "Moving Platform Low",
                new Vector3(0f, PlatformTopY(3) + 0.2f, 0f), Quaternion.identity);
            PlaceProp(group, MovingPlatformPath, "Moving Platform High",
                new Vector3(0f, PlatformTopY(12) + 0.2f, 0f), Quaternion.identity);
        }

        /// <summary>
        /// Hangs a 10 m ladder on the inner face of the platform two tiers up (same side),
        /// so the top exit steps forward onto the deck instead of into the shaft wall.
        /// </summary>
        static void PlaceLadder(Transform parent, string name, int fromTier, float x)
        {
            float faceSign = IsNorth(fromTier) ? 1f : -1f;
            var rotation = IsNorth(fromTier) ? Quaternion.identity : Quaternion.Euler(0f, 180f, 0f);

            var go = PlaceProp(parent, LadderPath, name, Vector3.zero, rotation);
            if (go == null)
                return;

            var ladder = go.GetComponentInChildren<MLadder>(true);
            if (ladder == null)
                return;

            const float stepDistance = 0.5f;
            int steps = Mathf.RoundToInt(TierStep * 2f / stepDistance);

            ladder.StepDistance = stepDistance;
            ladder.Steps = steps;

            bool stepsUsable = ladder.stepsObjects != null
                && ladder.stepsObjects.Count > 0
                && !ladder.stepsObjects.Exists(step => step == null);

            if (stepsUsable)
                ladder.CreateSteps(steps);

            EditorUtility.SetDirty(ladder);

            float ladderZ = PlatformInnerZ(fromTier + 2) - faceSign * 0.35f;
            AlignChildTo(go, ladder.transform,
                new Vector3(x, PlatformTopY(fromTier), ladderZ));
        }

        /// <summary>
        /// Climb wall rises from the platform edge; a catwalk along the shaft wall carries
        /// the climber from the top-out across the core to the next platform.
        /// </summary>
        static void PlaceClimbable(Transform parent, string name, int fromTier, float x, Quaternion rotation)
        {
            var go = PlaceProp(parent, ClimbablePath, name, Vector3.zero, rotation);
            if (go == null)
                return;

            float faceSign = IsNorth(fromTier) ? 1f : -1f;
            float surfaceZ = PlatformInnerZ(fromTier) - faceSign * 1.25f;

            var surface = FindByTag(go.transform, "Climb");
            if (surface != null)
            {
                AlignChildTo(go, surface,
                    new Vector3(x, PlatformTopY(fromTier) + TierStep * 0.5f, surfaceZ));
            }

            float catwalkX = x > 0f ? x - 0.65f : x + 0.65f;
            Solid(parent, name + " Catwalk",
                new Vector3(catwalkX, PlatformTopY(fromTier + 1) - 0.15f, 0f),
                new Vector3(1.8f, 0.3f, Interior - PlatformDepth * 2f + 1f), _padMat);
        }

        static void PlaceWallRun(Transform parent, string name, Vector3 position, Vector3 size)
        {
            var go = PlaceProp(parent, WallRunPath, name, position, Quaternion.identity);
            if (go != null)
                go.transform.localScale = size;
        }

        static void AlignChildTo(GameObject propRoot, Transform child, Vector3 targetLocal)
        {
            var parent = propRoot.transform.parent;
            var currentLocal = parent.InverseTransformPoint(child.position);
            propRoot.transform.localPosition += targetLocal - currentLocal;
        }

        static Transform FindByTag(Transform root, string tag)
        {
            foreach (var candidate in root.GetComponentsInChildren<Transform>(true))
            {
                if (candidate.CompareTag(tag))
                    return candidate;
            }
            return null;
        }

        static GameObject PlaceProp(Transform parent, string path, string name, Vector3 position, Quaternion rotation)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogWarning($"[GrandShaft] Prop missing, skipped: {path}");
                return null;
            }

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent.gameObject.scene);
            go.transform.SetParent(parent, false);
            go.name = name;
            go.transform.localPosition = position;
            go.transform.localRotation = rotation;
            go.SetActive(true);
            return go;
        }

        #endregion

        #region Hazards

        static void PlaceTraps(Transform root)
        {
            var group = Group(root, "Hazards");

            // Floor traps sit on the platform surface, clear of the jump lane.
            PlaceFloorTrap(group, "Spike Floor Large B", 2);
            PlaceFloorTrap(group, "Moving Saw B", 5);
            PlaceFloorTrap(group, "Spiked Roller B", 7);
            PlaceFloorTrap(group, "Spiked Rotator B", 10);
            PlaceFloorTrap(group, "Bear Trap B", 12);

            // Hanging traps anchor above a platform and swing into head height.
            PlaceHangingTrap(group, "Iron Ball B", 4);
            PlaceHangingTrap(group, "Swinging Scythe B", 9);
            PlaceHangingTrap(group, "Spiked Mace B", 13);
        }

        static void PlaceFloorTrap(Transform parent, string prefabName, int tier)
        {
            PlaceProp(parent, TrapDir + prefabName + ".prefab", prefabName,
                new Vector3(TrapX(tier), PlatformTopY(tier), PlatformCenterZ(tier)),
                Quaternion.Euler(0f, IsNorth(tier) ? 180f : 0f, 0f));
        }

        static void PlaceHangingTrap(Transform parent, string prefabName, int tier)
        {
            PlaceProp(parent, TrapDir + prefabName + ".prefab", prefabName,
                new Vector3(TrapX(tier), PlatformTopY(tier) + 3.5f, PlatformCenterZ(tier)),
                Quaternion.Euler(0f, IsNorth(tier) ? 180f : 0f, 0f));
        }

        #endregion

        #region Mode triggers

        static void BuildModeTriggers(Transform root)
        {
            var group = Group(root, "Mode Triggers");
            var green = new Color(0.2f, 0.85f, 0.35f, 0.4f);
            var red = new Color(0.9f, 0.35f, 0.2f, 0.4f);
            float outer = Half + WallThickness;

            Trigger(group, "Enter Third Person Trigger",
                PlayerModeSwitchTrigger.SwitchMode.EnterThirdPerson,
                new Vector3(-(Half - 1.2f), DoorHeight * 0.5f, 0f),
                new Vector3(1.6f, DoorHeight - 0.4f, DoorWidth - 0.2f),
                green);

            // Summit exit on the balcony through the top door.
            Trigger(group, "Exit To First Person Trigger",
                PlayerModeSwitchTrigger.SwitchMode.ExitToFirstPerson,
                new Vector3(0f, PlatformTopY(Tiers) + DoorHeight * 0.5f, -(outer + 1f)),
                new Vector3(DoorWidth - 0.2f, DoorHeight - 0.4f, 1.2f),
                red);

            // Bail-out exit, so walking back out the ground door also returns to FPS.
            Trigger(group, "Exit To First Person Trigger (Door)",
                PlayerModeSwitchTrigger.SwitchMode.ExitToFirstPerson,
                new Vector3(-(outer + 1.2f), DoorHeight * 0.5f, 0f),
                new Vector3(1.2f, DoorHeight - 0.4f, DoorWidth - 0.2f),
                red);
        }

        static void Trigger(Transform parent, string name, PlayerModeSwitchTrigger.SwitchMode mode,
            Vector3 position, Vector3 scale, Color tint)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;

            go.GetComponent<Collider>().isTrigger = true;

            var trigger = go.AddComponent<PlayerModeSwitchTrigger>();
            var so = new SerializedObject(trigger);
            so.FindProperty("mode").enumValueIndex = (int)mode;
            so.ApplyModifiedPropertiesWithoutUndo();

            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                string key = mode == PlayerModeSwitchTrigger.SwitchMode.EnterThirdPerson
                    ? "TriggerEnterTPP"
                    : "TriggerExitFPS";
                renderer.sharedMaterial = EnsureTransparentMaterial(key, tint);
            }
        }

        #endregion

        #region Primitives and materials

        static Transform Group(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        static GameObject Solid(Transform parent, string name, Vector3 position, Vector3 size,
            Material material, string tag = null, bool collider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = size;

            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null && material != null)
                renderer.sharedMaterial = material;

            if (!collider)
                Object.DestroyImmediate(go.GetComponent<Collider>());

            if (!string.IsNullOrEmpty(tag))
                go.tag = tag;

            return go;
        }

        static GameObject Blocker(Transform parent, string name, Vector3 position, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;

            var box = go.AddComponent<BoxCollider>();
            box.size = size;
            return go;
        }

        static void EnsureMaterials()
        {
            _wallMat = EnsureMaterial("GrandWall", new Color(0.93f, 0.93f, 0.95f), 0.2f, 0f);
            _floorMat = EnsureMaterial("GrandFloor", new Color(0.72f, 0.78f, 0.88f), 0.3f, 0.1f);
            _accentMat = EnsureMaterial("GrandAccent", new Color(0.45f, 0.45f, 0.85f), 0.5f, 0.4f);
            _padMat = EnsureMaterial("GrandPad", new Color(0.95f, 0.75f, 0.1f), 0.4f, 0.2f);

            _tileMats = new Material[7];
            for (int i = 0; i < _tileMats.Length; i++)
            {
                var color = Color.HSVToRGB(i / (float)_tileMats.Length, 0.65f, 0.95f);
                _tileMats[i] = EnsureMaterial($"GrandTile{i}", color, 0.35f, 0.1f);
            }
        }

        static Material EnsureMaterial(string name, Color color, float smoothness, float metallic)
        {
            string path = $"{MaterialDir}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
                return existing;

            var shader = Shader.Find("Standard");
            if (shader == null)
                return null;

            var material = new Material(shader);
            material.SetColor("_Color", color);
            material.SetFloat("_Glossiness", smoothness);
            material.SetFloat("_Metallic", metallic);

            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        static Material EnsureTransparentMaterial(string name, Color tint)
        {
            string path = $"{MaterialDir}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
                return existing;

            var shader = Shader.Find("Standard");
            if (shader == null)
                return null;

            var material = new Material(shader) { color = tint };
            material.SetFloat("_Mode", 3);
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.EnableKeyword("_ALPHABLEND_ON");
            material.renderQueue = 3000;

            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        #endregion
    }
}
#endif
