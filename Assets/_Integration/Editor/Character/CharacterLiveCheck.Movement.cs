#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unity.Cinemachine;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CollarCali.EditorTools
{
    public static partial class CharacterLiveCheck
    {
        /// <summary>
        /// -liveMovement: how the player moves, measured on the floor scene (the other checks are skipped).
        ///   - Sprint keeps the direction: running forward and then sprinting must not turn the body or
        ///     the camera around.
        ///   - Feet: how high the sole of the supporting foot is over the floor standing, moving and
        ///     sprinting. Below zero is sinking in, above is floating. Standing across a 12° and a 30° slope
        ///     both resting shoes lie on it. Running down a 20° ramp the resting soles must lie on it,
        ///     neither hovering over it nor shaking, and walking up 0.18 m steps no foot may jump a riser's
        ///     height from one frame to the next, nor FootGrounder lift one onto the next tread all at once, nor
        ///     the body be drawn popping up a riser (the frames around the worst of each go in the report).
        ///     Running up them, the planted shoes keep up with the treads instead of being left in them.
        ///     Over stairs as the levels have them - First_Parkour's staircase walked up and down, and a flight
        ///     whose treads are tilted a few degrees walked and run up and down - each foot is held to the same
        ///     limits from one frame to the next as up the level steps.
        ///     Sprinting across a floor tile thinner than a sprint plants its toes, no toe sinks into it.
        ///   - Air control: how far sideways a standing jump carries with the stick held sideways.
        ///   - Landing: how far the body slides once it lands a running jump with the stick let go in the
        ///     air, next to how far it slides stopping from a run on the ground.
        ///   - Smoothness: how evenly the body moves on screen while the camera follows it, and how often
        ///     its pose changes from one frame to the next. On a slow screen (30 fps: two physics steps in
        ///     most frames) the pose must still be shown between the same two steps as the body.
        ///   - Camera: a wall put between the camera and the player dissolves, and comes back once it is
        ///     out of the way, as it was (no property block left on it). So does what only grazes the view
        ///     of them - a ball, a pillar - and a crate behind them the camera looks up over, which must not
        ///     pull the camera in. ToonScapes stone dissolves in its own colour, mapped as the stone is.
        ///     Looking up as far as it goes, the camera stays above the floor, and inside a tunnel made in
        ///     one mesh, along it or turned towards a wall, and off the player's head by its right wall with
        ///     the view turned a little away from it; so it does in a tunnel of two such pieces joined behind
        ///     the player, and looking down from a heap lying on a tunnel's floor (and the tunnel never fades).
        ///     With its hand's sphere already touching the tunnel, the floor still stops it. A wall too big
        ///     to fade behind the player keeps the camera in front of it, and so does a hall as big made in one
        ///     mesh with its faces turned in, which its sweep does not meet. Walking down stairs with the view
        ///     level it neither goes into them nor pumps in and out, and against a chest-high block, looking
        ///     down over it, it stays out of the player's body. Neither the floor tile the player crouches on,
        ///     nor the wall they walk up against, nor the one they run along right beside them fades.
        ///   - With -liveStockRig, Malbers' stock camera rig waits in the scene switched off, as in
        ///     Game.unity: the player must film with the team's rig and leave that one off.
        /// What the checks stand on, run into or look past they build as they go, on parts of the floor
        /// the runs never reach.
        /// </summary>
        partial class Probe
        {
            enum MoveStep { Setup, Turn, Run, Sprint, SprintStop, FeetIdle, FeetMove, FeetSprint, FeetStop, SlopeGo, SlopeStand, SteepGo, SteepStand, SlopeBack, StopRun, StopWatch, AirRest, AirJump, SlideRest, SlideRun, SlideJump, TurnRest, TurnRun, TurnJump, HoldRest, HoldRun, HoldJump, CamSetup, CamWall, CamShot, CamClear, CamBack, RoundSetup, RoundBall, RoundPillar, StoneSetup, StoneBox, LookSetup, LookUp, CrateSetup, CrateWatch, BlockSetup, BlockLook, TunnelSetup, TunnelLook, TunnelMove, BigWallSetup, BigWallWatch, BigHallWatch, StairsSetup, StairsWalk, TileSetup, TileCrouch, TileStand, WallSetup, WallPush, SideSetup, SideRun, RampSetup, RampRun, StepsSetup, StepsWalk, StepsRunSetup, StepsRun, FlightSetup, FlightGo, TileSprintSetup, TileSprint, SlowSetup, SlowRun, Done }

            /// <summary>Running and then sprinting may turn the direction of travel this much at most (degrees).</summary>
            const float SprintTurnLimit = 30f;

            /// <summary>The supporting foot's sole may sit this far into or over the floor (m): median, and 90% of frames.</summary>
            const float SoleMedianLimit = 0.02f, SoleSpreadLimit = 0.04f;

            /// <summary>The slide after landing may be this long (m).</summary>
            const float LandingSlideLimit = 0.25f;

            /// <summary>Looking up, the camera may come this much nearer the floor than its ground collider's radius (m).</summary>
            const float LookUpSlack = 0.02f;

            /// <summary>The camera's pitch (down +) for the checks that set it: a little above the player, as players hold it.</summary>
            const float CheckPitch = 10f;

            /// <summary>
            /// The floor tile crouched on (m): First_Parkour's stone floor tiles are 4 m square and under 5 cm thick.
            /// The camera looks down on it a little more steeply (degrees), so the view to the player's legs meets
            /// the tile from above as well.
            /// </summary>
            const float TileSize = 4f, TileThickness = 0.05f, TilePitch = 15f;

            /// <summary>
            /// The wall walked up against (m): its height, how far in front of the player its face is, and how near the
            /// body's middle comes to it to stand against it (the body is 0.27 m round).
            /// </summary>
            const float FrontWallHeight = 3f, FrontWallAhead = 2f, AgainstWall = 0.45f;

            /// <summary>
            /// The wall run along right beside the player (m): its height and length, and how far its face is from the
            /// body's middle when the run starts - a hair more than the body's 0.27 m, as a player hugging a corridor wall.
            /// </summary>
            const float SideWallHeight = 3f, SideWallLength = 14f, SideWallGap = 0.3f;

            /// <summary>
            /// The ramp run down (degrees, m), where the run starts below its top edge, and the stretch of it that is
            /// measured: once the run is going, and short of the bottom.
            /// </summary>
            const float RampAngle = 20f, RampLength = 12f, RampWidth = 5f, RampThickness = 0.6f, RampStart = 1.5f, RampMeasureFrom = 3f, RampMeasureTo = 10.5f;

            /// <summary>A sole this close to the ground under it rests on it (m).</summary>
            const float RestingClearance = 0.05f;

            /// <summary>
            /// How far a resting sole may move from one frame to the next on the ramp, on average (m). A planted foot
            /// keeps still; put on the ground against the physics root instead of the drawn body, it rose a few
            /// centimetres with every physics step and sank back in between.
            /// </summary>
            const float RestingJitterLimit = 0.006f;

            /// <summary>
            /// How much a resting sole on the ramp may rise with the body drawn higher over its physics root (cm per
            /// cm). Put on the ground where the body is drawn it does not at all; against the physics root, one for one.
            /// It is only told when that height varies by RampLagSpread (m) or more.
            /// </summary>
            const float RampFollowLimit = 0.5f, RampLagSpread = 0.003f;

            /// <summary>The steps walked up (m); the last one is a landing.</summary>
            const float StepRiser = 0.18f, StepTread = 0.4f, StepLanding = 3f, StepWidth = 3f;
            const int StepCount = 5;

            /// <summary>The most a foot may jump from one frame to the next on the steps (m): half a riser. Unsmoothed, it jumped a whole one.</summary>
            const float StepPopLimit = 0.5f * StepRiser;

            /// <summary>
            /// The most the body may be drawn rising from one frame to the next on the steps (m). Malbers lifts the root
            /// up a riser by a share of what is left of it each physics step - a fifth with the Align Pos Lerp of 5 its
            /// Human ships with - so the body drawn between steps rises under 2 cm a frame at 144 Hz. At three
            /// fifths a step (Malbers' code default of 15) it would rise 11 cm of an 18 cm riser in 20 ms, about 4 cm a
            /// frame: the body popping up each step.
            /// </summary>
            const float StepBodyRiseLimit = 0.03f;

            /// <summary>
            /// The most FootGrounder may change a foot's own correction from one frame to the next on the steps (m). It
            /// eases a riser in - a walking shoe rises about 3 cm a frame at 144 Hz, and a planted one sinks back as
            /// fast as the drawn body climbs - instead of lifting the shoe the whole riser at once.
            /// </summary>
            const float StepCorrectionLimit = 0.08f;

            /// <summary>
            /// The same steps run up at Malbers' own running speed, from StepsRunUp (m) in front of them. The ground
            /// rises under a shoe sweeping up them faster than FootGrounder eases a lone edge in (Edge Max Speed), and
            /// its planted shoes have to keep up all the same: a planted shoe - within RestingClearance of the tread
            /// under it and moving along it slower than PlantedSpeed (m/s; a swinging one goes several times the run)
            /// - sits within SoleMedianLimit of the tread at the median, more than StepsRunDeepLimit (m) in it in no more
            /// than StepsRunDeepFrames frames, and never StepsRunDeepest (m) in. Held to Edge Max Speed alone, worked
            /// through for these steps, a running shoe's ground would lag 30 to 45 cm under the treads and the planted shoe
            /// be put that far into them. A run gives only 12 to 17 planted frames, too few for a percentile to be a share
            /// of them - interpolated between frames, the 5th lay most of the way to the second-lowest and the 10th between
            /// the second- and third-lowest - so frames are counted: a shoe just landed on the next tread, still easing up
            /// to it, may be that far in for one frame (the lowest read -3 to -5 cm from run to run), not for two.
            /// </summary>
            const float StepsRunUp = 3.5f, PlantedSpeed = 1.5f, StepsRunDeepLimit = 0.05f, StepsRunDeepest = 0.1f;
            const int StepsRunDeepFrames = 1;

            /// <summary>
            /// First_Parkour's interior staircase, as its prefab is: one non-convex MeshCollider made from its render mesh, 19
            /// treads of about 20 cm on 22 cm goings, six of them tilted 2 to 5.5° (one's two triangles 9° apart). In its own
            /// space (m) its treads run across x 0 to 1.22 (RealStairsMiddle their middle) and climb along -z, from its first
            /// riser at RealStairsFoot to the back of its top tread at RealStairsTop; its own upper floor lies RealStairsHigh up.
            /// It is put down at RealStairsSpot turned round, to climb along +z, and a landing as high is built on past its top.
            /// </summary>
            const string RealStairsPath = "Assets/A_StayInRange/Prefabs/Medieval_Castle_Prefabs/House/MC_Staircase_Interior_01.prefab";
            const float RealStairsMiddle = 0.61f, RealStairsFoot = 0.309f, RealStairsTop = -4.333f, RealStairsHigh = 4f, RealStairsLanding = 6f;

            /// <summary>
            /// A flight whose treads are tilted, as stone slabs and worn treads in the levels are: TiltTreads.Length steps of
            /// TiltRiser on TiltGoing (m), each tread turned about the middle of its top by its TiltTreads angle (degrees; nose
            /// down, below zero nose up), then a level landing TiltLanding deep, long enough to run down from.
            /// </summary>
            const float TiltRiser = 0.18f, TiltGoing = 0.3f, TiltLanding = 5f, TiltWidth = 3f;
            static readonly float[] TiltTreads = { 0f, 3f, 0f, 3f, 0f, -5f, 0f, 3f };

            /// <summary>Heel and toe over ground whose slopes are more than this far apart (degrees) stand on two slopes.</summary>
            const float TwoSlopes = 2f;

            /// <summary>
            /// The slow screen (frames a second). A frame's pose is shown between the same two physics steps as the body
            /// when it is off what the body's place calls for by at most PoseLagLimit of a step, and that has to hold in
            /// PoseMatchShare of the frames.
            /// </summary>
            const float SlowFps = 30f, PoseLagLimit = 0.25f, PoseMatchShare = 0.9f;

            /// <summary>
            /// The bones the slow screen's pose is judged by: the arms, which only the Animator poses. The legs are
            /// FootGrounder's after Update, and Malbers' look-at bends the neck and head after each step.
            /// </summary>
            static readonly HumanBodyBones[] ArmBones =
                { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm };

            /// <summary>
            /// The steeper slope stood across (degrees): as steep as the levels' walkable rock gets (Malbers slides from
            /// 40°). Across it, as across the 12° one, each resting shoe's sole has to lie on the slope: off it by at
            /// most SlopeLieLimit (degrees) at the median, which leaves the idle pose its own sway. With the tilt fading
            /// out from 24.5° it lay 16° off at 30°.
            /// </summary>
            const float SteepAngle = 30f, SlopeLieLimit = 7f;

            /// <summary>
            /// The floor tile sprinted across (m), after a run-up: as thin as First_Parkour's stone floor tiles. A sprint
            /// plants the toes up to 7 cm into the floor, through the tile's underside, and they have to be put on its
            /// top all the same: no shoe may be drawn more than SprintToeLimit into it (on the floor itself they are
            /// about a centimetre in at worst).
            /// </summary>
            const float SprintTileThickness = 0.04f, SprintTileWidth = 4f, SprintTileLength = 10f, SprintRunUp = 6f, SprintToeLimit = 0.03f;

            /// <summary>
            /// The round things put in the view (m): a ball as big as a boulder and a standing pillar, half way along the
            /// view, their surface RoundGap beside the line to the player's chest. Met first by their rounded side, away
            /// from that line, each still hides part of the body.
            /// </summary>
            const float RoundBallRadius = 1f, RoundPillarRadius = 0.5f, RoundPillarHeight = 3.5f, RoundGap = 0.05f;

            /// <summary>What is put in the view is judged from FadeSettle (s) after, and has to be faded in FadeShare of those frames.</summary>
            const float FadeSettle = 0.3f, FadeShare = 0.9f;

            /// <summary>
            /// The ToonScapes stone shader of the TSU rocks and stone pieces, the keyword the dissolve maps a copy of it
            /// with as the stone is mapped (in object space, from three sides), and the stone's tiling on the rocks.
            /// </summary>
            const string StoneShaderPath = "Assets/ToonScapes/Shared Assets/Shaders/ToonScapes_Stone.shader", StoneShaderName = "ToonScapes/URP/Stone",
                TriplanarKeyword = "_OCCLUDER_TRIPLANAR", TriplanarScaleName = "_TriplanarScale", TriplanarFalloffName = "_TriplanarFalloff";

            /// <summary>
            /// The stone's tiling as on the rocks, and a blend between its three projections (Coverage Falloff) other than
            /// the shader's own 2, so a copy that left the dissolve's default in place shows.
            /// </summary>
            const float StoneTiling = 12f, StoneFalloff = 4f;

            /// <summary>
            /// The crate behind the player (m), and how far up the camera looks over it (degrees): the line from the
            /// shoulder back to the camera passes over the crate's top about 3 m back. The camera this much nearer
            /// (m) than before the crate was there has been pulled in.
            /// </summary>
            const float CrateSize = 1f, CrateBehind = 3f, CrateLookUp = 10f, PulledIn = 0.3f;

            /// <summary>
            /// The block stood against (m): chest high, its face this far in front of the body's middle (the body is
            /// 0.27 m round), and how far down the camera looks over it (degrees, BlockLookFrom to BlockLookTo over
            /// BlockSweep s). The camera nearer than NearestCamera (m) to what it films is in the player's body.
            /// </summary>
            const float BlockHeight = 1.25f, BlockAhead = 0.3f, BlockWidth = 2f, BlockDepth = 1f,
                BlockLookFrom = 60f, BlockLookTo = 70f, BlockSweep = 0.8f, NearestCamera = 1f;

            /// <summary>
            /// The tunnels (m): 2.5 m wide and 3 m high inside, as Game.unity's narrowest, reaching well behind the
            /// player for the camera, their floor TunnelLift above the scene's own: a camera that went through it would
            /// otherwise be caught by the scene's floor just under it and look as if it had stayed in. Looked up in over
            /// TunnelSweep (s), along it and with the view turned TunnelTurn (degrees) towards either wall. Then with the
            /// player TunnelHug right of its middle, 0.5 m from the right wall: the rig's own hand, 0.45 m right of the
            /// follow target, would be 5 cm from that wall, so the arm is cut short at it and the hand brought in; along
            /// it, and with the view turned TunnelAway and TunnelAwayMore away from that wall, where the camera's line from
            /// a hand left right by the wall meets it again within a metre. The same with the body against the wall
            /// (TunnelAgainst right, 0.3 m off it) and just beyond where the arm is cut short (TunnelNear right, 0.65 m off).
            /// </summary>
            const float TunnelWidth = 2.5f, TunnelHeight = 3f, TunnelBehind = 12f, TunnelAhead = 6f, TunnelLift = 1f,
                TunnelSweep = 0.8f, TunnelTurn = 25f, TunnelHug = 0.75f, TunnelAgainst = 0.95f, TunnelNear = 0.6f,
                TunnelAway = 2f, TunnelAwayMore = 5f;

            /// <summary>
            /// A heap of rubbish lying on the tunnel's floor, stood on (m): as high as Game.unity's Garbage03 heaps, its
            /// middle HeapAhead along from where the other looks stand. Looked down from it as far as TunnelLookDown
            /// (degrees): the camera rises to the roof, which stops it only if the tunnel still counts as what the player
            /// stands on.
            /// </summary>
            const float HeapWidth = 2f, HeapHeight = 0.5f, HeapAhead = 3f, TunnelLookDown = 40f;

            /// <summary>
            /// The tunnel made of two pieces, one collider each, joined flush as Game.unity's Tun01_Straight chains are,
            /// the joint behind the player: by the right wall JointClose (m) past it, so the camera's hand is over the piece
            /// behind; in the middle JointFar past it with the view turned TunnelTurn either way. Along it by the wall, also
            /// turned JointGraze (degrees) away from that wall.
            /// </summary>
            const float JointClose = 0.05f, JointFar = 1f, JointGraze = 1f;

            /// <summary>
            /// One look in a tunnel, and what it measured. Where the player stands: Side right of the middle (m) - on the
            /// heap, or in the tunnel of two pieces Past (m) beyond their joint - and how far the view is turned off along
            /// it (degrees, right +); whether the pitch goes up to its bottom clamp or down to TunnelLookDown. Measured: the
            /// camera's least clearance from the floor, the walls and the roof, the nearest it came to what it films, and the
            /// frames in which the tunnel faded.
            /// </summary>
            sealed class TunnelView
            {
                public readonly string shot, way;
                public readonly float side, yaw, past;
                public readonly bool heap, down;
                public float floor = float.NaN, wall = float.NaN, roof = float.NaN, nearest = float.NaN;
                public int frames, faded;

                public TunnelView(string shot, string way, float side, float yaw, float past = float.NaN, bool heap = false, bool down = false)
                {
                    this.shot = shot;
                    this.way = way;
                    this.side = side;
                    this.yaw = yaw;
                    this.past = past;
                    this.heap = heap;
                    this.down = down;
                }

                /// <summary>In the tunnel of two pieces.</summary>
                public bool Joint => !float.IsNaN(past);
            }

            static string OffWall(float side) => $"{TunnelWidth / 2f - side:0.00} m from the right wall";

            /// <summary>
            /// The stairs walked down (m): 18 cm risers, enough of them to rise above a level camera behind the player
            /// (1.7 m over their feet), and a landing at the top long enough to be under it. Watched until StairsBeyond
            /// past the bottom. The camera's distance may turn back by StairsSwing (m) or more StairsTurnLimit times at
            /// most: once is its way back out as the player walks off at the bottom.
            /// </summary>
            const float StairsRiser = 0.18f, StairsTread = 0.3f, StairsLanding = 8f, StairsWidth = 3f, StairsBeyond = 1.5f, StairsSwing = 0.5f;
            const int StairsCount = 14, StairsTurnLimit = 2;

            // Where the checks that build their own geometry stand on the floor scene's floor. The runs above head
            // off the other way (towards +x and -z), so nothing of theirs is in the way. What is built is left where
            // it is afterwards: Malbers carries a body along with whatever it stands on, so nothing is moved from
            // under the player. (What is only put in the view, and the slopes once left, go out of the way.)
            static readonly Vector3 LookSpot = new Vector3(-40f, 0f, 40f), TileSpot = new Vector3(-40f, 0f, 55f),
                WallSpot = new Vector3(-55f, 0f, 40f), RampSpot = new Vector3(-70f, 0f, 28f),
                StepsSpot = new Vector3(-85f, 0f, 32f), SlowSpot = new Vector3(-55f, 0f, 55f),
                SideSpot = new Vector3(-25f, 0f, 55f), RoundSpot = new Vector3(-40f, 0f, 70f),
                StoneSpot = new Vector3(-40f, 0f, 85f), CrateSpot = new Vector3(-70f, 0f, 55f),
                BlockSpot = new Vector3(-70f, 0f, 70f), TunnelSpot = new Vector3(-85f, 0f, 85f),
                JointSpot = new Vector3(-95f, 0f, 50f), BigWallSpot = new Vector3(-55f, 0f, 96f),
                StairsSpot = new Vector3(-85f, 0f, 62f), SprintTileSpot = new Vector3(-10f, 0f, 70f),
                RealStairsSpot = new Vector3(-25f, 0f, 25f), TiltSpot = new Vector3(-10f, 0f, 25f),
                // The middle of the steep slope (the 12° one's is at (40, 0.3, 0)).
                SteepSpot = new Vector3(40f, 0.65f, 15f);

            static readonly string RampKey = $"running down a {RampAngle:0}° ramp", SlopeKey = $"standing across a {SlopeAngle:0}° slope",
                SteepKey = $"standing across a {SteepAngle:0}° slope", SprintTileKey = $"sprinting across a {SprintTileThickness * 100f:0} cm floor tile";

            const string LookShot = "camera 4 looking up", TileShot = "camera 5 crouching on a tile", WallShot = "camera 6 against a wall",
                SideShot = "camera 7 beside a wall", BallShot = "camera 8 a ball beside the view", StoneShot = "camera 9 ToonScapes stone in the way",
                CrateShot = "camera 10 a crate behind, looking up", BlockShot = "camera 11 against a block, looking down",
                StairsShot = "camera 15 walking down stairs";

            const string BigWallShot = "camera 27 a wall too big to fade behind", BigHallShot = "camera 28 a hall too big to fade behind";

            /// <summary>
            /// A wall behind the player far bigger than CameraOccluderFade's Max Size across (m), BigWallBehind behind them
            /// and square to the view: it can never fade, so the camera has to stay in front of it rather than film its back.
            /// Then, in its place, a hall as wide made in one mesh with its faces turned in, as Game.unity's RoomHall01 is,
            /// BigHallDepth deep: the camera's sweep from the player's side goes into it without meeting its near wall, and
            /// from inside, that wall hides the player.
            /// </summary>
            const float BigWallWidth = 30f, BigWallHeight = 4f, BigWallThickness = 0.5f, BigWallBehind = 3f, BigHallDepth = 4f;

            /// <summary>
            /// The camera's hand put right by the tunnel's right wall (OverlapGap off it, OverlapHeight over its floor; m), so
            /// close its sphere already touches the tunnel, looking OverlapPitch (degrees) up along it: what the player stands
            /// in has to stop the camera all the same, Radius over the floor - both with the camera's whole distance and with
            /// one that ends OverlapShort over the floor. No pose of the player puts the hand there: asked of the camera's
            /// ground collider directly.
            /// </summary>
            const float OverlapGap = 0.1f, OverlapHeight = 1.5f, OverlapPitch = 30f, OverlapShort = 0.05f;

            MoveStep _moveStep = MoveStep.Setup;
            float _moveStart = -1f;
            bool _moveFirst;
            int _moveFrame = -1;

            CinemachineBrain _brain;
            Camera _moveCamera;

            // Travel direction and camera, running then sprinting.
            readonly List<Vector3> _runDirections = new List<Vector3>(), _sprintDirections = new List<Vector3>();
            readonly List<float> _runCameraYaw = new List<float>(), _sprintCameraYaw = new List<float>();
            readonly HashSet<string> _runCameras = new HashSet<string>(), _sprintCameras = new HashSet<string>();
            Vector3 _movePrevious;
            bool _sprintEngaged;

            // Smoothness, while running and sprinting (no mesh baking in those steps).
            class Smooth
            {
                public readonly List<Vector2> screen = new List<Vector2>();
                public readonly List<float> times = new List<float>();
                public int frames, poseChanged;
                public Quaternion lastPose;
                public bool hasPose;
            }

            readonly Dictionary<string, Smooth> _smooth = new Dictionary<string, Smooth>();

            // Feet: the lowest vertices of each shoe, picked once while standing.
            readonly List<SkinnedMeshRenderer> _skins = new List<SkinnedMeshRenderer>();
            readonly List<int[]> _leftSole = new List<int[]>(), _rightSole = new List<int[]>();
            Mesh _bake;
            readonly List<Vector3> _bakeVertices = new List<Vector3>();
            readonly Dictionary<string, List<Vector2>> _soles = new Dictionary<string, List<Vector2>>();

            // Air control and landing.
            float _airTakeoff = -1f, _airLanded = -1f;
            Vector3 _airStart, _airRight, _airEnd;
            float _airSideways = float.NaN, _airTime = float.NaN;

            Vector3 _slideLandPosition;
            float _slideLandedAt = -1f, _slideTakeoff = -1f;
            float _slideDistance = float.NaN, _slideTime = float.NaN;

            Vector3 _stopFrom;
            float _stopReleasedAt = -1f;
            float _stopDistance = float.NaN, _stopTime = float.NaN;

            // Coming to a stop: after the run, after the landing, after the held landing.
            readonly StillWatch _stopStill = new StillWatch(), _slideStill = new StillWatch(), _holdStill = new StillWatch();

            // Air control on a running jump: the stick turned sideways at take-off.
            Vector3 _turnRunDirection, _turnStart;
            float _turnTakeoff = -1f, _turnLanded = -1f, _turnHalfAt = -1f;
            float _turnSideways = float.NaN, _turnAngle = float.NaN;

            // A side slope to stand on, away from where the rest happens.
            GameObject _slope;
            Vector3 _slopeReturn;
            const float SlopeAngle = 12f;

            // Landing a running jump with the stick held, letting go as the feet touch down.
            Vector3 _holdLandPosition;
            float _holdTakeoff = -1f, _holdLandedAt = -1f;
            float _holdDistance = float.NaN, _holdTime = float.NaN;

            // How far a planted shoe moves along the floor (skating), per measured stretch.
            readonly Dictionary<string, float> _skate = new Dictionary<string, float>(), _skateTime = new Dictionary<string, float>();
            Vector3 _leftSoleWas, _rightSoleWas;
            bool _leftPlantedWas, _rightPlantedWas;
            string _skateKey;

            // Batch mode runs flat out; the movement checks hold it to a screen's frames: a 144 Hz one, and a
            // 30 Hz one for the slow-screen run.
            readonly System.Diagnostics.Stopwatch _frameClock = System.Diagnostics.Stopwatch.StartNew();
            double _lastFrameMs = -1;
            float _holdFps = 144f;

            // The camera with a wall put between it and the player.
            GameObject _camWall;
            Renderer _camWallRenderer;
            Material _camWallOwn;
            float _camDistanceBefore = float.NaN, _camDistanceWall = float.NaN;
            float _camWallDissolve = float.NaN;
            string _camWallShader = "-", _camWallShaderAfter = "-";
            int _fadedWhileWall = -1, _fadedAfter = -1;
            // Back out of the way, the wall still carried a property block (it had none before it faded).
            bool _camWallBlockAfter;
            readonly List<string> _camShots = new List<string>();

            readonly List<string> _moveNotes = new List<string>();

            // Looking up: the camera's lowest over the floor at its bottom pitch, and what keeps it up there.
            float _lookLowest = float.NaN, _lookPitch = float.NaN, _lookDistance = float.NaN, _lookRadius = float.NaN;
            bool _lookHasCollider;

            // Crouching on a thin floor tile: frames on it standing and crouched, and how many of them it was faded in.
            Renderer _tileRenderer;
            MalbersAnimations.StanceID _tileCrouch;
            float _tileTop = float.NaN, _tileTargetLow = float.NaN;
            int _tileStanding, _tileStandingFaded, _tileCrouched, _tileCrouchedFaded;
            bool _tileTried;

            // Going up against a wall in front: frames of the push, frames against it, frames it was faded in.
            Renderer _frontWallRenderer;
            float _frontWallFace = float.NaN, _frontClosest = float.NaN;
            int _frontFrames, _frontAgainst, _frontFaded, _frontSpeedBefore = -1;

            // Running along a wall right beside the player: which side it is on, where its face and ends are, frames
            // alongside it, the nearest the body's middle came to it, frames it was faded in.
            Renderer _sideWallRenderer;
            string _sideWhich = "-";
            float _sideWallFace = float.NaN, _sideWallFrom = float.NaN, _sideWallTo = float.NaN, _sideClosest = float.NaN;
            int _sideFrames, _sideFaded;

            // Running down a ramp: its top edge, the ways down it and out of it, and the resting soles on it.
            Vector3 _rampTop, _rampDown, _rampNormal, _rampFrom;
            float _rampFromAt = -1f, _rampSpeed = float.NaN, _rampDrawnHigh = float.NaN;
            float _rampLeftWas = float.NaN, _rampRightWas = float.NaN;
            bool _rampRan;
            readonly List<float> _rampResting = new List<float>(), _rampJitter = new List<float>();
            // Each resting sole's height over the ramp (y) against how far the body was drawn over its physics root (x).
            readonly List<Vector2> _rampLagged = new List<Vector2>();

            // Walking up steps: each foot's height over where the body is drawn, from one frame to the next, and
            // FootGrounder's own correction of it.
            float _stepsLeftWas = float.NaN, _stepsRightWas = float.NaN, _stepsPopLeft = float.NaN, _stepsPopRight = float.NaN;
            float _stepsLeftFixWas = float.NaN, _stepsRightFixWas = float.NaN, _stepsFixPopLeft = float.NaN, _stepsFixPopRight = float.NaN;
            // The body drawn, and the physics root, a frame ago; the most the drawn body rose from one frame to the next,
            // and the most the root rose in one physics step.
            float _stepsDrawnWas = float.NaN, _stepsRootWas = float.NaN, _stepsBodyRise = float.NaN, _stepsRootRise = float.NaN;
            int _stepsHitches;
            float _stepsFrom = float.NaN, _stepsRise = float.NaN;
            int _stepsSpeedBefore = -1, _stepsSpeed = -1;
            readonly List<float> _stepsJumps = new List<float>();

            /// <summary>
            /// One frame of a walk up the steps as the check saw it: when it was (and how many frames and physics steps
            /// since the last), where the root and the drawn body were (over the floor at the foot of the steps) and how
            /// far along, Malbers' state, each foot's height over the drawn body and FootGrounder's correction of it with
            /// what that was worked out from, and the jumps the verdict took (NaN where it took none).
            /// </summary>
            class StepsFrame
            {
                public int walk, frame, frames, steps;
                public float time, dt, fixedTime, interp, along, rootY, drawnY, pending;
                public string state;
                public bool grounded, grounding, judged, hitch;
                public float alignLerp, weight, hips;
                public float leftOver, rightOver, leftFix, rightFix;
                public float leftJump = float.NaN, rightJump = float.NaN, leftFixJump = float.NaN, rightFixJump = float.NaN;
                public FootGrounder.LegReading left, right;
            }

            // Every frame of the walks up the steps, for the report's worst frames and steps.csv beside it; the walks
            // done, and the frame and physics time of the last frame logged.
            readonly List<StepsFrame> _stepsLog = new List<StepsFrame>();
            int _stepsWalks, _stepsFrameWas = -1;
            float _stepsFixedWas = float.NaN;

            /// <summary>How many times the steps are walked up (-liveStepsRepeat; once by default).</summary>
            int StepsWalks => Mathf.Max(1, _job.stepsRepeat);

            /// <summary>
            /// Malbers' ray down from the body to the ground (internal): what is left of its distance from Height after the
            /// physics step's alignment is how much of a rise the root has still to make.
            /// </summary>
            static readonly System.Reflection.FieldInfo GroundRay = typeof(MalbersAnimations.Controller.MAnimal).GetField("hit_Hip",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            // Running up the steps: the planted shoes against the tread under them, the feet a frame ago, the run's
            // speed and Malbers' speed for it.
            readonly List<float> _stepsRunPlanted = new List<float>();
            Vector3 _stepsRunLeftWas, _stepsRunRightWas, _stepsRunFrom;
            bool _stepsRunTried, _stepsRunHasWas;
            float _stepsRunFromAt = -1f, _stepsRunPace = float.NaN;
            int _stepsRunFrames, _stepsRunSpeed = -1;

            /// <summary>
            /// One way over a flight of stairs, walking or running, and what each foot did on it from one frame to the next:
            /// as up the level steps, its height over the body drawn and FootGrounder's own correction of it; and what moved
            /// it - the lift FootGrounder's flat sole adds (Flat), whether the ground under its heel and toe was of one slope
            /// or two, and whether those points had ground in reach.
            /// </summary>
            sealed class FlightRun
            {
                public readonly string what;
                public readonly bool real, down, run;
                public bool tried;
                public int speed = -1, judged, hitches, legFrames, twoSlopes, slopeTurns, reachTurns;
                public float walked = float.NaN, pace = float.NaN, startedAt = -1f;
                public Vector3 startedFrom;
                public float popLeft = float.NaN, popRight = float.NaN, fixLeft = float.NaN, fixRight = float.NaN, flat = float.NaN;
                public readonly List<float> jumps = new List<float>();

                // A frame ago (NaN or -1: none): heights, corrections, the flat sole's lift; one slope (0) or two (1), and
                // which points had ground in reach (heel 1, toe 2).
                public float leftWas, rightWas, leftFixWas, rightFixWas, leftFlatWas, rightFlatWas;
                public int leftSlopes, rightSlopes, leftReach, rightReach;

                public FlightRun(string what, bool real, bool down, bool run)
                {
                    this.what = what;
                    this.real = real;
                    this.down = down;
                    this.run = run;
                    Forget();
                }

                /// <summary>Starts again from the next frame, as after a hitch.</summary>
                public void Forget()
                {
                    leftWas = rightWas = leftFixWas = rightFixWas = leftFlatWas = rightFlatWas = float.NaN;
                    leftSlopes = rightSlopes = leftReach = rightReach = -1;
                }
            }

            readonly FlightRun[] _flightRuns =
            {
                new FlightRun("walking up First_Parkour's staircase", real: true, down: false, run: false),
                new FlightRun("walking down First_Parkour's staircase", real: true, down: true, run: false),
                new FlightRun("walking up a flight with tilted treads", real: false, down: false, run: false),
                new FlightRun("walking down the flight with tilted treads", real: false, down: true, run: false),
                new FlightRun("running up the flight with tilted treads", real: false, down: false, run: true),
                new FlightRun("running down the flight with tilted treads", real: false, down: true, run: true),
            };

            // The flights: built yet; the staircase's middle line (x), first riser and top (z), the height of its upper floor,
            // and of its first and top treads as found; the same for the tilted flight. Every frame of the runs over them, the
            // run on now, Malbers' speed before it, and the frame and physics time of the last frame logged.
            bool _flightsBuilt, _realBuilt;
            float _realX, _realFoot, _realTop, _realHigh, _realFirstTread = float.NaN, _realTopTread = float.NaN;
            float _tiltX, _tiltFoot, _tiltTop, _tiltHigh;
            readonly List<StepsFrame> _flightLog = new List<StepsFrame>();
            int _flightCase, _flightSpeedBefore = -1, _flightFrameWas = -1;
            float _flightFixedWas = float.NaN;

            // The slow screen: what each physics step left, recorded right after it, and the frames judged by it.
            class StepSnapshot
            {
                public Vector3 root;
                public Quaternion[] bones;
            }

            readonly List<StepSnapshot> _snapshots = new List<StepSnapshot>();
            Transform[] _snapshotBones = Array.Empty<Transform>();
            Coroutine _stepRecorder;
            int _physicsSteps, _physicsStepsWas = -1;
            int _slowFrames, _slowTwoSteps, _slowJudged, _slowMatched, _slowOffStep;
            float _slowFrom = -1f, _slowTo = -1f;
            readonly List<float> _slowLag = new List<float>();

            // -liveStockRig: the camera rigs when the checks began and at the end.
            string _stockRigBegan, _stockRigEnded;

            // Across a slope: each foot's up in its own bone's space while standing on flat ground (summed), and against
            // it how far each resting shoe's sole lies off the slope stood across (degrees; left, right), per slope.
            Vector3 _leftFlatUp, _rightFlatUp;
            readonly Dictionary<string, List<Vector2>> _slopeLie = new Dictionary<string, List<Vector2>>();
            GameObject _steep;

            // Sprinting across a thin floor tile: where it begins and ends along the run, and whether the run was made.
            float _sprintTileFrom = float.NaN, _sprintTileTo = float.NaN;
            bool _sprintTileRan, _sprintTileSprinted;

            /// <summary>Something put in the view to see it fade: frames watched once it had time to, and frames it was faded in.</summary>
            class FadeWatch
            {
                public Renderer renderer;
                public int frames, faded;
            }

            readonly FadeWatch _ballFade = new FadeWatch(), _pillarFade = new FadeWatch(), _crateFade = new FadeWatch();

            // The ToonScapes stone in the way: the dissolving copy it wore, its colour, and whether that maps it in object
            // space (and whether the dissolve shader can at all), and at what scale.
            Renderer _stoneRenderer;
            bool _stoneTried, _stoneFaded, _stoneHasKeyword, _stoneTriplanar;
            string _stoneCopyShader = "-";
            Color _stoneCopyColor;
            float _stoneTriplanarScale = float.NaN, _stoneTriplanarFalloff = float.NaN;

            // The crate behind: the camera's distance before it was put there, and the nearest with it there.
            float _crateClear = float.NaN, _crateNearest = float.NaN;

            // Against the block: the nearest the camera came to what it films, and how far down it looked then.
            float _blockNearest = float.NaN, _blockPitch = float.NaN;
            bool _blockTried;

            // The tunnels: the middle of the one-piece tunnel's floor (where the player stands), and of the two-piece one's
            // at their joint; their renderers; the camera's ground radius, and the looks taken in them.
            Vector3 _tunnelFloor, _jointFloor;
            readonly List<Renderer> _tunnelRenderers = new List<Renderer>(), _jointRenderers = new List<Renderer>();
            int _tunnelCase;
            bool _tunnelTried;
            float _tunnelRadius = float.NaN;

            readonly TunnelView[] _tunnelViews =
            {
                new TunnelView("camera 12 tunnel, looking up", "along it", 0f, 0f),
                new TunnelView("camera 13 tunnel, turned right", $"the view turned {TunnelTurn:0}° right", 0f, TunnelTurn),
                new TunnelView("camera 14 tunnel, turned left", $"the view turned {TunnelTurn:0}° left", 0f, -TunnelTurn),
                new TunnelView("camera 16 tunnel, by its right wall",
                    $"along it {OffWall(TunnelHug)}, where the rig's own hand would touch it", TunnelHug, 0f),
                new TunnelView("camera 17 tunnel, by its right wall, turned a little away",
                    $"{OffWall(TunnelHug)}, the view turned {TunnelAway:0}° away from it", TunnelHug, -TunnelAway),
                new TunnelView("camera 18 tunnel, by its right wall, turned away",
                    $"{OffWall(TunnelHug)}, turned {TunnelAwayMore:0}° away", TunnelHug, -TunnelAwayMore),
                new TunnelView("camera 19 tunnel, against its right wall, turned a little away",
                    $"{OffWall(TunnelAgainst)} (the body against it), turned {TunnelAway:0}° away", TunnelAgainst, -TunnelAway),
                new TunnelView("camera 20 tunnel, against its right wall, turned away",
                    $"{OffWall(TunnelAgainst)}, turned {TunnelAwayMore:0}° away", TunnelAgainst, -TunnelAwayMore),
                new TunnelView("camera 21 tunnel, near its right wall, turned away",
                    $"{OffWall(TunnelNear)}, turned {TunnelAwayMore:0}° away", TunnelNear, -TunnelAwayMore),
                new TunnelView("camera 22 tunnel, on a heap on its floor, looking down",
                    $"standing on a {HeapHeight:0.0} m heap lying on its floor, looking down to {TunnelLookDown:0}°", 0f, 0f, heap: true, down: true),
                new TunnelView("camera 23 tunnel of two pieces, by its right wall",
                    $"{JointClose:0.00} m past their joint and {OffWall(TunnelHug)}", TunnelHug, 0f, JointClose),
                new TunnelView("camera 24 tunnel of two pieces, by its right wall, turned a little away",
                    $"{JointClose:0.00} m past the joint and {OffWall(TunnelHug)}, turned {JointGraze:0}° away from the wall", TunnelHug, -JointGraze, JointClose),
                new TunnelView("camera 25 tunnel of two pieces, turned left",
                    $"{JointFar:0} m past the joint, turned {TunnelTurn:0}° left", 0f, -TunnelTurn, JointFar),
                new TunnelView("camera 26 tunnel of two pieces, turned right",
                    $"{JointFar:0} m past the joint, turned {TunnelTurn:0}° right", 0f, TunnelTurn, JointFar),
            };

            // The hand put right by the tunnel's wall, asked of the ground collider directly: the camera's height over the
            // floor with its whole distance, and with a distance that ends just over the floor.
            float _overlapWhole = float.NaN, _overlapShort = float.NaN;
            bool _overlapTried;

            // The wall too big to fade: where its face is (z), the camera's distance before it was put there, and the
            // least the camera kept in front of that face (below zero it was beyond it).
            float _bigWallFace = float.NaN, _bigWallBefore = float.NaN, _bigWallFront = float.NaN, _bigWallNearest = float.NaN,
                _bigWallRadius = float.NaN, _bigHallFront = float.NaN, _bigHallNearest = float.NaN;
            Renderer _bigWallRenderer;

            // Walking down the stairs: where the landing ends and how high it is, how far the walk got, the camera's
            // lowest over the stairs right under it and the frames it was in them, and its distance from what it films:
            // nearest, farthest, and how often it turned back by StairsSwing or more (the way it is going, and how far).
            MeshCollider _stairsCollider;
            float _stairsFront = float.NaN, _stairsTopY = float.NaN, _stairsWalked = float.NaN, _stairsLowest = float.NaN;
            float _stairsNearest = float.NaN, _stairsFarthest = float.NaN;
            int _stairsFrames, _stairsInside, _stairsTurns, _stairsSpeedBefore = -1;
            float _pumpLow = float.NaN, _pumpHigh = float.NaN, _pumpExtreme = float.NaN;
            int _pumpHeading;

            // What made shapes are drawn with.
            Material _plainMaterial;

            float InMove => Now - _moveStart;

            void NextMove(MoveStep step)
            {
                _moveStep = step;
                _moveStart = Now;
                _moveFirst = true;
            }

            static Vector3 FlatV(Vector3 v) => Vector3.ProjectOnPlane(v, Vector3.up);

            static float Yaw(Vector3 forward) => Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;

            string LiveCamera => _brain != null && _brain.ActiveVirtualCamera != null ? _brain.ActiveVirtualCamera.Name : "none";

            /// <summary>One frame of the movement checks; true when they are done.</summary>
            bool StepMovement()
            {
                // The Editor now and then ticks twice for one frame of play (7% of the frames walking up the steps).
                // Nothing has moved since the last tick then, so there is nothing to measure or do; and holding the
                // frame rate on that tick as well kept the next frame waiting a second time: it lasted two (13-14 ms),
                // under the steps' hitch cut, and everything moved twice as far in it.
                if (Time.frameCount == _moveFrame)
                    return false;
                _moveFrame = Time.frameCount;

                if (_moveStart < 0f)
                    _moveStart = Now;
                bool first = _moveFirst;
                _moveFirst = false;
                HoldFrameRate();

                switch (_moveStep)
                {
                    case MoveStep.Setup:
                        SetUpMovement();
                        // The camera turned well away from where the body faces, as a player does with the
                        // mouse before running off: every camera of the rig has to follow that turn.
                        TurnCamera(150f);
                        NextMove(MoveStep.Turn);
                        break;

                    case MoveStep.Turn:
                        if (InMove > 1.0f)
                        {
                            _movePrevious = _animal.transform.position;
                            Move(new Vector3(0f, 0f, 1f));
                            NextMove(MoveStep.Run);
                        }

                        break;

                    case MoveStep.Run:
                        if (InMove > 0.6f)
                            RecordTravel(_runDirections, _runCameraYaw, _runCameras, "run");
                        else
                            _movePrevious = _animal.transform.position;
                        if (InMove > 2.0f)
                        {
                            _animal.Sprint = true;
                            NextMove(MoveStep.Sprint);
                        }

                        break;

                    case MoveStep.Sprint:
                        if (_animal.Sprint)
                            _sprintEngaged = true;
                        if (InMove > 0.5f)
                            RecordTravel(_sprintDirections, _sprintCameraYaw, _sprintCameras, "sprint");
                        else
                        {
                            _movePrevious = _animal.transform.position;
                            _sprintCameras.Add(LiveCamera);
                        }
                        if (InMove > 2.5f)
                        {
                            _animal.Sprint = false;
                            Move(Vector3.zero);
                            NextMove(MoveStep.SprintStop);
                        }

                        break;

                    case MoveStep.SprintStop:
                        if (InMove > 1.5f && Standing())
                            NextMove(MoveStep.FeetIdle);
                        break;

                    case MoveStep.FeetIdle:
                        if (first)
                            PickSoles();
                        if (InMove > 0.5f)
                        {
                            SampleSoles("standing");
                            SampleFlatUp();
                        }
                        if (InMove > 2.5f)
                        {
                            Move(new Vector3(0f, 0f, 1f));
                            NextMove(MoveStep.FeetMove);
                        }

                        break;

                    case MoveStep.FeetMove:
                        if (InMove > 0.6f)
                            SampleSoles("moving", skate: true);
                        if (InMove > 2.6f)
                        {
                            _animal.Sprint = true;
                            NextMove(MoveStep.FeetSprint);
                        }

                        break;

                    case MoveStep.FeetSprint:
                        if (InMove > 0.6f)
                            SampleSoles("sprinting");
                        if (InMove > 2.6f)
                        {
                            _animal.Sprint = false;
                            Move(Vector3.zero);
                            NextMove(MoveStep.FeetStop);
                        }

                        break;

                    case MoveStep.FeetStop:
                        if (InMove > 1.5f && Standing())
                            NextMove(MoveStep.SlopeGo);
                        break;

                    case MoveStep.SlopeGo:
                        if (first)
                        {
                            // Standing across a slope: one shoe has higher ground under it than the other.
                            _slopeReturn = _animal.transform.position;
                            _slope = GameObject.CreatePrimitive(PrimitiveType.Cube);
                            _slope.name = "Side slope";
                            _slope.transform.SetPositionAndRotation(new Vector3(40f, 0.3f, 0f), Quaternion.Euler(0f, 0f, SlopeAngle));
                            _slope.transform.localScale = new Vector3(6f, 0.6f, 6f);
                            _animal.Teleport(new Vector3(40f, 1.4f, 0f));
                        }
                        else if (InMove > 0.1f && InMove < 0.2f)
                            _animal.transform.rotation = Quaternion.identity;
                        else if (InMove > 1.6f && Standing())
                            NextMove(MoveStep.SlopeStand);
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add("Never settled on the side slope.");
                            NextMove(MoveStep.SteepGo);
                        }

                        break;

                    case MoveStep.SlopeStand:
                        if (InMove > 0.3f)
                        {
                            SampleSoles(SlopeKey);
                            SampleSlopeLie(SlopeKey, Quaternion.Euler(0f, 0f, SlopeAngle) * Vector3.up);
                        }
                        if (InMove > 2.3f)
                            NextMove(MoveStep.SteepGo);
                        break;

                    case MoveStep.SteepGo:
                        if (first)
                        {
                            // The same across a 30° slope, as steep as the levels' rock gets: both shoes still lie on it.
                            var middle = Spot(SteepSpot);
                            _steep = GameObject.CreatePrimitive(PrimitiveType.Cube);
                            _steep.name = "Steep side slope";
                            _steep.transform.SetPositionAndRotation(middle, Quaternion.Euler(0f, 0f, SteepAngle));
                            _steep.transform.localScale = new Vector3(6f, 0.6f, 6f);
                            _animal.Teleport(middle + Vector3.up * 1.2f);
                        }
                        else if (InMove > 0.1f && InMove < 0.2f)
                            _animal.transform.rotation = Quaternion.identity;
                        else if (InMove > 1.6f && Standing())
                            NextMove(MoveStep.SteepStand);
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add($"Never settled on the {SteepAngle:0}° side slope.");
                            NextMove(MoveStep.SlopeBack);
                        }

                        break;

                    case MoveStep.SteepStand:
                        if (InMove > 0.3f)
                        {
                            SampleSoles(SteepKey);
                            SampleSlopeLie(SteepKey, Quaternion.Euler(0f, 0f, SteepAngle) * Vector3.up);
                        }
                        if (InMove > 2.3f)
                            NextMove(MoveStep.SlopeBack);
                        break;

                    case MoveStep.SlopeBack:
                        if (first)
                            _animal.Teleport(_slopeReturn + Vector3.up * 0.2f);
                        else if (InMove > 1.2f && Standing())
                        {
                            if (_slope != null)
                                _slope.transform.position += Vector3.up * 1000f;
                            if (_steep != null)
                                _steep.transform.position += Vector3.up * 1000f;
                            Move(new Vector3(0f, 0f, 1f));
                            NextMove(MoveStep.StopRun);
                        }

                        break;

                    case MoveStep.StopRun:
                        // Stopping from a run on the ground: the slide a landing is compared with.
                        if (InMove > 1.8f)
                        {
                            Move(Vector3.zero);
                            _stopFrom = _animal.transform.position;
                            _stopStill.Begin(Now, _stopFrom);
                            _stopReleasedAt = Now;
                            NextMove(MoveStep.StopWatch);
                        }

                        break;

                    case MoveStep.StopWatch:
                        if (WatchStill(_stopStill) || InMove > 3f)
                        {
                            _stopDistance = FlatV(_stopStill.Where(_animal.transform.position) - _stopFrom).magnitude;
                            _stopTime = _stopStill.When(Now) - _stopReleasedAt;
                            NextMove(MoveStep.AirRest);
                        }

                        break;

                    case MoveStep.AirRest:
                        if (InMove > 1.0f && Standing())
                        {
                            Press(true);
                            _airTakeoff = _airLanded = -1f;
                            _airRight = FlatV(_moveCamera != null ? _moveCamera.transform.right : _animal.transform.right).normalized;
                            NextMove(MoveStep.AirJump);
                        }

                        break;

                    case MoveStep.AirJump:
                        if (_held && InMove > 0.15f)
                            Press(false);
                        if (_airTakeoff < 0f && !_animal.Grounded)
                        {
                            // Off the ground: the stick goes sideways for the whole flight.
                            _airTakeoff = Now;
                            _airStart = _animal.transform.position;
                            Move(new Vector3(1f, 0f, 0f));
                        }
                        else if (_airTakeoff > 0f && _airLanded < 0f && _animal.Grounded && Now - _airTakeoff > 0.1f)
                        {
                            _airLanded = Now;
                            _airEnd = _animal.transform.position;
                            _airSideways = Vector3.Dot(_airEnd - _airStart, _airRight);
                            _airTime = _airLanded - _airTakeoff;
                            Move(Vector3.zero);
                        }

                        if (_airLanded > 0f && Now - _airLanded > 0.3f || InMove > 4f)
                        {
                            if (_airTakeoff < 0f)
                                _moveNotes.Add("The standing jump never left the ground.");
                            Move(Vector3.zero);
                            NextMove(MoveStep.SlideRest);
                        }

                        break;

                    case MoveStep.SlideRest:
                        if (InMove > 1.2f && Standing())
                        {
                            Move(new Vector3(0f, 0f, 1f));
                            NextMove(MoveStep.SlideRun);
                        }

                        break;

                    case MoveStep.SlideRun:
                        if (InMove > 1.8f)
                        {
                            Press(true);
                            _slideTakeoff = _slideLandedAt = -1f;
                            NextMove(MoveStep.SlideJump);
                        }

                        break;

                    case MoveStep.SlideJump:
                        if (_held && InMove > 0.15f)
                            Press(false);
                        if (_slideTakeoff < 0f && !_animal.Grounded)
                        {
                            // The stick let go in the air: on landing the body should stop.
                            _slideTakeoff = Now;
                            Move(Vector3.zero);
                        }
                        else if (_slideTakeoff > 0f && _slideLandedAt < 0f && _animal.Grounded && Now - _slideTakeoff > 0.1f)
                        {
                            _slideLandedAt = Now;
                            _slideLandPosition = _animal.transform.position;
                            _slideStill.Begin(Now, _slideLandPosition);
                        }
                        else if (_slideLandedAt > 0f && (WatchStill(_slideStill) || Now - _slideLandedAt > 3f))
                        {
                            _slideDistance = FlatV(_slideStill.Where(_animal.transform.position) - _slideLandPosition).magnitude;
                            _slideTime = _slideStill.When(Now) - _slideLandedAt;
                            NextMove(MoveStep.TurnRest);
                        }
                        else if (InMove > 6f)
                        {
                            _moveNotes.Add("The running jump never landed within 6 s.");
                            NextMove(MoveStep.TurnRest);
                        }

                        break;

                    case MoveStep.TurnRest:
                        if (InMove > 1.2f && Standing())
                        {
                            Move(new Vector3(0f, 0f, 1f));
                            _movePrevious = _animal.transform.position;
                            NextMove(MoveStep.TurnRun);
                        }

                        break;

                    case MoveStep.TurnRun:
                        if (InMove > 1.8f)
                        {
                            _turnRunDirection = FlatV(_animal.transform.forward).normalized;
                            Press(true);
                            _turnTakeoff = _turnLanded = -1f;
                            NextMove(MoveStep.TurnJump);
                        }

                        break;

                    case MoveStep.TurnJump:
                        if (_held && InMove > 0.15f)
                            Press(false);
                        if (_turnTakeoff > 0f && _turnLanded < 0f && _turnHalfAt < 0f)
                        {
                            var going = FlatV(_animal.HorizontalVelocity);
                            if (going.sqrMagnitude > 0.01f && Vector3.Angle(_turnRunDirection, going) >= 45f)
                                _turnHalfAt = Now;
                        }
                        if (_turnTakeoff < 0f && !_animal.Grounded)
                        {
                            // Off the ground running: the stick goes hard right for the whole flight.
                            _turnTakeoff = Now;
                            _turnStart = _animal.transform.position;
                            Move(new Vector3(1f, 0f, 0f));
                        }
                        else if (_turnTakeoff > 0f && _turnLanded < 0f && _animal.Grounded && Now - _turnTakeoff > 0.1f)
                        {
                            _turnLanded = Now;
                            var travelled = FlatV(_animal.transform.position - _turnStart);
                            var right = Vector3.Cross(Vector3.up, _turnRunDirection).normalized;
                            _turnSideways = Vector3.Dot(travelled, right);
                            var velocity = FlatV(_animal.HorizontalVelocity);
                            _turnAngle = velocity.sqrMagnitude > 0.01f ? Vector3.SignedAngle(_turnRunDirection, velocity, Vector3.up) : float.NaN;
                            Move(Vector3.zero);
                        }

                        if (_turnLanded > 0f && Now - _turnLanded > 0.3f || InMove > 5f)
                        {
                            Move(Vector3.zero);
                            NextMove(MoveStep.HoldRest);
                        }

                        break;

                    case MoveStep.HoldRest:
                        if (InMove > 1.2f && Standing())
                        {
                            Move(new Vector3(0f, 0f, 1f));
                            NextMove(MoveStep.HoldRun);
                        }

                        break;

                    case MoveStep.HoldRun:
                        if (InMove > 1.8f)
                        {
                            Press(true);
                            _holdTakeoff = _holdLandedAt = -1f;
                            NextMove(MoveStep.HoldJump);
                        }

                        break;

                    case MoveStep.HoldJump:
                        // The stick stays forward through the jump and lets go the moment the feet land.
                        if (_held && InMove > 0.15f)
                            Press(false);
                        if (_holdTakeoff < 0f && !_animal.Grounded)
                            _holdTakeoff = Now;
                        else if (_holdTakeoff > 0f && _holdLandedAt < 0f && _animal.Grounded && Now - _holdTakeoff > 0.1f)
                        {
                            _holdLandedAt = Now;
                            _holdLandPosition = _animal.transform.position;
                            _holdStill.Begin(Now, _holdLandPosition);
                            Move(Vector3.zero);
                        }
                        else if (_holdLandedAt > 0f)
                        {
                            if (Now - _holdLandedAt < 0.8f)
                                SampleSoles("landing", skate: true);
                            if (WatchStill(_holdStill) && Now - _holdLandedAt > 0.8f || Now - _holdLandedAt > 3f)
                            {
                                _holdDistance = FlatV(_holdStill.Where(_animal.transform.position) - _holdLandPosition).magnitude;
                                _holdTime = _holdStill.When(Now) - _holdLandedAt;
                                NextMove(MoveStep.CamSetup);
                            }
                        }
                        else if (InMove > 6f)
                        {
                            _moveNotes.Add("The held running jump never landed within 6 s.");
                            NextMove(MoveStep.CamSetup);
                        }

                        break;

                    case MoveStep.CamSetup:
                        if (InMove > 1.0f && Standing())
                        {
                            _camDistanceBefore = CameraToTarget();
                            Shoot("camera 1 clear");
                            PutWall();
                            NextMove(MoveStep.CamWall);
                        }

                        break;

                    case MoveStep.CamWall:
                        if (InMove > 0.12f && _camShots.Count < 2)
                            Shoot("camera 2 dissolving");
                        if (InMove > 0.8f)
                            NextMove(MoveStep.CamShot);
                        break;

                    case MoveStep.CamShot:
                        _camDistanceWall = CameraToTarget();
                        _fadedWhileWall = Fader != null ? Fader.FadedCount : -1;
                        if (_camWallRenderer != null)
                        {
                            _camWallShader = _camWallRenderer.sharedMaterial != null ? _camWallRenderer.sharedMaterial.shader.name : "none";
                            var block = new MaterialPropertyBlock();
                            _camWallRenderer.GetPropertyBlock(block);
                            _camWallDissolve = block.GetFloat("_Dissolve");
                        }
                        Shoot("camera 3 behind the wall");
                        // Out of the way again: it should come back as it was.
                        if (_camWall != null)
                            _camWall.transform.position += Vector3.right * 30f;
                        NextMove(MoveStep.CamBack);
                        break;

                    case MoveStep.CamBack:
                        if (InMove > 1.2f)
                        {
                            _fadedAfter = Fader != null ? Fader.FadedCount : -1;
                            _camWallShaderAfter = _camWallRenderer != null && _camWallRenderer.sharedMaterial != null ? _camWallRenderer.sharedMaterial.shader.name : "none";
                            // A made cube has no property block; one left on it after the fade keeps it out of the SRP Batcher.
                            _camWallBlockAfter = _camWallRenderer != null && _camWallRenderer.HasPropertyBlock();
                            NextMove(MoveStep.RoundSetup);
                        }

                        break;

                    case MoveStep.RoundSetup:
                        // Round things half way along the view, their surface just beside the line to the player's chest: a
                        // boulder, a pillar, hiding part of the player. Met first by their rounded side, off that line, they
                        // have to fade all the same.
                        if (first)
                            PlaceForCheck(Spot(RoundSpot), 0f, CheckPitch);
                        else if (InMove > 1.0f && Standing())
                        {
                            _ballFade.renderer = PutBeside(RoundBallRadius, pillar: false);
                            NextMove(MoveStep.RoundBall);
                        }
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add("Never settled for the round things beside the view; they were skipped.");
                            NextMove(MoveStep.StoneSetup);
                        }

                        break;

                    case MoveStep.RoundBall:
                        if (InMove > FadeSettle)
                            WatchFade(_ballFade);
                        if (InMove > 0.5f && !_camShots.Contains(BallShot))
                            Shoot(BallShot);
                        if (InMove > 0.9f)
                        {
                            OutOfTheWay(_ballFade.renderer);
                            _pillarFade.renderer = PutBeside(RoundPillarRadius, pillar: true);
                            NextMove(MoveStep.RoundPillar);
                        }

                        break;

                    case MoveStep.RoundPillar:
                        if (InMove > FadeSettle)
                            WatchFade(_pillarFade);
                        if (InMove > 0.9f)
                        {
                            OutOfTheWay(_pillarFade.renderer);
                            NextMove(MoveStep.StoneSetup);
                        }

                        break;

                    case MoveStep.StoneSetup:
                        // A box of ToonScapes stone in the way, its material as the TSU rocks' are: the tint switched off,
                        // a red left in it. The dissolving copy has to be the stone's own colour, and mapped in object space
                        // as the stone is.
                        if (first)
                            PlaceForCheck(Spot(StoneSpot), 0f, CheckPitch);
                        else if (InMove > 1.0f && Standing())
                        {
                            _stoneRenderer = PutStone();
                            NextMove(_stoneRenderer != null ? MoveStep.StoneBox : MoveStep.LookSetup);
                        }
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add("Never settled for the ToonScapes stone in the view; it was skipped.");
                            NextMove(MoveStep.LookSetup);
                        }

                        break;

                    case MoveStep.StoneBox:
                        if (InMove > 0.1f)
                            SampleStone();
                        if (InMove > 0.4f && !_camShots.Contains(StoneShot))
                            Shoot(StoneShot);
                        if (InMove > 0.8f)
                        {
                            OutOfTheWay(_stoneRenderer);
                            NextMove(MoveStep.LookSetup);
                        }

                        break;

                    case MoveStep.LookSetup:
                        // Looking up as far as the camera goes, on flat floor: it swings down behind the player,
                        // and with nothing pushing it out of walls any more only the ground can stop it there.
                        if (first)
                            PlaceForCheck(Spot(LookSpot), 0f, 0f);
                        else if (InMove > 1.0f && Standing())
                        {
                            var ground = OnLiveCamera<CameraGroundCollider>();
                            _lookHasCollider = ground != null && ground.enabled;
                            _lookRadius = _lookHasCollider ? ground.radius : 0f;
                            NextMove(MoveStep.LookUp);
                        }
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add("Never settled for the look-up check; it was skipped.");
                            NextMove(MoveStep.CrateSetup);
                        }

                        break;

                    case MoveStep.LookUp:
                    {
                        // Held at the bottom clamp, as a mouse pushed all the way: Malbers eases the view there.
                        var follow = LiveFollow();
                        if (follow != null)
                            follow._cinemachineTargetPitch = follow.BottomClamp.Value;
                        SampleLookUp();
                        if (InMove > 0.8f && !_camShots.Contains(LookShot))
                            Shoot(LookShot);
                        if (InMove > 1.2f)
                        {
                            AimCamera(0f, CheckPitch);
                            NextMove(MoveStep.CrateSetup);
                        }

                        break;
                    }

                    case MoveStep.CrateSetup:
                        // A crate 3 m behind the player, the camera looking 10° up: the line from the shoulder back to the
                        // camera passes over the crate's top, but the camera, 6.5 m out, is beyond it and above the floor.
                        // It stays out there and the crate fades, as a wall in the way does.
                        if (first)
                            PlaceForCheck(Spot(CrateSpot), 0f, -CrateLookUp);
                        else if (InMove > 1.0f && Standing())
                        {
                            _crateClear = CameraToTarget();
                            _crateFade.renderer = PutCrate();
                            NextMove(MoveStep.CrateWatch);
                        }
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add("Never settled in front of the crate; looking up over it was skipped.");
                            NextMove(MoveStep.BlockSetup);
                        }

                        break;

                    case MoveStep.CrateWatch:
                    {
                        float distance = CameraToTarget();
                        if (!float.IsNaN(distance))
                            _crateNearest = float.IsNaN(_crateNearest) ? distance : Mathf.Min(_crateNearest, distance);
                        if (InMove > FadeSettle)
                            WatchFade(_crateFade);
                        if (InMove > 0.6f && !_camShots.Contains(CrateShot))
                            Shoot(CrateShot);
                        if (InMove > 1.0f)
                            NextMove(MoveStep.BlockSetup);
                        break;
                    }

                    case MoveStep.BlockSetup:
                        // Right up against a chest-high block (a ledge, a wall top, stacked crates), looking down over it
                        // 60 to 70°: the camera hangs from a point over the block's top, and must not be put in the body.
                        if (first)
                        {
                            BuildBlock();
                            PlaceForCheck(Spot(BlockSpot), 0f, BlockLookFrom);
                        }
                        else if (InMove > 1.0f && Standing())
                        {
                            _blockTried = true;
                            NextMove(MoveStep.BlockLook);
                        }
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add("Never settled against the block; looking down over it was skipped.");
                            NextMove(MoveStep.TunnelSetup);
                        }

                        break;

                    case MoveStep.BlockLook:
                    {
                        // Lowered as the mouse does it: Malbers eases the view after the pitch.
                        var follow = LiveFollow();
                        if (follow != null)
                            follow._cinemachineTargetPitch = Mathf.Lerp(BlockLookFrom, BlockLookTo, InMove / BlockSweep);
                        float distance = CameraToTarget();
                        if (!float.IsNaN(distance) && (float.IsNaN(_blockNearest) || distance < _blockNearest))
                        {
                            _blockNearest = distance;
                            _blockPitch = ViewPitch();
                        }
                        if (InMove > BlockSweep && !_camShots.Contains(BlockShot))
                            Shoot(BlockShot);
                        if (InMove > BlockSweep + 0.4f)
                            NextMove(MoveStep.TunnelSetup);
                        break;
                    }

                    case MoveStep.TunnelSetup:
                        // A tunnel in one collider - floor, walls and roof one mesh, as Game.unity's tunnel pieces are - and
                        // the player in it; and one made of two such pieces. Looking up as far as the camera goes, along it,
                        // turned towards either wall and by the right wall, the camera stays inside and keeps off the player:
                        // what the player stands in, and the piece that carries its floor on behind them, are solid to it.
                        if (first)
                        {
                            BuildTunnel();
                            PlaceForCheck(_tunnelFloor, 0f, 0f);
                        }
                        else if (InMove > 1.0f && Standing())
                        {
                            var ground = OnLiveCamera<CameraGroundCollider>();
                            _tunnelRadius = ground != null && ground.enabled ? ground.radius : 0f;
                            _tunnelTried = true;
                            ProbeOverlap(ground);
                            _tunnelCase = 0;
                            AimCamera(_tunnelViews[0].yaw, 0f);
                            NextMove(MoveStep.TunnelLook);
                        }
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add("Never settled in the tunnel; looking up in it was skipped.");
                            NextMove(MoveStep.BigWallSetup);
                        }

                        break;

                    case MoveStep.TunnelLook:
                    {
                        // From level up to the bottom of the pitch (or down to TunnelLookDown), as a mouse pushed all the way;
                        // then the next look, moving first if it stands somewhere else.
                        var view = _tunnelViews[_tunnelCase];
                        var follow = LiveFollow();
                        if (follow != null)
                            follow._cinemachineTargetPitch = Mathf.Lerp(0f, view.down ? TunnelLookDown : follow.BottomClamp.Value, InMove / TunnelSweep);
                        SampleTunnel(view);
                        if (InMove > TunnelSweep && !_camShots.Contains(view.shot))
                            Shoot(view.shot);
                        if (InMove > TunnelSweep + 0.4f)
                        {
                            if (++_tunnelCase >= _tunnelViews.Length)
                                NextMove(MoveStep.BigWallSetup);
                            else if (TunnelStand(_tunnelViews[_tunnelCase]) != TunnelStand(view))
                                NextMove(MoveStep.TunnelMove);
                            else
                            {
                                AimCamera(_tunnelViews[_tunnelCase].yaw, 0f);
                                NextMove(MoveStep.TunnelLook);
                            }
                        }

                        break;
                    }

                    case MoveStep.TunnelMove:
                        // To where the next look stands, settled before it.
                        if (first)
                            PlaceForCheck(TunnelStand(_tunnelViews[_tunnelCase]), 0f, 0f);
                        else if (InMove > 1.0f && Standing())
                        {
                            AimCamera(_tunnelViews[_tunnelCase].yaw, 0f);
                            NextMove(MoveStep.TunnelLook);
                        }
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add($"Never settled in the tunnel {_tunnelViews[_tunnelCase].way}; the looks from there on were skipped.");
                            NextMove(MoveStep.BigWallSetup);
                        }

                        break;

                    case MoveStep.BigWallSetup:
                        // A wall too big for CameraOccluderFade to fade, put right behind the player between them and the
                        // camera: gone through, it would hide them, so the camera has to stay in front of it.
                        if (first)
                            PlaceForCheck(Spot(BigWallSpot), 0f, CheckPitch);
                        else if (InMove > 1.0f && Standing())
                        {
                            _bigWallBefore = CameraToTarget();
                            var ground = OnLiveCamera<CameraGroundCollider>();
                            _bigWallRadius = ground != null && ground.enabled ? ground.radius : 0f;
                            BuildBigWall();
                            NextMove(MoveStep.BigWallWatch);
                        }
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add("Never settled in front of the big wall; the camera before it was not checked.");
                            NextMove(MoveStep.StairsSetup);
                        }

                        break;

                    case MoveStep.BigWallWatch:
                        // Judged once the camera had a frame to meet it; then the hall in its place.
                        if (InMove > 0.1f)
                            SampleBigWall(ref _bigWallFront, ref _bigWallNearest);
                        if (InMove > 0.6f && !_camShots.Contains(BigWallShot))
                            Shoot(BigWallShot);
                        if (InMove > 1.0f)
                        {
                            OutOfTheWay(_bigWallRenderer);
                            BuildBigHall();
                            NextMove(MoveStep.BigHallWatch);
                        }

                        break;

                    case MoveStep.BigHallWatch:
                        if (InMove > 0.1f)
                            SampleBigWall(ref _bigHallFront, ref _bigHallNearest);
                        if (InMove > 0.6f && !_camShots.Contains(BigHallShot))
                            Shoot(BigHallShot);
                        if (InMove > 1.0f)
                            NextMove(MoveStep.StairsSetup);
                        break;

                    case MoveStep.StairsSetup:
                        // A flight of 18 cm steps in one mesh, as First_Parkour's staircases are, walked down with the view
                        // level: behind the player the steps rise higher than the camera. It must not end up in them, nor
                        // pump in and out as the line to it passes one step's edge after another.
                        if (first)
                        {
                            BuildStairs();
                            PlaceForCheck(new Vector3(Spot(StairsSpot).x, _stairsTopY, _stairsFront - 0.6f), 0f, 0f);
                        }
                        else if (InMove > 1.0f && Standing())
                        {
                            // Walking, as a player picks their way down steps.
                            _stairsSpeedBefore = _animal.CurrentSpeedIndex;
                            _animal.Speed_CurrentIndex_Set(1);
                            Move(new Vector3(0f, 0f, 1f));
                            NextMove(MoveStep.StairsWalk);
                        }
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add("Never settled at the top of the stairs; walking down them was skipped.");
                            NextMove(MoveStep.TileSetup);
                        }

                        break;

                    case MoveStep.StairsWalk:
                    {
                        const float flight = (StairsCount - 1) * StairsTread;
                        float into = _animal.transform.position.z - _stairsFront;
                        // Malbers may pick its own speed again as the walk starts.
                        if (InMove < 0.5f && _animal.CurrentSpeedIndex != 1)
                            _animal.Speed_CurrentIndex_Set(1);
                        SampleStairs(into >= 0f && into <= flight + StairsBeyond);
                        if (into > flight - 0.6f && !_camShots.Contains(StairsShot))
                            Shoot(StairsShot);
                        if (into > flight + StairsBeyond || InMove > 7f)
                        {
                            _stairsWalked = Mathf.Max(0f, into);
                            if (into < flight)
                                _moveNotes.Add($"The walk down the stairs got {_stairsWalked:0.0} m of their {flight:0.0} m.");
                            Move(Vector3.zero);
                            if (_stairsSpeedBefore > 0)
                                _animal.Speed_CurrentIndex_Set(_stairsSpeedBefore);
                            NextMove(MoveStep.TileSetup);
                        }

                        break;
                    }

                    case MoveStep.TileSetup:
                        // A thin floor tile with no LODs, like First_Parkour's stone floors, the player on it and the
                        // camera a little above: crouching takes the camera target down close to it.
                        if (first)
                        {
                            var spot = Spot(TileSpot);
                            var tile = GameObject.CreatePrimitive(PrimitiveType.Cube);
                            tile.name = "Thin floor tile";
                            tile.transform.SetPositionAndRotation(spot + Vector3.up * (TileThickness / 2f), Quaternion.identity);
                            tile.transform.localScale = new Vector3(TileSize, TileThickness, TileSize);
                            _tileRenderer = tile.GetComponent<Renderer>();
                            _tileTop = spot.y + TileThickness;
                            _tileTried = true;
                            PlaceForCheck(spot + Vector3.up * (TileThickness + 0.02f), 0f, TilePitch);
                        }
                        else if (InMove > 1.2f && Standing())
                        {
                            _tileCrouch = CrouchStance();
                            if (_tileCrouch != null)
                            {
                                _animal.Stance_Set(_tileCrouch);
                                NextMove(MoveStep.TileCrouch);
                            }
                            else
                            {
                                _moveNotes.Add("No Crouch stance on this character: the floor tile was only stood on.");
                                NextMove(MoveStep.WallSetup);
                            }
                        }
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add("Never settled on the floor tile; crouching on it was skipped.");
                            NextMove(MoveStep.WallSetup);
                        }
                        else if (InMove > 0.6f)
                            SampleTile(crouched: false);

                        break;

                    case MoveStep.TileCrouch:
                        SampleTile(crouched: true);
                        if (InMove > 1.2f && !_camShots.Contains(TileShot))
                            Shoot(TileShot);
                        if (InMove > 1.8f)
                        {
                            _animal.Stance_Reset();
                            NextMove(MoveStep.TileStand);
                        }

                        break;

                    case MoveStep.TileStand:
                        // Standing up again before the next spot.
                        if (InMove > 0.8f)
                            NextMove(MoveStep.WallSetup);
                        break;

                    case MoveStep.WallSetup:
                        // A 3 m wall in front of the player, the camera behind them: they go up to it and stand
                        // pushing against it. It is never between the camera and the player, so it must not fade.
                        // Walking: at a run Malbers' Ledge Grab ("3M Grab") takes the top of a 3 m wall and climbs it.
                        if (first)
                        {
                            var spot = Spot(WallSpot);
                            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                            wall.name = "Wall in front";
                            wall.transform.SetPositionAndRotation(spot + new Vector3(0f, FrontWallHeight / 2f, FrontWallAhead + 0.15f), Quaternion.identity);
                            wall.transform.localScale = new Vector3(4f, FrontWallHeight, 0.3f);
                            _frontWallRenderer = wall.GetComponent<Renderer>();
                            _frontWallFace = spot.z + FrontWallAhead;
                            PlaceForCheck(spot, 0f, CheckPitch);
                        }
                        else if (InMove > 1.0f && Standing())
                        {
                            _frontSpeedBefore = _animal.CurrentSpeedIndex;
                            _animal.Speed_CurrentIndex_Set(1);
                            Move(new Vector3(0f, 0f, 1f));
                            NextMove(MoveStep.WallPush);
                        }
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add("Never settled in front of the wall; going up against it was skipped.");
                            NextMove(MoveStep.SideSetup);
                        }

                        break;

                    case MoveStep.WallPush:
                        if (InMove < 0.5f && _animal.CurrentSpeedIndex != 1)
                            _animal.Speed_CurrentIndex_Set(1);
                        SampleFrontWall();
                        if (InMove > 2.0f && !_camShots.Contains(WallShot))
                            Shoot(WallShot);
                        if (InMove > 2.8f)
                        {
                            Move(Vector3.zero);
                            if (_frontSpeedBefore > 0)
                                _animal.Speed_CurrentIndex_Set(_frontSpeedBefore);
                            NextMove(MoveStep.SideSetup);
                        }

                        break;

                    case MoveStep.SideSetup:
                        // A 3 m wall right beside the player along the way they run, the camera behind them: they run
                        // along it, hugging it as along a corridor wall. It is next to the view, never in it, so it must
                        // not fade. Built once the camera has settled, on the side away from the shoulder the camera
                        // sits over (on that side the camera would be behind the wall, and it would rightly fade).
                        if (first)
                            PlaceForCheck(Spot(SideSpot), 0f, CheckPitch);
                        else if (InMove > 1.0f && Standing())
                        {
                            BuildSideWall();
                            Move(new Vector3(0f, 0f, 1f));
                            NextMove(MoveStep.SideRun);
                        }
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add("Never settled beside the wall; running along it was skipped.");
                            NextMove(MoveStep.RampSetup);
                        }

                        break;

                    case MoveStep.SideRun:
                        SampleSideWall();
                        if (InMove > 1.0f && !_camShots.Contains(SideShot))
                            Shoot(SideShot);
                        if (InMove > 2.2f || _animal.transform.position.z > _sideWallTo - 1f)
                        {
                            Move(Vector3.zero);
                            NextMove(MoveStep.RampSetup);
                        }

                        break;

                    case MoveStep.RampSetup:
                        // A 20° ramp to run down. Each physics step drops the body a few centimetres and it is drawn
                        // gliding down in between, so the feet have to be put on the ramp where the body is drawn.
                        if (first)
                        {
                            BuildRamp();
                            PlaceForCheck(_rampTop + _rampDown * RampStart + Vector3.up * 0.05f, 0f, CheckPitch);
                        }
                        else if (InMove > 1.2f && Standing())
                        {
                            _rampRan = true;
                            _rampLeftWas = _rampRightWas = float.NaN;
                            Move(new Vector3(0f, 0f, 1f));
                            NextMove(MoveStep.RampRun);
                        }
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add("Never settled at the top of the ramp; running down it was skipped.");
                            NextMove(MoveStep.StepsSetup);
                        }

                        break;

                    case MoveStep.RampRun:
                    {
                        float along = Vector3.Dot(_animal.transform.position - _rampTop, _rampDown);
                        if (along > RampMeasureFrom && along < RampMeasureTo && Standing())
                            SampleRamp();
                        else
                            _rampLeftWas = _rampRightWas = float.NaN;
                        if (along > RampLength + 1f || InMove > 6f)
                        {
                            if (along < RampMeasureTo)
                                _moveNotes.Add($"The run down the ramp got {along:0.0} m of its {RampLength:0} m.");
                            Move(Vector3.zero);
                            NextMove(MoveStep.StepsSetup);
                        }

                        break;
                    }

                    case MoveStep.StepsSetup:
                        // Steps of 0.18 m to walk up: a shoe crossing a riser has a whole step more ground under it.
                        // With -liveStepsRepeat the walk comes back here for the next one.
                        if (first)
                        {
                            if (_stepsWalks == 0)
                                BuildSteps();
                            PlaceForCheck(Spot(StepsSpot) - Vector3.forward * 2f, 0f, CheckPitch);
                        }
                        else if (InMove > 1.0f && Standing())
                        {
                            // Walking, as a player picks their way up steps.
                            _stepsSpeedBefore = _animal.CurrentSpeedIndex;
                            _animal.Speed_CurrentIndex_Set(1);
                            _stepsFrom = _animal.transform.position.y;
                            _stepsLeftWas = _stepsRightWas = _stepsLeftFixWas = _stepsRightFixWas = _stepsDrawnWas = _stepsRootWas = float.NaN;
                            _stepsFrameWas = -1;
                            _stepsFixedWas = float.NaN;
                            Move(new Vector3(0f, 0f, 1f));
                            NextMove(MoveStep.StepsWalk);
                        }
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add("Never settled in front of the steps; walking up them was skipped.");
                            NextMove(MoveStep.StepsRunSetup);
                        }

                        break;

                    case MoveStep.StepsWalk:
                    {
                        const float flight = (StepCount - 1) * StepTread;
                        float into = _animal.transform.position.z - Spot(StepsSpot).z;
                        // Malbers may pick its own speed again as the walk starts.
                        if (InMove < 0.5f && _animal.CurrentSpeedIndex != 1)
                            _animal.Speed_CurrentIndex_Set(1);
                        else if (_stepsSpeed < 0)
                            _stepsSpeed = _animal.CurrentSpeedIndex;
                        var row = LogStepsFrame(into);
                        if (into > -0.6f && into < flight + 0.6f && Standing())
                            SampleSteps(row);
                        else
                            _stepsLeftWas = _stepsRightWas = _stepsLeftFixWas = _stepsRightFixWas = _stepsDrawnWas = _stepsRootWas = float.NaN;
                        if (into > flight + 1.2f || InMove > 7f)
                        {
                            _stepsRise = _animal.transform.position.y - _stepsFrom;
                            if (into < flight)
                                _moveNotes.Add($"The walk up the steps got {Mathf.Max(0f, into):0.0} m of their {flight:0.0} m" +
                                               (StepsWalks > 1 ? $" (walk {_stepsWalks + 1})." : "."));
                            Move(Vector3.zero);
                            if (_stepsSpeedBefore > 0)
                                _animal.Speed_CurrentIndex_Set(_stepsSpeedBefore);
                            _stepsWalks++;
                            NextMove(_stepsWalks < StepsWalks ? MoveStep.StepsSetup : MoveStep.StepsRunSetup);
                        }

                        break;
                    }

                    case MoveStep.StepsRunSetup:
                        // The same steps run up, at Malbers' own running speed as on the ramp: a run-up first, so the
                        // shoes sweep up the flight at speed.
                        if (first)
                            PlaceForCheck(Spot(StepsSpot) - Vector3.forward * StepsRunUp, 0f, CheckPitch);
                        else if (InMove > 1.0f && Standing())
                        {
                            _stepsRunTried = true;
                            _stepsRunHasWas = false;
                            Move(new Vector3(0f, 0f, 1f));
                            NextMove(MoveStep.StepsRun);
                        }
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add("Never settled in front of the steps; running up them was skipped.");
                            NextMove(MoveStep.FlightSetup);
                        }

                        break;

                    case MoveStep.StepsRun:
                    {
                        // From the first riser to a metre onto the landing: the plants on the treads, and the first on the
                        // landing after the last riser.
                        const float flight = (StepCount - 1) * StepTread;
                        float into = _animal.transform.position.z - Spot(StepsSpot).z;
                        if (into > 0f && into < flight + 1f && Standing())
                            SampleStepsRun();
                        else
                            _stepsRunHasWas = false;
                        if (into > flight + 1.2f || InMove > 4f)
                        {
                            if (into < flight)
                                _moveNotes.Add($"The run up the steps got {Mathf.Max(0f, into):0.0} m of their {flight:0.0} m.");
                            Move(Vector3.zero);
                            NextMove(MoveStep.FlightSetup);
                        }

                        break;
                    }

                    case MoveStep.FlightSetup:
                    {
                        // Stairs as the levels have them, one way over one flight at a time (see FlightRun): walking at
                        // Malbers' walking speed, as up the level steps, or running at its own running speed.
                        if (_flightCase >= _flightRuns.Length)
                        {
                            NextMove(MoveStep.TileSprintSetup);
                            break;
                        }

                        var course = _flightRuns[_flightCase];
                        if (first)
                        {
                            if (!_flightsBuilt)
                                BuildFlights();
                            if (course.real && !_realBuilt)
                            {
                                _flightCase++;
                                NextMove(MoveStep.FlightSetup);
                                break;
                            }

                            PlaceForCheck(FlightStart(course), course.down ? 180f : 0f, CheckPitch);
                        }
                        else if (InMove > 1.0f && Standing())
                        {
                            course.tried = true;
                            course.Forget();
                            _flightFrameWas = -1;
                            _flightFixedWas = float.NaN;
                            _flightSpeedBefore = _animal.CurrentSpeedIndex;
                            if (!course.run)
                                _animal.Speed_CurrentIndex_Set(1);
                            Move(new Vector3(0f, 0f, 1f));
                            NextMove(MoveStep.FlightGo);
                        }
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add($"Never settled for {course.what}; it was skipped.");
                            _flightCase++;
                            NextMove(MoveStep.FlightSetup);
                        }

                        break;
                    }

                    case MoveStep.FlightGo:
                    {
                        var course = _flightRuns[_flightCase];
                        float length = FlightLength(course), along = FlightAlong(course, _animal.transform.position);
                        // Malbers may pick its own speed again as the walk starts.
                        if (!course.run && InMove < 0.5f && _animal.CurrentSpeedIndex != 1)
                            _animal.Speed_CurrentIndex_Set(1);
                        else if (course.speed < 0)
                            course.speed = _animal.CurrentSpeedIndex;
                        var row = LogFrame(along, _flightCase + 1, _flightLog, ref _flightFrameWas, ref _flightFixedWas);
                        if (along > -0.6f && along < length + 0.6f && Standing())
                            SampleFlight(course, row);
                        else
                            course.Forget();
                        if (along > length + 1.2f || InMove > (course.run ? 5f : 8f))
                        {
                            course.walked = Mathf.Max(0f, along);
                            if (along < length)
                                _moveNotes.Add($"{Capital(course.what)} got {course.walked:0.0} m of its {length:0.0} m.");
                            Move(Vector3.zero);
                            if (_flightSpeedBefore > 0)
                                _animal.Speed_CurrentIndex_Set(_flightSpeedBefore);
                            _flightCase++;
                            NextMove(MoveStep.FlightSetup);
                        }

                        break;
                    }

                    case MoveStep.TileSprintSetup:
                        // A floor tile 4 cm thick lying on the floor, as the levels' stone tiles do, to sprint across: a
                        // sprint plants the toes deeper than the tile is thick, and a toe through its underside still has to
                        // be put on its top.
                        if (first)
                        {
                            BuildSprintTile();
                            PlaceForCheck(Spot(SprintTileSpot), 0f, CheckPitch);
                        }
                        else if (InMove > 1.0f && Standing())
                        {
                            _sprintTileRan = true;
                            Move(new Vector3(0f, 0f, 1f));
                            NextMove(MoveStep.TileSprint);
                        }
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add("Never settled before the floor tile; sprinting across it was skipped.");
                            NextMove(MoveStep.SlowSetup);
                        }

                        break;

                    case MoveStep.TileSprint:
                    {
                        // Sprinting once the body moves: Malbers only takes the sprint while it does.
                        if (InMove > 0.2f)
                            _animal.Sprint = true;
                        if (_animal.Sprint)
                            _sprintTileSprinted = true;
                        float z = _animal.transform.position.z;
                        if (z > _sprintTileFrom + 0.8f && z < _sprintTileTo - 0.8f)
                            SampleSoles(SprintTileKey);
                        if (z > _sprintTileTo + 1f || InMove > 6f)
                        {
                            if (z < _sprintTileTo)
                                _moveNotes.Add($"The sprint across the floor tile got {Mathf.Max(0f, z - _sprintTileFrom):0.0} m of its {SprintTileLength:0} m.");
                            _animal.Sprint = false;
                            Move(Vector3.zero);
                            NextMove(MoveStep.SlowSetup);
                        }

                        break;
                    }

                    case MoveStep.SlowSetup:
                        // A slow screen, 30 frames a second: most frames run two physics steps, and the pose must
                        // still be shown between the same two steps as the body.
                        if (first)
                        {
                            PlaceForCheck(Spot(SlowSpot), 0f, CheckPitch);
                            StartStepRecorder();
                        }
                        else if (InMove > 1.0f && Standing())
                        {
                            _holdFps = SlowFps;
                            Move(new Vector3(0f, 0f, 1f));
                            NextMove(MoveStep.SlowRun);
                        }
                        else if (InMove > 5f)
                        {
                            _moveNotes.Add("Never settled for the slow-screen run; it was skipped.");
                            StopStepRecorder();
                            NextMove(MoveStep.Done);
                        }

                        break;

                    case MoveStep.SlowRun:
                        if (InMove > 0.6f)
                            SampleSlowPose();
                        else
                            _physicsStepsWas = _physicsSteps;
                        if (InMove > 2.8f)
                        {
                            _holdFps = 144f;
                            Move(Vector3.zero);
                            StopStepRecorder();
                            NextMove(MoveStep.Done);
                        }

                        break;

                    case MoveStep.Done:
                        Move(Vector3.zero);
                        CheckStockRig(atEnd: true);
                        JudgeMovement();
                        return true;
                }

                return false;
            }

            void SetUpMovement()
            {
                _brain = Object.FindFirstObjectByType<CinemachineBrain>();
                _moveCamera = _brain != null ? _brain.OutputCamera : Camera.main;
                if (_moveCamera == null)
                    _moveNotes.Add("No camera found: directions are judged against the body instead.");
                int brains = Object.FindObjectsByType<CinemachineBrain>(FindObjectsSortMode.None).Length;
                if (brains > 1)
                    _moveNotes.Add($"{brains} camera brains in the scene (more than one camera rig).");
                CheckStockRig(atEnd: false);
            }

            CameraOccluderFade Fader => _moveCamera != null ? _moveCamera.GetComponent<CameraOccluderFade>() : null;

            Vector3 CameraTarget()
            {
                var live = _brain != null ? _brain.ActiveVirtualCamera as CinemachineVirtualCameraBase : null;
                var follow = live != null ? live.Follow : null;
                return follow != null ? follow.position : _animal.transform.position + Vector3.up * 1.3f;
            }

            float CameraToTarget() => _moveCamera != null ? Vector3.Distance(_moveCamera.transform.position, CameraTarget()) : float.NaN;

            /// <summary>A wall half way between the camera and the player, square to the view.</summary>
            void PutWall()
            {
                if (_moveCamera == null)
                    return;
                var target = CameraTarget();
                var eye = _moveCamera.transform.position;
                var middle = Vector3.Lerp(target, eye, 0.5f);
                var flat = FlatV(eye - target).normalized;
                _camWall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _camWall.name = "Wall in the way";
                _camWall.transform.SetPositionAndRotation(new Vector3(middle.x, 1.5f, middle.z), Quaternion.LookRotation(flat, Vector3.up));
                _camWall.transform.localScale = new Vector3(4f, 3f, 0.3f);
                _camWallRenderer = _camWall.GetComponent<Renderer>();
                _camWallOwn = _camWallRenderer.sharedMaterial;
            }

            /// <summary>What the game camera sees, saved beside the report.</summary>
            void Shoot(string name)
            {
                if (_moveCamera == null)
                    return;
                var rt = RenderTexture.GetTemporary(960, 540, 24, RenderTextureFormat.ARGB32);
                var before = _moveCamera.targetTexture;
                var active = RenderTexture.active;
                try
                {
                    _moveCamera.targetTexture = rt;
                    _moveCamera.Render();
                    RenderTexture.active = rt;
                    var picture = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    picture.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    picture.Apply();
                    var folder = System.IO.Path.Combine(_job.folder, "camera");
                    System.IO.Directory.CreateDirectory(folder);
                    var path = System.IO.Path.Combine(folder, name + ".png");
                    System.IO.File.WriteAllBytes(path, picture.EncodeToPNG());
                    Object.DestroyImmediate(picture);
                    _camShots.Add(name);
                }
                finally
                {
                    _moveCamera.targetTexture = before;
                    RenderTexture.active = active;
                    RenderTexture.ReleaseTemporary(rt);
                }
            }

            /// <summary>Sleeps out the rest of a frame at the held frame rate (144 Hz, or 30 for the slow screen), so the frame timing is a real screen's.</summary>
            void HoldFrameRate()
            {
                double frameMs = 1000.0 / _holdFps;
                double now = _frameClock.Elapsed.TotalMilliseconds;
                if (_lastFrameMs >= 0)
                {
                    double wait = frameMs - (now - _lastFrameMs);
                    if (wait > 1.0)
                        System.Threading.Thread.Sleep((int)wait);
                }

                _lastFrameMs = _frameClock.Elapsed.TotalMilliseconds;
            }

            /// <summary>Turns the live Malbers camera to <paramref name="yaw"/>, the way the mouse does.</summary>
            void TurnCamera(float yaw)
            {
                var live = _brain != null ? _brain.ActiveVirtualCamera as Component : null;
                var follow = live != null ? live.GetComponent<MalbersAnimations.ThirdPersonFollowTarget>() : null;
                var targets = follow != null
                    ? new[] { follow }
                    : Object.FindObjectsByType<MalbersAnimations.ThirdPersonFollowTarget>(FindObjectsSortMode.None);
                foreach (var t in targets)
                    t._cinemachineTargetYaw = yaw;
                _moveNotes.Add($"Camera turned to {yaw:0}° on {(follow != null ? follow.name : targets.Length + " follow targets")} before running.");
            }

            /// <summary>Once per frame while moving: where the body went, where the camera looks, how smooth it is.</summary>
            void RecordTravel(List<Vector3> directions, List<float> cameraYaw, HashSet<string> cameras, string smoothKey)
            {
                var position = _animal.transform.position;
                var step = FlatV(position - _movePrevious);
                _movePrevious = position;
                if (Time.deltaTime > 0f && step.magnitude / Time.deltaTime > 0.5f)
                    directions.Add(step.normalized);
                if (_moveCamera != null)
                    cameraYaw.Add(Yaw(FlatV(_moveCamera.transform.forward)));
                cameras.Add(LiveCamera);
                SampleSmooth(smoothKey);
            }

            void SampleSmooth(string key)
            {
                if (_moveCamera == null)
                    return;
                if (!_smooth.TryGetValue(key, out var s))
                    _smooth[key] = s = new Smooth();

                var hips = _animal.Anim.GetBoneTransform(HumanBodyBones.Hips);
                var leg = _animal.Anim.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
                if (hips == null)
                    return;

                var viewport = _moveCamera.WorldToViewportPoint(hips.position);
                s.screen.Add(new Vector2(viewport.x * 1920f, viewport.y * 1080f));
                s.times.Add(Now);
                s.frames++;
                if (leg != null)
                {
                    if (s.hasPose && Quaternion.Angle(s.lastPose, leg.localRotation) > 0.01f)
                        s.poseChanged++;
                    s.lastPose = leg.localRotation;
                    s.hasPose = true;
                }
            }

            /// <summary>The window a stop's speed is taken over (s), and the speed under which the body stands still (m/s): 1 cm in 0.1 s.</summary>
            const float StillWindow = 0.1f, StillSpeed = 0.1f;

            /// <summary>
            /// A body coming to a stop. Its speed is taken over the last StillWindow, not from one frame to the
            /// next: Malbers moves the root only in physics steps, 50 a second, so on a 144 Hz screen it moves
            /// either not at all or a whole step between two frames. Taken frame by frame the speed read nothing
            /// on two frames in three and three times the real speed on the third, so a stop counted only under
            /// about 3.5 cm/s, later than its rule says.
            /// </summary>
            class StillWatch
            {
                public readonly List<(float time, Vector3 position)> samples = new List<(float, Vector3)>();

                /// <summary>When the body began to stand still; below 0 while it moves.</summary>
                public float since = -1f;

                /// <summary>Where it was then.</summary>
                public Vector3 where;

                /// <summary>Starts watching from where the body is now.</summary>
                public void Begin(float time, Vector3 position)
                {
                    samples.Clear();
                    samples.Add((time, position));
                    since = -1f;
                }

                /// <summary>When the body began to stand still, or <paramref name="now"/> if it has not.</summary>
                public float When(float now) => since >= 0f ? since : now;

                /// <summary>Where the body began to stand still, or <paramref name="now"/> if it has not.</summary>
                public Vector3 Where(Vector3 now) => since >= 0f ? where : now;
            }

            /// <summary>
            /// True once the body has stood still - moved under 1 cm in every 0.1 s - for a quarter of a second
            /// from when it began to. The watch keeps when and where that was, so a stop's time and distance end
            /// there and leave out the creep while it is confirmed.
            /// </summary>
            bool WatchStill(StillWatch watch)
            {
                var position = _animal.transform.position;
                var samples = watch.samples;
                samples.Add((Now, position));
                // The oldest sample kept is the newest one a whole window old.
                while (samples.Count > 1 && Now - samples[1].time >= StillWindow)
                    samples.RemoveAt(0);

                var (time, from) = samples[0];
                float span = Now - time;
                if (span < StillWindow)
                    return false;

                if (FlatV(position - from).magnitude / span < StillSpeed)
                {
                    if (watch.since < 0f)
                    {
                        watch.since = time;
                        watch.where = from;
                    }

                    return Now - watch.since > 0.25f;
                }

                watch.since = -1f;
                return false;
            }

            // ---- Feet ----------------------------------------------------------------------------------

            /// <summary>The vertices within 3 cm of the bottom of each shoe, standing: the soles.</summary>
            void PickSoles()
            {
                _skins.Clear();
                _leftSole.Clear();
                _rightSole.Clear();
                if (_bake == null)
                    _bake = new Mesh();

                var leftFoot = _animal.Anim.GetBoneTransform(HumanBodyBones.LeftFoot);
                var rightFoot = _animal.Anim.GetBoneTransform(HumanBodyBones.RightFoot);
                if (leftFoot == null || rightFoot == null)
                {
                    _moveNotes.Add("No foot bones: feet not measured.");
                    return;
                }

                foreach (var skin in _animal.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    if (!skin.enabled || skin.sharedMesh == null || !skin.gameObject.activeInHierarchy)
                        continue;
                    skin.BakeMesh(_bake, false);
                    _bake.GetVertices(_bakeVertices);
                    var m = Matrix4x4.TRS(skin.transform.position, skin.transform.rotation, Vector3.one);

                    float bottom = float.MaxValue;
                    var world = new Vector3[_bakeVertices.Count];
                    for (int i = 0; i < world.Length; i++)
                    {
                        world[i] = m.MultiplyPoint3x4(_bakeVertices[i]);
                        bottom = Mathf.Min(bottom, world[i].y);
                    }

                    var left = new List<int>();
                    var right = new List<int>();
                    for (int i = 0; i < world.Length; i++)
                    {
                        if (world[i].y > bottom + 0.15f)
                            continue;
                        float toLeft = (world[i] - leftFoot.position).sqrMagnitude, toRight = (world[i] - rightFoot.position).sqrMagnitude;
                        if (Mathf.Min(toLeft, toRight) > 0.3f * 0.3f)
                            continue;
                        (toLeft <= toRight ? left : right).Add(i);
                    }

                    if (left.Count + right.Count == 0)
                        continue;
                    _skins.Add(skin);
                    _leftSole.Add(left.ToArray());
                    _rightSole.Add(right.ToArray());
                }

                if (_skins.Count == 0)
                    _moveNotes.Add("No shoe vertices found: feet not measured.");
            }

            /// <summary>
            /// Each foot's lowest point against the ground right under it (m; below zero is in it). With
            /// <paramref name="skate"/>, also how far a shoe resting on the floor (lowest point within 2 cm of
            /// it) slides along it: the middle of its sole, from frame to frame.
            /// </summary>
            void SampleSoles(string key, bool skate = false)
            {
                if (_skins.Count == 0)
                    return;

                float leftLow = float.MaxValue, rightLow = float.MaxValue;
                Vector3 leftAt = default, rightAt = default, leftMid = Vector3.zero, rightMid = Vector3.zero;
                int leftCount = 0, rightCount = 0;
                for (int s = 0; s < _skins.Count; s++)
                {
                    var skin = _skins[s];
                    skin.BakeMesh(_bake, false);
                    _bake.GetVertices(_bakeVertices);
                    var m = Matrix4x4.TRS(skin.transform.position, skin.transform.rotation, Vector3.one);
                    foreach (int i in _leftSole[s])
                    {
                        if (i >= _bakeVertices.Count)
                            continue;
                        var p = m.MultiplyPoint3x4(_bakeVertices[i]);
                        leftMid += p;
                        leftCount++;
                        if (p.y < leftLow) { leftLow = p.y; leftAt = p; }
                    }
                    foreach (int i in _rightSole[s])
                    {
                        if (i >= _bakeVertices.Count)
                            continue;
                        var p = m.MultiplyPoint3x4(_bakeVertices[i]);
                        rightMid += p;
                        rightCount++;
                        if (p.y < rightLow) { rightLow = p.y; rightAt = p; }
                    }
                }

                if (leftLow == float.MaxValue || rightLow == float.MaxValue)
                    return;

                float left = leftLow - GroundUnder(leftAt);
                float right = rightLow - GroundUnder(rightAt);
                if (!_soles.TryGetValue(key, out var list))
                    _soles[key] = list = new List<Vector2>();
                list.Add(new Vector2(left, right));

                if (!skate)
                    return;
                leftMid /= Mathf.Max(1, leftCount);
                rightMid /= Mathf.Max(1, rightCount);
                bool leftPlanted = left < 0.02f, rightPlanted = right < 0.02f;
                if (_skateKey != key)
                {
                    _skateKey = key;
                    _leftPlantedWas = _rightPlantedWas = false;
                }

                float slid = 0f;
                if (leftPlanted && _leftPlantedWas)
                    slid += FlatV(leftMid - _leftSoleWas).magnitude;
                if (rightPlanted && _rightPlantedWas)
                    slid += FlatV(rightMid - _rightSoleWas).magnitude;
                _skate[key] = (_skate.TryGetValue(key, out float sum) ? sum : 0f) + slid;
                _skateTime[key] = (_skateTime.TryGetValue(key, out float time) ? time : 0f) + Time.deltaTime;
                _leftSoleWas = leftMid;
                _rightSoleWas = rightMid;
                _leftPlantedWas = leftPlanted;
                _rightPlantedWas = rightPlanted;
            }

            float GroundUnder(Vector3 point)
            {
                var hits = Physics.RaycastAll(point + Vector3.up * 0.6f, Vector3.down, 2f, ~0, QueryTriggerInteraction.Ignore);
                float best = float.NegativeInfinity;
                foreach (var hit in hits)
                {
                    if (hit.collider.transform.IsChildOf(_animal.transform))
                        continue;
                    if (hit.point.y > best)
                        best = hit.point.y;
                }

                return float.IsNegativeInfinity(best) ? _groundY : best;
            }

            /// <summary>
            /// How far each shoe is over the ground (m; below zero is in it): the least of its sole vertices'
            /// heights, each over the ground right under that vertex, so a shoe lying tilted on a slope reads 0.
            /// False before the soles are picked.
            /// </summary>
            bool SoleClearance(Func<Vector3, float> groundAt, out float left, out float right)
            {
                left = right = float.MaxValue;
                for (int s = 0; s < _skins.Count; s++)
                {
                    var skin = _skins[s];
                    skin.BakeMesh(_bake, false);
                    _bake.GetVertices(_bakeVertices);
                    var m = Matrix4x4.TRS(skin.transform.position, skin.transform.rotation, Vector3.one);
                    foreach (int i in _leftSole[s])
                    {
                        if (i >= _bakeVertices.Count)
                            continue;
                        var p = m.MultiplyPoint3x4(_bakeVertices[i]);
                        left = Mathf.Min(left, p.y - groundAt(p));
                    }

                    foreach (int i in _rightSole[s])
                    {
                        if (i >= _bakeVertices.Count)
                            continue;
                        var p = m.MultiplyPoint3x4(_bakeVertices[i]);
                        right = Mathf.Min(right, p.y - groundAt(p));
                    }
                }

                return left < float.MaxValue && right < float.MaxValue;
            }

            /// <summary>
            /// Each foot's up in its own bone's space, standing on flat ground (summed over the frames): how a resting
            /// shoe lies across a slope is judged against how the same pose lies on the floor.
            /// </summary>
            void SampleFlatUp()
            {
                var leftFoot = _animal.Anim.GetBoneTransform(HumanBodyBones.LeftFoot);
                var rightFoot = _animal.Anim.GetBoneTransform(HumanBodyBones.RightFoot);
                if (leftFoot == null || rightFoot == null)
                    return;
                _leftFlatUp += Quaternion.Inverse(leftFoot.rotation) * Vector3.up;
                _rightFlatUp += Quaternion.Inverse(rightFoot.rotation) * Vector3.up;
            }

            /// <summary>
            /// Each frame standing across a slope facing <paramref name="normal"/>: how far each shoe's sole lies off it
            /// (degrees). A shoe turned to lie on it is off by what the idle pose sways; one left level, by the slope.
            /// </summary>
            void SampleSlopeLie(string key, Vector3 normal)
            {
                var leftFoot = _animal.Anim.GetBoneTransform(HumanBodyBones.LeftFoot);
                var rightFoot = _animal.Anim.GetBoneTransform(HumanBodyBones.RightFoot);
                if (leftFoot == null || rightFoot == null || _leftFlatUp.sqrMagnitude < 1e-6f || _rightFlatUp.sqrMagnitude < 1e-6f)
                    return;
                if (!_slopeLie.TryGetValue(key, out var list))
                    _slopeLie[key] = list = new List<Vector2>();
                list.Add(new Vector2(Vector3.Angle(leftFoot.rotation * _leftFlatUp.normalized, normal),
                                     Vector3.Angle(rightFoot.rotation * _rightFlatUp.normalized, normal)));
            }

            // ---- Checks that build their own geometry --------------------------------------------------

            /// <summary>A spot of the floor plan at the floor's height.</summary>
            Vector3 Spot(Vector3 spot) => new Vector3(spot.x, _groundY + spot.y, spot.z);

            /// <summary>
            /// The player at <paramref name="position"/> facing <paramref name="yaw"/>, standing still, with the
            /// camera straight behind them pitched <paramref name="pitch"/> degrees down.
            /// </summary>
            void PlaceForCheck(Vector3 position, float yaw, float pitch)
            {
                Move(Vector3.zero);
                PlaceBody(_animal, position, yaw);
                // ThirdPersonPlayer snaps its cameras after a teleport; without it they are snapped here (twice
                // would lose their damping: each snap keeps the damping it finds to put back).
                if (_tps == null)
                {
                    foreach (var follow in Object.FindObjectsByType<MalbersAnimations.ThirdPersonFollowTarget>(FindObjectsSortMode.None))
                    {
                        if (follow.isActiveAndEnabled)
                            follow.TargetTeleport();
                    }
                }

                AimCamera(yaw, pitch);
            }

            /// <summary>A component on the camera the brain films with now.</summary>
            T OnLiveCamera<T>() where T : Component
            {
                var live = _brain != null ? _brain.ActiveVirtualCamera as Component : null;
                return live != null ? live.GetComponent<T>() : null;
            }

            /// <summary>The live camera's Malbers follow target: what the mouse turns.</summary>
            MalbersAnimations.ThirdPersonFollowTarget LiveFollow() => OnLiveCamera<MalbersAnimations.ThirdPersonFollowTarget>();

            /// <summary>Turns the camera at once to <paramref name="yaw"/> and <paramref name="pitch"/> (down +), the way the mouse does.</summary>
            void AimCamera(float yaw, float pitch)
            {
                var live = LiveFollow();
                var targets = live != null
                    ? new[] { live }
                    : Object.FindObjectsByType<MalbersAnimations.ThirdPersonFollowTarget>(FindObjectsSortMode.None);
                foreach (var t in targets)
                {
                    t._cinemachineTargetYaw = yaw;
                    t._cinemachineTargetPitch = pitch;
                    // Malbers eases its pivot towards those; put there at once, the camera starts settled.
                    if (t.CamPivot != null)
                        t.CamPivot.rotation = Quaternion.Euler(pitch, yaw, 0f);
                }
            }

            /// <summary>The top of what is under <paramref name="point"/>, the player left out: above the point, if it is under the floor.</summary>
            float FloorAt(Vector3 point)
            {
                var from = new Vector3(point.x, Mathf.Max(point.y, _groundY) + 5f, point.z);
                float best = float.NegativeInfinity;
                foreach (var hit in Physics.RaycastAll(from, Vector3.down, 50f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (!hit.collider.transform.IsChildOf(_animal.transform))
                        best = Mathf.Max(best, hit.point.y);
                }

                return float.IsNegativeInfinity(best) ? _groundY : best;
            }

            /// <summary>Each frame looking up: how high the camera is over the floor under it, and how far up it looks.</summary>
            void SampleLookUp()
            {
                if (_moveCamera == null)
                    return;
                var view = _moveCamera.transform;
                float height = view.position.y - FloorAt(view.position);
                if (float.IsNaN(_lookLowest) || height < _lookLowest)
                {
                    _lookLowest = height;
                    _lookDistance = CameraToTarget();
                }

                float up = Mathf.Asin(Mathf.Clamp(view.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
                _lookPitch = float.IsNaN(_lookPitch) ? up : Mathf.Max(_lookPitch, up);
            }

            /// <summary>Malbers' Crouch stance on this character, or null.</summary>
            MalbersAnimations.StanceID CrouchStance()
            {
                var stance = _animal.Stances?.FirstOrDefault(s => s != null && s.ID != null && s.ID.name == "Crouch");
                return stance?.ID;
            }

            /// <summary>Each frame on the floor tile: whether the camera fades it, and how low the camera target comes over it.</summary>
            void SampleTile(bool crouched)
            {
                bool faded = Fader != null && _tileRenderer != null && Fader.IsFaded(_tileRenderer);
                if (!crouched)
                {
                    _tileStanding++;
                    if (faded)
                        _tileStandingFaded++;
                    return;
                }

                if (_animal.Stance != _tileCrouch)
                    return;
                _tileCrouched++;
                if (faded)
                    _tileCrouchedFaded++;
                float target = CameraTarget().y - _tileTop;
                _tileTargetLow = float.IsNaN(_tileTargetLow) ? target : Mathf.Min(_tileTargetLow, target);
            }

            /// <summary>Each frame going up against the wall in front: how near the body's middle is to its face, and whether it fades.</summary>
            void SampleFrontWall()
            {
                float gap = _frontWallFace - _animal.transform.position.z;
                _frontFrames++;
                _frontClosest = float.IsNaN(_frontClosest) ? gap : Mathf.Min(_frontClosest, gap);
                if (gap < AgainstWall)
                    _frontAgainst++;
                if (Fader != null && _frontWallRenderer != null && Fader.IsFaded(_frontWallRenderer))
                    _frontFaded++;
            }

            /// <summary>
            /// A wall along the run (+z) right beside the player, its face SideWallGap from the body's middle, starting
            /// just behind them. It goes on the side away from the camera: the camera sits over one shoulder, and on that
            /// side it would be behind the wall.
            /// </summary>
            void BuildSideWall()
            {
                var at = _animal.transform.position;
                float shoulder = _moveCamera != null ? Vector3.Dot(_moveCamera.transform.position - at, Vector3.right) : 1f;
                float side = shoulder >= 0f ? -1f : 1f;
                _sideWhich = side < 0f ? "left" : "right";
                _sideWallFace = at.x + side * SideWallGap;
                _sideWallFrom = at.z - 1f;
                _sideWallTo = _sideWallFrom + SideWallLength;
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = "Wall beside";
                wall.transform.SetPositionAndRotation(
                    new Vector3(_sideWallFace + side * 0.15f, _groundY + SideWallHeight / 2f, _sideWallFrom + SideWallLength / 2f), Quaternion.identity);
                wall.transform.localScale = new Vector3(0.3f, SideWallHeight, SideWallLength);
                _sideWallRenderer = wall.GetComponent<Renderer>();
            }

            /// <summary>Each frame running along the wall beside: how near the body's middle is to its face, and whether it fades.</summary>
            void SampleSideWall()
            {
                if (_sideWallRenderer == null)
                    return;
                var at = _animal.transform.position;
                if (at.z < _sideWallFrom + 0.5f || at.z > _sideWallTo - 0.5f)
                    return;
                _sideFrames++;
                float gap = Mathf.Abs(_sideWallFace - at.x);
                _sideClosest = float.IsNaN(_sideClosest) ? gap : Mathf.Min(_sideClosest, gap);
                if (Fader != null && Fader.IsFaded(_sideWallRenderer))
                    _sideFaded++;
            }

            /// <summary>A ramp down to the floor along +z, its top edge over RampSpot.</summary>
            void BuildRamp()
            {
                var tilt = Quaternion.Euler(RampAngle, 0f, 0f);
                _rampDown = tilt * Vector3.forward;
                _rampNormal = tilt * Vector3.up;
                _rampTop = Spot(RampSpot) + Vector3.up * (RampLength * Mathf.Sin(RampAngle * Mathf.Deg2Rad));
                var ramp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                ramp.name = "Ramp";
                // Its top face runs from the top edge down to the floor; the rest of the box is under it.
                ramp.transform.SetPositionAndRotation(_rampTop + _rampDown * (RampLength / 2f) - _rampNormal * (RampThickness / 2f), tilt);
                ramp.transform.localScale = new Vector3(RampWidth, RampThickness, RampLength);
            }

            /// <summary>The height of the ramp's top face right under (or over) <paramref name="point"/>.</summary>
            float RampHeight(Vector3 point) =>
                _rampTop.y - (_rampNormal.x * (point.x - _rampTop.x) + _rampNormal.z * (point.z - _rampTop.z)) / _rampNormal.y;

            /// <summary>Each frame on the ramp: both soles against it, into the feet table, and the resting ones on their own.</summary>
            void SampleRamp()
            {
                if (!SoleClearance(RampHeight, out float left, out float right))
                    return;
                if (!_soles.TryGetValue(RampKey, out var list))
                    _soles[RampKey] = list = new List<Vector2>();
                list.Add(new Vector2(left, right));

                var position = _animal.transform.position;
                if (_rampFromAt < 0f)
                {
                    _rampFromAt = Now;
                    _rampFrom = position;
                }
                else if (Now > _rampFromAt)
                    _rampSpeed = Vector3.Distance(position, _rampFrom) / (Now - _rampFromAt);

                // How far over its physics root the body is drawn as it glides down between steps (up to one step's
                // drop): what feet put on the ground against the root instead hover by.
                var smoother = _animal.GetComponent<CharacterVisualInterpolator>();
                float drawn = smoother != null && smoother.isActiveAndEnabled ? smoother.ShownPosition.y - position.y : float.NaN;
                if (!float.IsNaN(drawn))
                    _rampDrawnHigh = float.IsNaN(_rampDrawnHigh) ? drawn : Mathf.Max(_rampDrawnHigh, drawn);

                RestingSole(left, drawn, ref _rampLeftWas);
                RestingSole(right, drawn, ref _rampRightWas);
            }

            /// <summary>
            /// A sole within RestingClearance of the ramp rests on it: its height, how far it moved since the last
            /// frame, and the height against how far the body is drawn over its physics root (<paramref name="drawn"/>).
            /// </summary>
            void RestingSole(float clearance, float drawn, ref float was)
            {
                if (clearance < RestingClearance)
                {
                    _rampResting.Add(clearance);
                    if (!float.IsNaN(was) && was < RestingClearance)
                        _rampJitter.Add(Mathf.Abs(clearance - was));
                    if (!float.IsNaN(drawn))
                        _rampLagged.Add(new Vector2(drawn, clearance));
                }

                was = clearance;
            }

            /// <summary>How much y goes up for each unit x goes up (least squares), and how widely x varies; NaN with too few points.</summary>
            static float Slope(List<Vector2> points, out float spread)
            {
                spread = float.NaN;
                if (points.Count < 3)
                    return float.NaN;
                float mx = points.Average(p => p.x), my = points.Average(p => p.y);
                float xx = points.Sum(p => (p.x - mx) * (p.x - mx)), xy = points.Sum(p => (p.x - mx) * (p.y - my));
                spread = Mathf.Sqrt(xx / points.Count);
                return xx > 1e-9f ? xy / xx : float.NaN;
            }

            /// <summary>StepCount steps up along +z from StepsSpot; the last is a landing.</summary>
            void BuildSteps()
            {
                var foot = Spot(StepsSpot);
                var steps = new GameObject("Steps");
                for (int i = 0; i < StepCount; i++)
                {
                    bool landing = i == StepCount - 1;
                    float depth = landing ? StepLanding : StepTread, top = (i + 1) * StepRiser;
                    var step = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    step.name = landing ? "Landing" : "Step " + (i + 1);
                    step.transform.SetParent(steps.transform, false);
                    step.transform.SetPositionAndRotation(new Vector3(foot.x, foot.y + top / 2f, foot.z + i * StepTread + depth / 2f), Quaternion.identity);
                    step.transform.localScale = new Vector3(StepWidth, top, depth);
                }
            }

            /// <summary>
            /// Each frame on the steps: each foot's height over where the body is drawn, and how far that moved since
            /// the last frame - the animation's own step and FootGrounder's lift together. The body's own climb is
            /// left out: the drawn body glides up each riser.
            /// </summary>
            void SampleSteps(StepsFrame row)
            {
                row.judged = true;

                // A hitch - a frame that took more than two of the held screen's - lets everything move further in it:
                // that is no pop. Measuring starts again from the next frame.
                if (Time.deltaTime > 2f / _holdFps)
                {
                    row.hitch = true;
                    _stepsHitches++;
                    _stepsLeftWas = _stepsRightWas = _stepsLeftFixWas = _stepsRightFixWas = _stepsDrawnWas = _stepsRootWas = float.NaN;
                    return;
                }

                // The body itself rising up each riser, as it is drawn: Malbers' climb, glided between physics steps.
                if (!float.IsNaN(_stepsDrawnWas))
                    _stepsBodyRise = Worse(_stepsBodyRise, row.drawnY - _stepsDrawnWas);
                if (!float.IsNaN(_stepsRootWas) && row.steps == 1)
                    _stepsRootRise = Worse(_stepsRootRise, row.rootY - _stepsRootWas);
                _stepsDrawnWas = row.drawnY;
                _stepsRootWas = row.rootY;

                if (float.IsNaN(row.leftOver) || float.IsNaN(row.rightOver))
                    return;
                row.leftJump = StepFoot(row.leftOver, ref _stepsLeftWas, ref _stepsPopLeft, _stepsJumps);
                row.rightJump = StepFoot(row.rightOver, ref _stepsRightWas, ref _stepsPopRight, _stepsJumps);

                // FootGrounder's own part of that: how far it moves each foot from the animation this frame.
                if (!float.IsNaN(row.leftFix))
                {
                    row.leftFixJump = StepFoot(row.leftFix, ref _stepsLeftFixWas, ref _stepsFixPopLeft, null);
                    row.rightFixJump = StepFoot(row.rightFix, ref _stepsRightFixWas, ref _stepsFixPopRight, null);
                }
            }

            /// <summary>How far <paramref name="height"/> moved since the last frame (NaN for the first), kept as the most so far.</summary>
            float StepFoot(float height, ref float was, ref float pop, List<float> jumps)
            {
                float jump = float.NaN;
                if (!float.IsNaN(was))
                {
                    jump = Mathf.Abs(height - was);
                    jumps?.Add(jump);
                    pop = float.IsNaN(pop) ? jump : Mathf.Max(pop, jump);
                }

                was = height;
                return jump;
            }

            /// <summary>
            /// Logs this frame of a walk up the steps, as SampleSteps measures it, and what made it: the frame's timing,
            /// the root against where the body is drawn, Malbers' state and FootGrounder's reading of each leg.
            /// </summary>
            StepsFrame LogStepsFrame(float along) => LogFrame(along, _stepsWalks + 1, _stepsLog, ref _stepsFrameWas, ref _stepsFixedWas);

            /// <summary>
            /// Logs this frame of a way over steps or stairs into <paramref name="log"/> (see LogStepsFrame): <paramref name="walk"/>
            /// numbers the way, and the frame and physics time of the last frame logged are kept in <paramref name="frameWas"/> and
            /// <paramref name="fixedWas"/>. Heights are over the floor the checks stand on.
            /// </summary>
            StepsFrame LogFrame(float along, int walk, List<StepsFrame> log, ref int frameWas, ref float fixedWas)
            {
                var smoother = _animal.GetComponent<CharacterVisualInterpolator>();
                var grounder = _animal.GetComponent<FootGrounder>();
                var leftFoot = _animal.Anim.GetBoneTransform(HumanBodyBones.LeftFoot);
                var rightFoot = _animal.Anim.GetBoneTransform(HumanBodyBones.RightFoot);
                float floor = Spot(StepsSpot).y;
                float drawn = smoother != null && smoother.isActiveAndEnabled ? smoother.ShownPosition.y : _animal.transform.position.y;
                var row = new StepsFrame
                {
                    walk = walk,
                    frame = Time.frameCount,
                    frames = frameWas >= 0 ? Time.frameCount - frameWas : 1,
                    steps = float.IsNaN(fixedWas) || Time.fixedDeltaTime <= 0f ? 0 : Mathf.RoundToInt((Time.fixedTime - fixedWas) / Time.fixedDeltaTime),
                    time = Time.time,
                    dt = Time.deltaTime,
                    fixedTime = Time.fixedTime,
                    interp = Time.fixedDeltaTime > 0f ? Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime) : 0f,
                    along = along,
                    rootY = _animal.transform.position.y - floor,
                    drawnY = drawn - floor,
                    state = StateName(),
                    grounded = _animal.Grounded,
                    alignLerp = _animal.AlignPosLerpDelta,
                    pending = GroundRay != null && (_animal.MainRay || _animal.FrontRay) && GroundRay.GetValue(_animal) is RaycastHit ray
                        ? _animal.Height - ray.distance
                        : float.NaN,
                    leftOver = leftFoot != null ? leftFoot.position.y - drawn : float.NaN,
                    rightOver = rightFoot != null ? rightFoot.position.y - drawn : float.NaN,
                    leftFix = float.NaN,
                    rightFix = float.NaN,
                };

                if (grounder != null && grounder.isActiveAndEnabled)
                {
                    row.grounding = grounder.Grounding;
                    row.weight = grounder.Weight;
                    row.hips = grounder.HipsCorrection;
                    row.leftFix = grounder.LeftCorrection;
                    row.rightFix = grounder.RightCorrection;
                    row.left = grounder.LeftReading;
                    row.right = grounder.RightReading;
                }

                frameWas = Time.frameCount;
                fixedWas = Time.fixedTime;
                log.Add(row);
                return row;
            }

            /// <summary>The larger of two jumps, a missing one (NaN) counting as none.</summary>
            static float Worse(float a, float b) => float.IsNaN(a) ? b : float.IsNaN(b) ? a : Mathf.Max(a, b);

            /// <summary>The frame of <paramref name="log"/> with the largest <paramref name="jump"/>, or -1 if none was judged.</summary>
            static int WorstFrame(List<StepsFrame> log, Func<StepsFrame, float> jump)
            {
                int worst = -1;
                float most = float.NegativeInfinity;
                for (int i = 0; i < log.Count; i++)
                {
                    float value = jump(log[i]);
                    if (!float.IsNaN(value) && value > most)
                    {
                        most = value;
                        worst = i;
                    }
                }

                return worst;
            }

            /// <summary>
            /// The frames around the walk's largest change of FootGrounder's correction, and of a foot's height over the
            /// drawn body, so a failure on the steps says what moved: the frame's timing, the root and the drawn body,
            /// and that foot's reading. Every frame of the walks goes to steps.csv beside the report.
            /// </summary>
            void WriteStepsFrames(StringBuilder md)
            {
                if (_stepsLog.Count == 0)
                    return;
                WriteFramesCsv(_stepsLog, "steps.csv");

                int fix = WorstFrame(_stepsLog, r => Worse(r.leftFixJump, r.rightFixJump));
                int foot = WorstFrame(_stepsLog, r => Worse(r.leftJump, r.rightJump));
                if (fix < 0 && foot < 0)
                    return;

                md.AppendLine($"- Worst frames on the steps (every frame of the walk{(StepsWalks > 1 ? "s" : "")} is in steps.csv). Heights over the " +
                              "floor at the foot of the steps; a foot over the body drawn; the ground under its heel and toe as shown, with the part of " +
                              "an edge still being eased in (offset); Steps: physics steps since the frame before; a jump the verdict did not take is -.");
                md.AppendLine();
                string Walk(StepsFrame r) => StepsWalks > 1 ? $" of walk {r.walk}" : "";
                if (fix >= 0)
                    WorstFramesBlock(md, _stepsLog, fix, true, Walk);
                if (foot >= 0 && (fix < 0 || Mathf.Abs(foot - fix) > 6 || _stepsLog[foot].walk != _stepsLog[fix].walk))
                    WorstFramesBlock(md, _stepsLog, foot, false, Walk);
                else if (foot >= 0)
                    md.AppendLine($"The largest jump of a foot over the body ({Worse(_stepsLog[foot].leftJump, _stepsLog[foot].rightJump) * 100f:0.0} cm) " +
                                  $"is in the same frames (frame {_stepsLog[foot].frame}).");
                md.AppendLine();
            }

            /// <summary>
            /// The frames of <paramref name="log"/> around its frame <paramref name="at"/>: the largest change of a foot's
            /// correction, or of its height over the body drawn; <paramref name="which"/> says which way it was on.
            /// </summary>
            void WorstFramesBlock(StringBuilder md, List<StepsFrame> log, int at, bool correction, Func<StepsFrame, string> which)
            {
                var worst = log[at];
                bool left = correction
                    ? Worse(worst.leftFixJump, float.NegativeInfinity) >= Worse(worst.rightFixJump, float.NegativeInfinity)
                    : Worse(worst.leftJump, float.NegativeInfinity) >= Worse(worst.rightJump, float.NegativeInfinity);
                float most = correction ? (left ? worst.leftFixJump : worst.rightFixJump) : (left ? worst.leftJump : worst.rightJump);
                md.AppendLine((correction ? "The largest change of FootGrounder's correction" : "The largest jump of a foot over the body drawn") +
                              $": {most * 100f:0.0} cm, the {(left ? "left" : "right")} foot, frame {worst.frame}" + which(worst) + ".");
                md.AppendLine();
                md.AppendLine("| Frame | ms | Steps | t | Along | Root | Drawn | Rise left | State | Over body | Jump | Correction | Change | Lifted | " +
                              "Resting | Clear | Want | Heel ground (offset) | Toe ground (offset) | Weight | Hips |");
                md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
                float floor = Spot(StepsSpot).y;
                for (int i = Mathf.Max(0, at - 6); i <= Mathf.Min(log.Count - 1, at + 3); i++)
                {
                    var r = log[i];
                    if (r.walk != worst.walk)
                        continue;
                    var leg = left ? r.left : r.right;
                    float over = left ? r.leftOver : r.rightOver, fixNow = left ? r.leftFix : r.rightFix;
                    float jump = left ? r.leftJump : r.rightJump, fixJump = left ? r.leftFixJump : r.rightFixJump;
                    string Ground(bool found, float ground, float offset) =>
                        found ? $"{(ground - floor) * 100f:0.0} ({offset * 100f:+0.0;-0.0;0.0})" : "none";
                    md.AppendLine($"| {(i == at ? "**" + r.frame + "**" : r.frame.ToString())}{(r.frames != 1 ? $" ({r.frames} frames)" : "")} | " +
                                  $"{r.dt * 1000f:0.0}{(r.hitch ? " hitch" : "")} | {r.steps} | {r.interp:0.00} | {r.along:0.00} | {r.rootY:0.000} | {r.drawnY:0.000} | " +
                                  $"{(float.IsNaN(r.pending) ? "-" : (r.pending * 100f).ToString("0.0"))} | " +
                                  $"{r.state}{(r.grounded ? "" : ", not grounded")}{(r.grounding ? "" : ", not grounding")} | " +
                                  $"{over * 100f:0.0} | {(float.IsNaN(jump) ? "-" : (jump * 100f).ToString("0.0"))} | " +
                                  $"{fixNow * 100f:+0.0;-0.0;0.0} | {(float.IsNaN(fixJump) ? "-" : (fixJump * 100f).ToString("0.0"))} | " +
                                  $"{leg.lifted * 100f:0.0} | {leg.resting:0.00} | {leg.clear * 100f:0.0} | {leg.want * 100f:+0.0;-0.0;0.0} | " +
                                  $"{Ground(leg.heelFound, leg.heelGround, leg.heelOffset)} | {Ground(leg.toeFound, leg.toeGround, leg.toeOffset)} | " +
                                  $"{r.weight:0.00} | {r.hips * 100f:0.0} |");
                }

                md.AppendLine();
            }

            /// <summary>Every frame of <paramref name="log"/> (the walks up the steps, or the ways over the flights), as logged, into <paramref name="file"/> in the report's folder.</summary>
            void WriteFramesCsv(List<StepsFrame> log, string file)
            {
                static string G(float v) => float.IsNaN(v) ? "" : v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
                static string B(bool v) => v ? "1" : "0";
                var csv = new StringBuilder();
                csv.Append("walk,frame,frames,time,dt,fixedTime,steps,interp,along,rootY,drawnY,pending,state,grounded,grounding,judged,hitch,alignLerp,weight,hips");
                foreach (var side in new[] { "L", "R" })
                    csv.Append($",over{side},fix{side},jump{side},fixJump{side},lifted{side},resting{side},clear{side},want{side},lift{side}," +
                               $"heelHeight{side},toeHeight{side},heelFound{side},heelGround{side},heelOffset{side},toeFound{side},toeGround{side},toeOffset{side}");
                csv.AppendLine();
                float floor = Spot(StepsSpot).y;
                foreach (var r in log)
                {
                    csv.Append(r.walk).Append(',').Append(r.frame).Append(',').Append(r.frames).Append(',').Append(G(r.time)).Append(',')
                       .Append(G(r.dt)).Append(',').Append(G(r.fixedTime)).Append(',').Append(r.steps).Append(',').Append(G(r.interp)).Append(',')
                       .Append(G(r.along)).Append(',').Append(G(r.rootY)).Append(',').Append(G(r.drawnY)).Append(',').Append(G(r.pending)).Append(',')
                       .Append(r.state).Append(',')
                       .Append(B(r.grounded)).Append(',').Append(B(r.grounding)).Append(',').Append(B(r.judged)).Append(',').Append(B(r.hitch)).Append(',')
                       .Append(G(r.alignLerp)).Append(',').Append(G(r.weight)).Append(',').Append(G(r.hips));
                    for (int s = 0; s < 2; s++)
                    {
                        var leg = s == 0 ? r.left : r.right;
                        csv.Append(',').Append(G(s == 0 ? r.leftOver : r.rightOver)).Append(',').Append(G(s == 0 ? r.leftFix : r.rightFix))
                           .Append(',').Append(G(s == 0 ? r.leftJump : r.rightJump)).Append(',').Append(G(s == 0 ? r.leftFixJump : r.rightFixJump))
                           .Append(',').Append(G(leg.lifted)).Append(',').Append(G(leg.resting)).Append(',').Append(G(leg.clear)).Append(',')
                           .Append(G(leg.want)).Append(',').Append(G(leg.lift)).Append(',').Append(G(leg.heelHeight - floor)).Append(',')
                           .Append(G(leg.toeHeight - floor)).Append(',').Append(B(leg.heelFound)).Append(',')
                           .Append(G(leg.heelGround - floor)).Append(',').Append(G(leg.heelOffset)).Append(',').Append(B(leg.toeFound)).Append(',')
                           .Append(G(leg.toeGround - floor)).Append(',').Append(G(leg.toeOffset));
                    }

                    csv.AppendLine();
                }

                System.IO.Directory.CreateDirectory(_job.folder);
                System.IO.File.WriteAllText(System.IO.Path.Combine(_job.folder, file), csv.ToString());
            }

            /// <summary>The top of the steps right under (or over) <paramref name="point"/>: a tread, the landing, or the floor round them.</summary>
            float StepTop(Vector3 point)
            {
                var foot = Spot(StepsSpot);
                float into = point.z - foot.z;
                if (into < 0f || into > (StepCount - 1) * StepTread + StepLanding || Mathf.Abs(point.x - foot.x) > StepWidth / 2f)
                    return foot.y;
                return foot.y + (Mathf.Min(Mathf.FloorToInt(into / StepTread), StepCount - 1) + 1) * StepRiser;
            }

            /// <summary>
            /// Each frame running up the steps: each shoe's lowest point against the step right under it, as the feet table
            /// measures the floor, kept when the shoe is planted - resting on them and all but still along them. Not every
            /// sole vertex against the step under it, as on the ramp: a toe tip reaching over the next riser would read as
            /// a riser's height in that step, while the shoe stands on the tread.
            /// </summary>
            void SampleStepsRun()
            {
                var leftFoot = _animal.Anim.GetBoneTransform(HumanBodyBones.LeftFoot);
                var rightFoot = _animal.Anim.GetBoneTransform(HumanBodyBones.RightFoot);
                if (leftFoot == null || rightFoot == null || Time.deltaTime <= 0f || !LowestSoles(out var leftAt, out var rightAt))
                    return;

                float left = leftAt.y - StepTop(leftAt), right = rightAt.y - StepTop(rightAt);
                _stepsRunFrames++;
                var position = _animal.transform.position;
                if (_stepsRunFromAt < 0f)
                {
                    _stepsRunFromAt = Now;
                    _stepsRunFrom = position;
                    _stepsRunSpeed = _animal.CurrentSpeedIndex;
                }
                else if (Now > _stepsRunFromAt)
                    _stepsRunPace = FlatV(position - _stepsRunFrom).magnitude / (Now - _stepsRunFromAt);

                if (_stepsRunHasWas)
                {
                    PlantedSole(left, leftFoot.position, _stepsRunLeftWas);
                    PlantedSole(right, rightFoot.position, _stepsRunRightWas);
                }

                _stepsRunLeftWas = leftFoot.position;
                _stepsRunRightWas = rightFoot.position;
                _stepsRunHasWas = true;
            }

            /// <summary>One shoe's sole against the steps under it, kept if the shoe rests on them and its foot has all but stopped along them.</summary>
            void PlantedSole(float clearance, Vector3 foot, Vector3 was)
            {
                if (clearance < RestingClearance && FlatV(foot - was).magnitude / Time.deltaTime < PlantedSpeed)
                    _stepsRunPlanted.Add(clearance);
            }

            /// <summary>Each shoe's lowest sole vertex in the world; false before the soles are picked.</summary>
            bool LowestSoles(out Vector3 left, out Vector3 right)
            {
                left = right = default;
                float leftLow = float.MaxValue, rightLow = float.MaxValue;
                for (int s = 0; s < _skins.Count; s++)
                {
                    var skin = _skins[s];
                    skin.BakeMesh(_bake, false);
                    _bake.GetVertices(_bakeVertices);
                    var m = Matrix4x4.TRS(skin.transform.position, skin.transform.rotation, Vector3.one);
                    foreach (int i in _leftSole[s])
                    {
                        if (i >= _bakeVertices.Count)
                            continue;
                        var p = m.MultiplyPoint3x4(_bakeVertices[i]);
                        if (p.y < leftLow) { leftLow = p.y; left = p; }
                    }

                    foreach (int i in _rightSole[s])
                    {
                        if (i >= _bakeVertices.Count)
                            continue;
                        var p = m.MultiplyPoint3x4(_bakeVertices[i]);
                        if (p.y < rightLow) { rightLow = p.y; right = p; }
                    }
                }

                return leftLow < float.MaxValue && rightLow < float.MaxValue;
            }

            /// <summary>
            /// The flights the ways in FlightRuns go over: First_Parkour's staircase as its prefab is, put down at RealStairsSpot
            /// turned to climb along +z, with a landing as high as its upper floor on past its top; and the flight with tilted
            /// treads along +z from TiltSpot.
            /// </summary>
            void BuildFlights()
            {
                _flightsBuilt = true;
                var spot = Spot(RealStairsSpot);
                var prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(RealStairsPath);
                if (prefab == null)
                    _moveNotes.Add($"No {RealStairsPath}: First_Parkour's staircase was not walked.");
                else
                {
                    // Turned round, its own (x, z) is (-x, -z) from where it is put.
                    var stairs = Object.Instantiate(prefab, spot, Quaternion.Euler(0f, 180f, 0f));
                    stairs.name = prefab.name;
                    _realX = spot.x - RealStairsMiddle;
                    _realFoot = spot.z - RealStairsFoot;
                    _realTop = spot.z - RealStairsTop;
                    _realHigh = spot.y + RealStairsHigh;
                    var landing = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    landing.name = "Landing past the staircase";
                    landing.transform.SetPositionAndRotation(
                        new Vector3(_realX, spot.y + RealStairsHigh / 2f, _realTop + 0.07f + RealStairsLanding / 2f), Quaternion.identity);
                    landing.transform.localScale = new Vector3(3f, RealStairsHigh, RealStairsLanding);
                    _realBuilt = true;

                    // Its first and top treads as they are found, for the report.
                    Physics.SyncTransforms();
                    float Tread(float along) => Physics.Raycast(new Vector3(_realX, spot.y + RealStairsHigh + 2f, spot.z + along), Vector3.down,
                        out var hit, RealStairsHigh + 3f, ~0, QueryTriggerInteraction.Ignore) ? hit.point.y - spot.y : float.NaN;
                    _realFirstTread = Tread(-0.2f);
                    _realTopTread = Tread(4.1f);
                }

                var foot = Spot(TiltSpot);
                var flight = new GameObject("Flight with tilted treads");
                int count = TiltTreads.Length;
                for (int k = 0; k <= count; k++)
                {
                    bool landing = k == count;
                    float depth = landing ? TiltLanding : TiltGoing, top = (k + 1) * TiltRiser, tilt = landing ? 0f : TiltTreads[k];
                    var step = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    step.name = landing ? "Landing" : $"Tread {k + 1} ({tilt:0}°)";
                    step.transform.SetParent(flight.transform, false);
                    step.transform.localScale = new Vector3(TiltWidth, top, depth);
                    // Turned about the middle of its top, its front edge down for a positive tilt.
                    var turn = Quaternion.Euler(-tilt, 0f, 0f);
                    var pivot = new Vector3(foot.x, foot.y + top, foot.z + k * TiltGoing + depth / 2f);
                    step.transform.SetPositionAndRotation(pivot + turn * (Vector3.down * (top / 2f)), turn);
                }

                _tiltX = foot.x;
                _tiltFoot = foot.z;
                _tiltTop = foot.z + count * TiltGoing;
                _tiltHigh = foot.y + (count + 1) * TiltRiser;
            }

            /// <summary>Where a way over a flight starts: walking, 1.6 m short of it; running, StepsRunUp (on its landing, going down).</summary>
            Vector3 FlightStart(FlightRun course)
            {
                float before = course.run ? StepsRunUp : 1.6f;
                float x = course.real ? _realX : _tiltX, foot = course.real ? _realFoot : _tiltFoot, top = course.real ? _realTop : _tiltTop;
                return course.down
                    ? new Vector3(x, course.real ? _realHigh : _tiltHigh, top + before)
                    : new Vector3(x, _groundY, foot - before);
            }

            /// <summary>How long a flight is, from its first riser to the back of its top tread (m).</summary>
            float FlightLength(FlightRun course) => course.real ? _realTop - _realFoot : _tiltTop - _tiltFoot;

            /// <summary>How far along a way over a flight <paramref name="position"/> is, from its first riser (going down, its top).</summary>
            float FlightAlong(FlightRun course, Vector3 position)
            {
                float foot = course.real ? _realFoot : _tiltFoot, top = course.real ? _realTop : _tiltTop;
                return course.down ? top - position.z : position.z - foot;
            }

            /// <summary>
            /// Each frame on a flight, as on the level steps (SampleSteps): each foot's height over where the body is drawn and
            /// FootGrounder's correction of it, from one frame to the next, hitches left out; and what moved them.
            /// </summary>
            void SampleFlight(FlightRun course, StepsFrame row)
            {
                row.judged = true;
                course.judged++;
                if (Time.deltaTime > 2f / _holdFps)
                {
                    row.hitch = true;
                    course.hitches++;
                    course.Forget();
                    return;
                }

                var position = _animal.transform.position;
                if (course.startedAt < 0f)
                {
                    course.startedAt = Now;
                    course.startedFrom = position;
                }
                else if (Now > course.startedAt)
                    course.pace = FlatV(position - course.startedFrom).magnitude / (Now - course.startedAt);

                if (float.IsNaN(row.leftOver) || float.IsNaN(row.rightOver))
                    return;
                row.leftJump = StepFoot(row.leftOver, ref course.leftWas, ref course.popLeft, course.jumps);
                row.rightJump = StepFoot(row.rightOver, ref course.rightWas, ref course.popRight, course.jumps);
                var grounder = _animal.GetComponent<FootGrounder>();
                if (float.IsNaN(row.leftFix) || grounder == null)
                    return;
                row.leftFixJump = StepFoot(row.leftFix, ref course.leftFixWas, ref course.fixLeft, null);
                row.rightFixJump = StepFoot(row.rightFix, ref course.rightFixWas, ref course.fixRight, null);
                StepFoot(FlatLift(row.left), ref course.leftFlatWas, ref course.flat, null);
                StepFoot(FlatLift(row.right), ref course.rightFlatWas, ref course.flat, null);

                // The ground under each shoe's heel and toe: one slope or two, and which of them had any in reach.
                var anim = _animal.Anim;
                Slopes(course, grounder, anim.GetBoneTransform(HumanBodyBones.LeftFoot), anim.GetBoneTransform(HumanBodyBones.LeftToes),
                    grounder.leftHeel, grounder.leftToe, ref course.leftSlopes);
                Slopes(course, grounder, anim.GetBoneTransform(HumanBodyBones.RightFoot), anim.GetBoneTransform(HumanBodyBones.RightToes),
                    grounder.rightHeel, grounder.rightToe, ref course.rightSlopes);
                InReach(course, row.left, ref course.leftReach);
                InReach(course, row.right, ref course.rightReach);
            }

            /// <summary>
            /// How much lower FootGrounder judged a shoe over the ground than the lower of its heel and toe over the ground
            /// under each (m): what its flat sole adds to the lift.
            /// </summary>
            static float FlatLift(FootGrounder.LegReading leg) =>
                Mathf.Max(0f, Mathf.Min(leg.heelHeight - leg.heelGround, leg.toeHeight - leg.toeGround) - leg.clear);

            /// <summary>
            /// Whether the ground right under a shoe's heel and toe (the highest within FootGrounder's reach) is of two slopes
            /// more than TwoSlopes apart, counted in the course, with each turn between one slope and two.
            /// </summary>
            void Slopes(FlightRun course, FootGrounder grounder, Transform foot, Transform toes, FootGrounder.SolePoint heelPoint,
                FootGrounder.SolePoint toePoint, ref int was)
            {
                if (foot == null)
                    return;
                var heel = (heelPoint.onToes && toes != null ? toes : foot).TransformPoint(heelPoint.local);
                var toe = (toePoint.onToes && toes != null ? toes : foot).TransformPoint(toePoint.local);
                course.legFrames++;
                if (!GroundNormal(grounder, heel, out var heelNormal) || !GroundNormal(grounder, toe, out var toeNormal))
                    return;
                int now = Vector3.Angle(heelNormal, toeNormal) > TwoSlopes ? 1 : 0;
                course.twoSlopes += now;
                if (was >= 0 && now != was)
                    course.slopeTurns++;
                was = now;
            }

            /// <summary>The slope of the highest ground under <paramref name="point"/> within FootGrounder's reach, the player left out.</summary>
            bool GroundNormal(FootGrounder grounder, Vector3 point, out Vector3 normal)
            {
                normal = Vector3.up;
                float best = float.NegativeInfinity;
                var from = new Vector3(point.x, point.y + grounder.maxStep + 0.1f, point.z);
                foreach (var hit in Physics.RaycastAll(from, Vector3.down, grounder.maxStep + grounder.maxDrop + 0.2f, _animal.GroundLayer,
                             QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider.transform.IsChildOf(_animal.transform) || hit.point.y <= best)
                        continue;
                    best = hit.point.y;
                    normal = hit.normal;
                }

                return !float.IsNegativeInfinity(best);
            }

            /// <summary>Counts a heel or toe losing ground in reach, or finding it again (as FootGrounder read it).</summary>
            static void InReach(FlightRun course, FootGrounder.LegReading leg, ref int was)
            {
                int now = (leg.heelFound ? 1 : 0) | (leg.toeFound ? 2 : 0);
                if (was >= 0 && now != was)
                    course.reachTurns++;
                was = now;
            }

            static string Capital(string text) => string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

            /// <summary>The ways over the flights, each foot against the level steps' limits, and the frames around the worst.</summary>
            void WriteFlights(StringBuilder md)
            {
                if (!_flightRuns.Any(c => c.tried))
                    return;
                md.AppendLine("- Stairs as the levels have them (every frame in stairs.csv, its walk column numbering the ways below from 1): " +
                              (_realBuilt
                                  ? $"First_Parkour's staircase, its prefab {System.IO.Path.GetFileNameWithoutExtension(RealStairsPath)} as it is (19 treads of about " +
                                    $"20 cm on 22 cm goings in one mesh collider, six of them tilted 2-5.5°; its first tread found {_realFirstTread * 100f:0.0} cm " +
                                    $"up and its top {_realTopTread:0.000} m), and "
                                  : "") +
                              $"a flight of {TiltTreads.Length} steps of {TiltRiser * 100f:0} cm on {TiltGoing * 100f:0} cm whose treads are tilted " +
                              string.Join(", ", TiltTreads.Select(t => t.ToString("0"))) + "° (nose down; below zero nose up). Limits as up the level steps: " +
                              $"a foot {StepPopLimit * 100f:0} cm over the body drawn, FootGrounder's correction {StepCorrectionLimit * 100f:0} cm, from one " +
                              "frame to the next. Flat: the most the lift FootGrounder's flat sole adds changed from one frame to the next. Two slopes: " +
                              $"leg-frames whose heel and toe stood on ground more than {TwoSlopes:0}° apart in slope, and how often that turned.");
                md.AppendLine();
                md.AppendLine("| | Malbers speed, pace | Frames | Foot over body, left / right | 95% | Correction, left / right | Flat | Two slopes (turns) | Reach turns | Hitches | Walked |");
                md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
                static string Cm(float v) => float.IsNaN(v) ? "-" : (v * 100f).ToString("0.0");
                for (int i = 0; i < _flightRuns.Length; i++)
                {
                    var c = _flightRuns[i];
                    if (!c.tried)
                        continue;
                    string p95 = c.jumps.Count > 0 ? Cm(Percentile(c.jumps, 0.95f)) : "-";
                    md.AppendLine($"| {i + 1}. {c.what} | {c.speed}, {(float.IsNaN(c.pace) ? "-" : c.pace.ToString("0.0"))} m/s | {c.judged} | " +
                                  $"{Cm(c.popLeft)} / {Cm(c.popRight)} cm | {p95} cm | {Cm(c.fixLeft)} / {Cm(c.fixRight)} cm | {Cm(c.flat)} cm | " +
                                  $"{c.twoSlopes} of {c.legFrames} ({c.slopeTurns}) | {c.reachTurns} | {c.hitches} | {c.walked:0.0} of {FlightLength(c):0.0} m |");
                }

                md.AppendLine();
                if (_flightLog.Count == 0)
                    return;
                WriteFramesCsv(_flightLog, "stairs.csv");
                int fix = WorstFrame(_flightLog, r => Worse(r.leftFixJump, r.rightFixJump));
                int foot = WorstFrame(_flightLog, r => Worse(r.leftJump, r.rightJump));
                string Way(StepsFrame r) => r.walk >= 1 && r.walk <= _flightRuns.Length ? $", {_flightRuns[r.walk - 1].what}" : "";
                if (fix >= 0)
                    WorstFramesBlock(md, _flightLog, fix, true, Way);
                if (foot >= 0 && (fix < 0 || Mathf.Abs(foot - fix) > 6 || _flightLog[foot].walk != _flightLog[fix].walk))
                    WorstFramesBlock(md, _flightLog, foot, false, Way);
            }

            /// <summary>The thin floor tile along the run from SprintTileSpot (+z), SprintRunUp ahead of it.</summary>
            void BuildSprintTile()
            {
                var spot = Spot(SprintTileSpot);
                _sprintTileFrom = spot.z + SprintRunUp;
                _sprintTileTo = _sprintTileFrom + SprintTileLength;
                var tile = GameObject.CreatePrimitive(PrimitiveType.Cube);
                tile.name = "Floor tile sprinted across";
                tile.transform.SetPositionAndRotation(
                    new Vector3(spot.x, spot.y + SprintTileThickness / 2f, (_sprintTileFrom + _sprintTileTo) / 2f), Quaternion.identity);
                tile.transform.localScale = new Vector3(SprintTileWidth, SprintTileThickness, SprintTileLength);
            }

            // ---- What comes in the view, and what holds the camera --------------------------------------

            /// <summary>One frame of something put in the view: counted, and counted as faded when it wears its dissolving copy.</summary>
            void WatchFade(FadeWatch watch)
            {
                if (watch.renderer == null)
                    return;
                watch.frames++;
                if (Fader != null && Fader.IsFaded(watch.renderer))
                    watch.faded++;
            }

            /// <summary>Something only put in the view, taken far out of the way once looked at (nothing stands on it).</summary>
            static void OutOfTheWay(Renderer renderer)
            {
                if (renderer != null)
                    renderer.transform.position += Vector3.up * 1000f;
            }

            /// <summary>
            /// A ball of <paramref name="radius"/>, or a pillar as thick standing on the floor, half way along the view to
            /// the camera target, its surface RoundGap beside that line on the side away from the camera's shoulder: there
            /// only the view of the player passes it, not the camera's own line out from the shoulder.
            /// </summary>
            Renderer PutBeside(float radius, bool pillar)
            {
                if (_moveCamera == null)
                    return null;
                var view = _moveCamera.transform;
                var eye = view.position;
                var target = CameraTarget();
                var middle = Vector3.Lerp(eye, target, 0.5f);
                var right = Vector3.Cross(Vector3.up, target - eye).normalized;
                // The camera looks out along the line from the shoulder: where that passes the target is the shoulder's side.
                var shoulderLine = eye + view.forward * Vector3.Dot(target - eye, view.forward);
                var away = Vector3.Dot(shoulderLine - target, right) >= 0f ? -right : right;
                var centre = middle + away * (radius + RoundGap);

                var thing = GameObject.CreatePrimitive(pillar ? PrimitiveType.Capsule : PrimitiveType.Sphere);
                if (pillar)
                {
                    // Upright, so the line passes its round side at whatever height it crosses.
                    thing.name = "Pillar beside the view";
                    thing.transform.position = new Vector3(centre.x, _groundY + RoundPillarHeight / 2f, centre.z);
                    thing.transform.localScale = new Vector3(2f * radius, RoundPillarHeight / 2f, 2f * radius);
                }
                else
                {
                    thing.name = "Ball beside the view";
                    thing.transform.position = centre;
                    thing.transform.localScale = Vector3.one * (2f * radius);
                }

                return thing.GetComponent<Renderer>();
            }

            /// <summary>
            /// A box of ToonScapes stone half way between the camera and the player, square to the view, its material made
            /// as the TSU rocks' are: the tint switched off with a red left in it, the stone tiled as on them.
            /// </summary>
            Renderer PutStone()
            {
                var shader = UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>(StoneShaderPath);
                if (shader == null)
                    shader = Shader.Find(StoneShaderName);
                if (shader == null || _moveCamera == null)
                {
                    _moveNotes.Add($"No ToonScapes stone shader ({StoneShaderPath}) or no camera: the stone's dissolve was not checked.");
                    return null;
                }

                var stone = new Material(shader) { name = "ToonScapes stone, tint off (live check)" };
                stone.SetFloat("_EnableColorTint", 0f);
                stone.DisableKeyword("_ENABLECOLORTINT_ON");
                stone.SetColor("_BaseTint", Color.red);
                stone.SetFloat("_SurfaceTextureTiling", StoneTiling);
                stone.SetFloat("_CoverageFalloff", StoneFalloff);

                var target = CameraTarget();
                var eye = _moveCamera.transform.position;
                var middle = Vector3.Lerp(target, eye, 0.5f);
                var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.name = "ToonScapes stone in the way";
                box.transform.SetPositionAndRotation(new Vector3(middle.x, _groundY + 1.5f, middle.z), Quaternion.LookRotation(FlatV(eye - target).normalized, Vector3.up));
                box.transform.localScale = new Vector3(2f, 3f, 0.3f);
                var renderer = box.GetComponent<Renderer>();
                renderer.sharedMaterial = stone;
                _stoneTried = true;
                return renderer;
            }

            /// <summary>Each frame the stone wears its dissolving copy: which shader, what colour, and whether it maps the stone in object space, at what scale and blend.</summary>
            void SampleStone()
            {
                if (_stoneRenderer == null || Fader == null || !Fader.IsFaded(_stoneRenderer))
                    return;
                var copy = _stoneRenderer.sharedMaterial;
                if (copy == null || copy.shader == null)
                    return;
                _stoneFaded = true;
                _stoneCopyShader = copy.shader.name;
                _stoneCopyColor = copy.HasProperty("_BaseColor") ? copy.GetColor("_BaseColor") : Color.clear;
                var keyword = copy.shader.keywordSpace.FindKeyword(TriplanarKeyword);
                _stoneHasKeyword = keyword.isValid;
                _stoneTriplanar = keyword.isValid && copy.IsKeywordEnabled(keyword);
                _stoneTriplanarScale = copy.HasProperty(TriplanarScaleName) ? copy.GetFloat(TriplanarScaleName) : float.NaN;
                _stoneTriplanarFalloff = copy.HasProperty(TriplanarFalloffName) ? copy.GetFloat(TriplanarFalloffName) : float.NaN;
            }

            /// <summary>A crate CrateBehind behind the player, under the line from the camera's shoulder back to the camera.</summary>
            Renderer PutCrate()
            {
                if (_moveCamera == null)
                    return null;
                var at = _animal.transform.position;
                var forward = FlatV(_moveCamera.transform.forward).normalized;
                var right = Vector3.Cross(Vector3.up, forward);
                float side = Vector3.Dot(_moveCamera.transform.position - at, right);
                var crate = GameObject.CreatePrimitive(PrimitiveType.Cube);
                crate.name = "Crate behind";
                crate.transform.SetPositionAndRotation(
                    new Vector3(at.x, _groundY + CrateSize / 2f, at.z) + right * side - forward * CrateBehind, Quaternion.LookRotation(forward, Vector3.up));
                crate.transform.localScale = Vector3.one * CrateSize;
                return crate.GetComponent<Renderer>();
            }

            /// <summary>A chest-high block in front of BlockSpot (+z), its face BlockAhead from where the body's middle stands.</summary>
            void BuildBlock()
            {
                var spot = Spot(BlockSpot);
                var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
                block.name = "Block in front";
                block.transform.SetPositionAndRotation(spot + new Vector3(0f, BlockHeight / 2f, BlockAhead + BlockDepth / 2f), Quaternion.identity);
                block.transform.localScale = new Vector3(BlockWidth, BlockHeight, BlockDepth);
            }

            /// <summary>How far down the camera looks (degrees; looking up is below zero).</summary>
            float ViewPitch() => _moveCamera != null ? -Mathf.Asin(Mathf.Clamp(_moveCamera.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg : float.NaN;

            /// <summary>
            /// Two tunnels along +z made as Game.unity's tunnel pieces are: floor, walls and roof one mesh facing in, and one
            /// non-convex collider of it. One in one piece, with a heap of rubbish lying on its floor further along; the
            /// player stands in the middle of its floor. The other of two such pieces joined flush at JointSpot, the one
            /// ahead of the joint as long as the first tunnel reaches ahead, the one behind as long as it reaches behind.
            /// </summary>
            void BuildTunnel()
            {
                var spot = Spot(TunnelSpot);
                _tunnelFloor = new Vector3(spot.x, spot.y + TunnelLift, spot.z);
                _tunnelRenderers.Add(TunnelPiece("Tunnel", _tunnelFloor, -TunnelBehind, TunnelAhead));

                var heap = GameObject.CreatePrimitive(PrimitiveType.Cube);
                heap.name = "Heap on the tunnel's floor";
                heap.transform.SetPositionAndRotation(_tunnelFloor + new Vector3(0f, HeapHeight / 2f, HeapAhead), Quaternion.identity);
                heap.transform.localScale = new Vector3(HeapWidth, HeapHeight, HeapWidth);

                var joint = Spot(JointSpot);
                _jointFloor = new Vector3(joint.x, joint.y + TunnelLift, joint.z);
                _jointRenderers.Add(TunnelPiece("Tunnel ahead of the joint", _jointFloor, 0f, TunnelAhead));
                _jointRenderers.Add(TunnelPiece("Tunnel behind the joint", _jointFloor, -TunnelBehind, 0f));
            }

            /// <summary>One piece of tunnel from <paramref name="from"/> to <paramref name="to"/> along +z of <paramref name="floor"/>, the middle of its floor.</summary>
            Renderer TunnelPiece(string name, Vector3 floor, float from, float to)
            {
                float y0 = floor.y, y1 = y0 + TunnelHeight;
                float x0 = floor.x - TunnelWidth / 2f, x1 = floor.x + TunnelWidth / 2f, z0 = floor.z + from, z1 = floor.z + to;
                var vertices = new List<Vector3>();
                var triangles = new List<int>();
                Quad(vertices, triangles, new Vector3(x0, y0, z0), new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y0, z0), Vector3.up);
                Quad(vertices, triangles, new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), Vector3.down);
                Quad(vertices, triangles, new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x0, y0, z1), Vector3.right);
                Quad(vertices, triangles, new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x1, y0, z1), Vector3.left);
                return MeshPiece(name, vertices, triangles).GetComponent<Renderer>();
            }

            /// <summary>Where the player stands for a look in the tunnels.</summary>
            Vector3 TunnelStand(TunnelView view) =>
                view.Joint ? _jointFloor + new Vector3(view.side, 0f, view.past)
                : _tunnelFloor + new Vector3(view.side, view.heap ? HeapHeight : 0f, view.heap ? HeapAhead : 0f);

            /// <summary>
            /// Each frame of a look in a tunnel: the camera's clearance from its floor, its walls and its roof (below zero is
            /// outside), how near it is to what it films, and whether the tunnel is faded.
            /// </summary>
            void SampleTunnel(TunnelView view)
            {
                if (_moveCamera == null)
                    return;
                var floor = view.Joint ? _jointFloor : _tunnelFloor;
                var at = _moveCamera.transform.position;
                view.floor = Least(view.floor, at.y - floor.y);
                view.wall = Least(view.wall, TunnelWidth / 2f - Mathf.Abs(at.x - floor.x));
                view.roof = Least(view.roof, floor.y + TunnelHeight - at.y);
                view.nearest = Least(view.nearest, CameraToTarget());
                view.frames++;
                var fader = Fader;
                if (fader != null && (view.Joint ? _jointRenderers : _tunnelRenderers).Any(r => r != null && fader.IsFaded(r)))
                    view.faded++;
            }

            static float Least(float was, float value) => float.IsNaN(was) || value < was ? value : was;

            /// <summary>
            /// The camera's ground collider asked where it stops a camera hanging from a hand right by the tunnel's right wall
            /// (its sphere already touching the tunnel), looking up along it: the camera's height over the floor with its whole
            /// distance, and with a distance that ends just over the floor.
            /// </summary>
            void ProbeOverlap(CameraGroundCollider ground)
            {
                if (ground == null || !ground.enabled)
                    return;
                _overlapTried = true;
                var hand = _tunnelFloor + new Vector3(TunnelWidth / 2f - OverlapGap, OverlapHeight, 0f);
                var down = Quaternion.AngleAxis(-OverlapPitch, Vector3.right) * Vector3.back;
                float toFloor = OverlapHeight / Mathf.Sin(OverlapPitch * Mathf.Deg2Rad);
                float whole = ground.ClearFrom(CameraTarget(), hand, down, CameraDistance());
                _overlapWhole = hand.y + down.y * whole - _tunnelFloor.y;
                float shortOne = toFloor - OverlapShort / Mathf.Sin(OverlapPitch * Mathf.Deg2Rad);
                float cut = ground.ClearFrom(CameraTarget(), hand, down, shortOne);
                _overlapShort = hand.y + down.y * cut - _tunnelFloor.y;
            }

            /// <summary>The live shoulder camera's own distance from its hand (m), or 6.5 m without one.</summary>
            float CameraDistance()
            {
                var follow = OnLiveCamera<CinemachineThirdPersonFollow>();
                return follow != null ? follow.CameraDistance : 6.5f;
            }

            /// <summary>The wall too big to fade, BigWallBehind behind the player and square to the view (+z).</summary>
            void BuildBigWall()
            {
                var spot = Spot(BigWallSpot);
                _bigWallFace = spot.z - BigWallBehind;
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = "Wall too big to fade";
                wall.transform.SetPositionAndRotation(new Vector3(spot.x, spot.y + BigWallHeight / 2f, _bigWallFace - BigWallThickness / 2f), Quaternion.identity);
                wall.transform.localScale = new Vector3(BigWallWidth, BigWallHeight, BigWallThickness);
                _bigWallRenderer = wall.GetComponent<Renderer>();
            }

            /// <summary>
            /// The hall too big to fade where the wall was: floor, walls and roof one mesh facing in and one non-convex
            /// collider of it, its near wall where the wall's face was.
            /// </summary>
            void BuildBigHall()
            {
                var spot = Spot(BigWallSpot);
                float x0 = spot.x - BigWallWidth / 2f, x1 = spot.x + BigWallWidth / 2f, y0 = spot.y, y1 = spot.y + BigWallHeight;
                float z0 = _bigWallFace - BigHallDepth, z1 = _bigWallFace;
                var vertices = new List<Vector3>();
                var triangles = new List<int>();
                Quad(vertices, triangles, new Vector3(x0, y0, z0), new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y0, z0), Vector3.up);
                Quad(vertices, triangles, new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0), Vector3.down);
                Quad(vertices, triangles, new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x0, y0, z1), Vector3.right);
                Quad(vertices, triangles, new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x1, y0, z1), Vector3.left);
                Quad(vertices, triangles, new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y0, z0), Vector3.forward);
                Quad(vertices, triangles, new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y0, z1), Vector3.back);
                MeshPiece("Hall too big to fade", vertices, triangles);
            }

            /// <summary>Each frame with the big wall or hall behind: how far the camera is in front of its face (below zero, beyond it).</summary>
            void SampleBigWall(ref float front, ref float nearest)
            {
                if (_moveCamera == null)
                    return;
                front = Least(front, _moveCamera.transform.position.z - _bigWallFace);
                nearest = Least(nearest, CameraToTarget());
            }

            /// <summary>
            /// A flight of StairsCount steps going down along +z to StairsSpot, a long landing at the top, made as
            /// First_Parkour's staircases are: one mesh of treads, risers, sides and back, and one non-convex collider of it.
            /// </summary>
            void BuildStairs()
            {
                var foot = Spot(StairsSpot);
                float x0 = foot.x - StairsWidth / 2f, x1 = foot.x + StairsWidth / 2f, y0 = foot.y;
                _stairsTopY = y0 + StairsCount * StairsRiser;
                _stairsFront = foot.z - (StairsCount - 1) * StairsTread;
                float back = _stairsFront - StairsLanding;
                var vertices = new List<Vector3>();
                var triangles = new List<int>();

                // The landing: its top, its sides and its back.
                Quad(vertices, triangles, new Vector3(x0, _stairsTopY, back), new Vector3(x0, _stairsTopY, _stairsFront),
                    new Vector3(x1, _stairsTopY, _stairsFront), new Vector3(x1, _stairsTopY, back), Vector3.up);
                Sides(back, _stairsFront, _stairsTopY);
                Quad(vertices, triangles, new Vector3(x0, y0, back), new Vector3(x0, _stairsTopY, back),
                    new Vector3(x1, _stairsTopY, back), new Vector3(x1, y0, back), Vector3.back);

                // Then each step down: its riser, and the tread in front of it (the last riser comes down to the floor).
                for (int k = 1; k <= StairsCount; k++)
                {
                    float z = _stairsFront + (k - 1) * StairsTread;
                    float high = _stairsTopY - (k - 1) * StairsRiser, low = _stairsTopY - k * StairsRiser;
                    Quad(vertices, triangles, new Vector3(x0, low, z), new Vector3(x0, high, z), new Vector3(x1, high, z), new Vector3(x1, low, z), Vector3.forward);
                    if (k == StairsCount)
                        break;
                    Quad(vertices, triangles, new Vector3(x0, low, z), new Vector3(x0, low, z + StairsTread),
                        new Vector3(x1, low, z + StairsTread), new Vector3(x1, low, z), Vector3.up);
                    Sides(z, z + StairsTread, low);
                }

                _stairsCollider = MeshPiece("Stairs", vertices, triangles).GetComponent<MeshCollider>();

                // Both sides of a stretch of the stairs as high as its top, from the floor up.
                void Sides(float from, float to, float top)
                {
                    Quad(vertices, triangles, new Vector3(x0, y0, from), new Vector3(x0, top, from), new Vector3(x0, top, to), new Vector3(x0, y0, to), Vector3.left);
                    Quad(vertices, triangles, new Vector3(x1, y0, from), new Vector3(x1, top, from), new Vector3(x1, top, to), new Vector3(x1, y0, to), Vector3.right);
                }
            }

            /// <summary>
            /// Each frame walking down the stairs: how high the camera is over the stairs right under it (below zero it is
            /// in them), and - while <paramref name="counting"/> - its distance from what it films, the turns of which tell
            /// it pumping in and out.
            /// </summary>
            void SampleStairs(bool counting)
            {
                if (_moveCamera == null || _stairsCollider == null)
                    return;
                _stairsFrames++;
                var at = _moveCamera.transform.position;
                float above = _stairsTopY + 1f;
                if (_stairsCollider.Raycast(new Ray(new Vector3(at.x, above, at.z), Vector3.down), out var hit, above - _groundY + 1f))
                {
                    float over = at.y - hit.point.y;
                    _stairsLowest = float.IsNaN(_stairsLowest) ? over : Mathf.Min(_stairsLowest, over);
                    if (over < -LookUpSlack)
                        _stairsInside++;
                }

                float distance = CameraToTarget();
                if (!counting || float.IsNaN(distance))
                    return;
                _stairsNearest = float.IsNaN(_stairsNearest) ? distance : Mathf.Min(_stairsNearest, distance);
                _stairsFarthest = float.IsNaN(_stairsFarthest) ? distance : Mathf.Max(_stairsFarthest, distance);
                CountTurns(distance);
            }

            /// <summary>
            /// Counts the camera's distance turning back: once it has gone one way by StairsSwing or more, a move back the
            /// other way by StairsSwing from the furthest it got is a turn. Smaller wobbles - the follow's own damping, the
            /// camera stepping in by a tread as the player steps down one - are not.
            /// </summary>
            void CountTurns(float distance)
            {
                if (_pumpHeading == 0)
                {
                    _pumpLow = float.IsNaN(_pumpLow) ? distance : Mathf.Min(_pumpLow, distance);
                    _pumpHigh = float.IsNaN(_pumpHigh) ? distance : Mathf.Max(_pumpHigh, distance);
                    if (distance >= _pumpLow + StairsSwing)
                        _pumpHeading = 1;
                    else if (distance <= _pumpHigh - StairsSwing)
                        _pumpHeading = -1;
                    _pumpExtreme = distance;
                    return;
                }

                if (_pumpHeading > 0 ? distance > _pumpExtreme : distance < _pumpExtreme)
                {
                    _pumpExtreme = distance;
                }
                else if (Mathf.Abs(distance - _pumpExtreme) >= StairsSwing)
                {
                    _stairsTurns++;
                    _pumpHeading = -_pumpHeading;
                    _pumpExtreme = distance;
                }
            }

            /// <summary>
            /// One quad of a made mesh, its corners in order round it, facing <paramref name="outward"/>: Unity draws a
            /// triangle, and a ray hits it, only from the side its corners go clockwise round.
            /// </summary>
            static void Quad(List<Vector3> vertices, List<int> triangles, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward)
            {
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f)
                    (b, d) = (d, b);
                int i = vertices.Count;
                vertices.Add(a);
                vertices.Add(b);
                vertices.Add(c);
                vertices.Add(d);
                triangles.Add(i);
                triangles.Add(i + 1);
                triangles.Add(i + 2);
                triangles.Add(i);
                triangles.Add(i + 2);
                triangles.Add(i + 3);
            }

            /// <summary>A static piece of level made of such quads: one mesh, drawn plain, and one non-convex collider of it, as a level's own meshes are.</summary>
            GameObject MeshPiece(string name, List<Vector3> vertices, List<int> triangles)
            {
                var mesh = new Mesh { name = name };
                mesh.SetVertices(vertices);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                var piece = new GameObject(name);
                piece.AddComponent<MeshFilter>().sharedMesh = mesh;
                piece.AddComponent<MeshRenderer>().sharedMaterial = PlainMaterial();
                piece.AddComponent<MeshCollider>().sharedMesh = mesh;
                return piece;
            }

            /// <summary>The material the made cubes get, for the made meshes too.</summary>
            Material PlainMaterial()
            {
                if (_plainMaterial == null)
                {
                    var probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    _plainMaterial = probe.GetComponent<Renderer>().sharedMaterial;
                    Object.DestroyImmediate(probe);
                }

                return _plainMaterial;
            }

            // ---- A slow screen -------------------------------------------------------------------------

            void StartStepRecorder()
            {
                var anim = _animal.Anim;
                _snapshotBones = ArmBones.Select(b => anim.GetBoneTransform(b)).Where(t => t != null).ToArray();
                _snapshots.Clear();
                _physicsSteps = 0;
                _physicsStepsWas = -1;
                if (_stepRecorder == null && _snapshotBones.Length > 0)
                    _stepRecorder = _animal.StartCoroutine(RecordSteps());
            }

            void StopStepRecorder()
            {
                if (_stepRecorder != null && _animal != null)
                    _animal.StopCoroutine(_stepRecorder);
                _stepRecorder = null;
            }

            /// <summary>
            /// Right after every physics step: where it left the root, and the pose it left the arms in (the
            /// Animator's own: nothing bends the arms after it).
            /// </summary>
            System.Collections.IEnumerator RecordSteps()
            {
                var wait = new WaitForFixedUpdate();
                while (true)
                {
                    yield return wait;
                    _physicsSteps++;
                    var snapshot = new StepSnapshot { root = _animal.transform.position, bones = new Quaternion[_snapshotBones.Length] };
                    for (int i = 0; i < _snapshotBones.Length; i++)
                        snapshot.bones[i] = _snapshotBones[i] != null ? _snapshotBones[i].localRotation : Quaternion.identity;
                    _snapshots.Add(snapshot);
                    if (_snapshots.Count > 3)
                        _snapshots.RemoveAt(0);
                }
            }

            /// <summary>
            /// One frame of the slow-screen run. The body is drawn a fraction of the way between where the last two
            /// physics steps left it; each arm should be shown the same fraction of the way between the poses those
            /// two steps left. How far it is off is counted in steps: showing the pose of a step too early reads about 1.
            /// </summary>
            void SampleSlowPose()
            {
                int steps = _physicsStepsWas >= 0 ? _physicsSteps - _physicsStepsWas : 0;
                _physicsStepsWas = _physicsSteps;
                _slowFrames++;
                if (_slowFrom < 0f)
                    _slowFrom = Now;
                _slowTo = Now;
                if (steps >= 2)
                    _slowTwoSteps++;

                var smoother = _animal.GetComponent<CharacterVisualInterpolator>();
                if (smoother == null || !smoother.isActiveAndEnabled || !smoother.smoothPose || _snapshots.Count < 2)
                    return;

                var older = _snapshots[_snapshots.Count - 2];
                var newer = _snapshots[_snapshots.Count - 1];
                var stride = newer.root - older.root;
                if (stride.sqrMagnitude < 0.01f * 0.01f)
                    return;

                // The fraction of the last step the body is drawn at.
                var shown = smoother.ShownPosition;
                float t = Vector3.Dot(shown - older.root, stride) / stride.sqrMagnitude;
                if (t < -0.02f || t > 1.02f || (shown - (older.root + stride * t)).sqrMagnitude > 0.01f * 0.01f)
                {
                    _slowOffStep++;
                    return;
                }

                t = Mathf.Clamp01(t);
                int arm = -1;
                float moved = 0f;
                for (int i = 0; i < _snapshotBones.Length; i++)
                {
                    float angle = Quaternion.Angle(older.bones[i], newer.bones[i]);
                    if (angle > moved)
                    {
                        moved = angle;
                        arm = i;
                    }
                }

                // At the ends of their swing the arms hardly move in a step: nothing to tell the fraction by.
                if (arm < 0 || moved < 1f || _snapshotBones[arm] == null)
                    return;

                var expected = Quaternion.Slerp(older.bones[arm], newer.bones[arm], t);
                float lag = Quaternion.Angle(_snapshotBones[arm].localRotation, expected) / moved;
                _slowJudged++;
                _slowLag.Add(lag);
                if (lag <= PoseLagLimit)
                    _slowMatched++;
            }

            // ---- -liveStockRig ---------------------------------------------------------------------------

            static string StockRigName => System.IO.Path.GetFileNameWithoutExtension(StockRig);

            /// <summary>
            /// Malbers' stock Cameras CM3, put in the scene switched off by -liveStockRig, has to stay off, and the
            /// player has to film with the team's rig: not the stock one, and with CameraOccluderFade on its brain.
            /// (ThirdPersonPlayer puts a fade on any rig it films with, so the brain alone would not tell.)
            /// </summary>
            void CheckStockRig(bool atEnd)
            {
                if (!_job.stockRig)
                    return;

                var stock = FindStockRig();
                var brain = _tps != null && _tps.Brain != null ? _tps.Brain : _brain;
                int brains = Object.FindObjectsByType<CinemachineBrain>(FindObjectsSortMode.None).Length;
                bool fades = brain != null && brain.GetComponent<CameraOccluderFade>() != null;
                bool stockFilms = stock != null && brain != null && brain.transform.IsChildOf(stock.transform);
                string films = brain != null ? "\"" + PathOf(brain.transform) + "\"" : "no camera";
                string state = $"{StockRigName} {(stock == null ? "gone" : stock.activeSelf ? "on" : "off")}, the player filming with {films} " +
                               $"({(fades ? "with" : "without")} CameraOccluderFade), {brains} active brain(s)";
                string when = atEnd ? "at the end" : "when the checks began";
                if (atEnd)
                    _stockRigEnded = state;
                else
                    _stockRigBegan = state;

                if (stock == null)
                    _failures.Add($"Stock rig: Malbers' {StockRigName} that -liveStockRig put in the scene was gone {when}.");
                else if (stock.activeSelf)
                    _failures.Add($"Stock rig: Malbers' {StockRigName}, left switched off in the scene, was switched on {when}.");
                if (brain == null || !fades || stockFilms)
                    _failures.Add($"Stock rig: {when} the player filmed with {films}, not with the team's rig (CameraOccluderFade on its brain).");
                if (brains > 1)
                    _failures.Add($"Stock rig: {brains} camera brains filmed at once {when}.");
            }

            static GameObject FindStockRig()
            {
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                {
                    foreach (var root in UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).GetRootGameObjects())
                    {
                        if (root.name == StockRigName)
                            return root;
                    }
                }

                return null;
            }

            // ---- Judging and the report ----------------------------------------------------------------

            static Vector3 MeanDirection(List<Vector3> directions)
            {
                var sum = Vector3.zero;
                foreach (var d in directions)
                    sum += d;
                return sum.sqrMagnitude > 0f ? sum.normalized : Vector3.zero;
            }

            static float Percentile(List<float> values, float p)
            {
                if (values.Count == 0)
                    return float.NaN;
                var sorted = values.OrderBy(v => v).ToList();
                float index = Mathf.Clamp01(p) * (sorted.Count - 1);
                int lo = Mathf.FloorToInt(index), hi = Mathf.CeilToInt(index);
                return Mathf.Lerp(sorted[lo], sorted[hi], index - lo);
            }

            float SprintTurn => _runDirections.Count > 0 && _sprintDirections.Count > 0
                ? Vector3.Angle(MeanDirection(_runDirections), MeanDirection(_sprintDirections))
                : float.NaN;

            /// <summary>The furthest the camera turned from where it looked at the end of the run, while sprinting.</summary>
            float SprintCameraTurn => _runCameraYaw.Count > 0 && _sprintCameraYaw.Count > 0
                ? _sprintCameraYaw.Max(y => Mathf.Abs(Mathf.DeltaAngle(_runCameraYaw.Last(), y)))
                : float.NaN;

            void JudgeMovement()
            {
                if (!_sprintEngaged)
                    _moveNotes.Add("Sprint never engaged (Malbers refused it): the sprint figures are of running.");
                float turn = SprintTurn;
                if (!float.IsNaN(turn) && turn > SprintTurnLimit)
                    _failures.Add($"Sprint: sprinting turned the direction of travel by {turn:0} degrees (limit {SprintTurnLimit:0}).");
                float cameraTurn = SprintCameraTurn;
                if (!float.IsNaN(cameraTurn) && cameraTurn > SprintTurnLimit)
                    _failures.Add($"Sprint: the camera swung {cameraTurn:0} degrees when sprinting started (limit {SprintTurnLimit:0}).");

                foreach (var pair in _soles)
                {
                    // The thin tile has its own, closer, limit (below).
                    if (pair.Key == SprintTileKey)
                        continue;
                    var support = pair.Value.Select(v => Mathf.Min(v.x, v.y)).ToList();
                    var higher = pair.Value.Select(v => Mathf.Max(v.x, v.y)).ToList();
                    float median = Percentile(support, 0.5f), low = Percentile(support, 0.05f), high = Percentile(support, 0.95f);
                    if (pair.Key.StartsWith("standing"))
                    {
                        // Standing, both shoes rest on the ground: neither in it nor over it.
                        float other = Percentile(higher, 0.5f);
                        if (Mathf.Abs(median) > SoleMedianLimit || low < -SoleSpreadLimit || other > SoleSpreadLimit)
                            _failures.Add($"Feet {pair.Key}: the lower sole sits at {SignedCm(median)} (5%: {SignedCm(low)}), the other at {SignedCm(other)}; " +
                                          $"limits {SignedCm(SoleMedianLimit)} and {SignedCm(SoleSpreadLimit)}.");
                    }
                    else if (low < -SoleMedianLimit || support.Min() < -SoleSpreadLimit)
                    {
                        // Moving, both shoes leave the ground at times (a run's flight): only sinking in counts.
                        _failures.Add($"Feet {pair.Key}: the supporting sole goes {SignedCm(low)} into the ground in 5% of frames, " +
                                      $"{SignedCm(support.Min())} at worst; limits {SignedCm(-SoleMedianLimit)} and {SignedCm(-SoleSpreadLimit)}.");
                    }
                }

                // Across a slope each resting shoe is turned to lie on it, as steep as it is.
                foreach (var pair in _slopeLie)
                {
                    float left = Percentile(pair.Value.Select(v => v.x).ToList(), 0.5f), right = Percentile(pair.Value.Select(v => v.y).ToList(), 0.5f);
                    if (left > SlopeLieLimit || right > SlopeLieLimit)
                        _failures.Add($"Feet {pair.Key}: the resting shoes' soles lie {left:0.0}° (left) and {right:0.0}° (right) off the slope at the median " +
                                      $"(limit {SlopeLieLimit:0}°): they are not turned to lie on it.");
                }

                if (_steep != null && !_slopeLie.ContainsKey(SteepKey))
                    _moveNotes.Add($"How the shoes lie across the {SteepAngle:0}° slope was not measured (no foot bones, or nothing measured on flat ground).");

                // Sprinting across the thin tile the shoes stay on its top, though a sprint plants them deeper than it is thick.
                if (_soles.TryGetValue(SprintTileKey, out var tile) && tile.Count > 0)
                {
                    var support = tile.Select(v => Mathf.Min(v.x, v.y)).ToList();
                    float deepest = support.Min(), low = Percentile(support, 0.05f);
                    if (deepest < -SprintToeLimit || low < -SoleMedianLimit)
                        _failures.Add($"Feet {SprintTileKey}: a shoe was drawn {SignedCm(deepest)} into the tile's top at worst, {SignedCm(low)} in 5% of frames " +
                                      $"(limits {SignedCm(-SprintToeLimit)} and {SignedCm(-SoleMedianLimit)}): a toe planted through the tile is not put back on its top.");
                }
                else if (_sprintTileRan)
                    _moveNotes.Add("The sprint across the floor tile measured no frame over it.");
                if (_sprintTileRan && !_sprintTileSprinted)
                    _moveNotes.Add("The run across the floor tile never sprinted (Malbers refused it): its figures are of running.");

                if (!float.IsNaN(_camDistanceWall) && !float.IsNaN(_camDistanceBefore) && _camDistanceWall < _camDistanceBefore - 0.3f)
                    _failures.Add($"Camera: a wall in the way pulled the camera in from {_camDistanceBefore:0.00} m to {_camDistanceWall:0.00} m.");
                if (_camWall != null && !_camWallShader.Contains("Occluder Dissolve"))
                    _failures.Add($"Camera: the wall in the way did not dissolve (it wore {_camWallShader}).");
                else if (_camWall != null && (float.IsNaN(_camWallDissolve) || _camWallDissolve < 0.99f))
                    _failures.Add($"Camera: the wall in the way only dissolved to {_camWallDissolve:0.00} after 0.8 s.");
                if (_camWall != null && _camWallShaderAfter.Contains("Occluder Dissolve"))
                    _failures.Add("Camera: the wall kept the dissolve material once it was out of the way.");
                else if (_camWall != null && _camWallBlockAfter)
                    _failures.Add("Camera: the wall came back with a property block it did not have before it faded: it stays out of the SRP Batcher.");
                if (!float.IsNaN(_holdDistance) && _holdDistance > LandingSlideLimit)
                    _failures.Add($"Landing: the body slid {_holdDistance:0.00} m after landing a running jump and letting go (limit {LandingSlideLimit:0.00} m).");
                if (!float.IsNaN(_slideDistance) && _slideDistance > LandingSlideLimit)
                    _failures.Add($"Landing: the body slid {_slideDistance:0.00} m over {_slideTime:0.00} s after landing with the stick let go (limit {LandingSlideLimit:0.00} m).");

                // Looking up, the ground has to stop the camera: nothing else does now.
                if (!float.IsNaN(_lookLowest) && _lookLowest < _lookRadius - LookUpSlack)
                    _failures.Add($"Camera: looking up ({_lookPitch:0}° up, the bottom of its pitch) took the camera to {SignedM(_lookLowest)} over the floor; " +
                                  (_lookHasCollider
                                      ? $"its ground collider should keep it {_lookRadius:0.00} m above."
                                      : "the live camera has no CameraGroundCollider to keep it above."));

                // What only grazes the view of the player hides part of them all the same, and fades: a ball, a pillar, and a
                // crate behind them the camera looks up over - which must leave the camera where it was.
                JudgeFade(_ballFade, $"a ball of {RoundBallRadius:0} m radius half way along the view, {RoundGap * 100f:0} cm beside the line to the player's chest,");
                JudgeFade(_pillarFade, $"a pillar {2f * RoundPillarRadius:0} m thick placed the same way");
                JudgeFade(_crateFade, $"a {CrateSize:0} m crate {CrateBehind:0} m behind the player, the camera looking {CrateLookUp:0}° up over it,");
                if (!float.IsNaN(_crateNearest) && !float.IsNaN(_crateClear) && _crateNearest < _crateClear - PulledIn)
                    _failures.Add($"Camera: a {CrateSize:0} m crate {CrateBehind:0} m behind the player, the camera looking {CrateLookUp:0}° up over it, pulled the camera " +
                                  $"in from {_crateClear:0.00} m to {_crateNearest:0.00} m: out there the camera is beyond the crate and above the floor, so it should stay " +
                                  "and the crate fade.");

                // ToonScapes stone dissolves in the colour it is drawn in - its tint is off - and mapped as it is.
                if (_stoneTried && !_stoneFaded)
                    _failures.Add("Camera: the box of ToonScapes stone in the way never faded.");
                else if (_stoneFaded)
                {
                    if (_stoneCopyColor.r < 0.99f || _stoneCopyColor.g < 0.99f || _stoneCopyColor.b < 0.99f)
                        _failures.Add($"Camera: the ToonScapes stone in the way dissolved coloured ({_stoneCopyColor.r:0.00}, {_stoneCopyColor.g:0.00}, {_stoneCopyColor.b:0.00}), " +
                                      "not white: its material has the tint switched off (a red left in it), so the stone is drawn untinted.");
                    if (!_stoneHasKeyword)
                        _failures.Add($"Camera: {_stoneCopyShader} has no {TriplanarKeyword} keyword: ToonScapes stone dissolves mapped by its UVs, not in object space as it is drawn.");
                    else if (!_stoneTriplanar)
                        _failures.Add($"Camera: the dissolving copy of the ToonScapes stone has {TriplanarKeyword} off: it is mapped by its UVs, not in object space " +
                                      $"at 1/{StoneTiling:0} as the stone is.");
                    else if (float.IsNaN(_stoneTriplanarScale) || Mathf.Abs(_stoneTriplanarScale - 1f / StoneTiling) > 1e-4f ||
                             float.IsNaN(_stoneTriplanarFalloff) || Mathf.Abs(_stoneTriplanarFalloff - StoneFalloff) > 1e-3f)
                        _failures.Add($"Camera: the dissolving copy of the ToonScapes stone maps it at {_stoneTriplanarScale:0.0000} per unit with a falloff of " +
                                      $"{_stoneTriplanarFalloff:0.00}, not 1/{StoneTiling:0} ({1f / StoneTiling:0.0000}) with {StoneFalloff:0} as the stone is: " +
                                      "it changes scale or blend as it starts to fade.");
                }

                // Against a chest-high block, looking down over it, the camera stays out of the player's body.
                if (!float.IsNaN(_blockNearest) && _blockNearest < NearestCamera)
                    _failures.Add($"Camera: standing against a {BlockHeight:0.00} m block and looking down {BlockLookFrom:0}-{BlockLookTo:0}° over it, the camera came within " +
                                  $"{_blockNearest:0.00} m of what it films (looking {_blockPitch:0}° down; limit {NearestCamera:0} m): it is in the player's body.");

                // In a tunnel made in one collider - what the player stands in - the camera stays inside, as clear of it as of
                // the floor, and out of the player's body; the tunnel never fades. So it does in one made of two pieces, the
                // joint behind the player, and standing on a heap lying on the tunnel's floor.
                foreach (var view in _tunnelViews)
                {
                    if (float.IsNaN(view.floor))
                        continue;
                    string where = view.Joint ? $"a tunnel of two pieces, one collider each ({TunnelWidth} x {TunnelHeight:0} m inside)"
                        : $"a tunnel made in one collider ({TunnelWidth} x {TunnelHeight:0} m inside)";
                    string looking = view.down ? "looking down" : "looking up";
                    float least = _tunnelRadius - LookUpSlack;
                    if (view.floor < least || view.wall < least || view.roof < least)
                        _failures.Add($"Camera: {looking} in {where}, {view.way}, the camera came to {SignedM(view.floor)} over its floor, " +
                                      $"{SignedM(view.wall)} inside its walls and {SignedM(view.roof)} under its roof (below zero is outside it; " +
                                      $"it should keep {_tunnelRadius:0.00} m).");
                    if (!view.down && view.nearest < NearestCamera)
                        _failures.Add($"Camera: {looking} in {where}, {view.way}, the camera came within {view.nearest:0.00} m of what it films " +
                                      $"(limit {NearestCamera:0} m): it is on the player's head or in their body.");
                    if (view.faded > 0)
                        _failures.Add($"Camera: {looking} in {where}, {view.way}, the tunnel faded in {view.faded} of {view.frames} frames, " +
                                      "though the player stands in it.");
                }

                // A hand whose sphere already touches what the player stands in: the floor still stops the camera.
                float overlapLeast = _tunnelRadius - LookUpSlack;
                if (_overlapTried && (float.IsNaN(_overlapWhole) || _overlapWhole < overlapLeast || float.IsNaN(_overlapShort) || _overlapShort < overlapLeast))
                    _failures.Add($"Camera: with its hand {OverlapGap:0.00} m from a tunnel's wall (its sphere touching the tunnel the player stands in), " +
                                  $"looking {OverlapPitch:0}° up along it, the camera came to {SignedM(_overlapWhole)} over the floor at its whole distance " +
                                  $"and {SignedM(_overlapShort)} with a distance ending {OverlapShort * 100f:0} cm over it (it should keep {_tunnelRadius:0.00} m).");

                // What cannot fade is never gone through: the camera stays in front of it, whichever way its faces are turned.
                if (!float.IsNaN(_bigWallFront) && _bigWallFront < _bigWallRadius - LookUpSlack)
                    _failures.Add($"Camera: a {BigWallWidth:0} m wall {BigWallBehind:0} m behind the player, too big to fade (CameraOccluderFade's Max Size), " +
                                  $"had the camera {SignedM(_bigWallFront)} in front of its face (below zero beyond it, filming its back): it should stop " +
                                  "in front of it.");
                if (!float.IsNaN(_bigHallFront) && _bigHallFront < _bigWallRadius - LookUpSlack)
                    _failures.Add($"Camera: a {BigWallWidth:0} m hall made in one mesh with its faces turned in, {BigWallBehind:0} m behind the player and too " +
                                  $"big to fade, had the camera {SignedM(_bigHallFront)} in front of its near wall (below zero inside it, where that wall hides " +
                                  "the player): it should stop in front of it.");

                // Walking down stairs with the view level, the camera neither goes into them nor pumps in and out.
                if (_stairsInside > 0)
                    _failures.Add($"Camera: walking down {StairsCount} steps of {StairsRiser * 100f:0} cm with the view level, the camera was inside the stairs in " +
                                  $"{_stairsInside} of {_stairsFrames} frames, up to {-_stairsLowest:0.00} m deep.");
                if (_stairsTurns > StairsTurnLimit)
                    _failures.Add($"Camera: walking down the stairs, the camera's distance turned back by {StairsSwing:0.0} m or more {_stairsTurns} times " +
                                  $"(limit {StairsTurnLimit}): it pumps in and out, between {_stairsNearest:0.00} and {_stairsFarthest:0.00} m.");

                // Neither the floor under the player nor a wall in front of or beside them is between the camera and the player.
                if (_tileStandingFaded + _tileCrouchedFaded > 0)
                    _failures.Add($"Camera: the floor tile under the player faded in {_tileCrouchedFaded} of {_tileCrouched} frames crouched " +
                                  $"and {_tileStandingFaded} of {_tileStanding} standing on it.");
                if (_tileTried && _tileCrouch != null && _tileCrouched == 0)
                    _moveNotes.Add("The player never crouched on the floor tile: only standing on it was checked.");
                if (_frontFaded > 0)
                    _failures.Add($"Camera: the wall in front of the player faded in {_frontFaded} of {_frontFrames} frames of walking up against it " +
                                  $"({_frontAgainst} of them against it), though it was never between the camera and the player.");
                if (_frontFrames > 0 && _frontAgainst == 0)
                    _moveNotes.Add($"The run never reached the wall in front (the body's middle came {_frontClosest:0.00} m from its face).");
                if (_sideFaded > 0)
                    _failures.Add($"Camera: the wall beside the player faded in {_sideFaded} of {_sideFrames} frames of running along it " +
                                  $"(the body's middle {_sideClosest:0.00} m from its face at the closest), though it was never between the camera and the player.");
                if (_sideWallRenderer != null && _sideFrames == 0)
                    _moveNotes.Add("The run along the wall beside the player never got alongside it.");
                else if (_sideFrames > 0 && _sideClosest > AgainstWall)
                    _moveNotes.Add($"Running along the wall beside, the body's middle stayed {_sideClosest:0.00} m or more from it: " +
                                   "that says little about a wall right next to the player.");

                // Down the ramp the resting soles lie on it: no hovering, no shaking from one frame to the next, and
                // nothing of how far the body is drawn over its physics root (where it is drawn is where they stand).
                if (_rampResting.Count > 0)
                {
                    float hover = Percentile(_rampResting, 0.5f);
                    float jitter = _rampJitter.Count > 0 ? _rampJitter.Average() : 0f;
                    if (hover > SoleMedianLimit || jitter > RestingJitterLimit)
                        _failures.Add($"Feet {RampKey}: the resting soles sit {SignedCm(hover)} over it at the median and move {jitter * 100f:0.00} cm " +
                                      $"from one frame to the next on average; limits {SignedCm(SoleMedianLimit)} and {RestingJitterLimit * 100f:0.0} cm.");
                    float follows = Slope(_rampLagged, out float lagSpread);
                    if (!float.IsNaN(follows) && lagSpread >= RampLagSpread && follows > RampFollowLimit)
                        _failures.Add($"Feet {RampKey}: the resting soles rise {follows:0.00} cm for every cm the body is drawn over its physics root " +
                                      $"(limit {RampFollowLimit:0.0}): they are put on the ground against the root, not where the body is drawn.");
                    else if (!float.IsNaN(lagSpread) && lagSpread < RampLagSpread)
                        _moveNotes.Add($"Down the ramp the body was drawn within {lagSpread * 100f:0.00} cm of the same height over its physics root " +
                                       "all along: too little to tell whether the feet follow it.");
                }
                else if (_rampRan)
                    _moveNotes.Add("No resting sole was measured on the ramp.");

                // Up the steps no foot jumps a riser from one frame to the next.
                foreach (var (foot, pop) in new[] { ("left", _stepsPopLeft), ("right", _stepsPopRight) })
                {
                    if (!float.IsNaN(pop) && pop > StepPopLimit)
                        _failures.Add($"Feet walking up {StepRiser * 100f:0} cm steps: the {foot} foot jumped {pop * 100f:0.0} cm from one frame to the next, " +
                                      $"over where the body is drawn (limit {StepPopLimit * 100f:0} cm, half a riser).");
                }

                // Nor does FootGrounder lift one a riser at once: it eases the new ground in.
                foreach (var (foot, pop) in new[] { ("left", _stepsFixPopLeft), ("right", _stepsFixPopRight) })
                {
                    if (!float.IsNaN(pop) && pop > StepCorrectionLimit)
                        _failures.Add($"Feet walking up {StepRiser * 100f:0} cm steps: FootGrounder changed the {foot} foot's correction by {pop * 100f:0.0} cm " +
                                      $"from one frame to the next (limit {StepCorrectionLimit * 100f:0} cm): a riser is taken at once instead of eased in.");
                }

                if (_stepsJumps.Count > 0 && float.IsNaN(_stepsFixPopLeft) && float.IsNaN(_stepsFixPopRight))
                    _moveNotes.Add("No working FootGrounder on the character: its own correction was not measured on the steps.");

                // Nor does the body itself pop up a riser: Malbers climbs it over several physics steps.
                if (!float.IsNaN(_stepsBodyRise) && _stepsBodyRise > StepBodyRiseLimit)
                    _failures.Add($"Feet walking up {StepRiser * 100f:0} cm steps: the body was drawn rising {_stepsBodyRise * 100f:0.0} cm from one frame to the " +
                                  $"next (limit {StepBodyRiseLimit * 100f:0} cm; the physics root rose up to {_stepsRootRise * 100f:0.0} cm in one physics step): " +
                                  "Malbers lifts it up a riser all but at once (its Align Pos Lerp).");

                // Running up them, the planted shoes keep up with the treads: on them, not left in them.
                if (_stepsRunPlanted.Count > 0)
                {
                    float median = Percentile(_stepsRunPlanted, 0.5f), lowest = _stepsRunPlanted.Min();
                    int deep = _stepsRunPlanted.Count(v => v < -StepsRunDeepLimit);
                    if (Mathf.Abs(median) > SoleMedianLimit || deep > StepsRunDeepFrames || lowest < -StepsRunDeepest)
                        _failures.Add($"Feet running up {StepRiser * 100f:0} cm steps: the planted shoes sit {SignedCm(median)} over the tread under them at the " +
                                      $"median, {deep} of {_stepsRunPlanted.Count} planted frames more than {StepsRunDeepLimit * 100f:0} cm in it and " +
                                      $"{SignedCm(lowest)} at the lowest (limits {SoleMedianLimit * 100f:0.0} cm either way, {StepsRunDeepFrames} frame and " +
                                      $"{SignedCm(-StepsRunDeepest)}): FootGrounder falls behind the steps rising under a running shoe and leaves it in them.");
                }
                else if (_stepsRunTried)
                    _moveNotes.Add("Running up the steps measured no planted shoe.");

                // Over stairs as the levels have them, walking or running, up or down, no foot jumps and FootGrounder changes no
                // correction more from one frame to the next than up the level steps.
                foreach (var course in _flightRuns)
                {
                    foreach (var (foot, pop) in new[] { ("left", course.popLeft), ("right", course.popRight) })
                    {
                        if (!float.IsNaN(pop) && pop > StepPopLimit)
                            _failures.Add($"Feet {course.what}: the {foot} foot jumped {pop * 100f:0.0} cm from one frame to the next, over where the " +
                                          $"body is drawn (limit {StepPopLimit * 100f:0} cm, as up the level steps).");
                    }

                    foreach (var (foot, pop) in new[] { ("left", course.fixLeft), ("right", course.fixRight) })
                    {
                        if (!float.IsNaN(pop) && pop > StepCorrectionLimit)
                            _failures.Add($"Feet {course.what}: FootGrounder changed the {foot} foot's correction by {pop * 100f:0.0} cm from one frame " +
                                          $"to the next (limit {StepCorrectionLimit * 100f:0} cm, as up the level steps).");
                    }

                    if (course.tried && course.jumps.Count == 0)
                        _moveNotes.Add($"{Capital(course.what)}: no frame was measured.");
                }

                // On a slow screen the pose is shown between the same two physics steps as the body.
                if (_slowJudged > 0 && _slowMatched < PoseMatchShare * _slowJudged)
                    _failures.Add($"Smoothness at {SlowFps:0} fps: the arms were shown between the same two physics steps as the body in only " +
                                  $"{100f * _slowMatched / _slowJudged:0}% of {_slowJudged} frames (limit {PoseMatchShare * 100f:0}%), off by " +
                                  $"{Percentile(_slowLag, 0.5f):0.00} of a step at the median.");
                if (_slowFrames > 0 && _slowJudged == 0)
                    _moveNotes.Add("The slow-screen run had no frame to judge the pose by (no smoothed pose, or the arms never moved).");
                else if (_slowFrames > 0 && _slowTwoSteps < 0.3f * _slowFrames)
                    _moveNotes.Add($"The slow screen ran two physics steps in only {_slowTwoSteps} of {_slowFrames} frames: it says little about them.");
            }

            /// <summary>Something put in the view hides part of the player: it has to be faded in FadeShare of the frames watched.</summary>
            void JudgeFade(FadeWatch watch, string what)
            {
                if (watch.renderer != null && watch.frames > 0 && watch.faded < FadeShare * watch.frames)
                    _failures.Add($"Camera: {what} faded in only {watch.faded} of {watch.frames} frames (limit {FadeShare * 100f:0}%), though it hides part of the player.");
            }

            static string SignedCm(float metres) => float.IsNaN(metres) ? "-" : $"{metres * 100f:+0.0;-0.0;0.0} cm";

            static string SignedM(float metres) => float.IsNaN(metres) ? "-" : $"{metres:+0.00;-0.00;0.00} m";

            void WriteMovement(StringBuilder md)
            {
                if (!_job.movement)
                    return;

                md.AppendLine("## Movement");
                md.AppendLine();
                foreach (var note in _moveNotes)
                    md.AppendLine("- " + note);
                if (_moveNotes.Count > 0)
                    md.AppendLine();

                md.AppendLine("### Sprint");
                md.AppendLine();
                md.AppendLine($"- Running: travel yaw {(_runDirections.Count > 0 ? Yaw(MeanDirection(_runDirections)).ToString("0") : "-")}°, camera yaw " +
                              $"{(_runCameraYaw.Count > 0 ? _runCameraYaw.Average().ToString("0") : "-")}°, live camera {string.Join(", ", _runCameras)}.");
                md.AppendLine($"- Sprinting (engaged: {_sprintEngaged}): travel yaw {(_sprintDirections.Count > 0 ? Yaw(MeanDirection(_sprintDirections)).ToString("0") : "-")}°, camera yaw " +
                              $"{(_sprintCameraYaw.Count > 0 ? _sprintCameraYaw.Average().ToString("0") : "-")}°, live camera {string.Join(", ", _sprintCameras)}.");
                md.AppendLine($"- Turn from running to sprinting: travel {SprintTurn:0}°, camera {SprintCameraTurn:0}° (limit {SprintTurnLimit:0}°).");
                md.AppendLine();

                md.AppendLine("### Feet");
                md.AppendLine();
                md.AppendLine("The sole of the lower foot over the floor right under it, every frame (negative = in the floor).");
                md.AppendLine();
                md.AppendLine("| | Frames | Median | 5% | 95% | Deepest | Highest support | Left / right median |");
                md.AppendLine("|---|---|---|---|---|---|---|---|");
                foreach (var pair in _soles)
                {
                    var support = pair.Value.Select(v => Mathf.Min(v.x, v.y)).ToList();
                    var left = pair.Value.Select(v => v.x).ToList();
                    var right = pair.Value.Select(v => v.y).ToList();
                    md.AppendLine($"| {pair.Key} | {support.Count} | {SignedCm(Percentile(support, 0.5f))} | {SignedCm(Percentile(support, 0.05f))} | " +
                                  $"{SignedCm(Percentile(support, 0.95f))} | {SignedCm(support.Min())} | {SignedCm(support.Max())} | " +
                                  $"{SignedCm(Percentile(left, 0.5f))} / {SignedCm(Percentile(right, 0.5f))} |");
                }
                md.AppendLine();
                if (_rampResting.Count > 0)
                {
                    float follows = Slope(_rampLagged, out float lagSpread);
                    md.AppendLine($"- Running down a {RampAngle:0}° ramp at {_rampSpeed:0.0} m/s (its row above: each sole against the ramp right under it): " +
                                  $"the resting soles (within {RestingClearance * 100f:0} cm of it) sit {SignedCm(Percentile(_rampResting, 0.5f))} over it at the median " +
                                  $"and move {(_rampJitter.Count > 0 ? _rampJitter.Average() * 100f : 0f):0.00} cm from one frame to the next on average " +
                                  $"(limits {SignedCm(SoleMedianLimit)} and {RestingJitterLimit * 100f:0.0} cm). The body was drawn up to " +
                                  $"{SignedCm(_rampDrawnHigh)} over its physics root, gliding down between steps (spread {SignedCm(lagSpread)}); the resting " +
                                  $"soles rose {follows:0.00} cm with each cm of that (limit {RampFollowLimit:0.0}: the feet stand where the body is drawn).");
                }
                if (_stepsJumps.Count > 0)
                    md.AppendLine($"- Walking up {StepCount} steps of {StepRiser * 100f:0} cm" + (StepsWalks > 1 ? $", {_stepsWalks} times" : "") +
                                  $" (Malbers speed {_stepsSpeed}; the body rose {_stepsRise:0.00} m): " +
                                  $"the most a foot jumped from one frame to the next, over where the body is drawn, was {_stepsPopLeft * 100f:0.0} cm left " +
                                  $"and {_stepsPopRight * 100f:0.0} cm right; 95% of frames under {Percentile(_stepsJumps, 0.95f) * 100f:0.0} cm " +
                                  $"(limit {StepPopLimit * 100f:0} cm, half a riser). FootGrounder's own correction changed by at most " +
                                  $"{_stepsFixPopLeft * 100f:0.0} cm left and {_stepsFixPopRight * 100f:0.0} cm right from one frame to the next " +
                                  $"(limit {StepCorrectionLimit * 100f:0} cm). The body drawn rose at most {_stepsBodyRise * 100f:0.0} cm from one frame to " +
                                  $"the next (limit {StepBodyRiseLimit * 100f:0} cm), the physics root at most {_stepsRootRise * 100f:0.0} cm in one physics step" +
                                  (_stepsHitches > 0 ? $"; {_stepsHitches} hitch frame(s) left out." : "."));
                WriteStepsFrames(md);
                if (_stepsRunTried)
                {
                    string pace = (_stepsRunSpeed >= 0 ? $"Malbers speed {_stepsRunSpeed}" : "no Malbers speed") +
                                  (float.IsNaN(_stepsRunPace) ? "" : $", {_stepsRunPace:0.0} m/s over them");
                    md.AppendLine(_stepsRunPlanted.Count > 0
                        ? $"- Running up the same steps ({pace}): the planted shoes (the lowest point within {RestingClearance * 100f:0} cm of the step under it, " +
                          $"the foot moving under {PlantedSpeed:0.0} m/s along it) sat {SignedCm(Percentile(_stepsRunPlanted, 0.5f))} over it at the median " +
                          $"(limit {SoleMedianLimit * 100f:0.0} cm either way); {_stepsRunPlanted.Count(v => v < -StepsRunDeepLimit)} of " +
                          $"{_stepsRunPlanted.Count} planted frames (in {_stepsRunFrames} frames measured) were more than {StepsRunDeepLimit * 100f:0} cm in it " +
                          $"(limit {StepsRunDeepFrames}, and none more than {StepsRunDeepest * 100f:0} cm); the lowest: " +
                          string.Join(", ", _stepsRunPlanted.OrderBy(v => v).Take(4).Select(SignedCm)) + "."
                        : $"- Running up the same steps ({pace}): no planted shoe was measured in {_stepsRunFrames} frames.");
                }
                if (_slopeLie.Count > 0)
                    md.AppendLine("- Standing across a slope, how far each resting shoe's sole lies off it at the median (left / right; limit " +
                                  $"{SlopeLieLimit:0}°, against the same pose on flat ground): " +
                                  string.Join("; ", _slopeLie.Select(p => $"{p.Key} {Percentile(p.Value.Select(v => v.x).ToList(), 0.5f):0.0}° / " +
                                                                          $"{Percentile(p.Value.Select(v => v.y).ToList(), 0.5f):0.0}°")) + ".");
                bool tileMeasured = _soles.TryGetValue(SprintTileKey, out var tile) && tile.Count > 0;
                if (tileMeasured)
                    md.AppendLine($"- Sprinting across a {SprintTileThickness * 100f:0} cm floor tile lying on the floor (its row above; sprint engaged: {_sprintTileSprinted}): " +
                                  $"a shoe went {SignedCm(tile.Min(v => Mathf.Min(v.x, v.y)))} into the tile's top at worst (limit {SignedCm(-SprintToeLimit)}).");
                if (_rampResting.Count > 0 || _stepsJumps.Count > 0 || _stepsRunTried || _slopeLie.Count > 0 || tileMeasured)
                    md.AppendLine();
                WriteFlights(md);

                md.AppendLine("### Air control and landing");
                md.AppendLine();
                md.AppendLine($"- Standing jump, stick held sideways from take-off: {(_airSideways):0.00} m sideways in {_airTime:0.00} s.");
                md.AppendLine($"- Running jump, stick let go in the air: slid {_slideDistance:0.00} m in {_slideTime:0.00} s after landing (limit {LandingSlideLimit:0.00} m).");
                md.AppendLine($"- Stopping from a run on the ground: {_stopDistance:0.00} m in {_stopTime:0.00} s.");
                md.AppendLine($"- Running jump, stick turned hard right at take-off: {_turnSideways:0.00} m to the side, " +
                              $"travelling {_turnAngle:0}° off the run when it landed; turned 45° " +
                              $"{(_turnHalfAt > 0f ? (_turnHalfAt - _turnTakeoff).ToString("0.00") + " s" : "never")} after take-off.");
                md.AppendLine($"- Running jump, stick held until the feet land, then let go: {_holdDistance:0.00} m in {_holdTime:0.00} s after landing.");
                foreach (var pair in _skate)
                {
                    float time = _skateTime.TryGetValue(pair.Key, out float t) ? t : 0f;
                    md.AppendLine($"- Planted shoes sliding along the floor while {pair.Key}: {pair.Value * 100f:0.0} cm in {time:0.00} s " +
                                  $"({(time > 0f ? pair.Value * 100f / time : 0f):0.0} cm/s).");
                }
                md.AppendLine();

                md.AppendLine("### Camera");
                md.AppendLine();
                md.AppendLine($"- Distance to the player: {_camDistanceBefore:0.00} m clear, {_camDistanceWall:0.00} m with a wall in between.");
                md.AppendLine($"- The wall wore {_camWallShader}, dissolved {_camWallDissolve:0.00}; {_fadedWhileWall} renderer(s) faded. " +
                              $"Out of the way it wears {_camWallShaderAfter} again, {(_camWallBlockAfter ? "still with" : "with no")} property block; " +
                              $"{_fadedAfter} still faded.");
                if (!float.IsNaN(_lookLowest))
                    md.AppendLine($"- Looking up as far as it goes ({_lookPitch:0}° up) on flat floor: the camera came down to {SignedM(_lookLowest)} over the floor, " +
                                  $"{_lookDistance:0.00} m from its target; " +
                                  (_lookHasCollider ? $"its ground collider keeps it {_lookRadius:0.00} m above." : "the live camera has no CameraGroundCollider."));
                if (_tileTried)
                    md.AppendLine($"- Crouching on a {TileSize:0} x {TileThickness:0.00} x {TileSize:0} m floor tile: the camera target came down to " +
                                  $"{_tileTargetLow:0.00} m over it; the tile faded in {_tileCrouchedFaded} of {_tileCrouched} frames crouched and " +
                                  $"{_tileStandingFaded} of {_tileStanding} standing.");
                if (_frontFrames > 0)
                    md.AppendLine($"- Walking up against a {FrontWallHeight:0} m wall in front, the camera behind: {_frontAgainst} of {_frontFrames} frames against it " +
                                  $"(the body's middle {_frontClosest:0.00} m from its face at the closest); it faded in {_frontFaded}.");
                if (_sideWallRenderer != null)
                    md.AppendLine($"- Running along a {SideWallHeight:0} m wall right beside them, on the {_sideWhich} (away from the camera's shoulder), " +
                                  $"the camera behind: {_sideFrames} frames alongside it (the body's middle {_sideClosest:0.00} m from its face at the " +
                                  $"closest); it faded in {_sideFaded}.");
                if (_ballFade.renderer != null)
                    md.AppendLine($"- A ball of {RoundBallRadius:0} m radius half way along the view, {RoundGap * 100f:0} cm beside the line to the player's chest " +
                                  $"(on the side away from the camera's shoulder): it faded in {_ballFade.faded} of {_ballFade.frames} frames; a pillar " +
                                  $"{2f * RoundPillarRadius:0} m thick placed the same way, in {_pillarFade.faded} of {_pillarFade.frames} (limit {FadeShare * 100f:0}%).");
                if (_stoneTried)
                    md.AppendLine("- A box of ToonScapes stone in the way, its tint switched off with a red left in it: " +
                                  (_stoneFaded
                                      ? $"it dissolved on {_stoneCopyShader}, coloured ({_stoneCopyColor.r:0.00}, {_stoneCopyColor.g:0.00}, {_stoneCopyColor.b:0.00}) " +
                                        $"(white wanted), {TriplanarKeyword} {(_stoneHasKeyword ? (_stoneTriplanar ? "on" : "off") : "not in the shader")} (on wanted), " +
                                        $"{_stoneTriplanarScale:0.0000} per unit (1/{StoneTiling:0} wanted), falloff {_stoneTriplanarFalloff:0.00} " +
                                        $"({StoneFalloff:0} wanted)."
                                      : "it never faded."));
                if (!float.IsNaN(_crateClear))
                    md.AppendLine($"- A {CrateSize:0} m crate {CrateBehind:0} m behind the player, the camera looking {CrateLookUp:0}° up over it: the camera was " +
                                  $"{_crateClear:0.00} m from its target before it was put there and at the nearest {_crateNearest:0.00} m with it there " +
                                  $"(limit {PulledIn:0.0} m nearer); it faded in {_crateFade.faded} of {_crateFade.frames} frames.");
                if (_blockTried)
                    md.AppendLine($"- Standing against a {BlockHeight:0.00} m block, looking down {BlockLookFrom:0}-{BlockLookTo:0}° over it: the camera came within " +
                                  $"{_blockNearest:0.00} m of its target at the nearest (looking {_blockPitch:0}° down; limit {NearestCamera:0} m).");
                if (_tunnelTried)
                {
                    foreach (bool joint in new[] { false, true })
                    {
                        var looks = _tunnelViews.Where(v => v.Joint == joint && !float.IsNaN(v.floor)).ToList();
                        if (looks.Count == 0)
                            continue;
                        md.AppendLine($"- Looking up to the bottom of the pitch (unless said otherwise) in a tunnel " +
                                      (joint ? "of two pieces, one collider each, joined behind the player" : "made in one collider") +
                                      $" ({TunnelWidth} x {TunnelHeight:0} m inside), the camera's least clearance from its floor / walls / roof, and the " +
                                      "nearest it came to its target: " +
                                      string.Join("; ", looks.Select(v => $"{v.way} {SignedM(v.floor)} / {SignedM(v.wall)} / {SignedM(v.roof)}, {v.nearest:0.00} m" +
                                                                          (v.faded > 0 ? $" (the tunnel faded in {v.faded} of {v.frames} frames)" : ""))) +
                                      $" (it should keep {_tunnelRadius:0.00} m, its ground collider's radius, and {NearestCamera:0} m from its target " +
                                      "looking up).");
                    }

                    if (_overlapTried)
                        md.AppendLine($"- Its hand put {OverlapGap:0.00} m from the tunnel's wall (its sphere touching the tunnel), looking {OverlapPitch:0}° up along it: " +
                                      $"the ground collider stops the camera {SignedM(_overlapWhole)} over the floor at its whole distance, and " +
                                      $"{SignedM(_overlapShort)} with a distance ending {OverlapShort * 100f:0} cm over it.");
                }
                if (!float.IsNaN(_bigWallFront))
                    md.AppendLine($"- A {BigWallWidth:0} x {BigWallHeight:0} m wall {BigWallBehind:0} m behind the player, too big to fade: the camera, " +
                                  $"{_bigWallBefore:0.00} m from its target before, kept {SignedM(_bigWallFront)} in front of its face, " +
                                  $"{_bigWallNearest:0.00} m from its target at the nearest (it should keep {_bigWallRadius:0.00} m in front)" +
                                  (float.IsNaN(_bigHallFront) ? "." : $"; a hall made in one mesh in its place, its faces turned in: {SignedM(_bigHallFront)} " +
                                                                      $"in front of its near wall, {_bigHallNearest:0.00} m from its target at the nearest."));
                if (_stairsFrames > 0)
                    md.AppendLine($"- Walking down {StairsCount} steps of {StairsRiser * 100f:0} cm made in one mesh, the view level ({_stairsWalked:0.0} m walked from the " +
                                  $"top): the camera was inside the stairs in {_stairsInside} of {_stairsFrames} frames (at its lowest {SignedM(_stairsLowest)} over them); " +
                                  $"its distance from its target went between {_stairsNearest:0.00} and {_stairsFarthest:0.00} m and turned back by {StairsSwing:0.0} m " +
                                  $"or more {_stairsTurns} times (limit {StairsTurnLimit}).");
                if (_job.stockRig)
                    md.AppendLine($"- Malbers' stock {StockRigName}, put in the scene switched off: when the checks began {_stockRigBegan ?? "-"}; " +
                                  $"at the end {_stockRigEnded ?? "-"}.");
                md.AppendLine($"- Pictures in camera/: {string.Join(", ", _camShots)}.");
                md.AppendLine();

                md.AppendLine("### Smoothness");
                md.AppendLine();
                md.AppendLine("The hips on a 1920x1080 screen while the camera follows: frame-to-frame change of their on-screen speed " +
                              "(jerk, px), and how many frames show a new pose.");
                md.AppendLine();
                md.AppendLine("| | Frames | fps | Jerk RMS | Jerk 95% | Frames with a new pose |");
                md.AppendLine("|---|---|---|---|---|---|");
                foreach (var pair in _smooth)
                {
                    var s = pair.Value;
                    var jerk = new List<float>();
                    for (int i = 2; i < s.screen.Count; i++)
                        jerk.Add((s.screen[i] - 2f * s.screen[i - 1] + s.screen[i - 2]).magnitude);
                    float span = s.times.Count > 1 ? s.times.Last() - s.times.First() : 0f;
                    float fps = span > 0f ? (s.times.Count - 1) / span : 0f;
                    float rms = jerk.Count > 0 ? Mathf.Sqrt(jerk.Average(j => j * j)) : float.NaN;
                    md.AppendLine($"| {pair.Key} | {s.frames} | {fps:0} | {rms:0.00} | {Percentile(jerk, 0.95f):0.00} | " +
                                  $"{(s.frames > 1 ? 100f * s.poseChanged / (s.frames - 1) : 0f):0}% |");
                }
                md.AppendLine();
                if (_slowFrames > 0)
                {
                    float slowFps = _slowTo > _slowFrom ? (_slowFrames - 1) / (_slowTo - _slowFrom) : 0f;
                    md.AppendLine($"- A slow screen ({slowFps:0} fps; {100f * _slowTwoSteps / _slowFrames:0}% of {_slowFrames} frames ran two or more physics steps): " +
                                  $"the arms were shown between the same two steps as the body in " +
                                  $"{(_slowJudged > 0 ? 100f * _slowMatched / _slowJudged : 0f):0}% of {_slowJudged} frames (limit {PoseMatchShare * 100f:0}%), " +
                                  $"off by {Percentile(_slowLag, 0.5f):0.00} of a step at the median and {Percentile(_slowLag, 0.95f):0.00} at 95%" +
                                  (_slowOffStep > 0 ? $"; in {_slowOffStep} frames the body was not drawn between its last two steps." : "."));
                    md.AppendLine();
                }
            }
        }
    }
}
#endif
