#if WINUI_TEST
using Easydict.WinUI.Models;
using Easydict.WinUI.Views;
using Microsoft.UI.Windowing;

namespace Easydict.WinUI.Services;

/// <summary>Deterministic UI fixtures; excluded from production builds. No OCR or network requests.</summary>
internal static class HoverLookupLayoutTestHost
{
    internal static nint Show(HoverLookupWindow window, int scenario)
    {
        var area = DisplayArea.Primary.WorkArea;
        var anchor = new OcrRect(area.X + area.Width / 2, area.Y + area.Height / 4, 40, 20);
        if (scenario == 0)
        {
            window.HidePopup();
            return window.WindowHandle;
        }

        if (scenario == 5)
        {
            window.ShowLoading("cat", anchor);
            return window.WindowHandle;
        }

        var word = scenario == 2 ? "pneumonoultramicroscopicsilicovolcanoconiosis" : "cat";
        var body = scenario switch
        {
            3 => string.Join('\n', Enumerable.Range(1, 12).Select(i => $"Definition {i:00}: meaning")),
            4 => string.Join('\n', Enumerable.Range(1, 200).Select(i => $"Definition {i:000}: meaning")),
            _ => "猫",
        };
        window.ShowContent(new HoverLookupContent(word, null, body, "Test dictionary"), anchor);
        return window.WindowHandle;
    }
}
#endif
