#if CMPSETUP_COMPLETE
using cowsins;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CollarCali.UI
{
    /// <summary>
    /// Reads the first-person controller (Cowsins) for the HUD: the weapon in hand, the dash
    /// charges, what is under the crosshair and whether it is an enemy.
    ///
    /// It reads Cowsins' own data and events rather than its UI, so the Cowsins HUD can stay switched
    /// off. Events that fire only at start-up (dash count, weapon init) happened before the HUD
    /// existed, so their state is read directly when binding.
    /// </summary>
    public sealed class CowsinsHudSource
    {
        const float SlotStripSeconds = 1.5f;

        PlayerDependencies _deps;
        PlayerMovement _movement;

        bool _enemySpotted;

        bool _reloading;
        float _reloadStartedAt;
        float _reloadSeconds;

        int _dashes;
        int _maxDashes;
        float _dashCooldown;
        float _rechargeStartedAt = -1f;

        bool _interaction;
        bool _blocked;
        float _progress;

        int _shownSlot = -1;
        int _shownSlotCount = -1;
        float _slotsUntil;
        WeaponGlyph[] _slotGlyphs = new WeaponGlyph[0];

        public PlayerDependencies Dependencies => _deps;
        public bool EnemySpotted => _enemySpotted && _deps != null;

        public void Bind(PlayerDependencies deps)
        {
            if (deps == _deps)
                return;

            Unbind();
            _deps = deps;
            if (deps == null)
                return;

            _movement = deps.GetComponent<PlayerMovement>();

            var weapon = deps.WeaponEvents?.Events;
            if (weapon != null)
            {
                weapon.OnEnemySpotted.AddListener(OnEnemySpotted);
                weapon.OnStartReload.AddListener(OnStartReload);
                weapon.OnFinishReload.AddListener(OnReloadEnded);
                weapon.OnCancelReload.AddListener(OnReloadEnded);
            }

            var movement = deps.PlayerMovementEvents?.Events;
            if (movement != null)
            {
                movement.OnDashUsed.AddListener(OnDashUsed);
                movement.OnDashGained.AddListener(OnDashGained);
            }

            var interact = deps.InteractEvents?.Events;
            if (interact != null)
            {
                interact.OnAllowedInteraction.AddListener(OnAllowed);
                interact.OnForbiddenInteraction.AddListener(OnForbidden);
                interact.OnInteractionProgressChanged.AddListener(OnProgress);
                interact.OnDisableInteraction.AddListener(OnInteractionGone);
                interact.OnFinishInteraction.AddListener(OnInteractionGone);
            }

            // The dash count was announced in Start, before this existed: start full.
            if (_movement != null)
            {
                var settings = _movement.playerSettings;
                _maxDashes = settings.canDash && !settings.infiniteDashes ? settings.amountOfDashes : 0;
                _dashes = _maxDashes;
                _dashCooldown = Mathf.Max(0.1f, settings.dashCooldown);
            }
        }

        public void Unbind()
        {
            if (_deps == null)
                return;

            var weapon = _deps.WeaponEvents?.Events;
            if (weapon != null)
            {
                weapon.OnEnemySpotted.RemoveListener(OnEnemySpotted);
                weapon.OnStartReload.RemoveListener(OnStartReload);
                weapon.OnFinishReload.RemoveListener(OnReloadEnded);
                weapon.OnCancelReload.RemoveListener(OnReloadEnded);
            }

            var movement = _deps.PlayerMovementEvents?.Events;
            if (movement != null)
            {
                movement.OnDashUsed.RemoveListener(OnDashUsed);
                movement.OnDashGained.RemoveListener(OnDashGained);
            }

            var interact = _deps.InteractEvents?.Events;
            if (interact != null)
            {
                interact.OnAllowedInteraction.RemoveListener(OnAllowed);
                interact.OnForbiddenInteraction.RemoveListener(OnForbidden);
                interact.OnInteractionProgressChanged.RemoveListener(OnProgress);
                interact.OnDisableInteraction.RemoveListener(OnInteractionGone);
                interact.OnFinishInteraction.RemoveListener(OnInteractionGone);
            }

            _deps = null;
            _movement = null;
        }

        #region Events

        void OnEnemySpotted(bool spotted) => _enemySpotted = spotted;

        void OnStartReload()
        {
            _reloading = true;
            _reloadStartedAt = Time.time;
            var weapon = _deps != null ? _deps.WeaponReference?.Weapon : null;
            _reloadSeconds = weapon != null ? Mathf.Max(0.1f, weapon.reloadTime) : 1f;
        }

        void OnReloadEnded() => _reloading = false;

        void OnDashUsed(int remaining)
        {
            if (_dashes >= _maxDashes)
                _rechargeStartedAt = Time.time;
            _dashes = remaining;
        }

        void OnDashGained(int remaining)
        {
            _dashes = remaining;
            // Each charge comes back on its own timer: the next one starts now.
            _rechargeStartedAt = _dashes < _maxDashes ? Time.time : -1f;
        }

        void OnAllowed(string text)
        {
            _interaction = true;
            _blocked = false;
            _progress = 0f;
        }

        void OnForbidden()
        {
            _interaction = true;
            _blocked = true;
            _progress = 0f;
        }

        void OnProgress(float progress01) => _progress = Mathf.Clamp01(progress01);

        void OnInteractionGone()
        {
            _interaction = false;
            _blocked = false;
            _progress = 0f;
        }

        #endregion

        #region Reads

        public void ReadHealth(out float health, out float maxHealth, out float shield01)
        {
            var stats = _deps != null ? _deps.PlayerStats : null;
            if (stats != null)
            {
                health = stats.Health;
                maxHealth = Mathf.Max(1f, stats.MaxHealth);
                shield01 = stats.MaxShield > 0f ? Mathf.Clamp01(stats.Shield / stats.MaxShield) : 0f;
                return;
            }

            health = 0f;
            maxHealth = 100f;
            shield01 = 0f;
        }

        public WeaponData ReadWeapon()
        {
            var reference = _deps != null ? _deps.WeaponReference : null;
            var weapon = reference?.Weapon;
            var id = reference?.Id;
            if (weapon == null || id == null)
                return default;

            var data = new WeaponData
            {
                Visible = true,
                Name = weapon._name,
                Glyph = GlyphFor(weapon),
                Magazine = id.bulletsLeftInMagazine,
                MagazineSize = id.magazineSize > 0 ? id.magazineSize : weapon.magazineSize,
                Reserve = id.totalBullets,
                UnlimitedReserve = !weapon.limitedMagazines,
                Heat01 = -1f,
            };

            bool reloading = _deps.WeaponBehaviour != null && _deps.WeaponBehaviour.IsReloading;
            if (!reloading)
                _reloading = false;
            data.Reloading = reloading;
            data.Reload01 = _reloading ? Mathf.Clamp01((Time.time - _reloadStartedAt) / _reloadSeconds) : 0f;

            if (weapon.reloadStyle == ReloadingStyle.Overheat)
            {
                data.Heat01 = Mathf.Clamp01(id.heatRatio);
                data.Overheated = data.Heat01 >= 0.999f;
            }

            FillSlots(reference, ref data);
            return data;
        }

        void FillSlots(IWeaponReferenceProvider reference, ref WeaponData data)
        {
            var inventory = reference.Inventory;
            int count = inventory != null ? inventory.Length : 0;
            if (_slotGlyphs.Length != count)
                _slotGlyphs = new WeaponGlyph[count];

            int filled = 0;
            for (int i = 0; i < count; i++)
            {
                var slot = inventory[i];
                _slotGlyphs[i] = slot != null && slot.weapon != null ? GlyphFor(slot.weapon) : WeaponGlyph.None;
                if (slot != null)
                    filled = i + 1;
            }

            // The strip shows for a moment after a swap or a pickup, then gets out of the way.
            int active = reference.CurrentWeaponIndex + 1;
            if (active != _shownSlot || filled != _shownSlotCount)
            {
                if (_shownSlot >= 0)
                    _slotsUntil = Time.time + SlotStripSeconds;
                _shownSlot = active;
                _shownSlotCount = filled;
            }

            data.ActiveSlot = active;
            data.SlotCount = filled;
            data.SlotGlyphs = _slotGlyphs;
            data.ShowSlots = Time.time < _slotsUntil;
        }

        static WeaponGlyph GlyphFor(Weapon_SO weapon)
        {
            var name = weapon != null ? weapon._name : null;
            if (string.IsNullOrEmpty(name))
                return WeaponGlyph.None;
            if (name.IndexOf("MP7", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("SMG", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return WeaponGlyph.Mp7;
            if (name.IndexOf("Pistol", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return WeaponGlyph.Pistol;
            return WeaponGlyph.None;
        }

        /// <summary>Dash charges from Cowsins, and stamina from whichever controller has it.</summary>
        public MovementData ReadMovement(bool thirdPerson, float stamina01)
        {
            var data = new MovementData { Visible = true, Stamina01 = stamina01 };
            if (thirdPerson || _deps == null)
                return data;

            data.MaxDashes = _maxDashes;
            data.Dashes = Mathf.Clamp(_dashes, 0, _maxDashes);
            data.Recharge01 = _rechargeStartedAt < 0f || data.Dashes >= _maxDashes
                ? 0f
                : Mathf.Clamp01((Time.time - _rechargeStartedAt) / _dashCooldown);
            return data;
        }

        /// <summary>The prompt for whatever Cowsins has highlighted, reworded the way the design says it.</summary>
        public PromptData ReadPrompt()
        {
            var manager = _deps != null ? _deps.InteractManager : null;
            var target = manager?.HighlightedInteractable;
            if (!_interaction || target == null)
                return default;

            var data = new PromptData
            {
                Visible = true,
                Key = InteractKey(),
                Blocked = _blocked,
                Hold = !target.InstantInteraction,
                Progress01 = _progress,
                Text = PromptText(target),
            };

            if (target is ReviveStation station)
            {
                data.Text = station.PromptText;
                if (station.CooldownLeft > 0f)
                    data.Hint = "ready again in " + Mathf.CeilToInt(station.CooldownLeft) + " s";
                else if (!station.HasBodyInRange)
                    data.Hint = "bring a body here to revive";
            }
            else if (_blocked)
            {
                data.Hint = "can't use this right now";
            }

            return data;
        }

        static string PromptText(Interactable target)
        {
            var text = (target.interactText ?? string.Empty).Trim().TrimEnd('.').Trim();

            // Cowsins names a pickup by its item alone ("MP7") or not at all; the design says what
            // the key does.
            if (target is BulletsPickeable)
                return "Pick up ammo";
            if (target is Pickeable && !text.StartsWith("Pick", System.StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrEmpty(text) ? "Pick up" : "Pick up " + text;
            return string.IsNullOrEmpty(text) ? "Use" : text;
        }

        static string InteractKey()
        {
            var action = InputManager.inputActions?.GameControls.Interacting;
            if (action == null || action.bindings.Count == 0)
                return "E";

            var path = action.bindings[0].effectivePath;
            return string.IsNullOrEmpty(path)
                ? "E"
                : InputControlPath.ToHumanReadableString(path, InputControlPath.HumanReadableStringOptions.OmitDevice)
                    .ToUpperInvariant();
        }

        #endregion
    }
}
#endif
