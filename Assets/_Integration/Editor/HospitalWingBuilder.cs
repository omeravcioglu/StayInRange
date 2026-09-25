#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CollarCali.EditorTools
{
    /// <summary>
    /// Builds a hospital wing from an ASCII map using the Hivemind Horror Hospital modular pieces.
    ///
    /// Edit the MAP below and re-run Tools > CollarCali > Build Hospital Wing. Each character is
    /// one 2m cell on the pack's grid:
    ///
    ///   .   corridor floor
    ///   D   doorway - a corridor cell whose edges open a door into any adjacent room or the void
    ///   A-Z room floor; the letter is the room's identity, and RoomTypes maps letters to a type
    ///   ' ' void
    ///
    /// Walls are not drawn. They are derived: every edge between two cells with different codes
    /// gets a wall, unless one side is a doorway, in which case it gets a door. That means you
    /// never have to keep a wall map in sync with a floor map - just paint rooms.
    /// </summary>
    public static class HospitalWingBuilder
    {
        // ------------------------------------------------------------------------------------
        // LAYOUT - north is the top row. Columns map to +x, rows to +z.
        // ------------------------------------------------------------------------------------

        static readonly string[] Map =
        {
            //         1111111111222 2
            // 1234567890123456789012 3
            "AAAAAABBBBBBSSSSSS.DCCCC",   // z7  wards A,B | surgery S | dead-end corridor | storage C
            "AAAAAABBBBBBSSSSSS..CCCC",   // z6
            "AAAAAABBBBBBSSSSSS..CCCC",   // z5
            "AAAAAABBBBBBSSSSSS..CCCC",   // z4
            "D......D......D..D....D.",   // z3  main corridor - D marks each doorway
            "EEEGGGGGGTTTTTTHHHHHHFFF",   // z2  storage E | ward G | surgery T | ward H | storage F
            "EEEGGGGGGTTTTTTHHHHHHFFF",   // z1
            "EEEGGGGGGTTTTTTHHHHHHFFF",   // z0  <- this row sits on the existing slab's south edge
        };

        enum RoomType { Corridor, Ward, Surgery, Storage }

        static readonly Dictionary<char, RoomType> RoomTypes = new Dictionary<char, RoomType>
        {
            { 'A', RoomType.Ward }, { 'B', RoomType.Ward }, { 'G', RoomType.Ward }, { 'H', RoomType.Ward },
            { 'S', RoomType.Surgery }, { 'T', RoomType.Surgery },
            { 'C', RoomType.Storage }, { 'E', RoomType.Storage }, { 'F', RoomType.Storage },
        };

        /// <summary>
        /// World position of map cell (0,0)'s centre. Matches the existing slab, whose tiles sit
        /// at x = 2k - 0.6, z = 2k - 0.48 - so the generated floor lands exactly on top of it.
        /// </summary>
        static readonly Vector3 Origin = new Vector3(-0.6f, 0.03f, -0.48f);
        const float Cell = 2f;

        const string ParentName = "HospitalWing (Generated)";
        const int Seed = 1337;

        // ------------------------------------------------------------------------------------
        // PREFABS - the same HDRP-folder pieces the existing hospital already uses.
        // ------------------------------------------------------------------------------------

        const string PrefabRoot = "Assets/Hivemind/HorrorHospital/HDRP(Default)/Art/Prefabs/";

        const string Floor = "SM_Modular_Floor_01a";
        const string Wall = "SM_Modular_Wall_01a";
        const string Ceiling = "SM_Modular_Ceiling_01a";
        const string CorridorLight = "SM_Fluoresent_Light_Tube_01a";
        const string RoomLight = "SM_Fluoresent_Light_Tube_01b";

        // A doorway is a frame (the wall piece with the hole) plus a leaf.
        const string RoomDoorFrame = "SM_Room_Door_Frame_3";
        const string RoomDoorLeaf = "SM_Room_Door_01a";
        const string HallDoorFrame = "SM_Hallway_Door_Frame2";
        const string HallDoorLeaf = "SM_Hallway_Door_02a";

        // Dressing
        const string Bed = "SM_Hospital_Bed_NN_01a";
        const string IvStand = "SM_IV_Stand_01a";
        const string SteelCabinet = "SM_Steel_Cabinet_01a";
        const string ToolCabinet = "SM_Tool_Cabinet_01a";
        const string SurgicalTable = "SM_Surgical_Table_01a";
        const string Electrosurgical = "SM_Electrosurgical_Unit_01a";
        const string Tray1 = "SM_tray1";
        const string Tray2 = "SM_tray2";
        const string PillA = "SM_Pill_Bottle_01a";
        const string PillB = "SM_Pill_Bottle_01b";
        const string Rag = "SM_Rag_01b";
        const string DeskLamp = "SM_Desk_Lamp_01a";

        static readonly string[] SurgicalLamp =
        {
            "SM_Surgical_Lamp_01a_piece1", "SM_Surgical_Lamp_01a_piece2",
            "SM_Surgical_Lamp_01a_piece3", "SM_Surgical_Lamp_01a_piece4",
            "SM_Surgical_Lamp_01a_light",
        };

        // ------------------------------------------------------------------------------------

        static readonly Dictionary<string, GameObject> _prefabs = new Dictionary<string, GameObject>();
        static readonly Dictionary<string, Bounds> _bounds = new Dictionary<string, Bounds>();
        static Transform _parent;
        static System.Random _rng;
        static float _wallHeight;

        struct Room
        {
            public char Code;
            public RoomType Type;
            public int MinX, MaxX, MinZ, MaxZ;
            public List<(int x, int z)> Cells;
            public HashSet<(int x, int z)> DoorCells;
        }

        [MenuItem("Tools/CollarCali/Build Hospital Wing")]
        public static void Build()
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || scene.name != "Game")
            {
                if (!EditorUtility.DisplayDialog("Build Hospital Wing",
                        "The active scene is '" + scene.name + "', not Game. Build here anyway?",
                        "Build", "Cancel"))
                    return;
            }

            _prefabs.Clear();
            _bounds.Clear();
            _rng = new System.Random(Seed);

            if (!ValidateMap())
                return;

            var existing = GameObject.Find(ParentName);
            if (existing != null)
                Undo.DestroyObjectImmediate(existing);

            _parent = new GameObject(ParentName).transform;
            Undo.RegisterCreatedObjectUndo(_parent.gameObject, "Build Hospital Wing");

            _wallHeight = Measure(Wall).size.y;

            var existingFloors = CollectExistingFloorCells();
            int floors = 0, walls = 0, doors = 0, ceilings = 0;

            // 1. floors + ceilings
            foreach (var (x, z, code) in Cells())
            {
                if (!existingFloors.Contains((x, z)))
                {
                    PlaceOnFloor(Floor, CellCentre(x, z), 0f, Group("Floors"));
                    floors++;
                }
                PlaceCeiling(x, z);
                ceilings++;
            }

            // 2. walls and doors along every edge
            foreach (var (x, z, code) in Cells())
            {
                // East edge (between this cell and x+1) and north edge (z+1). Every interior edge
                // is visited exactly once this way; void neighbours are visited from the cell side.
                walls += Edge(x, z, x + 1, z, isEastEdge: true, ref doors);
                walls += Edge(x, z, x, z + 1, isEastEdge: false, ref doors);
                // West and south edges only need handling when the neighbour is void, because a
                // non-void neighbour handles them as its own east/north edge.
                if (CodeAt(x - 1, z) == ' ')
                    walls += Edge(x, z, x - 1, z, isEastEdge: true, ref doors);
                if (CodeAt(x, z - 1) == ' ')
                    walls += Edge(x, z, x, z - 1, isEastEdge: false, ref doors);
            }

            // 3. lights and dressing
            var rooms = CollectRooms();
            LightCorridors();
            foreach (var room in rooms)
                Dress(room);

            SetStaticRecursive(_parent.gameObject);
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log($"[CollarCali] Hospital wing built: {floors} floors, {ceilings} ceilings, " +
                      $"{walls} walls, {doors} doors, {rooms.Count} rooms. " +
                      "Re-bake the NavMesh so zombies can path through the new rooms.");
        }

        #region Map

        static int Width => Map[0].Length;
        static int Height => Map.Length;

        static bool ValidateMap()
        {
            for (int i = 0; i < Map.Length; i++)
            {
                if (Map[i].Length != Width)
                {
                    Debug.LogError($"[CollarCali] Map row {i} is {Map[i].Length} wide, expected {Width}.");
                    return false;
                }
            }

            foreach (var (x, z, code) in Cells())
            {
                if (code != '.' && code != 'D' && !RoomTypes.ContainsKey(code))
                {
                    Debug.LogError($"[CollarCali] Map cell ({x},{z}) has code '{code}' with no entry in RoomTypes.");
                    return false;
                }
            }
            return true;
        }

        /// <summary>Row 0 of the array is the NORTH edge, so z counts down the rows.</summary>
        static char CodeAt(int x, int z)
        {
            if (x < 0 || x >= Width || z < 0 || z >= Height)
                return ' ';
            return Map[Height - 1 - z][x];
        }

        static IEnumerable<(int x, int z, char code)> Cells()
        {
            for (int z = 0; z < Height; z++)
                for (int x = 0; x < Width; x++)
                {
                    char c = CodeAt(x, z);
                    if (c != ' ')
                        yield return (x, z, c);
                }
        }

        static bool IsCorridor(char c) => c == '.' || c == 'D';

        static Vector3 CellCentre(int x, int z) =>
            Origin + new Vector3(x * Cell, 0f, z * Cell);

        static List<Room> CollectRooms()
        {
            var byCode = new Dictionary<char, Room>();

            foreach (var (x, z, code) in Cells())
            {
                if (IsCorridor(code))
                    continue;

                if (!byCode.TryGetValue(code, out var room))
                {
                    room = new Room
                    {
                        Code = code, Type = RoomTypes[code],
                        MinX = x, MaxX = x, MinZ = z, MaxZ = z,
                        Cells = new List<(int, int)>(),
                        DoorCells = new HashSet<(int, int)>(),
                    };
                }

                room.MinX = Mathf.Min(room.MinX, x); room.MaxX = Mathf.Max(room.MaxX, x);
                room.MinZ = Mathf.Min(room.MinZ, z); room.MaxZ = Mathf.Max(room.MaxZ, z);
                room.Cells.Add((x, z));

                // A room cell next to a doorway is where the door opens; props stay out of it.
                if (CodeAt(x + 1, z) == 'D' || CodeAt(x - 1, z) == 'D' ||
                    CodeAt(x, z + 1) == 'D' || CodeAt(x, z - 1) == 'D')
                    room.DoorCells.Add((x, z));

                byCode[code] = room;
            }

            return new List<Room>(byCode.Values);
        }

        /// <summary>
        /// The slab already has floor tiles; laying another on top would z-fight. Anything of
        /// the floor prefab within half a cell of a map cell counts as already present.
        /// </summary>
        static HashSet<(int, int)> CollectExistingFloorCells()
        {
            var cells = new HashSet<(int, int)>();
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t == null || t.parent == _parent || !t.name.StartsWith(Floor))
                    continue;
                if (PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject) == null)
                    continue;

                var local = t.position - Origin;
                int cx = Mathf.RoundToInt(local.x / Cell);
                int cz = Mathf.RoundToInt(local.z / Cell);
                if (Mathf.Abs(local.x - cx * Cell) < Cell * 0.5f && Mathf.Abs(local.z - cz * Cell) < Cell * 0.5f &&
                    Mathf.Abs(local.y) < 1f)
                    cells.Add((cx, cz));
            }
            return cells;
        }

        #endregion

        #region Edges

        /// <summary>
        /// Decides what sits on the edge between cell A and cell B and places it. Returns the
        /// number of walls placed; doors are counted through the ref.
        /// </summary>
        static int Edge(int ax, int az, int bx, int bz, bool isEastEdge, ref int doors)
        {
            char a = CodeAt(ax, az);
            char b = CodeAt(bx, bz);

            if (a == b)
                return 0;
            if (IsCorridor(a) && IsCorridor(b))
                return 0;

            // Midpoint of the shared edge, at floor height.
            var mid = (CellCentre(ax, az) + CellCentre(bx, bz)) * 0.5f;
            // A wall runs along the edge: an east edge runs north-south (along z).
            float yaw = isEastEdge ? 90f : 0f;
            // Face the piece toward whichever side is walkable so door leaves open inward.
            bool roomIsA = !IsCorridor(a) && a != ' ';
            if (roomIsA == (isEastEdge ? bx > ax : bz > az))
                yaw += 180f;

            bool doorway = a == 'D' || b == 'D';
            if (doorway)
            {
                bool exterior = a == ' ' || b == ' ';
                string frame = exterior ? HallDoorFrame : RoomDoorFrame;
                string leaf = exterior ? HallDoorLeaf : RoomDoorLeaf;
                PlaceAlongEdge(frame, mid, yaw, Group("Doors"));
                PlaceAlongEdge(leaf, mid, yaw, Group("Doors"));
                doors++;
                return 0;
            }

            PlaceAlongEdge(Wall, mid, yaw, Group("Walls"));
            return 1;
        }

        #endregion

        #region Placement

        static Transform Group(string name)
        {
            var existing = _parent.Find(name);
            if (existing != null)
                return existing;
            var go = new GameObject(name);
            go.transform.SetParent(_parent, false);
            return go.transform;
        }

        static GameObject Load(string name)
        {
            if (_prefabs.TryGetValue(name, out var cached))
                return cached;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabRoot + name + ".prefab");
            if (prefab == null)
                Debug.LogWarning("[CollarCali] Hospital prefab missing: " + name);
            _prefabs[name] = prefab;
            return prefab;
        }

        /// <summary>
        /// Renderer bounds of the prefab at identity, cached. Everything is positioned from these
        /// rather than from pivots, so it does not matter where the pack's authors put them.
        /// </summary>
        static Bounds Measure(string name)
        {
            if (_bounds.TryGetValue(name, out var cached))
                return cached;

            var prefab = Load(name);
            if (prefab == null)
                return default;

            var temp = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            temp.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var renderers = temp.GetComponentsInChildren<Renderer>(true);

            Bounds b = default;
            bool any = false;
            foreach (var r in renderers)
            {
                if (r == null) continue;
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            Object.DestroyImmediate(temp);

            _bounds[name] = b;
            return b;
        }

        static GameObject Spawn(string name, Transform parent)
        {
            var prefab = Load(name);
            if (prefab == null)
                return null;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            return go;
        }

        /// <summary>Place so the bottom of the bounds sits at floor level, centred on XZ.</summary>
        static GameObject PlaceOnFloor(string name, Vector3 centre, float yaw, Transform parent, float lift = 0f)
        {
            var go = Spawn(name, parent);
            if (go == null)
                return null;

            var b = Measure(name);
            var rot = Quaternion.Euler(0f, yaw, 0f);
            // The bounds were measured at identity; rotate their centre offset with the piece.
            var offset = rot * new Vector3(b.center.x, 0f, b.center.z);
            go.transform.SetPositionAndRotation(
                new Vector3(centre.x - offset.x, Origin.y - b.min.y + lift, centre.z - offset.z), rot);
            return go;
        }

        /// <summary>
        /// Walls and doors: rotated so their long horizontal axis follows the edge, then seated
        /// on the floor with their centre on the edge midpoint.
        /// </summary>
        static GameObject PlaceAlongEdge(string name, Vector3 mid, float yaw, Transform parent)
        {
            var b = Measure(name);
            // If the piece is authored long along z rather than x, turn it a quarter so the
            // caller's yaw (which assumes long-along-x for a north edge) still holds.
            if (b.size.z > b.size.x)
                yaw += 90f;
            return PlaceOnFloor(name, mid, yaw, parent);
        }

        static void PlaceCeiling(int x, int z)
        {
            var go = Spawn(Ceiling, Group("Ceilings"));
            if (go == null)
                return;
            var b = Measure(Ceiling);
            var c = CellCentre(x, z);
            // Underside of the ceiling tile at the top of the wall.
            go.transform.SetPositionAndRotation(
                new Vector3(c.x - b.center.x, Origin.y + _wallHeight - b.min.y, c.z - b.center.z),
                Quaternion.identity);
        }

        static void PlaceUnderCeiling(string name, Vector3 centre, float yaw, Transform parent)
        {
            var go = Spawn(name, parent);
            if (go == null)
                return;
            var b = Measure(name);
            var rot = Quaternion.Euler(0f, yaw, 0f);
            var offset = rot * new Vector3(b.center.x, 0f, b.center.z);
            // Top of the fixture kisses the ceiling underside.
            go.transform.SetPositionAndRotation(
                new Vector3(centre.x - offset.x, Origin.y + _wallHeight - b.max.y - 0.02f, centre.z - offset.z), rot);
        }

        /// <summary>
        /// Multi-prefab assemblies (the surgical lamp is five pieces sharing one origin) must all
        /// receive the same transform. Placing each piece by its own bounds would pull the parts
        /// apart, so the union of their bounds decides the pose and every piece gets it verbatim.
        /// </summary>
        static void PlaceAssemblyUnderCeiling(string[] pieces, Vector3 centre, float yaw, Transform parent)
        {
            Bounds union = default;
            bool any = false;
            foreach (var piece in pieces)
            {
                if (Load(piece) == null) continue;
                var b = Measure(piece);
                if (!any) { union = b; any = true; } else union.Encapsulate(b);
            }
            if (!any)
                return;

            var rot = Quaternion.Euler(0f, yaw, 0f);
            var offset = rot * new Vector3(union.center.x, 0f, union.center.z);
            var pos = new Vector3(centre.x - offset.x, Origin.y + _wallHeight - union.max.y - 0.02f, centre.z - offset.z);

            foreach (var piece in pieces)
            {
                var go = Spawn(piece, parent);
                if (go != null)
                    go.transform.SetPositionAndRotation(pos, rot);
            }
        }

        static void SetStaticRecursive(GameObject go)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
                GameObjectUtility.SetStaticEditorFlags(t.gameObject,
                    StaticEditorFlags.BatchingStatic | StaticEditorFlags.NavigationStatic |
                    StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic);
        }

        #endregion

        #region Lighting and dressing

        static void LightCorridors()
        {
            foreach (var (x, z, code) in Cells())
            {
                if (!IsCorridor(code))
                    continue;
                // One tube every third corridor cell, alternating so the dead-end stub gets one too.
                if ((x + z) % 3 != 0)
                    continue;
                bool alongX = IsCorridor(CodeAt(x + 1, z)) || IsCorridor(CodeAt(x - 1, z));
                PlaceUnderCeiling(CorridorLight, CellCentre(x, z), alongX ? 0f : 90f, Group("Lights"));
            }
        }

        static void Dress(Room room)
        {
            var group = Group("Props");
            var centre = RoomCentre(room);

            // One light per room, centred.
            PlaceUnderCeiling(RoomLight, centre, 0f, Group("Lights"));

            switch (room.Type)
            {
                case RoomType.Ward: DressWard(room, group); break;
                case RoomType.Surgery: DressSurgery(room, centre, group); break;
                case RoomType.Storage: DressStorage(room, group); break;
            }
        }

        static Vector3 RoomCentre(Room r) =>
            (CellCentre(r.MinX, r.MinZ) + CellCentre(r.MaxX, r.MaxZ)) * 0.5f;

        /// <summary>
        /// Cells against a given wall of the room that are not next to a door - the places furniture
        /// naturally goes. Side: 0 north, 1 east, 2 south, 3 west.
        /// </summary>
        static List<(int x, int z)> WallCells(Room r, int side)
        {
            var result = new List<(int, int)>();
            foreach (var c in r.Cells)
            {
                if (r.DoorCells.Contains(c))
                    continue;
                bool onWall = side switch
                {
                    0 => c.z == r.MaxZ,
                    1 => c.x == r.MaxX,
                    2 => c.z == r.MinZ,
                    _ => c.x == r.MinX,
                };
                if (onWall)
                    result.Add(c);
            }
            return result;
        }

        /// <summary>Yaw that faces a prop from the given wall into the room.</summary>
        static float FacingFromWall(int side) => side switch { 0 => 180f, 1 => 270f, 2 => 0f, _ => 90f };

        /// <summary>Nudged toward the wall the prop stands against so it sits flush rather than mid-cell.</summary>
        static Vector3 AgainstWall(int x, int z, int side, float depth)
        {
            var c = CellCentre(x, z);
            float push = Cell * 0.5f - depth * 0.5f - 0.05f;
            return side switch
            {
                0 => c + new Vector3(0f, 0f, push),
                1 => c + new Vector3(push, 0f, 0f),
                2 => c - new Vector3(0f, 0f, push),
                _ => c - new Vector3(push, 0f, 0f),
            };
        }

        static T Pick<T>(List<T> list)
        {
            var item = list[_rng.Next(list.Count)];
            list.Remove(item);
            return item;
        }

        static float Jitter(float range) => (float)(_rng.NextDouble() * 2.0 - 1.0) * range;

        static bool AllDoorsOnSouth(Room room)
        {
            if (room.DoorCells.Count == 0)
                return false;
            foreach (var c in room.DoorCells)
                if (c.z != room.MinZ)
                    return false;
            return true;
        }

        static void DressWard(Room room, Transform group)
        {
            // Beds line the wall furthest from the door, heads to the wall.
            int side = AllDoorsOnSouth(room) ? 0 : 2;
            var slots = WallCells(room, side);

            int beds = Mathf.Clamp(slots.Count / 2, 1, 3);
            var bedDepth = Measure(Bed).size.z;
            for (int i = 0; i < beds && slots.Count > 0; i++)
            {
                var cell = Pick(slots);
                PlaceOnFloor(Bed, AgainstWall(cell.x, cell.z, side, bedDepth), FacingFromWall(side), group);

                // An IV stand beside most beds.
                if (_rng.NextDouble() < 0.7)
                {
                    var iv = CellCentre(cell.x, cell.z) + new Vector3(Jitter(0.3f) + 0.9f, 0f, Jitter(0.3f));
                    PlaceOnFloor(IvStand, iv, Jitter(180f), group);
                }
            }

            // A cabinet on a side wall.
            var sideSlots = WallCells(room, 1);
            if (sideSlots.Count > 0)
            {
                var cell = Pick(sideSlots);
                PlaceOnFloor(SteelCabinet, AgainstWall(cell.x, cell.z, 1, Measure(SteelCabinet).size.z),
                    FacingFromWall(1), group);
            }

            ScatterDebris(room, group, 3);
        }

        static void DressSurgery(Room room, Vector3 centre, Transform group)
        {
            // Table dead centre, lamp assembly directly above it.
            PlaceOnFloor(SurgicalTable, centre, 90f, group);
            PlaceAssemblyUnderCeiling(SurgicalLamp, centre, 0f, group);

            // Machines and cabinets against whichever walls have no door.
            for (int side = 0; side < 4; side++)
            {
                var slots = WallCells(room, side);
                if (slots.Count == 0)
                    continue;

                var cell = Pick(slots);
                string prop = side % 2 == 0 ? Electrosurgical : ToolCabinet;
                PlaceOnFloor(prop, AgainstWall(cell.x, cell.z, side, Measure(prop).size.z), FacingFromWall(side), group);
            }

            // Trays near the table.
            PlaceOnFloor(Tray1, centre + new Vector3(1.4f, 0f, Jitter(0.6f)), Jitter(30f), group);
            PlaceOnFloor(Tray2, centre + new Vector3(-1.4f, 0f, Jitter(0.6f)), Jitter(30f), group);

            ScatterDebris(room, group, 2);
        }

        static void DressStorage(Room room, Transform group)
        {
            // Cabinets shoulder to shoulder along every wall without a door.
            for (int side = 0; side < 4; side++)
            {
                var slots = WallCells(room, side);
                float depth = Measure(SteelCabinet).size.z;
                foreach (var cell in slots)
                {
                    if (_rng.NextDouble() < 0.25)
                        continue;   // a gap here and there reads better than a solid ring
                    PlaceOnFloor(SteelCabinet, AgainstWall(cell.x, cell.z, side, depth), FacingFromWall(side), group);
                }
            }

            // Small clutter in the middle.
            var centre = RoomCentre(room);
            PlaceOnFloor(Tray1, centre + new Vector3(Jitter(0.6f), 0f, Jitter(0.6f)), Jitter(180f), group);
            for (int i = 0; i < 3; i++)
                PlaceOnFloor(_rng.NextDouble() < 0.5 ? PillA : PillB,
                    centre + new Vector3(Jitter(0.9f), 0f, Jitter(0.9f)), Jitter(180f), group);

            ScatterDebris(room, group, 2);
        }

        static void ScatterDebris(Room room, Transform group, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var cell = room.Cells[_rng.Next(room.Cells.Count)];
                var p = CellCentre(cell.x, cell.z) + new Vector3(Jitter(0.8f), 0f, Jitter(0.8f));
                PlaceOnFloor(Rag, p, Jitter(180f), group);
            }
        }

        #endregion
    }
}
#endif
