using UnityEngine;

namespace CollarCali.UI
{
    /// <summary>
    /// Gallery pages for the in-game HUD (boards 09-12 and the HUD sheets), built from the same
    /// widgets HudRoot uses, fed with the boards' own mock numbers.
    /// </summary>
    static class HudScenarios
    {
        public static void Register(System.Collections.Generic.List<UiScenarios.Scenario> list)
        {
            list.Add(new UiScenarios.Scenario("09-hud-explore", Explore));
            list.Add(new UiScenarios.Scenario("10-hud-fight", Fight));
            list.Add(new UiScenarios.Scenario("11-hud-critical", Critical));
            list.Add(new UiScenarios.Scenario("12-hud-too-far", TooFar));
            list.Add(new UiScenarios.Scenario("hud-team-rows", TeamRows));
            list.Add(new UiScenarios.Scenario("hud-weapon-states", WeaponStates));
            list.Add(new UiScenarios.Scenario("hud-collar-states", CollarStates));
            list.Add(new UiScenarios.Scenario("world-enemy-bars", EnemyBars));
        }

        /// <summary>The enemy bar's states, drawn twice their world size so the slice is easy to judge.</summary>
        static void EnemyBars(RectTransform page)
        {
            UiScenarios.Label(page, "ENEMY BARS", TextStyle.Heading.WithSize(56f), 72f, 40f);
            var states = new[] { (0.8f, 0.95f, "Hit · white slice is the damage that just landed"),
                                 (0.55f, 0.55f, "Hurt"),
                                 (0.2f, 0.32f, "Nearly dead · under 25%") };
            for (int i = 0; i < states.Length; i++)
            {
                var bar = SketchBar.Create(page, "Bar", new Vector2(600f, 88f), UiTheme.Rgb(0xFF5A52), shine: false);
                bar.SetValue(states[i].Item1);
                if (states[i].Item1 < 0.25f)
                    bar.SetFillColor(UiTheme.Rgb(0xC8160F));
                bar.SetGhost(states[i].Item2, new Color(1f, 1f, 1f, 0.9f));
                UiScenarios.At((RectTransform)bar.transform, 72f, 180f + i * 200f);
                UiScenarios.Label(page, states[i].Item3, TextStyle.Small.WithSize(26f), 72f, 280f + i * 200f);
            }

            // The stalker cannot be hurt, so it gets a warning instead of a bar (ZombieHealthBar.ShowAsUnkillable).
            var run = UiScenarios.At(UiKit.CreateRow(page, "Run", 24f, TextAnchor.MiddleCenter), 900f, 170f);
            UiKit.CreateImage(run, "Skull", UiSprites.Skull, Color.white).Sized(120f, 120f);
            UiKit.CreateText(run, "Text", "RUN!", TextStyle.Prompt.WithSize(136f).WithTint(ColorRole.Danger));
            UiScenarios.Label(page, "Stalker · cannot be hurt, so a warning instead of a bar", TextStyle.Small.WithSize(26f), 900f, 380f);
        }

        internal static TeamRowData Row(string name, int colour, float metres, bool self = false, float health = 88f,
            RowStatus status = RowStatus.Alive, string carrying = null, string carriedBy = null, bool watched = false,
            VoiceState voice = VoiceState.Hidden)
        {
            return new TeamRowData
            {
                Name = name,
                Colour = PlayerColorPalette.Get(colour),
                IsSelf = self,
                Status = status,
                Health = health,
                MaxHealth = 100f,
                Metres = metres,
                Carrying = carrying,
                CarriedBy = carriedBy,
                Watched = watched,
                Voice = voice,
            };
        }

        static WeaponData Pistol(int magazine, bool reloading = false, float reload01 = 0f)
        {
            return new WeaponData
            {
                Visible = true,
                Name = "Pistol",
                Glyph = WeaponGlyph.Pistol,
                Magazine = magazine,
                MagazineSize = 17,
                UnlimitedReserve = true,
                Reloading = reloading,
                Reload01 = reload01,
                Heat01 = -1f,
                ActiveSlot = 1,
                SlotCount = 2,
                SlotGlyphs = new[] { WeaponGlyph.Pistol, WeaponGlyph.Mp7 },
            };
        }

