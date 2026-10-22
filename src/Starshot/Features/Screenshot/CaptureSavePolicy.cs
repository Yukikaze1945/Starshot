using System;
using System.IO;

namespace Starshot.Features.Screenshot;

internal static class CaptureSavePolicy
{
    internal static bool ChooseLocation(RegionCaptureAction action) => action == RegionCaptureAction.Save;

    // The picker confirms replacing the primary image, not unrelated companion files.
    internal static bool HasCompanionConflict(string image, bool video, bool jpeg, Func<string, bool> exists)
    {
        string mp4 = Path.ChangeExtension(image, ".mp4");
        return (video && (exists(mp4) || exists(mp4 + ".starshot.json")))
            || (jpeg && exists(Path.ChangeExtension(image, ".jpg")));
    }
}
