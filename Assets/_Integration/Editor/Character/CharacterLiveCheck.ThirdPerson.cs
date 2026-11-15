#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GameCreator.Runtime.Melee;
using MalbersAnimations;
using MalbersAnimations.Controller;
using MalbersAnimations.InputSystem;
using MalbersAnimations.Scriptables;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace CollarCali.EditorTools
{
    public static partial class CharacterLiveCheck
    {
        /// <summary>
        /// The checks of ThirdPersonPlayer (third-person plan, phase 3), run last when the player has
        /// one: what telekinesis, the HUD, pause and the network bridge rely on. Carrying (slower, the
        /// throw's keys muted, no sword), the interact key shared by two holders, the throw aim view
        /// in and out, a menu (and another lock outliving it), a teleport, and going down and being
        /// revived.
        /// </summary>
        partial class Probe
        {
            enum RigStep { Carry, CarryEnd, Keys, AimIn, AimOut, Menu, Teleport, Downed, Revive, ReviveWalk, Done }

            ThirdPersonPlayer _tps;
            RigStep _rigStep = RigStep.Carry;
            float _rigStepStart = -1f;
            bool _rigStepBegun;
            readonly List<(string check, bool pass, string detail)> _rigChecks = new List<(string, bool, string)>();

            float _carryWalked = -1f, _menuWalked = -1f, _reviveWalked = -1f, _carrySpeed = 1f;
            float _timeMultiplierBefore, _timeMultiplierCarrying;
            Vector3 _rigWalkFrom, _teleportTarget, _revivePoint;
            float _menuStartY, _menuMaxY, _cameraGapBefore, _cameraGapAfter;
            CinemachineBlendDefinition _blendBefore;
            int _shadowOnlyBefore;
            bool _strafeBefore, _modeReenabled;

            float InRigStep => Now - _rigStepStart;

            void RigCheck(string check, bool pass, string detail = null)
            {
                _rigChecks.Add((check, pass, detail));
                if (!pass)
                    _failures.Add("Third-person player: " + check + (string.IsNullOrEmpty(detail) ? "." : " (" + detail + ")."));
            }

            void NextRigStep(RigStep step)
            {
                _rigStep = step;
                _rigStepStart = Now;
                _rigStepBegun = false;
            }

            /// <summary>Whether the input link takes <paramref name="input"/> now; null when it has no such input.</summary>
            bool? InputLive(string input)
            {
                var link = _animal.GetComponent<MInputLink>();
                var map = link != null ? link.ActiveMActionMap : null;
                if (map?.buttons == null)
                    return null;

                bool? live = null;
                foreach (var button in map.buttons)
                {
                    if (button == null || button.Name.Trim() != input)
                        continue;
                    live = (live ?? true) && button.active.Value;
                }

                return live;
            }

            int ShadowOnlyRenderers() => _animal.GetComponentsInChildren<Renderer>(true)
                .Count(r => r.enabled && r.shadowCastingMode == ShadowCastingMode.ShadowsOnly);

            string LiveCameraName()
            {
                var live = _tps.Brain != null ? _tps.Brain.ActiveVirtualCamera as Component : null;
                return live != null ? live.name : "none";
            }

            float CameraGap() => _tps.Brain != null ? Vector3.Distance(_tps.Brain.transform.position, _animal.transform.position) : -1f;

            /// <summary>What ThirdPersonPlayer set up when play started, read once the character has settled.</summary>
            void CheckRigStart()
            {
                RigCheck("registers as the local player (PlayerRigs.Local)", ReferenceEquals(PlayerRigs.Local, _tps));

                var brains = Object.FindObjectsByType<CinemachineBrain>(FindObjectsSortMode.None);
                RigCheck("makes one camera rig and films with it", _tps.CameraRig != null && _tps.Brain != null && brains.Length == 1,
                    $"{brains.Length} brain(s), rig {(_tps.CameraRig != null ? _tps.CameraRig.name : "none")}");
                var malbersCamera = _animal.m_MainCamera.Value;
                RigCheck("Malbers moves by the rig's camera", _tps.Brain != null && malbersCamera == _tps.Brain.transform,
                    "Malbers' camera: " + (malbersCamera != null ? PathOf(malbersCamera) : "none"));

                var attackModes = _animal.modes.Where(m => m?.ID != null && m.ID.name.Contains("Attack")).ToList();
                RigCheck("Malbers' own attack modes are off", attackModes.All(m => !m.Active),
                    attackModes.Count == 0 ? "none on this character" : string.Join(", ", attackModes.Select(m => m.ID.name + (m.Active ? " on" : " off"))));
                var combos = _animal.GetComponentsInChildren<ComboManager>(true);
                RigCheck("Malbers' combo manager is off", combos.All(c => !c.enabled), combos.Length + " combo manager(s)");

                var pause = InputLive("Pause");
                RigCheck("Malbers' pause key is muted", pause != true, pause == null ? "no Pause input on this character" : null);

                var bridge = _tps.MeleeBridge;
                var sword = FindAsset<MeleeWeapon>("TeamSword_Weapon");
                RigCheck("equips the team sword", bridge != null && bridge.Character != null && sword != null && bridge.Character.Combat.IsEquipped(sword));
                RigCheck("gives Malbers the input, unlocked", _tps.IsRunning && !_animal.LockInput && !_animal.LockMovement);
            }

            /// <summary>One frame of the third-person player's checks; true when they are over.</summary>
            bool StepRig()
            {
                if (_rigStepStart < 0f)
                {
                    _rigStepStart = Now;
                    // Put back what the earlier checks borrowed: Malbers' attack mode they played, and the
                    // shared camera hooks the stand-in teammate took and cleared.
                    if (_modeReenabled && _mode != null)
                        _mode.Disable();
                    foreach (var hook in _animal.GetComponentsInChildren<TransformHook>(true))
                    {
                        if (hook.Hook != null && hook.Hook.Value != hook.Reference)
                        {
                            hook.UpdateHook();
                            _notes.Add("Hook " + hook.name + " was " + (hook.Hook.Value == null ? "cleared" : "taken") +
                                       " by the melee checks' teammate; the player claims it again.");
                        }
                    }
                }

                var settings = PlayerTuning.Active.cameraTransition;
                var locks = MAnimalLocks.Of(_animal);

                switch (_rigStep)
                {
                    case RigStep.Carry:
                        if (!_rigStepBegun)
                        {
                            if (!Standing())
                            {
                                if (InRigStep > 5f)
                                {
                                    RigCheck("stands before the checks", false, "in " + StateName());
                                    return true;
                                }

                                break;
                            }

                            _rigStepBegun = true;
                            // The game's own carry speed, as telekinesis asks for it.
                            _carrySpeed = PlayerTuning.Active.carry.carrySpeedMultiplier;
                            _timeMultiplierBefore = _animal.TimeMultiplier;
                            _tps.SetCarrying(true, _carrySpeed);
                            _timeMultiplierCarrying = _animal.TimeMultiplier;
                            RigCheck($"carrying scales Malbers' time multiplier by the carry speed ({_carrySpeed:0.00})",
                                Mathf.Abs(_timeMultiplierCarrying - _timeMultiplierBefore * _carrySpeed) < 0.001f,
                                $"{_timeMultiplierBefore:0.00} to {_timeMultiplierCarrying:0.00}");

                            var inputs = Names(ThirdPersonPlayer.CarryBlockedInputs).ToList();
                            var live = inputs.Where(i => InputLive(i) == true).ToList();
                            var absent = inputs.Where(i => InputLive(i) == null).ToList();
                            RigCheck("carrying mutes the keys the throw uses", live.Count == 0,
                                live.Count > 0 ? "still live: " + string.Join(", ", live) : null);
                            if (absent.Count > 0)
                                _notes.Add("Not on this character's input link, so nothing to mute: " + string.Join(", ", absent) + ".");

                            RigCheck("the sword is refused while carrying", !_tps.TryAttack() && _tps.LastAttackRefusal == "carrying",
                                "refused for: " + (_tps.LastAttackRefusal ?? "nothing, it swung"));
                            _rigWalkFrom = _animal.transform.position;
                            Move(Vector3.forward);
                        }

                        if (_rigStepBegun && InRigStep > 1.2f)
                        {
                            _carryWalked = Flat(_animal.transform.position - _rigWalkFrom);
                            Move(Vector3.zero);
                            NextRigStep(RigStep.CarryEnd);
                        }

                        break;

                    case RigStep.CarryEnd:
                        if (InRigStep > 0.6f)
                        {
                            RigCheck("carrying walks slower than walking", _walked > 0f && _carryWalked > 0.1f * _walked && _carryWalked < 0.95f * _walked,
                                $"{_carryWalked:0.00} m in 1.2 s at carry speed {_carrySpeed:0.00} ({(_walked > 0f ? _carryWalked / _walked : 0f):P0} of walking), {_walked:0.00} m walking");

                            _tps.SetCarrying(false, 1f);
                            bool refusedNow = !_tps.AttackButtonIsSwords(out string now) && now == "carrying";
                            RigCheck("the click that ends a carry cannot also swing", refusedNow, "the button belonged to: " + (now ?? "the sword"));
                            RigCheck("letting go puts the time multiplier back", Mathf.Abs(_animal.TimeMultiplier - _timeMultiplierBefore) < 0.001f,
                                $"{_animal.TimeMultiplier:0.00}");
                            var muted = Names(ThirdPersonPlayer.CarryBlockedInputs).Where(i => InputLive(i) == false).ToList();
                            RigCheck("letting go gives the keys back", muted.Count == 0, muted.Count > 0 ? "still muted: " + string.Join(", ", muted) : null);
                            NextRigStep(RigStep.Keys);
                        }

                        break;

                    case RigStep.Keys:
                        if (InRigStep > 0.1f)
                        {
                            RigCheck("the sword has the button again once the carry is over", _tps.AttackButtonIsSwords(out string who),
                                "the button belongs to: " + (who ?? "the sword"));

                            // Two holders of one key: telekinesis targets a body (interact), then lifts it.
                            _tps.SetInteractClaimed(true);
                            bool claimed = InputLive("Interact") == false;
                            _tps.SetCarrying(true, 1f);
                            _tps.SetInteractClaimed(false);
                            bool stillCarried = InputLive("Interact") == false;
                            _tps.SetCarrying(false, 1f);
                            bool back = InputLive("Interact") == true;
                            RigCheck("interact stays muted while anything still mutes it", claimed && stillCarried && back,
                                $"claimed: muted {claimed}; claim dropped while carrying: muted {stillCarried}; both over: live {back}");
                            NextRigStep(RigStep.AimIn);
                        }

                        break;

                    case RigStep.AimIn:
                        if (!_rigStepBegun)
                        {
                            if (InRigStep < 0.3f)
                                break;
                            _rigStepBegun = true;
                            _rigStepStart = Now;
                            _blendBefore = _tps.Brain != null ? _tps.Brain.DefaultBlend : default;
                            _shadowOnlyBefore = ShadowOnlyRenderers();
                            _strafeBefore = _animal.Strafe;
                            _tps.SetThirdPersonAimView(true);
                            RigCheck("the throw aim view starts", _tps.IsAimViewLive);
                        }
                        else if (InRigStep > settings.aimViewInSeconds + 0.4f)
                        {
                            RigCheck("the aim view films from the eyes", LiveCameraName() == "CM TPS Aim Eye", "live camera: " + LiveCameraName());
                            RigCheck("the character faces the aim (strafes)", _animal.Strafe,
                                $"can strafe {_animal.CanStrafe}, stance {(_animal.ActiveStance != null && _animal.ActiveStance.CanStrafe.Value)}, " +
                                $"state {(_animal.ActiveState != null && _animal.ActiveState.CanStrafe)}");
                            RigCheck("the body casts only its shadow while the view is in the eyes", ShadowOnlyRenderers() > _shadowOnlyBefore,
                                $"{ShadowOnlyRenderers()} shadow-only renderers, {_shadowOnlyBefore} before");

                            var eye = _tps.CameraRig.transform.Find("CM TPS Aim Eye");
                            bool aimed = _tps.TryGetAim(out var origin, out var forward);
                            RigCheck("a throw leaves from the eyes along the view",
                                aimed && eye != null && Vector3.Distance(origin, eye.position) < 0.01f &&
                                Vector3.Angle(forward, _tps.Brain.transform.forward) < 1f,
                                aimed ? $"{(eye != null ? Vector3.Distance(origin, eye.position) : -1f):0.000} m from the eye camera" : "no aim");
                            RigCheck("the sword is refused while aiming a throw", !_tps.AttackButtonIsSwords(out string why) && why == "aiming a throw",
                                "the button belonged to: " + (why ?? "the sword"));

                            _tps.SetThirdPersonAimView(false, 0f);
                            NextRigStep(RigStep.AimOut);
                        }

                        break;

                    case RigStep.AimOut:
                        if (InRigStep > settings.aimViewOutSeconds + 0.4f)
                        {
                            RigCheck("the aim view ends back over the shoulder", !_tps.IsAimViewLive && LiveCameraName() != "CM TPS Aim Eye",
                                "live camera: " + LiveCameraName());
                            RigCheck("strafing is put back", _animal.Strafe == _strafeBefore);
                            RigCheck("the body is visible again", ShadowOnlyRenderers() == _shadowOnlyBefore, $"{ShadowOnlyRenderers()} shadow-only renderers");
                            var blend = _tps.Brain != null ? _tps.Brain.DefaultBlend : default;
                            RigCheck("the brain's own blend is put back", blend.Style == _blendBefore.Style && Mathf.Approximately(blend.Time, _blendBefore.Time),
                                $"{blend.Style} {blend.Time:0.00} s, was {_blendBefore.Style} {_blendBefore.Time:0.00} s");
                            NextRigStep(RigStep.Menu);
                        }

                        break;

                    case RigStep.Menu:
                        if (!_rigStepBegun)
                        {
                            if (!Standing())
                            {
                                if (InRigStep > 5f)
                                {
                                    RigCheck("stands before the menu check", false, "in " + StateName());
                                    NextRigStep(RigStep.Teleport);
                                }

                                break;
                            }

                            _rigStepBegun = true;
                            _rigStepStart = Now;
                            _tps.SetMenuOpen(true);
                            RigCheck("a menu locks Malbers' movement and input", _animal.LockMovement && _animal.LockInput);
                            var follows = _tps.CameraRig.GetComponentsInChildren<ThirdPersonFollowTarget>(true);
                            RigCheck("a menu stops the camera turning", follows.Length > 0 && follows.All(f => !f.AllowCameraRotation.Value),
                                follows.Length + " follow target(s)");
                            RigCheck("the sword is refused in a menu", !_tps.TryAttack() && _tps.LastAttackRefusal == "a menu is open",
                                "refused for: " + (_tps.LastAttackRefusal ?? "nothing, it swung"));
                            _rigWalkFrom = _animal.transform.position;
                            _menuStartY = _menuMaxY = _animal.transform.position.y;
                            Move(Vector3.forward);
                            Press(true);
                        }
                        else
                        {
                            _menuMaxY = Mathf.Max(_menuMaxY, _animal.transform.position.y);
                            if (_held && InRigStep > 0.15f)
                                Press(false);

                            if (InRigStep > 1f)
                            {
                                _menuWalked = Flat(_animal.transform.position - _rigWalkFrom);
                                Move(Vector3.zero);
                                RigCheck("in a menu the stick and jump do nothing", _menuWalked < 0.05f && _menuMaxY - _menuStartY < 0.05f,
                                    $"moved {_menuWalked:0.00} m, rose {_menuMaxY - _menuStartY:0.00} m");

                                // Another holder of the same locks (a sword swing, a creep): closing the menu must
                                // not free the character while it still holds them.
                                var other = new object();
                                locks.Lock(other, movement: true, input: true);
                                _tps.SetMenuOpen(false);
                                bool heldByOther = _animal.LockMovement && _animal.LockInput;
                                locks.Release(other);
                                bool free = !_animal.LockMovement && !_animal.LockInput;
                                RigCheck("closing the menu frees the character only once nothing else holds it", heldByOther && free,
                                    $"with another hold: locked {heldByOther}; after it: free {free}");
                                var follows = _tps.CameraRig.GetComponentsInChildren<ThirdPersonFollowTarget>(true);
                                RigCheck("closing the menu lets the camera turn again", follows.All(f => f.AllowCameraRotation.Value));
                                NextRigStep(RigStep.Teleport);
                            }
                        }

                        break;

                    case RigStep.Teleport:
                        if (!_rigStepBegun)
                        {
                            if (InRigStep < 0.5f)
                                break;
                            _rigStepBegun = true;
                            _rigStepStart = Now;
                            _cameraGapBefore = CameraGap();
                            _teleportTarget = _animal.transform.position + new Vector3(12f, 0f, 6f);
                            _tps.TeleportGameplay(_teleportTarget, 90f);
                        }
                        else if (InRigStep > 0.3f)
                        {
                            var p = _animal.transform.position;
                            RigCheck("a teleport puts the player there, facing the given way",
                                Flat(p - _teleportTarget) < 0.1f && Mathf.Abs(Mathf.DeltaAngle(_animal.transform.eulerAngles.y, 90f)) < 2f,
                                $"{Flat(p - _teleportTarget):0.00} m off, facing {_animal.transform.eulerAngles.y:0}°");
                            _cameraGapAfter = CameraGap();
                            RigCheck("the camera arrives with the player", _cameraGapAfter >= 0f && _cameraGapAfter < _cameraGapBefore + 2f,
                                $"{_cameraGapAfter:0.0} m from the player 0.3 s after, {_cameraGapBefore:0.0} m before");
                            NextRigStep(RigStep.Downed);
                        }

                        break;

                    case RigStep.Downed:
                        if (!_rigStepBegun)
                        {
                            if (InRigStep < 0.5f)
                                break;
                            _rigStepBegun = true;
                            _tps.EnterDowned();
                            RigCheck("going down puts the character and its camera away",
                                !_tps.gameObject.activeSelf && _tps.CameraRig != null && !_tps.CameraRig.activeSelf && _tps.IsSuspended);
                            RigCheck("a downed player is not the local rig", !ReferenceEquals(PlayerRigs.Local, _tps));
                            RigCheck("a downed player has no aim and no crosshair", !_tps.TryGetAim(out _, out _) && !_tps.TryGetCrosshairRay(out _));
                            RigCheck("the sword is refused while downed", !_tps.AttackButtonIsSwords(out string why) && why == "downed",
                                "the button belonged to: " + (why ?? "the sword"));

                            // A revive places the player first (ReviveAt), while the character is still put away.
                            _revivePoint = _animal.transform.position + new Vector3(-6f, 0f, 3f);
                            _tps.TeleportGameplay(_revivePoint, 0f);
                            RigCheck("a downed player can be moved to where they will be revived", Flat(_animal.transform.position - _revivePoint) < 0.01f);
                        }
                        else if (InRigStep > 1f)
                        {
                            NextRigStep(RigStep.Revive);
                        }

                        break;

                    case RigStep.Revive:
                        if (!_rigStepBegun)
                        {
                            _rigStepBegun = true;
                            _tps.ExitDowned();
                            RigCheck("a revive brings the character and its camera back",
                                _tps.gameObject.activeSelf && _tps.CameraRig.activeSelf && !_tps.IsSuspended);
                            RigCheck("a revived player is the local rig again", ReferenceEquals(PlayerRigs.Local, _tps));
                            RigCheck("a revive gives Malbers the input back, unlocked", _tps.IsRunning && !_animal.LockInput && !_animal.LockMovement);
                            RigCheck("Malbers' pause key stays muted after a revive", InputLive("Pause") != true);
                        }
                        else if (InRigStep > 1.5f)
                        {
                            RigCheck("the revived player stands where the revive put them",
                                Standing() && Flat(_animal.transform.position - _revivePoint) < 0.3f,
                                $"{StateName()}, {Flat(_animal.transform.position - _revivePoint):0.00} m from the revive point");
                            _rigWalkFrom = _animal.transform.position;
                            Move(Vector3.forward);
                            NextRigStep(RigStep.ReviveWalk);
                        }

                        break;

                    case RigStep.ReviveWalk:
                        if (InRigStep > 1.2f)
                        {
                            _reviveWalked = Flat(_animal.transform.position - _rigWalkFrom);
                            Move(Vector3.zero);
                            RigCheck("a revived player walks as before", _walked > 0f && Mathf.Abs(_reviveWalked - _walked) < 0.25f * _walked,
                                $"{_reviveWalked:0.00} m in 1.2 s, {_walked:0.00} m before going down");
                            NextRigStep(RigStep.Done);
                        }

                        break;

                    case RigStep.Done:
                        return true;
                }

                return false;
            }

            void WriteRig(StringBuilder md)
            {
                if (_tps == null)
                    return;

                md.AppendLine("## Third-person player (ThirdPersonPlayer)");
                md.AppendLine($"- {_rigChecks.Count(c => c.pass)} of {_rigChecks.Count} checks pass.");
                if (_carryWalked >= 0f)
                    md.AppendLine($"- Walking 1.2 s: {_walked:0.00} m; carrying at the game's carry speed ({_carrySpeed:0.00}) {_carryWalked:0.00} m; " +
                                  $"after a revive {_reviveWalked:0.00} m. Malbers' time multiplier slows the animation and the root motion it " +
                                  "makes, so the carry speed compounds (DualPlayerController carries the same way).");
                md.AppendLine();
                md.AppendLine("| Check | Result | Detail |");
                md.AppendLine("|---|---|---|");
                foreach (var (check, pass, detail) in _rigChecks)
                    md.AppendLine($"| {check} | {(pass ? "PASS" : "**FAIL**")} | {detail ?? ""} |");
                md.AppendLine();
            }
        }
    }
}
#endif