        /// <summary>The boards' compass: heading, then SKIP, DOT and ZIG's bearings; -1 hides a pip.</summary>
        static void Compass(RectTransform page, float heading, float skip, float dot, float zig,
            float checkpoint = -1f, float station = -1f, string tooFar = null)
        {
            var markers = new System.Collections.Generic.List<CompassMarker>();
            if (checkpoint >= 0f)
                markers.Add(new CompassMarker { Bearing = checkpoint, Kind = CompassMark.Checkpoint });
            if (station >= 0f)
                markers.Add(new CompassMarker { Bearing = station, Kind = CompassMark.Station });
            var who = new[] { ("SKIP", 1, skip), ("DOT", 2, dot), ("ZIG", 3, zig) };
            foreach (var (name, colour, bearing) in who)
            {
                if (bearing < 0f)
                    continue;
                markers.Add(new CompassMarker
                {
                    Bearing = bearing,
                    Kind = CompassMark.Teammate,
                    Colour = PlayerColorPalette.Get(colour),
                    TooFar = name == tooFar,
                });
            }

            CompassView.Create(page).Set(heading, markers);
        }

        static MovementData Dashes(int ready, float recharge01 = 0f, float stamina01 = -1f)
        {
            return new MovementData { Visible = true, Dashes = ready, MaxDashes = 3, Recharge01 = recharge01, Stamina01 = stamina01 };
        }

        static void Explore(RectTransform page)
        {
            TeamPanelView.Create(page).Set(new[]
            {
                Row("NIBS", 0, 0f, self: true, health: 88f),
                Row("SKIP", 1, 8f),
                Row("DOT", 2, 12f),
                Row("ZIG", 3, 17f),
            });
            Compass(page, 0f, 20f, 335f, 200f, checkpoint: 8f);
            WeaponHudView.Create(page).Set(Pistol(17));
            MovementView.Create(page).Set(Dashes(3));
            CrosshairView.Create(page).Set(true, false);
            InteractionPromptView.Create(page).Set(new PromptData
            {
                Visible = true, Key = "E", Text = "Pick up MP7", Hold = true, Progress01 = 0.4f,
            });
            NoticesView.Create(page).SetCheckpoint(new CheckpointData
            {
                Pending = true, Id = 2, Here = 3, Needed = 4,
                Colours = new[] { PlayerColorPalette.Get(0), PlayerColorPalette.Get(1), PlayerColorPalette.Get(2), PlayerColorPalette.Get(3) },
                Arrived = new[] { true, true, true, false },
                WaitingFor = "ZIG",
            });
        }

        static void Fight(RectTransform page)
        {
            TeamPanelView.Create(page).Set(new[]
            {
                Row("NIBS", 0, 0f, self: true, health: 64f),
                Row("SKIP", 1, 6f, health: 72f),
                Row("DOT", 2, 9f, health: 40f),
                Row("ZIG", 3, 14f, health: 90f),
            });
            var weapon = Pistol(9);
            weapon.Name = "MP7";
            weapon.Glyph = WeaponGlyph.Mp7;
            weapon.MagazineSize = 36;
            weapon.ActiveSlot = 2;
            weapon.ShowSlots = true;
            Compass(page, 90f, 70f, 112f, 250f);
            WeaponHudView.Create(page).Set(weapon);
            MovementView.Create(page).Set(Dashes(2, 0.6f));

            var crosshair = CrosshairView.Create(page);
            crosshair.Flash(headshot: true, kill: false);
            crosshair.Set(true, true);

            var notices = NoticesView.Create(page);
            notices.AddKill("You", "Zombie");
            notices.AddKill("SKIP", "Crawler");
            notices.AddLine("DOT is down", ColorRole.Danger);
        }

        static void Critical(RectTransform page)
        {
            var fx = DamageFxView.Create(page);
            fx.SetCritical(true);
            fx.Refresh();

            TeamPanelView.Create(page).Set(new[]
            {
                Row("NIBS", 0, 0f, self: true, health: 22f),
                Row("SKIP", 1, 8f),
                Row("DOT", 2, 11f, status: RowStatus.Down),
                Row("ZIG", 3, 5f, carrying: null),
            });
            Compass(page, 180f, 150f, 205f, 170f, station: 140f);
            WeaponHudView.Create(page).Set(Pistol(0, reloading: true, reload01: 0.6f));
            MovementView.Create(page).Set(Dashes(0, 0.35f));
            CrosshairView.Create(page).Set(true, false);
            InteractionPromptView.Create(page).Set(new PromptData
            {
                Visible = true, Key = "E", Text = "Revive", Blocked = true, Hint = "bring a body here to revive",
            });
        }

