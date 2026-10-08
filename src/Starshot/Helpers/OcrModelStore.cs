using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Sdcb.SimdPaddleOCR.ModelProvider;
using Sdcb.SimdPaddleOCR.Models.TextLineOrientation;
using Serilog;

namespace Starshot.Helpers;

/// <summary>Data-only, pinned official Small resources. No assemblies are downloaded or loaded.</summary>
public sealed class OcrModelStore
{
    public const string Revision = "315e4fff4466260fd8f09cd4d53a70ec3b63f65c";
    public const string ModelVersion = "1.0.0";
    public sealed record Asset(string File, string Source, long Bytes, string Sha256, PaddleOcrModelKind Kind);
    public static readonly Asset[] Assets =
    [
        new("det.onnx", "small_det.onnx", 9880512, "D73E0058B7A8086BBD57F3D10B8BCD4FF95363F67E06E2762B5E814FE9C9410E", PaddleOcrModelKind.Detection),
        new("rec.onnx", "small_rec.onnx", 21159378, "5435FD747C9E0EFE15A96D0B378D5BD157E9492ED8FD80EDF08F30D02FA24634", PaddleOcrModelKind.Recognition),
        new("dict.txt", "rec_keys.txt", 74947, "B5F2BFE2BDD9448429E3E82B51C789775D9B42F2403D082B00662EB77E401C5D", PaddleOcrModelKind.Dictionary),
    ];
    public static long TotalBytes => Assets.Sum(a => a.Bytes);
    public static string SourceUrl(Asset asset) => $"https://raw.githubusercontent.com/sdcb/SimdPaddleOCR/{Revision}/models/{asset.Source}";
    public sealed record Status(string State, bool Installed, long DownloadedBytes, long TotalBytes,
        string? Error, string Directory, string Version);
    private static readonly HttpClient Client = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) })
        { Timeout = Timeout.InfiniteTimeSpan };
    public static OcrModelStore Current { get; } = new(
        () => Path.Combine(AppConfig.UserDataFolder, "Models", "OCR"), Client, OcrHelper.MutateSmallFilesAsync);

    private readonly Func<string> _root;
    private readonly HttpClient _client;
    private readonly Func<Action, CancellationToken, Task> _publish;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _operations = new(1);
    private CancellationTokenSource? _downloadCancellation;
    private Task? _download;
    private bool _mutating;
    private string? _fingerprint;
    private string _state = "notInstalled";
    private string? _error;
    private bool _installed;
    private long _downloaded;
    public string DirectoryPath => Path.Combine(Path.GetFullPath(_root()), "ppocrv6-small-1.0.0");

    // Explicit injection permits offline/error/cancellation tests without touching user data.
    internal OcrModelStore(Func<string> root, HttpClient client, Func<Action, CancellationToken, Task> publish)
    { _root = root; _client = client; _publish = publish; }

    private Status Snapshot()
    { lock (_sync) return new(_state, _installed, _downloaded, TotalBytes, _error, DirectoryPath, ModelVersion); }
    private void Set(string state, bool installed, string? error = null)
    { lock (_sync) { _state = state; _installed = installed; _error = error; } }

    public async Task<Status> GetStatusAsync(CancellationToken ct = default, bool force = false)
    {
        lock (_sync) if (_mutating) return Snapshot();
        // Never queue behind a download while the caller may hold EngineGate:
        // publication acquires that gate. Providers independently verify each open.
        if (!await _operations.WaitAsync(0, ct).ConfigureAwait(false)) return Snapshot();
        try
        {
            lock (_sync) if (_mutating) return Snapshot();
            string? signature = Fingerprint(DirectoryPath);
            lock (_sync) if (!force && signature is not null && signature == _fingerprint && _installed) return Snapshot();
            bool valid = await VerifyDirectoryAsync(DirectoryPath, ct).ConfigureAwait(false);
            lock (_sync)
            {
                if (_mutating) return Snapshot();
                _fingerprint = valid ? signature : null;
                if (valid) { _state = "installed"; _installed = true; _error = null; }
                else
                {
                    _installed = false;
                    if (Directory.Exists(DirectoryPath)) { _state = "corrupt"; _error = "Small 文件缺失或校验失败，将使用内置 Tiny。请删除后重新下载。"; }
                    else if (_state is not ("error" or "canceled")) { _state = "notInstalled"; _error = null; }
                }
            }
            return Snapshot();
        }
        finally { _operations.Release(); }
    }

    internal async Task<PaddleOcrModelBundle?> GetVerifiedBundleAsync(CancellationToken ct)
    {
        var status = await GetStatusAsync(ct, force: true).ConfigureAwait(false);
        if (!status.Installed || status.State != "installed") return null;
        return new("PP-OCRv6_small", "zh",
            new LocalProvider(DirectoryPath, Assets[0]), new LocalProvider(DirectoryPath, Assets[1]),
            new LocalProvider(DirectoryPath, Assets[2]), TextLineOrientationModel.Provider);
    }

    public Status StartDownload()
    {
        lock (_sync)
        {
            if (_mutating) throw new InvalidOperationException("模型操作正在进行，请稍后重试。");
            _mutating = true; _state = "downloading"; _installed = false; _error = null; _downloaded = 0;
            _downloadCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var token = _downloadCancellation.Token;
            _download = Task.Run(() => DownloadAsync(token));
            return Snapshot();
        }
    }

    public void CancelDownload() { lock (_sync) _downloadCancellation?.Cancel(); }
    internal Task WaitForDownloadAsync() { lock (_sync) return _download ?? Task.CompletedTask; }

    private async Task DownloadAsync(CancellationToken ct)
    {
        string? stage = null;
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            stage = Path.Combine(Path.GetFullPath(_root()), ".install-" + Guid.NewGuid().ToString("N"));
            ct.ThrowIfCancellationRequested();
            if (await VerifyDirectoryAsync(DirectoryPath, ct).ConfigureAwait(false))
            { Set("installed", true); return; }
            Directory.CreateDirectory(stage);
            foreach (var asset in Assets)
            {
                using var response = await _client.GetAsync(SourceUrl(asset), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long length && length != asset.Bytes)
                    throw new InvalidDataException("官方模型长度不符，下载未安装。请重试。");
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = new FileStream(Path.Combine(stage, asset.File), FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 65536, FileOptions.Asynchronous);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                long bytes = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    bytes += read;
                    if (bytes > asset.Bytes) throw new InvalidDataException("官方模型长度超出预期，下载未安装。");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    lock (_sync) _downloaded += read;
                }
                if (bytes != asset.Bytes || Convert.ToHexString(hash.GetHashAndReset()) != asset.Sha256)
                    throw new InvalidDataException("Small 模型 SHA-256 校验失败，下载未安装。请重试。");
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            Set("validating", false);
            if (!await VerifyDirectoryAsync(stage, ct).ConfigureAwait(false)) throw new InvalidDataException("安装前磁盘校验失败。");
            // Wait for an in-flight OCR before replacing/removing any model directory.
            await _publish(() => Publish(stage), ct).ConfigureAwait(false);
            lock (_sync) { _fingerprint = Fingerprint(DirectoryPath); _downloaded = TotalBytes; }
            Set("installed", true);
            Log.Information("[OCR DLC] Small 1.0.0 installed; bytes={Bytes}; SHA-256 verified", TotalBytes);
        }
        catch (OperationCanceledException) { Set("canceled", false, "下载已取消，继续使用内置 Tiny。"); }
        catch (Exception ex)
        {
            Set("error", false, ex is InvalidDataException ? ex.Message : "Small 下载失败，请检查网络后重试；继续使用内置 Tiny。");
            Log.Warning(ex, "[OCR DLC] Small download/install failed");
        }
        finally
        {
            try { if (stage is not null) RemoveOwnedDirectory(stage); }
            catch (Exception ex) { Log.Warning(ex, "[OCR DLC] Could not remove incomplete download"); }
            lock (_sync) { _mutating = false; _downloadCancellation?.Dispose(); _downloadCancellation = null; }
            _operations.Release();
        }
    }

    private void Publish(string stage)
    {
        string old = Path.Combine(Path.GetFullPath(_root()), ".old-" + Guid.NewGuid().ToString("N"));
        bool moved = false;
        try
        {
            if (Directory.Exists(DirectoryPath)) { Directory.Move(DirectoryPath, old); moved = true; }
            Directory.Move(stage, DirectoryPath); // Same-volume atomic publication of the complete bundle.
        }
        catch
        {
            if (moved && !Directory.Exists(DirectoryPath)) Directory.Move(old, DirectoryPath);
            throw;
        }
        if (moved)
        {
            try { RemoveOwnedDirectory(old); }
            catch (Exception ex) { Log.Warning(ex, "[OCR DLC] Installed Small; previous bundle cleanup failed"); }
        }
    }

    public async Task DeleteAsync(CancellationToken ct = default)
    {
        CancelDownload();
        await WaitForDownloadAsync().ConfigureAwait(false);
        lock (_sync)
        {
            if (_mutating) throw new InvalidOperationException("模型操作正在进行，请稍后重试。");
            _mutating = true; _state = "deleting"; _installed = false;
        }
        bool acquired = false;
        try
        {
            await _operations.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            await _publish(() => RemoveOwnedDirectory(DirectoryPath), ct).ConfigureAwait(false);
            lock (_sync) { _fingerprint = null; _downloaded = 0; }
            Set("notInstalled", false);
        }
        finally { lock (_sync) _mutating = false; if (acquired) _operations.Release(); }
    }

    private static string? Fingerprint(string directory)
    {
        try { return string.Join("/", Assets.Select(a => { var f = new FileInfo(Path.Combine(directory, a.File)); return f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "missing"; })); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static async Task<bool> VerifyDirectoryAsync(string directory, CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
            foreach (var asset in Assets)
            {
                string path = Path.Combine(directory, asset.File);
                if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length != asset.Bytes) return false;
                await using var file = File.OpenRead(path);
                if (Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)) != asset.Sha256) return false;
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void RemoveOwnedDirectory(string path)
    {
        string root = Path.GetFullPath(_root()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        path = Path.GetFullPath(path);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetDirectoryName(path) + Path.DirectorySeparatorChar != root)
            throw new InvalidOperationException("Unsafe OCR model cleanup path.");
        if (!Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || Directory.EnumerateDirectories(path).Any())
            throw new InvalidDataException("Unexpected directory in OCR model files.");
        foreach (string file in Directory.EnumerateFiles(path))
        {
            if (!Assets.Any(a => a.File == Path.GetFileName(file))) throw new InvalidDataException("Unexpected file in OCR model directory.");
            File.Delete(file);
        }
        Directory.Delete(path, recursive: false);
    }

    private sealed class LocalProvider(string directory, Asset asset) : IPaddleOcrModelProvider
    {
        public string Name => "PP-OCRv6_small_" + asset.Kind;
        public PaddleOcrModelKind Kind => asset.Kind;
        public string Format => asset.Kind == PaddleOcrModelKind.Dictionary ? "utf-8" : "onnx";
        public string? LanguageCode => "zh";
        public string? Version => "v6-small-1.0.0";
        public Stream OpenRead()
        {
            string path = Path.Combine(directory, asset.File);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Model resource cannot be a link.");
            var stream = File.OpenRead(path);
            try
            {
                if (stream.Length != asset.Bytes || Convert.ToHexString(SHA256.HashData(stream)) != asset.Sha256)
                    throw new InvalidDataException("Small 模型资源校验失败。");
                stream.Position = 0;
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
        public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(OpenRead()); }
    }
}
