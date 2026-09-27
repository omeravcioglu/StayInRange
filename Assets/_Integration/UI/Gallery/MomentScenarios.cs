using System.Collections.Generic;
using UnityEngine;

namespace CollarCali.UI
{
    /// <summary>
    /// Gallery pages for the death and revive flow (boards 01-08): the MomentsView cards with the
    /// boards' own mock state, and the team panel as each player would see it.
    /// </summary>
    static class MomentScenarios
    {
        public static void Register(List<UiScenarios.Scenario> list)
        {
            list.Add(new UiScenarios.Scenario("01-you-died", YouDied));
            list.Add(new UiScenarios.Scenario("02-spectating", Spectating));
            list.Add(new UiScenarios.Scenario("03-lifted", Lifted));
            list.Add(new UiScenarios.Scenario("04-revived", Revived));
            list.Add(new UiScenarios.Scenario("05-teammate-down", TeammateDown));
            list.Add(new UiScenarios.Scenario("07-grabbed", Grabbed));
            list.Add(new UiScenarios.Scenario("08-team-wipe", TeamWipe));
        }

        static TeamRowData Row(string name, int colour, float metres, bool self = false, RowStatus status = RowStatus.Alive,
            string carriedBy = null, string carrying = null, bool watched = false, float health = 80f)
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
                CarriedBy = carriedBy,
                Carrying = carrying,
                Watched = watched,
            };
        }

        /// <summary>DOT's team panel while dead: distances from DOT's body.</summary>
        static void DeadTeam(RectTransform page, bool carried, bool watchSkip)
        {
            TeamPanelView.Create(page).Set(new[]
            {
                Row("NIBS", 0, 9f),
                Row("SKIP", 1, carried ? 2f : 8f, carrying: carried ? "DOT" : null, watched: watchSkip),
                Row("DOT", 2, 0f, self: true, status: carried ? RowStatus.Carried : RowStatus.Down, carriedBy: carried ? "SKIP" : null),
                Row("ZIG", 3, 14f),
            });
        }

        static MomentsView Moments(RectTransform page) => MomentsView.Create(page, page);

        static void YouDied(RectTransform page)
        {
            Moments(page).ShowDeath(1f, 2);
            DeadTeam(page, carried: false, watchSkip: false);
        }

        static SpectatorData Watching(BodyStep step, string lifted, string station)
        {
            return new SpectatorData
            {
                WatchName = "SKIP",
                WatchColour = PlayerColorPalette.Get(1),
                WatchIndex = 2,
                WatchCount = 3,
                SelfColour = PlayerColorPalette.Get(2),
                Step = step,
                DownText = "Closest teammate: SKIP, 8 m",
                LiftedText = lifted,
                StationText = station,
            };
        }

        static void Spectating(RectTransform page)
        {
            Moments(page).ShowSpectating(Watching(BodyStep.Lifted, "A teammate has to lift you", "Look for the green light"));
            DeadTeam(page, carried: false, watchSkip: true);
        }

        static void Lifted(RectTransform page)
        {
            var moments = Moments(page);
            moments.ShowSpectating(Watching(BodyStep.AtStation, "SKIP is holding you up", "18 m to go"));
            moments.Toast("SKIP lifted you!", UiTheme.Active.warning);
            DeadTeam(page, carried: true, watchSkip: true);
        }

        static void Revived(RectTransform page)
        {
            Moments(page).ShowRevived("SKIP", 0.5f, 2.6f);
            TeamPanelView.Create(page).Set(new[]
            {
                Row("NIBS", 0, 9f), Row("SKIP", 1, 2f), Row("DOT", 2, 0f, self: true, health: 100f), Row("ZIG", 3, 14f),
            });
        }

        static void TeammateDown(RectTransform page)
        {
            Moments(page).ShowTeammateDown("DOT");
            TeamPanelView.Create(page).Set(new[]
            {
                Row("NIBS", 0, 6f), Row("SKIP", 1, 0f, self: true, health: 72f),
                Row("DOT", 2, 2f, status: RowStatus.Down), Row("ZIG", 3, 14f),
            });
        }

        static void Grabbed(RectTransform page)
        {
            Moments(page).ShowGrabbed();
            TeamPanelView.Create(page).Set(new[]
            {
                Row("NIBS", 0, 11f), Row("SKIP", 1, 7f), Row("DOT", 2, 16f), Row("ZIG", 3, 0f, self: true, health: 45f),
            });
        }

        static void TeamWipe(RectTransform page)
        {
            Moments(page).ShowTeamWipe("The whole team is down. Restarting from the last checkpoint.", 2);
            TeamPanelView.Create(page).Set(new[]
            {
                Row("NIBS", 0, 4f, status: RowStatus.Down), Row("SKIP", 1, 6f, status: RowStatus.Down),
                Row("DOT", 2, 0f, self: true, status: RowStatus.Down), Row("ZIG", 3, 9f, status: RowStatus.Down),
            });
        }
    }
}
