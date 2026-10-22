using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Starshot.Helpers;

namespace Starshot.Features.Screenshot;

internal partial class ScreenCaptureService
{
    private static async Task<string?> ChooseCaptureSavePathAsync(nint owner, string suggested, string extension,
        bool video = false, bool jpeg = false)
    {
        while (true)
        {
            var selected = await FileDialogHelper.OpenSaveFileDialogWithFilterIndexAsync(owner, suggested,
                ($"{extension.ToUpperInvariant()} 图像", "." + extension));
            if (selected is null) return null;
            string path = Path.GetFullPath(selected.Value.Path);
            if (!CaptureSavePolicy.HasCompanionConflict(path, video, jpeg, File.Exists)) return path;
            if (SaveConflictMessage(owner, "同名的伴随视频或 Ultra HDR 图片已存在。请重试并选择其他文件名，避免覆盖其他作品。",
                "保存图像", 0x35) != 4) return null; // Retry/Cancel + warning, never overwrite unconfirmed siblings.
            suggested = path;
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int SaveConflictMessage(nint owner, string text, string title, uint type);
}
