using System;
using System.Collections.Generic;
using Starshot.Helpers;
using Starshot.Features.ViewHost;

namespace Starshot.Features.Screenshot;

/// <summary>Each recognition owns an independent editor and its draft.</summary>
internal sealed class OcrResultWindow
{
    internal static Preparation Prepare() => new();
    internal sealed class Preparation : IDisposable
    {
        private MainWindow? _window = new(ocrPopup:true);
        private bool _delivered;
        internal void Show(IReadOnlyList<OcrLine> lines, bool translate=false)
        {
            var window=_window??throw new ObjectDisposedException(nameof(Preparation));
            window.ShowOcr(lines,translate);_delivered=true;
        }
        public void Dispose()
        {
            var window=_window;_window=null;
            // Delivered windows remain rooted by their native HWND until the user
            // closes them; an empty/failed OCR preparation is never kept idle.
            if(!_delivered)window?.Dispose();
        }
    }
    public OcrResultWindow(IReadOnlyList<OcrLine> lines, bool translateImmediately=false)
    {
        using var preparation=Prepare();preparation.Show(lines,translateImmediately);
    }
}
