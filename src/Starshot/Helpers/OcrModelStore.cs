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
        string? Error, string Directory, string Version, string? Warning, string? ActiveSource, ModelDownloadSource[] Sources, string? ActualHashes);
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
    private string? _warning, _activeSource, _actualHashes;
    private bool _installed;
    private long _downloaded;
    public string DirectoryPath => Path.Combine(Path.GetFullPath(_root()), "ppocrv6-small-1.0.0");

    // Explicit injection permits offline/error/cancellation tests without touching user data.
    internal OcrModelStore(Func<string> root, HttpClient client, Func<Action, CancellationToken, Task> publish)
    { _root = root; _client = client; _publish = publish; }

    private Status Snapshot()
    { lock (_sync) return new(_state, _installed, _downloaded, TotalBytes, _error, DirectoryPath, ModelVersion,
        _warning,_activeSource,ModelDownloadSources.For("ocr-small",SourceUrl(Assets[0])),_actualHashes); }
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
            var inspection = await VerifyDirectoryAsync(DirectoryPath, ct).ConfigureAwait(false);
            bool valid = inspection.Valid;
            lock (_sync)
            {
                if (_mutating) return Snapshot();
                _fingerprint = valid ? signature : null;
                if (valid) { _state = "installed"; _installed = true; _error = null; _warning=inspection.Warning; _actualHashes=inspection.Hashes; }
                else
                {
                    _installed = false;
                    _warning=_actualHashes=null;
                    if (Directory.Exists(DirectoryPath)) { _state = "corrupt"; _error = "Small 文件缺失或格式无法读取，将使用内置 Tiny。请重新导入或下载。"; }
                    else if (_state is not ("error" or "canceled")) { _state = "notInstalled"; _error = null; }
                }
            }
            return Snapshot();
        }
        finally { _operations.Release(); }
    }

    internal async Task<PaddleOcrModelBundle?> GetVerifiedBundleAsync(CancellationToken ct,bool force=true)
    {
        var status = await GetStatusAsync(ct, force: force).ConfigureAwait(false);
        if (!status.Installed || status.State != "installed") return null;
        return new("PP-OCRv6_small", "zh",
            new LocalProvider(DirectoryPath, Assets[0]), new LocalProvider(DirectoryPath, Assets[1]),
            new LocalProvider(DirectoryPath, Assets[2]), TextLineOrientationModel.Provider);
    }

    public Status StartDownload(string source = "auto") => StartOperation(source, null);
    public Status StartImport(string directory) => StartOperation("auto", Path.GetFullPath(directory));
    private Status StartOperation(string source, string? directory)
    {
        if (directory is null) _ = ModelDownloadSources.Resolve("ocr-small",SourceUrl(Assets[0]),source).ToArray();
        else if(!Directory.Exists(directory))throw new DirectoryNotFoundException("请选择包含 DET、REC 和字典的文件夹。");
        lock (_sync)
        {
            if (_mutating) throw new InvalidOperationException("模型操作正在进行，请稍后重试。");
            _mutating = true; _state = directory is null?"downloading":"importing"; _installed = false; _error = _warning = null; _downloaded = 0;
            _activeSource=directory is null?"GitHub 官方来源":"本地文件夹";
            _downloadCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var token = _downloadCancellation.Token;
            _download = Task.Run(() => DownloadAsync(token,source,directory));
            return Snapshot();
        }
    }

    public void CancelDownload() { lock (_sync) _downloadCancellation?.Cancel(); }
    internal Task WaitForDownloadAsync() { lock (_sync) return _download ?? Task.CompletedTask; }

    private async Task DownloadAsync(CancellationToken ct,string selectedSource,string? localDirectory)
    {
        string? stage = null;
        await _operations.WaitAsync().ConfigureAwait(false);
        try
        {
            stage = Path.Combine(Path.GetFullPath(_root()), ".install-" + Guid.NewGuid().ToString("N"));
            ct.ThrowIfCancellationRequested();
            if (localDirectory is null && (await VerifyDirectoryAsync(DirectoryPath, ct).ConfigureAwait(false)).Valid)
            { Set("installed", true); return; }
            Directory.CreateDirectory(stage);
            foreach (var asset in Assets)
            {
                using var response = localDirectory is null ? await _client.GetAsync(
                    ModelDownloadSources.Resolve("ocr-small",SourceUrl(asset),selectedSource).First().Url,
                    HttpCompletionOption.ResponseHeadersRead,ct).ConfigureAwait(false) : null;
                response?.EnsureSuccessStatusCode();
                string localPath=localDirectory is null?"":Path.Combine(localDirectory,asset.File);
                if(localDirectory is not null && !File.Exists(localPath))localPath=Path.Combine(localDirectory,asset.Source);
                await using Stream input = response is not null ? await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false) : File.OpenRead(localPath);
                await using var output = new FileStream(Path.Combine(stage, asset.File), FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 65536, FileOptions.Asynchronous);
                var buffer = new byte[65536];
                long bytes = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    bytes += read;
                    if (bytes > 128*1024*1024) throw new InvalidDataException("模型资源超过支持的大小。");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    lock (_sync) _downloaded += read;
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            Set("validating", false);
            var inspection=await VerifyDirectoryAsync(stage,ct).ConfigureAwait(false);
            if (!inspection.Valid) throw new InvalidDataException("模型格式无效或文件不完整。无法加载该 OCR 包。");
            // Wait for an in-flight OCR before replacing/removing any model directory.
            await _publish(() => Publish(stage), ct).ConfigureAwait(false);
            lock (_sync) { _fingerprint = Fingerprint(DirectoryPath); _warning=inspection.Warning; _actualHashes=inspection.Hashes; }
            Set("installed", true);
            Log.Information("[OCR DLC] Small installed; bytes={Bytes}; verificationWarning={Warning}", _downloaded,_warning);
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
            lock (_sync) { _fingerprint = null; _downloaded = 0; _warning=_activeSource=_actualHashes=null; }
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

    private static async Task<(bool Valid,string? Warning,string? Hashes)> VerifyDirectoryAsync(string directory, CancellationToken ct)
    {
        try
        {
            bool mismatch=false;
            var hashes=new System.Collections.Generic.List<string>();
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return (false,null,null);
            foreach (var asset in Assets)
            {
                string path = Path.Combine(directory, asset.File);
                if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return (false,null,null);
                await using var file = File.OpenRead(path);
                string hash=Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));hashes.Add(asset.File+"  "+hash);
                bool match=file.Length==asset.Bytes && hash==asset.Sha256;
                if(!match)
                {
                    mismatch=true;
                    if(LocalModelFiles.CheckFormat(path,asset.Kind==PaddleOcrModelKind.Dictionary?"dictionary":"onnx") is not null)return(false,null,null);
                }
            }
            return (true,mismatch?"SHA-256 与登记版本不一致，仍可使用；DET、REC、字典的兼容性将在识别时检查。":null,string.Join("\n",hashes));
        }
        catch (IOException) { return (false,null,null); }
        catch (UnauthorizedAccessException) { return (false,null,null); }
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
                bool match=stream.Length==asset.Bytes && Convert.ToHexString(SHA256.HashData(stream))==asset.Sha256;
                if (!match && LocalModelFiles.CheckFormat(path,asset.Kind==PaddleOcrModelKind.Dictionary?"dictionary":"onnx") is {} error)
                    throw new InvalidDataException(error);
                stream.Position = 0;
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }
        public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(OpenRead()); }
    }
}
