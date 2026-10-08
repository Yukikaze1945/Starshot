using System;
using System.Threading.Tasks;

namespace Starshot
{
    internal static class AppConfig { public static int ScreenCaptureMode { get; set; } = 2; }
}
namespace Starshot.Features.Screenshot
{
    internal static class ScreenCaptureHelper { public static bool IsWin10 => false; }
    internal static class MonitorCaptureContext
    {
        internal static int Releases;
        internal static Func<Task> Retire = () => Task.CompletedTask;
        public static Task ReleaseContextsAsync() { Releases++; return Retire(); }
    }
}
namespace Serilog
{
    internal static class Log { public static void Information(string template, params object[] values) { } }
}
