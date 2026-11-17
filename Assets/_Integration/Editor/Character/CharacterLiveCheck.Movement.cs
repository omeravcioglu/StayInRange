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
        ///     sprinting. Below zero is sinking in, above is floating.
        ///   - Air control: how far sideways a standing jump carries with the stick held sideways.
        ///   - Landing: how far the body slides once it lands a running jump with the stick let go in the
        ///     air, next to how far it slides stopping from a run on the ground.
        ///   - Smoothness: how evenly the body moves on screen while the camera follows it, and how often
        ///     its pose changes from one frame to the next.
        /// </summary>
        partial class Probe
        {
            enum MoveStep { Setup, Turn, Run, Sprint, SprintStop, FeetIdle, FeetMove, FeetSprint, FeetStop, SlopeGo, SlopeStand, SlopeBack, StopRun, StopWatch, AirRest, AirJump, SlideRest, SlideRun, SlideJump, TurnRest, TurnRun, TurnJump, HoldRest, HoldRun, HoldJump, CamSetup, CamWall, CamShot, CamClear, CamBack, Done }

            /// <summary>Running and then sprinting may turn the direction of travel this much at most (degrees).</summary>
            const float SprintTurnLimit = 30f;

            /// <summary>The supporting foot's sole may sit this far into or over the floor (m): median, and 90% of frames.</summary>
            const float SoleMedianLimit = 0.02f, SoleSpreadLimit = 0.04f;

            /// <summary>The slide after landing may be this long (m).</summary>
            const float LandingSlideLimit = 0.25f;

            MoveStep _moveStep = MoveStep.Setup;
            float _moveStart = -1f;
            bool _moveFirst;

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

            Vector3 _slideLandPosition, _slideLast;
            float _slideLandedAt = -1f, _slideStillAt = -1f, _slideTakeoff = -1f;
            float _slideDistance = float.NaN, _slideTime = float.NaN;

            Vector3 _stopFrom, _stopLast;
            float _stopReleasedAt = -1f, _stopStillAt = -1f;
            float _stopDistance = float.NaN, _stopTime = float.NaN;

            // Air control on a running jump: the stick turned sideways at take-off.
            Vector3 _turnRunDirection, _turnStart;
            float _turnTakeoff = -1f, _turnLanded = -1f, _turnHalfAt = -1f;
            float _turnSideways = float.NaN, _turnAngle = float.NaN;

            // A side slope to stand on, away from where the rest happens.
            GameObject _slope;
            Vector3 _slopeReturn;
            const float SlopeAngle = 12f;

            // Landing a running jump with the stick held, letting go as the feet touch down.
            Vector3 _holdLandPosition, _holdLast;
            float _holdTakeoff = -1f, _holdLandedAt = -1f, _holdStillAt = -1f;
            float _holdDistance = float.NaN, _holdTime = float.NaN;

            // How far a planted shoe moves along the floor (skating), per measured stretch.
            readonly Dictionary<string, float> _skate = new Dictionary<string, float>(), _skateTime = new Dictionary<string, float>();
            Vector3 _leftSoleWas, _rightSoleWas;
            bool _leftPlantedWas, _rightPlantedWas;
            string _skateKey;

            // Batch mode runs flat out; the movement checks hold it to a 144 Hz screen's frames.
            readonly System.Diagnostics.Stopwatch _frameClock = System.Diagnostics.Stopwatch.StartNew();
            double _lastFrameMs = -1;

            // The camera with a wall put between it and the player.
            GameObject _camWall;
            Renderer _camWallRenderer;
            Material _camWallOwn;
            float _camDistanceBefore = float.NaN, _camDistanceWall = float.NaN;
            float _camWallDissolve = float.NaN;
            string _camWallShader = "-", _camWallShaderAfter = "-";
            int _fadedWhileWall = -1, _fadedAfter = -1;
            readonly List<string> _camShots = new List<string>();

            readonly List<string> _moveNotes = new List<string>();

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
                            SampleSoles("standing");
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
                            NextMove(MoveStep.SlopeBack);
                        }

                        break;

                    case MoveStep.SlopeStand:
                        if (InMove > 0.3f)
                            SampleSoles($"standing across a {SlopeAngle:0}° slope");
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
                            Move(new Vector3(0f, 0f, 1f));
                            NextMove(MoveStep.StopRun);
                        }

                        break;

                    case MoveStep.StopRun:
                        // Stopping from a run on the ground: the slide a landing is compared with.
                        if (InMove > 1.8f)
                        {
                            Move(Vector3.zero);
                            _stopFrom = _stopLast = _animal.transform.position;
                            _stopReleasedAt = Now;
                            NextMove(MoveStep.StopWatch);
                        }

                        break;

                    case MoveStep.StopWatch:
                        if (WatchStill(ref _stopLast, ref _stopStillAt) || InMove > 3f)
                        {
                            _stopDistance = FlatV(_animal.transform.position - _stopFrom).magnitude;
                            _stopTime = (_stopStillAt > 0f ? _stopStillAt : Now) - _stopReleasedAt;
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
                            _slideTakeoff = _slideLandedAt = _slideStillAt = -1f;
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
                            _slideLandPosition = _slideLast = _animal.transform.position;
                        }
                        else if (_slideLandedAt > 0f && (WatchStill(ref _slideLast, ref _slideStillAt) || Now - _slideLandedAt > 3f))
                        {
                            _slideDistance = FlatV(_animal.transform.position - _slideLandPosition).magnitude;
                            _slideTime = (_slideStillAt > 0f ? _slideStillAt : Now) - _slideLandedAt;
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
                            _holdTakeoff = _holdLandedAt = _holdStillAt = -1f;
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
                            _holdLandPosition = _holdLast = _animal.transform.position;
                            Move(Vector3.zero);
                        }
                        else if (_holdLandedAt > 0f)
                        {
                            if (Now - _holdLandedAt < 0.8f)
                                SampleSoles("landing", skate: true);
                            if (WatchStill(ref _holdLast, ref _holdStillAt) && Now - _holdLandedAt > 0.8f || Now - _holdLandedAt > 3f)
                            {
                                _holdDistance = FlatV(_animal.transform.position - _holdLandPosition).magnitude;
                                _holdTime = (_holdStillAt > 0f ? _holdStillAt : Now) - _holdLandedAt;
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
                            NextMove(MoveStep.Done);
                        }

                        break;

                    case MoveStep.Done:
                        Move(Vector3.zero);
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

            /// <summary>Sleeps out the rest of a 144 Hz frame, so the frame timing is a real screen's.</summary>
            void HoldFrameRate()
            {
                const double frameMs = 1000.0 / 144.0;
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

            /// <summary>True once the body has stood (moved under 1 cm per 0.1 s) for a moment.</summary>
            bool WatchStill(ref Vector3 last, ref float stillAt)
            {
                var position = _animal.transform.position;
                float speed = Time.deltaTime > 0f ? FlatV(position - last).magnitude / Time.deltaTime : 0f;
                last = position;
                if (speed < 0.1f)
                {
                    if (stillAt < 0f)
                        stillAt = Now;
                    return Now - stillAt > 0.25f;
                }

                stillAt = -1f;
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

                if (!float.IsNaN(_camDistanceWall) && !float.IsNaN(_camDistanceBefore) && _camDistanceWall < _camDistanceBefore - 0.3f)
                    _failures.Add($"Camera: a wall in the way pulled the camera in from {_camDistanceBefore:0.00} m to {_camDistanceWall:0.00} m.");
                if (_camWall != null && !_camWallShader.Contains("Occluder Dissolve"))
                    _failures.Add($"Camera: the wall in the way did not dissolve (it wore {_camWallShader}).");
                else if (_camWall != null && (float.IsNaN(_camWallDissolve) || _camWallDissolve < 0.99f))
                    _failures.Add($"Camera: the wall in the way only dissolved to {_camWallDissolve:0.00} after 0.8 s.");
                if (_camWall != null && _camWallShaderAfter.Contains("Occluder Dissolve"))
                    _failures.Add("Camera: the wall kept the dissolve material once it was out of the way.");
                if (!float.IsNaN(_holdDistance) && _holdDistance > LandingSlideLimit)
                    _failures.Add($"Landing: the body slid {_holdDistance:0.00} m after landing a running jump and letting go (limit {LandingSlideLimit:0.00} m).");
                if (!float.IsNaN(_slideDistance) && _slideDistance > LandingSlideLimit)
                    _failures.Add($"Landing: the body slid {_slideDistance:0.00} m over {_slideTime:0.00} s after landing with the stick let go (limit {LandingSlideLimit:0.00} m).");
            }

            static string SignedCm(float metres) => float.IsNaN(metres) ? "-" : $"{metres * 100f:+0.0;-0.0;0.0} cm";

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
                              $"Out of the way it wears {_camWallShaderAfter} again; {_fadedAfter} still faded.");
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
            }
        }
    }
}
#endif
