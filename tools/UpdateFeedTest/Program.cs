using Starshot.Features.Update;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var preview = await ReleaseClient.GetLatestReleaseGitHubAsync(true, timeout.Token);
if (preview is null) throw new Exception("Public preview feed is empty.");
if (!preview.ZipUrl.EndsWith($"Starshot-{preview.TagName}-win-x64.zip", StringComparison.Ordinal))
    throw new Exception("Release asset does not match the native updater's x64 filename.");
if (!preview.ZipUrl.StartsWith("https://github.com/Yukikaze1945/Starshot-releases/releases/download/", StringComparison.Ordinal))
    throw new Exception("Update download left the fork distribution repository.");
if (preview.Version <= new SemanticVersioning.Version(2, 5, 3)) throw new Exception("Published preview is not newer than upstream 2.5.3.");
Console.WriteLine($"PASS unauthenticated native preview discovery: {preview.TagName}; {preview.ZipUrl}");
var stable = await ReleaseClient.GetLatestReleaseGitHubAsync(false, timeout.Token);
Console.WriteLine(stable is null ? "PASS stable channel has no release; no exception" : $"PASS stable discovery: {stable.TagName}");

namespace Starshot
{
    internal static class AppConfig
    {
        public const string RepoApiBaseUrl = "https://api.github.com/repos/Yukikaze1945/Starshot-releases";
        public const string CdnBase = "https://starshot-release.cialo.site";
        public static bool EnableGithubApiNoProxy => false;
        public static string AppVersion => "2.5.3";
    }
}
