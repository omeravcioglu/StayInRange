using System.Collections.Generic;
using MalbersAnimations;
using UnityEngine;

namespace CollarCali
{
    /// <summary>Menus over the running game, and being downed.</summary>
    public partial class ThirdPersonPlayer
    {
        readonly Hold _menuHold = new Hold("menu");
        readonly List<ThirdPersonFollowTarget> _menuHeldCameras = new List<ThirdPersonFollowTarget>();
        bool _menuOpen;

        /// <summary>True while a menu is open over the running game (pause, settings).</summary>
        public bool IsMenuOpen => _menuOpen;

        /// <summary>
        /// A menu over the running game, where nothing actually pauses: the player stands still and
        /// the camera stops following the mouse, while the rest of the world carries on. Safe to call
        /// every frame while the menu is up.
        /// </summary>
        public void SetMenuOpen(bool open)
        {
            if (open)
            {
                _menuOpen = true;
                Locks?.Lock(_menuHold, movement: true, input: true);
                SyncAttackKeys();
                HoldCamerasForMenu();
                return;
            }

            if (!_menuOpen)
                return;

            _menuOpen = false;
            Locks?.Release(_menuHold);
            SyncAttackKeys();
            ReleaseCamerasFromMenu();
        }

        void HoldCamerasForMenu()
        {
            if (_menuHeldCameras.Count > 0 || cameraRig == null)
                return;

            foreach (var follow in cameraRig.GetComponentsInChildren<ThirdPersonFollowTarget>(true))
            {
                if (follow == null || !follow.AllowCameraRotation.Value)
                    continue;
                follow.AllowCameraRotation.Value = false;
                _menuHeldCameras.Add(follow);
            }
        }

        void ReleaseCamerasFromMenu()
        {
            foreach (var follow in _menuHeldCameras)
            {
                if (follow != null)
                    follow.AllowCameraRotation.Value = true;
            }

            _menuHeldCameras.Clear();
        }

        /// <summary>
        /// The player is down: anything held is dropped, an attack stops, and the character and its
        /// camera are put away until a revive. The body the others see (PlayerDownState's ragdoll)
        /// lies where they fell, and the spectator camera takes the view.
        /// </summary>
        public void EnterDowned()
        {
            if (_suspended)
                return;

            EndAimViewImmediate();
            SetCarrying(false, 1f);
            SetInteractClaimed(false);
            StopAttacks();

            _suspended = true;
            StopMalbers();
            if (cameraRig != null)
                cameraRig.SetActive(false);
            gameObject.SetActive(false);
        }

        /// <summary>
        /// Gives the player control back after a revive. The caller has already placed the player
        /// (ReviveAt teleports before clearing IsDead).
        /// </summary>
        public void ExitDowned()
        {
            if (!_suspended)
                return;

            _suspended = false;
            gameObject.SetActive(true);
            if (cameraRig != null)
                cameraRig.SetActive(true);
            StartMalbers();
            SnapCameras();
            if (!_menuOpen)
                LockCursor();
        }
    }
}
