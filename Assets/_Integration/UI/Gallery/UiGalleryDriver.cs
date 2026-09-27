using UnityEngine;
using UnityEngine.InputSystem;

namespace CollarCali.UI
{
    /// <summary>
    /// Shows the gallery's pages in play mode, one at a time. Left and right arrows page through
    /// <see cref="UiScenarios.All"/>.
    ///
    /// For looking at the UI live - animations, hover, the real screen size. Capture Gallery renders
    /// the same pages to PNGs without entering play mode.
    /// </summary>
    public class UiGalleryDriver : MonoBehaviour
    {
        Canvas _canvas;
        RectTransform _page;
        int _index;

        void Start()
        {
            _canvas = UiKit.CreateCanvas("Gallery", UiLayers.Dev, interactive: true, parent: transform);
            Show(0);
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            if (keyboard.rightArrowKey.wasPressedThisFrame)
                Show(_index + 1);
            else if (keyboard.leftArrowKey.wasPressedThisFrame)
                Show(_index - 1);
        }

        void Show(int index)
        {
            int count = UiScenarios.All.Count;
            if (count == 0)
                return;

            _index = (index % count + count) % count;
            if (_page != null)
                Destroy(_page.gameObject);

            _page = UiScenarios.CreatePage(_canvas.transform);
            UiScenarios.All[_index].Build(_page);
        }
    }
}
