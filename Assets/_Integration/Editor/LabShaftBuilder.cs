#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using MalbersAnimations;
using MalbersAnimations.Controller;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali.Editor
{
    /// <summary>
    /// Generates the 60 m vertical laboratory shaft: alternating platforms, switchback
    /// stairs, railings, crawl vents every 15 m, and the parkour props from PlatformPrefabs.
    /// Tuned for Malbers Steve, so the shaft is gated by mode-switch triggers.
    /// </summary>
    public static class LabShaftBuilder
    {
        const string OutputDir = "Assets/_Integration/Prefabs";
        const string OutputPath = OutputDir + "/LabShaft.prefab";
        const string MaterialDir = "Assets/_Integration/Materials";
        const string GameScenePath = "Assets/Scenes/Game.unity";
        const string RootName = "LabShaft";

        const string LadderPath = "Assets/PlatformPrefabs/Ladder (02) Variant.prefab";
        const string WallRunPath = "Assets/PlatformPrefabs/Wall Run (2).prefab";
        const string ClimbablePath = "Assets/PlatformPrefabs/Climbable (1).prefab";
        const string SpringPadPath = "Assets/PlatformPrefabs/Zone Spring Pad Variant.prefab";
        const string MovingPlatformPath = "Assets/PlatformPrefabs/PlatForm Move Up Down.prefab";
        const string TrapDir = "Assets/AK Studio Art/Traps Pack/Prefabs/Worn White/";
        const string CrouchStancePath =
            "Assets/Malbers Animations/Common/Scriptable Assets/IDs/StanceID/Crouch.asset";

        /// <summary>Where the shaft lands in the Game scene. Nudge if it clips the playground.</summary>
        public static readonly Vector3 ShaftOrigin = new(-30f, 0f, -35f);

        const float Interior = 10f;
        const float Half = Interior * 0.5f;
        const float WallThickness = 0.5f;
        const float WallTop = 63f;
        const float TierStep = 5f;
        const int Tiers = 12;
        const int StairBandTiers = 3;
        const float PlatformDepth = 3f;
        const float PlatformThickness = 0.4f;
        const float RailHeight = 1.1f;
        const float RailGapWidth = 2f;
        const float StairWidth = 2f;
        const float StairLaneX = -3.5f;
        const float VentWidth = 1.6f;
        const float VentHeight = 1.5f;
        const float VentDepth = 2.5f;
        const float DoorWidth = 2.5f;
        const float DoorHeight = 3f;

        /// <summary>Rise per jump pad. Raise it for longer hops, lower it if Steve comes up short.</summary>
        public static float StepRise = TierStep / 4f;

        static Material _wallMat;
        static Material _platformMat;
        static Material _accentMat;
        static Material _padMat;

        [MenuItem("Tools/CollarCali/Build Lab Shaft")]
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
                Debug.LogWarning("[LabShaft] SaveAsPrefabAsset failed; building the shaft into the scene instead.");
                return null;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[LabShaft] Prefab saved to " + OutputPath);
            return AssetDatabase.LoadAssetAtPath<GameObject>(OutputPath);
        }

        static void Populate(Transform root)
        {
            BuildShell(root);
            BuildPlatforms(root);
            BuildStairs(root);
            BuildStepPads(root);
            BuildVents(root);
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

        /// <summary>
        /// Fallback for when the nested prop prefabs defeat SaveAsPrefabAsset, which is how the
        /// dual player builder ended up spawning its Malbers children at runtime.
        /// </summary>
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
            Debug.Log($"[LabShaft] Shaft placed in Game at {ShaftOrigin}.");
        }

        #region Tier math

        static float TierY(int tier) => tier * TierStep;

        /// <summary>Odd tiers sit on the north (+z) side, even tiers on the south.</summary>
        static bool IsNorth(int tier) => (tier % 2) == 1;

        static float PlatformCenterZ(int tier) =>
            IsNorth(tier) ? Half - PlatformDepth * 0.5f : -(Half - PlatformDepth * 0.5f);

        /// <summary>Edge of the platform that faces the open core.</summary>
        static float PlatformInnerZ(int tier) =>
            IsNorth(tier) ? Half - PlatformDepth : -(Half - PlatformDepth);

        static float PlatformTopY(int tier) => TierY(tier) + PlatformThickness * 0.5f;

        static float RailGapX(int tier) => (tier % 2 == 0) ? 2.5f : -2.5f;

        static bool IsVentTier(int tier) => tier % 3 == 0;

        static float VentX(int tier) => IsNorth(tier) ? 2.2f : -2.2f;

        #endregion

        #region Shell

        static void BuildShell(Transform root)
        {
            var shell = Group(root, "Shell");
            float outer = Half + WallThickness;

            Solid(shell, "Floor", new Vector3(0f, -WallThickness * 0.5f, 0f),
                new Vector3(outer * 2f, WallThickness, outer * 2f), _platformMat);

            Solid(shell, "Roof", new Vector3(0f, WallTop + WallThickness * 0.5f, 0f),
                new Vector3(outer * 2f, WallThickness, outer * 2f), _wallMat);

            var northHoles = new List<Rect>();
            var southHoles = new List<Rect>();
            for (int tier = 1; tier <= Tiers; tier++)
            {
                if (!IsVentTier(tier))
                    continue;

                var hole = new Rect(
                    VentX(tier) - VentWidth * 0.5f,
                    PlatformTopY(tier),
                    VentWidth,
                    VentHeight);

                if (IsNorth(tier))
                    northHoles.Add(hole);
                else
                    southHoles.Add(hole);
            }

            var doorHole = new Rect(-DoorWidth * 0.5f, 0f, DoorWidth, DoorHeight);

            BuildWall(shell, "Wall North", true, Half + WallThickness * 0.5f, -outer, outer, northHoles);
            BuildWall(shell, "Wall South", true, -(Half + WallThickness * 0.5f), -outer, outer, southHoles);
            BuildWall(shell, "Wall East", false, Half + WallThickness * 0.5f, -outer, outer, null);
            BuildWall(shell, "Wall West", false, -(Half + WallThickness * 0.5f), -outer, outer,
                new List<Rect> { doorHole });

            // Apron outside the ground door so the approach is solid whatever the terrain does.
            Solid(shell, "Door Apron", new Vector3(-(outer + 1.5f), -0.15f, 0f),
                new Vector3(3f, 0.3f, DoorWidth + 1.5f), _platformMat);

            // Balcony outside the top vent, otherwise the exit is a 60 m drop.
            Solid(shell, "Summit Balcony",
                new Vector3(VentX(Tiers), PlatformTopY(Tiers) - 0.15f, -(outer + VentDepth + 1.5f)),
                new Vector3(4f, 0.3f, 3f), _platformMat);

            BuildLighting(root);
        }

        /// <summary>The shaft is sealed, so it needs its own lights to be playable.</summary>
        static void BuildLighting(Transform root)
        {
            var group = Group(root, "Lighting");

            for (int tier = 0; tier <= Tiers; tier += 3)
            {
                var go = new GameObject($"Lamp y={TierY(tier):0}");
                go.transform.SetParent(group, false);
                go.transform.localPosition = new Vector3(0f, TierY(tier) + TierStep * 0.5f, 0f);

                var light = go.AddComponent<Light>();
                light.type = LightType.Point;
                light.range = 16f;
                light.intensity = 1.5f;
                light.color = new Color(0.88f, 0.94f, 1f);
                light.shadows = LightShadows.None;
            }
        }

        /// <summary>
        /// Emits a wall as solid segments around its holes. When spansX is true the wall runs
        /// along X with its normal on Z, otherwise it runs along Z with its normal on X.
        /// </summary>
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
                    _platformMat);

                BuildRailing(tierGroup, tier);
            }
        }

        /// <summary>Extra railing gaps where a ladder, climb or spring launch meets this platform.</summary>
        static float[] ExtraGapX(int tier) => tier switch
        {
            3 => new[] { 3.6f },          // ladder east base
            4 => new[] { -3.6f },         // ladder west base
            5 => new[] { 3.6f },          // ladder east arrival
            6 => new[] { -3.6f, 0f },     // ladder west arrival + spring pad lane
            9 => new[] { 4.3f },          // climbable base
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

            // Keep the stair lane clear where a flight lands on or leaves this platform.
            if (tier <= StairBandTiers)
                gaps.Add(new Vector2(StairLaneX - StairWidth * 0.5f, StairLaneX + StairWidth * 0.5f));

            var extras = ExtraGapX(tier);
            if (extras != null)
            {
                foreach (float x in extras)
                    gaps.Add(new Vector2(x - 1f, x + 1f));
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
                float t = posts == 1 ? 0.5f : i / (float)(posts - 1);
                float x = Mathf.Lerp(xStart + 0.05f, xEnd - 0.05f, t);
                Solid(parent, "Post", new Vector3(x, baseY + RailHeight * 0.5f, z),
                    new Vector3(0.08f, RailHeight, 0.08f), _accentMat, collider: false);
            }

            // One invisible blocker per run so nobody slips between the rails.
            Blocker(parent, "Rail Blocker", new Vector3(xCenter, baseY + RailHeight * 0.5f, z),
                new Vector3(length, RailHeight, 0.12f));
        }

        #endregion

        #region Stairs

        static void BuildStairs(Transform root)
        {
            var group = Group(root, "Stairs");

            for (int tier = 0; tier < StairBandTiers; tier++)
                BuildFlight(group, tier);
        }

        /// <summary>One switchback flight climbing from tier to tier + 1 in the west lane.</summary>
        static void BuildFlight(Transform parent, int tier)
        {
            int next = tier + 1;
            var group = Group(parent, $"Flight {next}");

            float startY = tier == 0 ? 0f : PlatformTopY(tier);
            float endY = PlatformTopY(next);

            // Start near the outer wall of the lower level, finish at the upper platform lip.
            float startZ = IsNorth(tier) ? Half - 0.2f : -(Half - 0.2f);
            float endZ = PlatformInnerZ(next);

            float rise = endY - startY;
            float run = endZ - startZ;
            int steps = Mathf.Max(8, Mathf.RoundToInt(rise / 0.2f));
            float stepRise = rise / steps;
            float stepRun = run / steps;

            for (int i = 0; i < steps; i++)
            {
                float treadY = startY + (i + 1) * stepRise;
                float treadZ = startZ + (i + 0.5f) * stepRun;

                Solid(group, "Step",
                    new Vector3(StairLaneX, treadY - stepRise * 0.5f, treadZ),
                    new Vector3(StairWidth, stepRise, Mathf.Abs(stepRun) * 1.05f),
                    _platformMat,
                    tag: "Stair");
            }

            // Smooth ramp over the nosings: the FPS capsule has no step offset and Steve
            // keeps a clean ground normal instead of one per tread.
            var from = new Vector3(StairLaneX, startY + 0.02f, startZ);
            var to = new Vector3(StairLaneX, endY + 0.02f, endZ);
            var dir = (to - from).normalized;

            Blocker(group, "Ramp Collider",
                (from + to) * 0.5f,
                new Vector3(StairWidth, 0.2f, Vector3.Distance(from, to)),
                Quaternion.LookRotation(dir, Vector3.up));
        }

        #endregion

        #region Step pads

        static void BuildStepPads(Transform root)
        {
            var group = Group(root, "Step Pads");
            int pads = Mathf.Max(1, Mathf.CeilToInt(TierStep / StepRise) - 1);

            // The stair band already has a route, so pads start above it.
            for (int tier = StairBandTiers; tier < Tiers; tier++)
            {
                int next = tier + 1;

                float fromY = PlatformTopY(tier);
                float toY = PlatformTopY(next);
                float fromZ = PlatformInnerZ(tier);
                float toZ = PlatformInnerZ(next);
                float x = RailGapX(next);

                for (int i = 1; i <= pads; i++)
                {
                    float t = i / (float)(pads + 1);

                    Solid(group, $"Pad {next}-{i}",
                        new Vector3(x, Mathf.Lerp(fromY, toY, t), Mathf.Lerp(fromZ, toZ, t)),
                        new Vector3(1.6f, 0.3f, 1.6f),
                        _padMat);
                }
            }
        }

        #endregion

        #region Vents

        static void BuildVents(Transform root)
        {
            var group = Group(root, "Vents");
            var crouchStance = AssetDatabase.LoadAssetAtPath<StanceID>(CrouchStancePath);
            if (crouchStance == null)
                Debug.LogWarning("[LabShaft] Crouch StanceID not found; crawl zones will be inert.");

            for (int tier = 1; tier <= Tiers; tier++)
            {
                if (!IsVentTier(tier))
                    continue;

                BuildVent(group, tier, crouchStance);
            }
        }

        static void BuildVent(Transform parent, int tier, StanceID crouchStance)
        {
            var group = Group(parent, $"Vent y={TierY(tier):0}");

            float sign = IsNorth(tier) ? 1f : -1f;
            float x = VentX(tier);
            float floorY = PlatformTopY(tier);
            float innerFace = Half + WallThickness;
            float tunnelCenter = sign * (innerFace + VentDepth * 0.5f);
            bool isTopExit = tier == Tiers;

            Solid(group, "Tunnel Floor",
                new Vector3(x, floorY - 0.15f, tunnelCenter),
                new Vector3(VentWidth + 0.4f, 0.3f, VentDepth), _platformMat);

            Solid(group, "Tunnel Ceiling",
                new Vector3(x, floorY + VentHeight + 0.15f, tunnelCenter),
                new Vector3(VentWidth + 0.4f, 0.3f, VentDepth), _wallMat);

            for (int side = -1; side <= 1; side += 2)
            {
                Solid(group, "Tunnel Side",
                    new Vector3(x + side * (VentWidth * 0.5f + 0.15f), floorY + VentHeight * 0.5f, tunnelCenter),
                    new Vector3(0.3f, VentHeight + 0.3f, VentDepth), _wallMat);
            }

            if (!isTopExit)
            {
                Solid(group, "Tunnel Cap",
                    new Vector3(x, floorY + VentHeight * 0.5f, sign * (innerFace + VentDepth + 0.15f)),
                    new Vector3(VentWidth + 0.6f, VentHeight + 0.6f, 0.3f), _wallMat);
            }

            var crawl = Blocker(group, "Crawl Zone",
                new Vector3(x, floorY + VentHeight * 0.5f, sign * (Half + WallThickness * 0.5f)),
                new Vector3(VentWidth, VentHeight, WallThickness + 1.2f));

            var collider = crawl.GetComponent<BoxCollider>();
            collider.isTrigger = true;

            var zone = crawl.AddComponent<SteveCrawlZone>();
            if (crouchStance != null)
            {
                var so = new SerializedObject(zone);
                so.FindProperty("crouchStance").objectReferenceValue = crouchStance;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        #endregion

        #region Parkour props

        static void PlaceProps(Transform root)
        {
            var group = Group(root, "Props");

            // Band 2 (y 15-30): ladders skip a tier, so they connect same-side platforms.
            PlaceLadder(group, "Ladder East", 3, 3.6f);
            PlaceLadder(group, "Ladder West", 4, -3.6f);

            // Band 3 (y 30-45): spring pad launch, then a wall run across the core.
            PlaceProp(group, SpringPadPath, "Spring Pad",
                new Vector3(0f, PlatformTopY(6) + 0.05f, PlatformCenterZ(6)),
                Quaternion.identity);

            PlaceWallRun(group, "Wall Run East", new Vector3(Half - 0.3f, TierY(7) + TierStep * 0.5f, 0f));
            PlaceWallRun(group, "Wall Run West", new Vector3(-(Half - 0.3f), TierY(8) + TierStep * 0.5f, 0f));

            // Band 4 (y 45-60): free climb onto a ledge, then the moving platform.
            PlaceClimbable(group, 9);

            PlaceProp(group, MovingPlatformPath, "Moving Platform",
                new Vector3(0f, PlatformTopY(10) + 0.2f, 0f),
                Quaternion.identity);
        }

        /// <summary>
        /// Hangs the ladder on the inner face of the platform two tiers up (same side),
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

            // Two tiers of rise, because consecutive platforms sit on opposite walls.
            const float stepDistance = 0.5f;
            int steps = Mathf.RoundToInt(TierStep * 2f / stepDistance);

            ladder.StepDistance = stepDistance;
            ladder.Steps = steps;

            // CreateSteps indexes the existing list, so only rebuild when it is intact.
            bool stepsUsable = ladder.stepsObjects != null
                && ladder.stepsObjects.Count > 0
                && !ladder.stepsObjects.Exists(step => step == null);

            if (stepsUsable)
                ladder.CreateSteps(steps);

            EditorUtility.SetDirty(ladder);

            // MLadder measures from its own transform, which sits under the prefab root.
            float ladderZ = PlatformInnerZ(fromTier + 2) - faceSign * 0.35f;
            AlignChildTo(go, ladder.transform,
                new Vector3(x, PlatformTopY(fromTier), ladderZ));
        }

        static void PlaceClimbable(Transform parent, int fromTier)
        {
            var go = PlaceProp(parent, ClimbablePath, "Climbable", Vector3.zero,
                Quaternion.Euler(0f, -90f, 0f));
            if (go == null)
                return;

            // The climb surface child carries a local offset, so seat it by the surface itself.
            var surface = FindByTag(go.transform, "Climb");
            if (surface != null)
            {
                AlignChildTo(go, surface,
                    new Vector3(Half - 0.45f, PlatformTopY(fromTier) + TierStep * 0.5f, 0f));
            }

            Solid(parent, "Climb Ledge",
                new Vector3(Half - 1.7f, PlatformTopY(fromTier + 1), 0f),
                new Vector3(2.4f, 0.3f, 2.4f), _padMat);
        }

        /// <summary>Shifts a prop root so one of its children lands on the wanted shaft-local point.</summary>
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

        static void PlaceWallRun(Transform parent, string name, Vector3 position)
        {
            var go = PlaceProp(parent, WallRunPath, name, position, Quaternion.identity);
            if (go == null)
                return;

            // Source prefab is a unit cube scaled into a huge slab; retrim it to the shaft.
            go.transform.localScale = new Vector3(0.4f, TierStep, Interior - 1f);
        }

        static GameObject PlaceProp(Transform parent, string path, string name, Vector3 position, Quaternion rotation)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                Debug.LogWarning($"[LabShaft] Prop missing, skipped: {path}");
                return null;
            }

            // Instantiate into the target scene first: the Transform overload is unreliable
            // for preview scenes.
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

            // One per 15 m band, parked clear of the jump lanes and the stair lane.
            PlaceTrap(group, "Spike Floor Row B", 2, 0f);
            PlaceTrap(group, "Spiked Roller B", 5, 0f);
            PlaceTrap(group, "Moving Saw B", 8, -3.2f);
            PlaceTrap(group, "Pendulum Blade B", 11, 3.2f);
        }

        static void PlaceTrap(Transform parent, string prefabName, int tier, float x)
        {
            PlaceProp(parent, TrapDir + prefabName + ".prefab", prefabName,
                new Vector3(x, PlatformTopY(tier), PlatformCenterZ(tier)),
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

            // Steve-only shaft: the FPS controller cannot use the ladders, climb or wall runs.
            Trigger(group, "Enter Third Person Trigger",
                PlayerModeSwitchTrigger.SwitchMode.EnterThirdPerson,
                new Vector3(-(Half - 1.2f), DoorHeight * 0.5f, 0f),
                new Vector3(1.6f, DoorHeight - 0.4f, DoorWidth - 0.2f),
                green);

            // Summit exit, at the far end of the top vent.
            Trigger(group, "Exit To First Person Trigger",
                PlayerModeSwitchTrigger.SwitchMode.ExitToFirstPerson,
                new Vector3(VentX(Tiers), PlatformTopY(Tiers) + VentHeight * 0.5f, -(outer + VentDepth - 0.5f)),
                new Vector3(VentWidth * 0.9f, VentHeight * 0.9f, 0.8f),
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

        static GameObject Blocker(Transform parent, string name, Vector3 position, Vector3 size) =>
            Blocker(parent, name, position, size, Quaternion.identity);

        static GameObject Blocker(Transform parent, string name, Vector3 position, Vector3 size,
            Quaternion rotation)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localRotation = rotation;

            var box = go.AddComponent<BoxCollider>();
            box.size = size;
            return go;
        }

        static void EnsureMaterials()
        {
            _wallMat = EnsureMaterial("LabWall", new Color(0.78f, 0.79f, 0.81f), 0.25f, 0.05f);
            _platformMat = EnsureMaterial("LabPlatform", new Color(0.55f, 0.57f, 0.60f), 0.35f, 0.45f);
            _accentMat = EnsureMaterial("LabAccent", new Color(0.86f, 0.87f, 0.90f), 0.6f, 0.7f);
            _padMat = EnsureMaterial("LabStepPad", new Color(0.85f, 0.68f, 0.13f), 0.4f, 0.2f);
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
