using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Graphics.Canvas;
using Starshot.Features.Codec;

// Ultra HDR 色彩管线回归测试。
// 一张 160x64 的色块图（R16G16B16A16Float，模拟 HDR 桌面抓帧）分别走「修改前」「修改后」两条管线，
// 编成真实 Ultra HDR 文件，再用 libultrahdr 解回来逐块比对。
// 表里的数全部来自 GPU effect graph + libultrahdr 编解码，不是 CPU 复刻实现。

Console.OutputEncoding = Encoding.UTF8;
var report = new StringBuilder();
void Log(string line = "")
{
    Console.WriteLine(line);
    report.AppendLine(line);
}

if (args.Length > 0 && args[0] == "icc")
{
    // 剥段逻辑是纯字节操作，不碰 GPU，放在建设备之前，无显卡环境也能跑。
    return IccStripTest.Run(AppContext.BaseDirectory);
}

var device = CanvasDevice.GetSharedDevice();
if (args.Length > 0 && args[0] == "capacity")
{
    return CapacityTest.Run(device);
}
if (args.Length > 0 && args[0] == "dump")
{
    Dump.Run(device, Path.Combine(AppContext.BaseDirectory, "uhdr-dump.txt"));
    return 0;
}
Log($"UhdrColorTest  device={device}");
Log(
    $"working gamut = {UhdrColor.WorkingPrimaries.Id} / {UhdrColor.WorkingColorGamut}  gain offset={UhdrColor.GainOffset}"
);
Log(
    $"working luma = ({UhdrColor.WorkingLuma.X:F7},{UhdrColor.WorkingLuma.Y:F7},{UhdrColor.WorkingLuma.Z:F7})  sum={(UhdrColor.WorkingLuma.X + UhdrColor.WorkingLuma.Y + UhdrColor.WorkingLuma.Z):F7}"
);
Log();

