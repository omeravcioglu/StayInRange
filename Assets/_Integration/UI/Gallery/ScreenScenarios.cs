using System.Collections.Generic;
using UnityEngine;

namespace CollarCali.UI
{
    /// <summary>Gallery pages for the menu screens (boards 13, 15, 19, 20) and the pause menu (board 21).</summary>
    static class ScreenScenarios
    {
        public static void Register(List<UiScenarios.Scenario> list)
        {
            list.Add(new UiScenarios.Scenario("13-main-menu", MainMenu));
            list.Add(new UiScenarios.Scenario("15-rooms", Rooms));
            list.Add(new UiScenarios.Scenario("19-lobby-host", LobbyHost));
            list.Add(new UiScenarios.Scenario("20-lobby-guest", LobbyGuest));
            list.Add(new UiScenarios.Scenario("21-pause", Pause));
        }

        static void MainMenu(RectTransform page)
        {
            var view = MainMenuView.Create(page);
            view.PlayerName = "NIBS";
            view.Region = 2;
            view.SetSettingsAvailable(false);
            view.FocusDefault();
        }

        static void Rooms(RectTransform page)
        {
            var view = RoomBrowserView.Create(page);
            view.SetRooms(new[]
            {
                new RoomListing { Name = "Ward Six", Players = 2, MaxPlayers = 4, Open = true },
                new RoomListing { Name = "no scream", Players = 3, MaxPlayers = 4, Open = true },
                new RoomListing { Name = "Crypt", Players = 1, MaxPlayers = 4, Password = true, Open = true },
                new RoomListing { Name = "Night Duty", Players = 4, MaxPlayers = 4, Open = true },
            });
            view.PreviewFocus("Ward Six");
            view.PreviewPassword("Crypt", "hunter", wrong: false);
        }

        static LobbyRowData Player(string name, int colour, LobbySlot status, bool you = false)
        {
            return new LobbyRowData { Name = name, Colour = colour, Status = status, IsYou = you };
        }

        static void LobbyHost(RectTransform page)
        {
            var view = LobbyView.Create(page);
            view.SetSubtitle("Ward Six · 3/4 players");
            view.SetRoster(new[]
            {
                Player("NIBS", 0, LobbySlot.Host, you: true),
                Player("SKIP", 1, LobbySlot.Ready),
                Player("DOT", 2, LobbySlot.NotReady),
            }, 4);
            view.SetPrimary("START GAME", visible: true, enabled: false, note: "not everyone is ready yet");
            view.SetHint("Waiting for everyone to ready up (2/3).");
            view.SetColour(0, new[] { null, "SKIP", "DOT", null });
        }

        static void LobbyGuest(RectTransform page)
        {
            var view = LobbyView.Create(page);
            view.SetSubtitle("Ward Six · 4/4 players");
            view.SetRoster(new[]
            {
                Player("NIBS", 0, LobbySlot.Host),
                Player("SKIP", 1, LobbySlot.Ready, you: true),
                Player("DOT", 2, LobbySlot.Ready),
                Player("ZIG", 3, LobbySlot.Ready),
            }, 4);
            view.SetPrimary("CANCEL READY", visible: true, enabled: true, note: null);
            view.ShowPrimaryFocused(true);
            view.SetHint("Waiting for the host to start the match…");
            view.SetColour(1, new[] { "NIBS", null, "DOT", "ZIG" });
        }

        /// <summary>The dim under the HUD, the team panel live over it, the menu on top - as in game.</summary>
        static void Pause(RectTransform page)
        {
            var back = UiKit.CreateRect("Back", page);
            back.Fill();
            var hud = UiKit.CreateRect("Hud", page);
            hud.Fill();
            var front = UiKit.CreateRect("Front", page);
            front.Fill();

            TeamPanelView.Create(hud).Set(new[]
            {
                HudScenarios.Row("NIBS", 0, 0f, self: true, health: 92f),
                HudScenarios.Row("SKIP", 1, 8f),
                HudScenarios.Row("DOT", 2, 12f),
                HudScenarios.Row("ZIG", 3, 17f),
            });

            var view = PauseMenuView.Create(back, front);
            view.SetSettingsAvailable(false);
            view.Show(instant: true);
        }
    }
}