        static void TooFar(RectTransform page)
        {
            TeamPanelView.Create(page).Set(new[]
            {
                Row("NIBS", 0, 0f, self: true, health: 88f),
                Row("SKIP", 1, 5f),
                Row("DOT", 2, 7f),
                Row("ZIG", 3, 31f),
            });
            Compass(page, 0f, 345f, 12f, 101f, tooFar: "ZIG");
            CollarWarningView.Create(page).Set(new CollarData
            {
                Stage = CollarStage.Breach, Who = "ZIG", Metres = 31f, SecondsLeft = 3.2f, Left01 = 0.64f,
            });
            WeaponHudView.Create(page).Set(Pistol(12));
            MovementView.Create(page).Set(Dashes(3, 0f, stamina01: 0.3f));
            CrosshairView.Create(page).Set(true, false);
        }

        static void TeamRows(RectTransform page)
        {
            UiScenarios.Label(page, "TEAM PANEL · ROW STATES", TextStyle.Heading.WithSize(56f), 72f, 40f);
            var rows = new[]
            {
                Row("NIBS", 0, 0f, self: true, health: 88f),
                Row("SKIP", 1, 12f, voice: VoiceState.Talking),
                Row("DOT", 2, 22f, voice: VoiceState.Idle),
                Row("ZIG", 3, 27f, voice: VoiceState.Muted),
                Row("NIBS", 0, 0f, self: true, health: 18f),
                Row("SKIP", 1, 9f, health: 24f),
                Row("DOT", 2, 4f, status: RowStatus.Down),
                Row("ZIG", 3, 3f, status: RowStatus.Carried, carriedBy: "SKIP"),
                Row("SKIP", 1, 3f, carrying: "ZIG", watched: true),
            };

            for (int i = 0; i < rows.Length; i++)
            {
                var panel = TeamPanelView.Create(page);
                ((RectTransform)panel.transform).anchoredPosition = new Vector2(72f + (i / 5) * 620f, -150f - (i % 5) * 100f);
                panel.Set(new[] { rows[i] });
            }
        }

        static void WeaponStates(RectTransform page)
        {
            UiScenarios.Label(page, "WEAPON STATES", TextStyle.Heading.WithSize(56f), 72f, 40f);

            var normal = Pistol(17);
            var low = Pistol(4);
            var reloading = Pistol(0, reloading: true, reload01: 0.55f);
            var empty = Pistol(0);
            var overheat = Pistol(20);
            overheat.Name = "MP7";
            overheat.Glyph = WeaponGlyph.Mp7;
            overheat.MagazineSize = 36;
            overheat.Heat01 = 1f;
            overheat.Overheated = true;
            var slots = Pistol(17);
            slots.ShowSlots = true;

            var states = new[] { normal, low, reloading, empty, overheat, slots };
            for (int i = 0; i < states.Length; i++)
            {
                var view = WeaponHudView.Create(page);
                var rect = (RectTransform)view.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(40f + (i % 3) * 620f, -120f - (i / 3) * 440f);
                view.Set(states[i]);
            }
        }

        static void CollarStates(RectTransform page)
        {
            var stages = new[]
            {
                new CollarData { Stage = CollarStage.Warning, Who = "ZIG", Metres = 22f },
                new CollarData { Stage = CollarStage.Danger, Who = "ZIG", Metres = 27f },
                new CollarData { Stage = CollarStage.Breach, Who = "ZIG", Metres = 31f, SecondsLeft = 3.2f, Left01 = 0.64f },
                new CollarData { Stage = CollarStage.Failed },
            };

            for (int i = 0; i < stages.Length; i++)
            {
                var view = CollarWarningView.Create(page);
                ((RectTransform)view.transform).anchoredPosition = new Vector2(0f, -20f - i * 265f);
                view.Set(stages[i]);
            }
        }
    }
}