var rows = new List<Row>();
var diag = new List<string>();
foreach (float sdrWhiteLevel in new[] { 80f, 320f })
{
    using var image = Harness.MakePatchImage(device);
    Dictionary<string, System.Numerics.Vector3> workings = Harness.MeasureWorkings(device, image);
    // 两条管线各自的增益两端：改前分母取 toneMapEffect、分子取原始 scRGB；改后两端都归一到同一个参考白。
    var chainNew = Harness.MeasureChain(device, image, sdrWhiteLevel, workingGamut: true);
    var chainOld = Harness.MeasureChain(device, image, sdrWhiteLevel, workingGamut: false);
    string legacyFailure = null;
    foreach (string arm in new[] { "old", "new" })
    {
        byte[] file;
        try
        {
            file = arm == "old"
                ? Harness.EncodeLegacy(image, sdrWhiteLevel)
                : Harness.EncodeNew(image, sdrWhiteLevel);
        }
        catch (Exception e) when (arm == "old")
        {
            // 改前实现在这里直接崩：所有增益都 <1，被 MathF.Max(…,1) 夹成 capacity max = 1，
            // libultrahdr 再以 capacity max 不大于 min 拒绝。这是分母取错节点的后果，照实记录。
            legacyFailure = $"{e.GetType().Name}: {e.Message}";
            Log($"!! old arm @ {sdrWhiteLevel:F0}nits 编码失败：{legacyFailure}");
            continue;
        }
        Dictionary<string, System.Numerics.Vector3> denominator = arm == "old" ? chainOld.Tone : chainNew.SdrLinear;
        rows.AddRange(
            Harness.Measure(
                device,
                arm,
                file,
                sdrWhiteLevel,
                workings,
                denominator,
                arm == "old" ? null : chainNew.HdrNormalized,
                diag
            )
        );
        File.WriteAllBytes(
            Path.Combine(AppContext.BaseDirectory, $"uhdr-{arm}-{sdrWhiteLevel:F0}nits.jpg"),
            file
        );
    }

    Log(
        $"===== maxCLL={Harness.MaxCll:F0}nits   SDR white={sdrWhiteLevel:F0}nits   (scRGB 里 SDR 白 = {sdrWhiteLevel / 80f:F3})   patch={Harness.Patch}px ====="
    );
    Log(
        "patch                          arm  input scRGB(linear)     working(base gamut)   base8(8bit)     recon(nits)     ref(nits)      maxRelErr  chroma-ref chroma-rec"
    );
    Log(new string('-', 168));
    foreach (string name in Harness.Swatches().Select(s => s.Name))
    {
        foreach (string arm in new[] { "old", "new" })
        {
            Row row = rows.Find(r => r.SdrWhiteLevel == sdrWhiteLevel && r.Arm == arm && r.Name == name);
            if (row is null)
            {
                Log($"{name,-32} {arm,-4} 该臂在此白电平下没有产出文件（编码失败）");
                continue;
            }
            Log(
                $"{name,-32} {arm,-4} {Harness.Fmt(row.Input),-24} {Harness.Fmt(row.Working),-24} {Harness.FmtBase(row.Base8),-16} {Harness.Fmt(row.ReconNits),-16} {Harness.Fmt(row.RefNits),-16} {row.MaxRelError,8:P1}  {row.RefChroma,7:F3}  {row.RecChroma,7:F3}"
            );
        }
        Log(new string('-', 168));
    }
    Log();

    Log(
        $"===== 增益两端量程核对   SDR white={sdrWhiteLevel:F0}nits   （「分母改后」==「InvOetf(base8)」才自洽；「期望 gain」应等于「实测 gain」）====="
    );
    Log(
        "patch                            分子改后(hdrNorm)     分母改后(SdrLinear)   InvOetf(base8_new)  期望gain  实测gain  分母改前(toneMap)   改前期望gain"
    );
    Log(new string('-', 190));
    foreach (string name in Harness.Swatches().Select(s => s.Name))
    {
        Row o = rows.Find(r => r.SdrWhiteLevel == sdrWhiteLevel && r.Arm == "old" && r.Name == name);
        Row n = rows.Find(r => r.SdrWhiteLevel == sdrWhiteLevel && r.Arm == "new" && r.Name == name);
        System.Numerics.Vector3 hdrNew = chainNew.HdrNormalized[name];
        System.Numerics.Vector3 denNew = chainNew.SdrLinear[name];
        System.Numerics.Vector3 denOld = chainOld.Tone[name];
        // 口径与断言一致：分子最大通道 / 文件里存着的 base
        string F(float v) => float.IsNaN(v) ? "n/a" : $"{v:F3}";
        System.Numerics.Vector3 legacyNum = Harness.LegacyNumerator((o?.Input ?? n?.Input) ?? default);
        Log(
            $"{name,-32} {Harness.Fmt(hdrNew),-24} {Harness.Fmt(denNew),-24} {Harness.Fmt(n?.StoredLinear ?? default),-24} {F(Harness.ExpectedGain(hdrNew, n?.StoredLinear ?? default)),-10} {F(Harness.AppliedGain(hdrNew, n is null ? default : n.ReconNits / sdrWhiteLevel, n?.StoredLinear ?? default)),-10} {Harness.Fmt(denOld),-24} {F(Harness.ExpectedGain(legacyNum, denOld)),-8}"
        );
    }
    Log(new string('-', 190));
    Log();
}

Log("decoded raw geometry / sampling diagnostics:");
foreach (string line in diag)
{
    Log($"  {line}");
}
Log();
Log("assertions:");
int failures = Harness.RunAssertions(rows, diag, Log);
Log();
Log(failures == 0 ? "ALL ASSERTIONS PASSED" : $"{failures} ASSERTION(S) FAILED");
string path = Path.Combine(AppContext.BaseDirectory, "uhdr-color-report.txt");
File.WriteAllText(path, report.ToString(), Encoding.UTF8);
Log($"report -> {path}");
return failures;
