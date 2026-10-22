using System;
using System.Collections.Generic;
using System.Linq;

namespace Starshot.Helpers;

public sealed record ModelDownloadSource(string Id, string Label, string? Url, string? Note = null);

public static class ModelDownloadSources
{
    public static ModelDownloadSource[] For(string id, string original) => id switch
    {
        "formula-rapidlatex" => [new("hf-mirror","HF 镜像 · hf-mirror.com",Mirror(FormulaHf(original)),"社区镜像 · 四个文件与官方发布 SHA-256 相同"),new("huggingface","Hugging Face",FormulaHf(original),"社区镜像 · 与官方发布相同"),new("github","GitHub 官方来源",original),new("modelscope","魔搭 ModelScope",null,"暂无核对过的同版资源，可导入本地模型包")],
        "layout-doclayout-s" or "layout-doclayout-m" or "layout-doclayout-l" or "table-slanet-plus" => [new("hf-mirror","HF 镜像 · hf-mirror.com",Mirror(original),"第三方镜像"),new("huggingface","Hugging Face",original),new("modelscope","魔搭 ModelScope",null,"暂无核对过的同版资源，可导入本地模型包")],
        "index-q5km" => [
            new("modelscope", "魔搭 ModelScope", "https://modelscope.cn/models/IndexTeam/Index-Translate-2B-GGUF/resolve/3bb6e0ce9d1c2974cb59e18e9fd69bcb721b2a96/Index-Translate-2B.Q5_K_M.gguf"),
            new("hf-mirror", "HF 镜像 · hf-mirror.com", Mirror(original), "第三方镜像"),
            new("huggingface", "Hugging Face", original)],
        "u2netp" => [
            new("modelscope", "魔搭 ModelScope", null, "尚未找到可核对的同版文件"),
            new("hf-mirror", "HF 镜像 · hf-mirror.com", Mirror(U2NetHf), "第三方镜像 · 同版文件"),
            new("huggingface", "Hugging Face", U2NetHf, "社区镜像 · 登记哈希与原版相同"),
            new("github", "GitHub 原始来源", original)],
        _ => [new("github", "GitHub 官方来源", original),
            new("modelscope", "魔搭 ModelScope", null, "暂无核对过的同版资源，可导入本地文件"),
            new("huggingface", "Hugging Face", null, "暂无核对过的同版资源"),
            new("hf-mirror", "HF 镜像 · hf-mirror.com", null, "此模型当前发布于 GitHub")]
    };
    private static string FormulaHf(string original)=>"https://huggingface.co/inYourOwnBrowser/rapid-latex-ocr-onnx/resolve/0d0a23977b50433b07cdf95513ed7b7e45d3a761/"+System.IO.Path.GetFileName(new Uri(original).AbsolutePath);
    private const string U2NetHf = "https://huggingface.co/fofr/comfyui/resolve/b1d7aa4aa716f042673b8f5b4b8ed4fec88631b4/rembg/u2netp.onnx";
    private static string Mirror(string url) => url.Replace("https://huggingface.co/", "https://hf-mirror.com/", StringComparison.Ordinal);
    public static IEnumerable<ModelDownloadSource> Resolve(string id, string original, string selected)
    {
        var sources = For(id, original);
        if (selected == "auto") return sources.Where(s => s.Url is not null);
        var chosen = sources.SingleOrDefault(s => s.Id == selected && s.Url is not null)
            ?? throw new ArgumentException("该模型暂无此下载源，请选择可用来源或导入本地文件。");
        return [chosen];
    }
}
