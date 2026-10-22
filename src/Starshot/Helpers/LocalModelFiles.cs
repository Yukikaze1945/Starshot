using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Starshot.Helpers;

/// <summary>Bounded data-only inspection. Never creates a model session or executes downloaded code.</summary>
public static class LocalModelFiles
{
    public sealed record Candidate(string Id, string Path, long Bytes);
    public sealed record ScanResult(Candidate[] Items, int Visited, bool Limited);
    public static Task<ScanResult> ScanAsync(IEnumerable<string> roots, CancellationToken ct) => Task.Run(() =>
    {
        var results = new List<Candidate>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>(roots.Where(Directory.Exists).Select(p => (System.IO.Path.GetFullPath(p), 0)));
        int visited = 0; bool limited = false; var clock = System.Diagnostics.Stopwatch.StartNew();
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (visited >= 6000 || results.Count >= 100 || clock.Elapsed > TimeSpan.FromSeconds(12)) { limited = true; break; }
            var (directory, depth) = queue.Dequeue(); if (!seen.Add(directory)) continue;
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0 || System.IO.Path.GetFileName(directory).StartsWith(".stage-",StringComparison.Ordinal)) continue;
                if(File.Exists(System.IO.Path.Combine(directory,"encoder.onnx"))&&File.Exists(System.IO.Path.Combine(directory,"decoder.onnx"))&&File.Exists(System.IO.Path.Combine(directory,"image_resizer.onnx"))&&File.Exists(System.IO.Path.Combine(directory,"tokenizer.json")))results.Add(new("formula-rapidlatex",directory,0));
                foreach(string variant in new[]{"s","m","l"})
                {
                    string id="layout-doclayout-"+variant;
                    string named=System.IO.Path.Combine(directory,variant=="s"?"pp_doclayout_s.onnx":$"PP-DocLayout-{variant.ToUpperInvariant()}_infer.onnx");
                    string canonical=System.IO.Path.Combine(directory,"layout.onnx");
                    long expected=variant=="s"?4917852:variant=="m"?23496395:129398697;
                    if(File.Exists(named)||(File.Exists(canonical)&&(new FileInfo(canonical).Length==expected
                        ||directory.Split(System.IO.Path.DirectorySeparatorChar,System.IO.Path.AltDirectorySeparatorChar).Contains(id,StringComparer.OrdinalIgnoreCase))))
                        results.Add(new(id,directory,File.Exists(named)?new FileInfo(named).Length:new FileInfo(canonical).Length));
                }
                string tableConfig=System.IO.Path.Combine(directory,File.Exists(System.IO.Path.Combine(directory,"table.yml"))?"table.yml":"inference.yml");
                if((File.Exists(System.IO.Path.Combine(directory,"table.onnx"))||File.Exists(System.IO.Path.Combine(directory,"inference.onnx")))&&File.Exists(tableConfig)&&new FileInfo(tableConfig).Length<65536&&File.ReadAllText(tableConfig).Contains("SLANet_plus"))results.Add(new("table-slanet-plus",directory,0));
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    ct.ThrowIfCancellationRequested(); if (++visited > 6000) { limited = true; break; }
                    string name = System.IO.Path.GetFileName(file);
                    string? id = name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                        && name.Contains("Index-Translate-2B", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("mmproj", StringComparison.OrdinalIgnoreCase) ? "index-q5km"
                        : name.Equals("u2netp.onnx", StringComparison.OrdinalIgnoreCase) ? "u2netp" : null;
                    if (id is not null) results.Add(new(id, System.IO.Path.GetFullPath(file), new FileInfo(file).Length));
                    if ((name is "small_det.onnx" or "det.onnx") && OcrAssetsPresent(directory))
                        if (!results.Any(c => c.Id == "ocr-small" && c.Path == directory)) results.Add(new("ocr-small", directory, 0));
                }
                if (depth < 7 && !limited)
                    foreach (var sub in Directory.EnumerateDirectories(directory))
                    { if (queue.Count >= 2000) { limited = true; break; } queue.Enqueue((sub, depth + 1)); }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new ScanResult(results.Distinct().Take(100).ToArray(), visited, limited);
    }, ct);
    private static bool OcrAssetsPresent(string path) =>
        (File.Exists(System.IO.Path.Combine(path, "small_rec.onnx")) || File.Exists(System.IO.Path.Combine(path, "rec.onnx")))
        && (File.Exists(System.IO.Path.Combine(path, "rec_keys.txt")) || File.Exists(System.IO.Path.Combine(path, "dict.txt")));

    internal static string? CheckFormat(string path, string kind)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (kind == "dictionary")
            {
                if (stream.Length is < 2 or > 5000000) return "字典为空或过大。";
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
                string text = reader.ReadToEnd();
                return text.Contains('\n') && !text.TrimStart().StartsWith('<') ? null : "不是有效的 OCR 字典。";
            }
            if (kind == "index-q5km") return CheckGguf(stream);
            if (kind is "u2netp" or "onnx") return CheckOnnx(stream) ? null : "不是可读取的 ONNX 模型（可能为下载错误页或不完整文件）。";
            return "未识别的本地模型格式。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { return "无法读取模型：" + ex.Message; }
    }
    private static string? CheckGguf(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, true);
        if (r.ReadUInt32() != 0x46554747 || r.ReadUInt32() is not (2 or 3)) return "不是支持的 GGUF v2/v3 文件。";
        ulong tensors = r.ReadUInt64(), count = r.ReadUInt64();
        if (tensors is 0 or > 100000 || count is 0 or > 100000) return "GGUF 目录无效。";
        string architecture = "", name = ""; uint alignment=32;
        string Text() { ulong n = r.ReadUInt64(); if (n > 16_000_000 || n > (ulong)(stream.Length-stream.Position) || stream.Position+(long)n>32_000_000) throw new InvalidDataException("GGUF 字符串截断或元数据过大。"); return Encoding.UTF8.GetString(r.ReadBytes((int)n)); }
        void Skip(uint type, int depth = 0)
        {
            if (depth > 1) throw new InvalidDataException("GGUF 嵌套数组不受支持。");
            if (type == 8) { _ = Text(); return; }
            if (type == 9) { uint t = r.ReadUInt32(); ulong n = r.ReadUInt64(); if (n > 2_000_000) throw new InvalidDataException("GGUF 数组过大。"); for (ulong i=0;i<n;i++) Skip(t,depth+1); return; }
            int size = type switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8, _ => throw new InvalidDataException("GGUF 元数据类型无效。") };
            if (stream.Position + size > stream.Length) throw new EndOfStreamException(); stream.Position += size;
        }
        for (ulong i=0;i<count;i++)
        {
            if (stream.Position > 32_000_000) return "GGUF 元数据超过检查上限。";
            string key = Text(); uint type = r.ReadUInt32();
            if (key is "general.architecture" or "general.name" && type == 8)
            { string value = Text(); if (key == "general.architecture") architecture=value; else name=value; }
            else if(key=="general.alignment"&&type==4)alignment=r.ReadUInt32();
            else Skip(type);
        }
        if (architecture != "qwen35" || !name.Contains("Index", StringComparison.OrdinalIgnoreCase)
            || !name.Contains("Translate", StringComparison.OrdinalIgnoreCase) || !name.Contains("2B",StringComparison.OrdinalIgnoreCase))
            return "请选择 Index-Translate-2B 的 qwen35 GGUF，而非其他模型或 mmproj。";
        ulong required = 0;
        for (ulong i=0;i<tensors;i++)
        {
            _ = Text(); uint dimensions = r.ReadUInt32(); if (dimensions is 0 or > 4) return "GGUF 张量维数无效。";
            ulong elements=1;for (int d=0;d<dimensions;d++){ulong n=r.ReadUInt64();if(n==0)return "GGUF 空张量。";elements=checked(elements*n);}
            uint type=r.ReadUInt32();ulong offset=r.ReadUInt64();
            (int block,int bytes)=type switch{0=>(1,4),1=>(1,2),2=>(32,18),3=>(32,20),6=>(32,22),7=>(32,24),8=>(32,34),
                9=>(32,40),10=>(256,84),11=>(256,110),12=>(256,144),13=>(256,176),14=>(256,210),15=>(256,292),_=>(1,1)};
            required=Math.Max(required,checked(offset+((elements+(ulong)block-1)/(ulong)block)*(ulong)bytes));
        }
        if(alignment is 0 or >4096 || (alignment&(alignment-1))!=0)return "GGUF 对齐值无效。";
        long dataStart=checked((stream.Position+alignment-1)&~((long)alignment-1));
        return dataStart<=stream.Length && required <= (ulong)(stream.Length-dataStart) ? null : "GGUF 张量数据不完整。";
    }
    private static bool CheckOnnx(Stream s)
    {
        if (s.Length is < 16 or > 2147483648L) return false;
        ulong Varint() { ulong v=0; for(int i=0;i<10;i++){int b=s.ReadByte();if(b<0)throw new EndOfStreamException();v|=(ulong)(b&127)<<(7*i);if(b<128)return v;}throw new InvalidDataException("Invalid protobuf"); }
        bool ir=false, graph=false; int fields=0;
        while (s.Position<s.Length && fields++<10000)
        {
            ulong key=Varint(); int field=(int)(key>>3),wire=(int)(key&7);if(field==0)return false;
            if(wire==0){ulong value=Varint();if(field==1)ir=value is >0 and <=20;}
            else if(wire==2){ulong length=Varint();if(length>(ulong)(s.Length-s.Position))return false;if(field==7)graph=length>0;s.Position+=(long)length;}
            else if(wire is 1 or 5){s.Position+=wire==1?8:4;if(s.Position>s.Length)return false;}
            else return false;
        }
        return ir && graph && s.Position==s.Length;
    }
}
