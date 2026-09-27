using System.Collections.Generic;
using UnityEngine;

namespace CollarCali.UI
{
    /// <summary>Gallery pages for the loading screen and the error popup (boards 16 and 17).</summary>
    static class SessionScenarios
    {
        public static void Register(List<UiScenarios.Scenario> list)
        {
            list.Add(new UiScenarios.Scenario("16-loading", Loading));
            list.Add(new UiScenarios.Scenario("17-error-popup", ErrorPopup));
        }

        static void Loading(RectTransform page)
        {
            var loading = LoadingOverlayView.Create(page);
            loading.Show("Connecting…", "Europe");
            loading.Snap();
        }

        static void ErrorPopup(RectTransform page)
        {
            ErrorPopupView.Create(page).Show("We lost the connection to the server.", "ServerTimeout", null);
        }
    }
}
