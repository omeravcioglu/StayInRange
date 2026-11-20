using MalbersAnimations.Controller;
using Unity.Cinemachine;
using UnityEngine;

namespace CollarCali
{
    /// <summary>
    /// Keeps a third-person camera out of what it must not go through. The rig's obstacle avoidance is off - what comes
    /// between the camera and the player dissolves instead (CameraOccluderFade) - so nothing else stopped the camera going
    /// through the floor when the player looked up: 6.5 m behind the shoulder at Malbers' -30 degree bottom clamp, it sat
    /// about 1.6 m under the feet.
    ///
    /// After the Body stage a sphere of Radius is swept from the hand the camera hangs from (CinemachineThirdPersonFollow's
    /// own, this frame) to the camera, and the camera is pulled in along that line to the first contact with anything it
    /// must not go through: at once, and back out over Ease Out seconds once the way is clear, so it does not pop. Along that
    /// line the view keeps pointing the same way, so the crosshair stays where it was.
    ///
    /// The arm from the follow target to the hand runs through the player's own shoulders, and a camera stopped on it was
    /// inside the body (the top of a ledge or a crate beside the player, grazed by the arm, did that), so the camera is never
    /// stopped on it. Only what is solid (below) shortens the arm - the wall of a tunnel the right shoulder is against - and
    /// then the hand comes in along it and the camera moves across with it, as Cinemachine's own obstacle avoidance does: at
    /// once, and back out over Ease Out. The hand also comes in where something solid the arm heads into would cut the
    /// camera's own line short. With the view turned a degree or two away from a wall beside the shoulder, the line from a
    /// hand right by the wall meets it within a metre, and the camera, pulled in to there, was on the player's head; from a
    /// hand further in, the same line has the room to go its whole way. Each centimetre the hand comes in moves the line that
    /// much further off the wall where the arm heads into it, so the hand comes in only where the line meets the wall at a
    /// shallower slant than the arm does (it then buys the camera more distance than the hand moves), and less and less as the
    /// two slants come alike.
    ///
    /// What is solid - stops the camera wherever it is met, and the arm too:
    ///   - Terrain: it never fades.
    ///   - What the player stands on (StandingOn: the first thing straight under the follow target, and what lies under
    ///     that within StandingDepth, as a tunnel under a puddle, a metal plate or a heap of rubbish lying on its floor).
    ///     CameraOccluderFade never dissolves it, so a camera that left a tunnel, a staircase or a level mesh the player is
    ///     on would have its wall, roof or steps between it and the player with nothing dissolving them. All of it, walls
    ///     and roof too, keeps the camera in.
    ///   - What carries that floor on: a piece whose floor, straight under where the sweep meets it, is as high as what the
    ///     player stands on (within ContinuesWithin). Looking back along a tunnel or a corridor made of pieces, the camera
    ///     goes into the piece behind the one the player stands in, and has to stay in that one as in this one.
    ///   - What cannot dissolve (CameraOccluderFade.CanSeeThrough): bigger than its Max Size (a hall in one mesh), drawn
    ///     with a material no dissolving copy could look like, or anything at all when the brain has no fade working. Gone
    ///     through, it would hide the player.
    /// Anything else stops the camera only if the camera would end up under it: a face of it that can be walked on (facing
    /// up, less than 60 degrees from flat) straight over the camera, at most Probe above it and less than Radius below it.
    /// That is a floor or a ramp the camera would sink through when the player looks up, or stairs it would go into past a
    /// riser: from there it would film the underside or the inside of them. A crate, low wall, railing or rock the camera
    /// ends up beyond is not, and fades; nor is a roof high over it. These are judged from the furthest in, each where the
    /// camera would stop without the nearer ones, so a flight of separate step blocks stops the camera in front of the first
    /// step the line meets rather than inside one further up. Nothing else stops it: walls, props, ceilings and roofs
    /// dissolve, and the camera keeps its distance behind them. Triggers, characters (the player's own body included) and
    /// bodies physics moves are never in its way.
    ///
    /// What the hand's sphere (or, for the arm, the follow target's) already touches is reported by the sweep at a distance
    /// of 0 and with no other contact, and is not stopped in front of: the camera would be on the hand, against the player's
    /// head. A thin line from the hand is cast at it alone instead, which meets only faces turned towards it. Something solid
    /// stops the camera Radius short of the face that line meets; anything else only if that face can be walked on and the
    /// camera would end up under it, as above.
    ///
    /// A sweep meets only the faces turned towards it, and the halls and tunnels made in one mesh have theirs turned in: from
    /// outside, the camera's sweep goes into one through its wall without meeting it, and the camera would film that wall's
    /// inside instead of the player. So a ray is cast back from where the camera (and the hand) would end up; a face of
    /// something solid it meets, turned away from the hand, stops the camera Radius short of it as well.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class CameraGroundCollider : CinemachineExtension
    {
        [Tooltip("What can stop the camera: the level's surfaces.")]
        public LayerMask layers = 1 << 0;

        [Tooltip("How far the camera keeps from what stops it (m).")]
        [Range(0.01f, 1f)] public float radius = 0.15f;

        [Tooltip("Seconds to ease back out to the full distance once the way is clear (0 = at once).")]
        [Range(0f, 3f)] public float easeOut = 0.5f;

        /// <summary>A face whose normal points at least this much up can be walked on: tilted under 60 degrees from flat.</summary>
        const float GroundUp = 0.5f;

        /// <summary>
        /// The sphere is swept this much wider than Radius, so the camera never ends up a hair inside what
        /// stopped it; the arm's twice as much, so the camera's own sweep from the hand starts clear of it.
        /// </summary>
        const float Skin = 0.01f;

        /// <summary>This close to the whole line (m), the camera is all the way out again.</summary>
        const float AllTheWayOut = 0.001f;

        /// <summary>
        /// The thin line from the hand keeps the camera Radius off the face it meets, measured square to the
        /// face; a line this close to grazing it (the cosine) is taken as this much, so the camera is not
        /// pulled far in for a face it only skims.
        /// </summary>
        const float LeastSquare = 0.2f;

        /// <summary>
        /// Last time's distance is eased from only if it is this recent (s, real time, so slow motion and
        /// pauses do not stretch it). Older, the camera was not filming in between (a camera on standby is
        /// not updated), so it starts afresh.
        /// </summary>
        const float StaleAfter = 0.25f;

        /// <summary>
        /// How far under the follow target what the player stands on is looked for (m): as far as
        /// CameraOccluderFade looks for it, so what the fade keeps is what stops the camera.
        /// </summary>
        const float StandingSearch = 3f;

        /// <summary>
        /// What else the ray straight down meets, at most this far (m) under the first thing it meets, is stood on as well.
        /// Game.unity lies puddles, metal plates and heaps of rubbish (up to about 0.9 m high) on the floors of tunnels and
        /// halls made in one mesh; standing on one, the player is still in that mesh.
        /// </summary>
        const float StandingDepth = 1f;

        /// <summary>
        /// A floor within this much (m) of the height of what the player stands on carries it on: the floors of a tunnel's
        /// pieces meet within millimetres, and a floor overlay lies a centimetre or two over them, while a step is 15 cm or more.
        /// </summary>
        const float ContinuesWithin = 0.1f;

        /// <summary>
        /// How high over the camera a face that can be walked on still counts as one the camera is under
        /// (m). The floor it sinks through when the player looks up is at most about 1.6 m over it, and a
        /// step or ledge it goes into not much more. A walkable face further up is taken for a roof or the
        /// floor of the storey above, and is left to the fade.
        /// </summary>
        const float Probe = 3f;

        static readonly RaycastHit[] Hits = new RaycastHit[32];
        static readonly RaycastHit[] Below = new RaycastHit[16];
        static readonly RaycastHit[] Back = new RaycastHit[16];

        // This update's: what the player stands on, what has been found carrying it on, and the fade on the brain that films.
        readonly RaycastHit[] _standing = new RaycastHit[8];
        int _standingCount;
        readonly Collider[] _continuing = new Collider[8];
        int _continuingCount;
        CinemachineBrain _brain;
        CameraOccluderFade _fade;

        /// <summary>What each camera this works on remembers from one update to the next.</summary>
        class VcamExtraState : VcamExtraStateBase
        {
            /// <summary>How far from the hand towards the camera the camera was put last time (m).</summary>
            public float shown;
            /// <summary>That was short of the whole line: the camera is pulled in, or still easing back out.</summary>
            public bool pulledIn;
            /// <summary>How far along the arm from the follow target the hand was put last time (m).</summary>
            public float arm;
            /// <summary>That was short of the whole arm: the hand is brought in, or still easing back out.</summary>
            public bool armIn;
            /// <summary>When that was (unscaled time, s); below 0 = not yet.</summary>
            public float at = -1f;
        }

        /// <inheritdoc />
        public override float GetMaxDampTime() => easeOut;

        /// <summary>Put somewhere on purpose (a cut, or taking over the last camera's place): nothing to ease from.</summary>
        public override void ForceCameraPosition(CinemachineVirtualCameraBase vcam, Vector3 pos, Quaternion rot)
        {
            var extra = GetExtraState<VcamExtraState>(vcam);
            extra.pulledIn = false;
            extra.armIn = false;
            extra.at = -1f;
        }

        protected override void PostPipelineStageCallback(
            CinemachineVirtualCameraBase vcam, CinemachineCore.Stage stage, ref CameraState state, float deltaTime)
        {
            if (stage != CinemachineCore.Stage.Body)
                return;

            var extra = GetExtraState<VcamExtraState>(vcam);
            float now = Time.unscaledTime;
            bool recent = deltaTime >= 0f && vcam.PreviousStateIsValid &&
                          extra.at >= 0f && now >= extra.at && now - extra.at <= StaleAfter;
            bool easing = extra.pulledIn && recent, easingArm = extra.armIn && recent;
            extra.at = now;
            extra.pulledIn = false;
            extra.armIn = false;

            if (!TryGetRig(vcam, out var target, out var hand))
                return;

            var up = state.ReferenceUp;
            BeginUpdate(vcam, target, up);
            var line = state.GetCorrectedPosition() - hand;
            float whole = line.magnitude;
            if (whole < Epsilon)
                return;
            var direction = line / whole;

            // The arm. Something solid in it brings the hand in towards the follow target, and the camera across with it;
            // so does something solid the arm heads into that would cut the camera's line from there short.
            float clear;
            var shift = Vector3.zero;
            var arm = hand - target;
            float armWhole = arm.magnitude;
            if (armWhole > Epsilon)
            {
                var along = arm / armWhole;
                float armClear = ArmClear(target, along, armWhole, up);
                clear = ClearDistance(target + along * armClear, direction, whole, up, out var face, out bool solid);
                float armWanted = armClear;
                if (solid && clear < whole - AllTheWayOut)
                {
                    // Per metre the hand comes in, the line moves `toward` further off that face; per metre along it, the
                    // line closes on it by `closing`. Coming in pays only while the line closes slower than the arm.
                    float toward = -Vector3.Dot(along, face), closing = -Vector3.Dot(direction, face);
                    if (toward > Epsilon && closing > Epsilon)
                    {
                        float needed = (closing * (whole - clear) + Skin) / toward;
                        armWanted = Mathf.Max(0f, armClear - Mathf.Clamp01(toward / closing - 1f) * needed);
                    }
                }

                float armShown = armWanted;
                if (easingArm && armWanted > extra.arm && easeOut > Epsilon)
                    armShown = Mathf.Min(armWanted, extra.arm + Damper.Damp(armWanted - extra.arm, easeOut, deltaTime));
                extra.arm = armShown;
                if (armShown < armWhole - AllTheWayOut)
                {
                    extra.armIn = true;
                    shift = along * (armShown - armWhole);
                    hand += shift;
                }

                if (armShown != armClear)
                    clear = ClearDistance(hand, direction, whole, up, out _, out _);
            }
            else
                clear = ClearDistance(hand, direction, whole, up, out _, out _);

            // In at once; back out gently from where it was last time rather than in one jump. The
            // distance from the hand is what is eased, so a zoom or the follow's own damping changing the
            // line's length does not ease the camera when nothing is in the way.
            float shown = clear;
            if (easing && clear > extra.shown && easeOut > Epsilon)
                shown = Mathf.Min(clear, extra.shown + Damper.Damp(clear - extra.shown, easeOut, deltaTime));
            extra.shown = shown;

            var correction = shift;
            if (shown < whole - AllTheWayOut)
            {
                extra.pulledIn = true;
                correction += direction * (shown - whole);
            }

            if (extra.armIn || extra.pulledIn)
                state.PositionCorrection += correction;
        }

        /// <summary>
        /// How far from <paramref name="hand"/> along <paramref name="direction"/> a camera <paramref name="distance"/> out
        /// would be let go (m), with the player's follow target at <paramref name="target"/>; up is world up. The same
        /// judgement as each update makes from the hand there, for checks that put a hand where no pose of the player puts
        /// one (its sphere already touching what the player stands on: CharacterLiveCheck).
        /// </summary>
        public float ClearFrom(Vector3 target, Vector3 hand, Vector3 direction, float distance)
        {
            if (distance < Epsilon || direction.sqrMagnitude < Epsilon)
                return 0f;
            BeginUpdate(ComponentOwner, target, Vector3.up);
            return ClearDistance(hand, direction.normalized, distance, Vector3.up, out _, out _);
        }

        /// <summary>What every judgement of this update goes by: what the player stands on, and the brain's fade.</summary>
        void BeginUpdate(CinemachineVirtualCameraBase vcam, Vector3 target, Vector3 up)
        {
            _standingCount = StandingOn(target, up, StandingSearch, layers, _standing);
            _continuingCount = 0;
            var brain = vcam != null ? CinemachineCore.FindPotentialTargetBrain(vcam) : null;
            if (brain != _brain || _fade == null)
            {
                _brain = brain;
                _fade = brain != null ? brain.GetComponent<CameraOccluderFade>() : null;
            }
        }

        /// <summary>
        /// How far along the arm from the follow target the hand may go (m) before something solid stops it:
        /// <paramref name="whole"/> when nothing does. Swept a little wider than the camera's own sphere, so the sweep
        /// from the hand starts clear of what stopped the arm.
        /// </summary>
        float ArmClear(Vector3 target, Vector3 direction, float whole, Vector3 up)
        {
            float wide = radius + 2f * Skin;
            int count = Physics.SphereCastNonAlloc(target, wide, direction, Hits, whole, layers, QueryTriggerInteraction.Ignore);
            SortByDistance(Hits, count);
            float clear = whole;
            for (int i = 0; i < count && Hits[i].distance < clear; i++)
            {
                var collider = Hits[i].collider;
                if (!MayStop(collider))
                    continue;
                if (Hits[i].distance > 0f)
                {
                    if (Solid(collider, target + direction * Hits[i].distance, up))
                        clear = Hits[i].distance;
                }
                else if (Solid(collider, target, up) &&
                         collider.Raycast(new Ray(target, direction), out var face, whole + wide / LeastSquare))
                {
                    // Already touching it at the follow target: the hand stops short of the face the arm itself meets.
                    float square = Mathf.Max(-Vector3.Dot(direction, face.normal), LeastSquare);
                    clear = Mathf.Min(clear, Mathf.Max(0f, face.distance - wide / square));
                }
            }

            var behind = Vector3.zero;
            bool solid = false;
            return ClearBehind(target, direction, clear, wide, up, ref behind, ref solid);
        }

        /// <summary>
        /// How far from the hand along <paramref name="direction"/> the camera may go (m) before it meets
        /// something that stops it: <paramref name="whole"/>, the camera's own distance, when nothing does. With it, the
        /// normal of the face that decided it (zero when nothing did) and whether something solid did.
        /// </summary>
        float ClearDistance(Vector3 hand, Vector3 direction, float whole, Vector3 up, out Vector3 face, out bool solid)
        {
            face = Vector3.zero;
            solid = false;
            int count = Physics.SphereCastNonAlloc(hand, radius + Skin, direction, Hits, whole, layers, QueryTriggerInteraction.Ignore);
            float clear = whole;

            // What the sphere meets on the way. What it already touches at the hand (a distance of 0, and no other contact
            // with it) is not stopped in front of: there is only the body there. A thin line from the hand is cast at it
            // alone instead. It meets only faces turned towards it: the floor, the far wall, the roof of a tunnel whose wall
            // the hand is against, and that wall itself only where the camera would go through it. Cast Radius / LeastSquare
            // beyond the camera, so a face just past it still keeps it that far off.
            int met = 0;
            for (int i = 0; i < count; i++)
            {
                var collider = Hits[i].collider;
                if (!MayStop(collider))
                    continue;
                if (Hits[i].distance > 0f)
                {
                    Hits[met++] = Hits[i];
                    continue;
                }

                if (!collider.Raycast(new Ray(hand, direction), out var along, whole + radius / LeastSquare))
                    continue;
                float square = Mathf.Max(-Vector3.Dot(direction, along.normal), LeastSquare);
                float at = Mathf.Max(0f, along.distance - radius / square);
                if (Solid(collider, hand, up))
                {
                    if (at < clear)
                    {
                        clear = at;
                        face = along.normal;
                        solid = true;
                    }
                }
                else if (Vector3.Dot(along.normal, up) > GroundUp)
                {
                    // A floor or a step the camera would come down onto: judged with the rest below, there.
                    var hit = Hits[i];
                    hit.distance = at;
                    hit.normal = along.normal;
                    Hits[met++] = hit;
                }
            }

            if (met == 0)
                return ClearBehind(hand, direction, Mathf.Min(clear, whole), radius, up, ref face, ref solid);
            SortByDistance(Hits, met);

            // Something solid stops it wherever it meets it.
            for (int i = 0; i < met && Hits[i].distance < clear; i++)
            {
                if (Solid(Hits[i].collider, hand + direction * Hits[i].distance, up))
                {
                    clear = Hits[i].distance;
                    face = Hits[i].normal;
                    solid = true;
                    break;
                }
            }

            // Anything else only if the camera would end up under it, where it stops so far. Further things
            // first: stopping in front of one can leave the camera under a nearer one (the step before it),
            // and that one is then judged where the camera stops now.
            for (int i = met - 1; i >= 0; i--)
            {
                if (Hits[i].distance < clear && IsUnder(Hits[i].collider, hand + direction * clear, up))
                {
                    clear = Hits[i].distance;
                    face = Hits[i].normal;
                    solid = false;
                }
            }

            return ClearBehind(hand, direction, Mathf.Min(clear, whole), radius, up, ref face, ref solid);
        }

        /// <summary>
        /// <paramref name="clear"/>, or less where going that far along <paramref name="direction"/> from
        /// <paramref name="from"/> would put the camera (or the hand) behind a face of something solid that is turned away
        /// from <paramref name="from"/>, <paramref name="keep"/> short of it. A sweep meets only faces turned towards it,
        /// and the halls and tunnels made in one mesh have their faces turned in: from outside, the sweep goes into one
        /// through its wall without meeting it, and the camera would film that wall's inside instead of the player. A ray
        /// back from where it would be meets that face.
        /// </summary>
        float ClearBehind(Vector3 from, Vector3 direction, float clear, float keep, Vector3 up, ref Vector3 face, ref bool solid)
        {
            if (clear <= Epsilon)
                return clear;
            var end = from + direction * clear;
            int count = Physics.RaycastNonAlloc(end, -direction, Back, clear, layers, QueryTriggerInteraction.Ignore);
            float allowed = clear;
            for (int i = 0; i < count; i++)
            {
                var collider = Back[i].collider;
                if (!MayStop(collider))
                    continue;
                float at = clear - Back[i].distance;
                float square = Mathf.Max(Vector3.Dot(direction, Back[i].normal), LeastSquare);
                float stop = Mathf.Max(0f, at - keep / square);
                if (stop < allowed && Solid(collider, from + direction * stop, up))
                {
                    allowed = stop;
                    face = Back[i].normal;
                    solid = true;
                }
            }

            return allowed;
        }

        /// <summary>
        /// The camera may not go through it, wherever it meets it: terrain, what the player stands on, what carries that
        /// on (judged straight under <paramref name="probe"/>, where the sweep's sphere met it), and what cannot dissolve.
        /// </summary>
        bool Solid(Collider collider, Vector3 probe, Vector3 up)
        {
            if (collider is TerrainCollider)
                return true;
            for (int i = 0; i < _standingCount; i++)
            {
                if (_standing[i].collider == collider)
                    return true;
            }

            if (_fade == null || !_fade.CanSeeThrough(collider))
                return true;
            for (int i = 0; i < _continuingCount; i++)
            {
                if (_continuing[i] == collider)
                    return true;
            }

            if (!Continues(collider, probe, up))
                return false;
            if (_continuingCount < _continuing.Length)
                _continuing[_continuingCount++] = collider;
            return true;
        }

        /// <summary>
        /// Whether the collider carries on the floor the player stands on: straight under <paramref name="probe"/>, its
        /// first face is one that can be walked on, within ContinuesWithin of the height of one of what they stand on.
        /// </summary>
        bool Continues(Collider collider, Vector3 probe, Vector3 up)
        {
            if (_standingCount == 0)
                return false;
            float depth = float.NegativeInfinity;
            for (int i = 0; i < _standingCount; i++)
                depth = Mathf.Max(depth, Vector3.Dot(probe - _standing[i].point, up));
            depth += ContinuesWithin;
            if (depth <= 0f || !collider.Raycast(new Ray(probe, -up), out var floor, depth) || Vector3.Dot(floor.normal, up) <= GroundUp)
                return false;
            for (int i = 0; i < _standingCount; i++)
            {
                if (Mathf.Abs(Vector3.Dot(floor.point - _standing[i].point, up)) <= ContinuesWithin)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// A camera at <paramref name="point"/> would be under that collider, or less than Radius over it:
        /// straight above the point, at most Probe up, its first face is one that can be walked on.
        /// </summary>
        bool IsUnder(Collider collider, Vector3 point, Vector3 up) =>
            collider.Raycast(new Ray(point + up * Probe, -up), out var top, Probe + radius) &&
            Vector3.Dot(top.normal, up) > GroundUp;

        /// <summary>
        /// What the player stands on, for the camera and for CameraOccluderFade alike: what straight under
        /// <paramref name="from"/> (at most <paramref name="search"/> down, on <paramref name="layers"/>) could stop the
        /// camera - the first thing there, and whatever else lies less than StandingDepth under that. A puddle, a metal plate
        /// or a heap of rubbish lying on the floor of a tunnel or a hall made in one mesh is met first, and the player is in
        /// that mesh all the same. Nearest first into <paramref name="into"/>; returns how many.
        /// </summary>
        public static int StandingOn(Vector3 from, Vector3 up, float search, LayerMask layers, RaycastHit[] into)
        {
            int count = Physics.RaycastNonAlloc(from, -up, Below, search, layers, QueryTriggerInteraction.Ignore);
            int kept = 0;
            for (int i = 0; i < count; i++)
            {
                if (MayStop(Below[i].collider))
                    Below[kept++] = Below[i];
            }

            SortByDistance(Below, kept);
            int found = 0;
            for (int i = 0; i < kept && found < into.Length && Below[i].distance <= Below[0].distance + StandingDepth; i++)
                into[found++] = Below[i];
            return found;
        }

        /// <summary>Level geometry: not a character (the player's own body included), nor a body physics moves.</summary>
        static bool MayStop(Collider collider)
        {
            if (collider == null || collider is CharacterController)
                return false;
            var body = collider.attachedRigidbody;
            if (body != null && !body.isKinematic)
                return false;
            return collider.GetComponentInParent<MAnimal>() == null;
        }

        /// <summary>Nearest first, in place; there are only ever a few.</summary>
        static void SortByDistance(RaycastHit[] hits, int count)
        {
            for (int i = 1; i < count; i++)
            {
                var hit = hits[i];
                int j = i - 1;
                for (; j >= 0 && hits[j].distance > hit.distance; j--)
                    hits[j + 1] = hits[j];
                hits[j + 1] = hit;
            }
        }

        /// <summary>
        /// The follow target and the hand the camera hangs from, as the shoulder camera placed them this
        /// frame. A camera without one hangs straight from its follow target.
        /// </summary>
        static bool TryGetRig(CinemachineVirtualCameraBase vcam, out Vector3 target, out Vector3 hand)
        {
            if (vcam.TryGetComponent(out CinemachineThirdPersonFollow follow) && follow.IsValid)
            {
                follow.GetRigPositions(out target, out _, out hand);
                return true;
            }

            var tracked = vcam.Follow;
            target = hand = tracked != null ? tracked.position : Vector3.zero;
            return tracked != null;
        }
    }
}
