using System.Collections.Generic;
using Starshot.Helpers;

namespace Starshot.Features.Screenshot;

/// <summary>One compact WebUI OCR window, independent of the translation workspace.</summary>
internal sealed class OcrResultWindow
{
    private static Starshot.Features.ViewHost.MainWindow? _popup;
    public OcrResultWindow(IReadOnlyList<OcrLine> lines, bool translateImmediately = false)
    {
        if (_popup is null)
        {
            _popup = new Starshot.Features.ViewHost.MainWindow(ocrPopup: true);
            _popup.Closed += (_, _) => _popup = null;
        }
        _popup.ShowOcr(lines, translateImmediately);
    }
}
