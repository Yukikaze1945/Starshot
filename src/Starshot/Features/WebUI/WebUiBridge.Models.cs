using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Starshot.Helpers;

namespace Starshot.Features.WebUI;

internal sealed partial class WebUiBridge
{
    private async Task<object?> ModelLibraryAsync(string method,JsonElement p,CancellationToken ct)
    {
        switch(method)
        {
            case "models.runtime.status":return OfflineTranslation.Status();
            case "models.runtime.load":await OfflineTranslation.LoadResidentAsync(ct);return OfflineTranslation.Status();
            case "models.runtime.unload":await OfflineTranslation.UnloadAsync(ct);return OfflineTranslation.Status();
            case "models.runtime.gpu":await OfflineTranslation.SetGpuAsync(p.GetProperty("enabled").GetBoolean(),ct);MainWindowChanged();return OfflineTranslation.Status();
            case "models.runtime.api":await OfflineTranslation.SetApiAsync(p.GetProperty("enabled").GetBoolean(),Range(p.GetProperty("port"),1024,65535),ct);return OfflineTranslation.Status();
            case "models.runtime.copyKey":ClipboardHelper.SetText(OfflineTranslation.ApiKeyForCopy());return null;
            case "models.list":
                var models=new List<DlcModelStore.Status>();foreach(var m in DlcModelStore.Catalog)models.Add(await DlcModelStore.Current.StatusAsync(m.Id,false,ct));
                return new{items=models,ocr=await OcrModelStatusAsync(ct,false),translationBackend=AppConfig.TranslationBackend,localLoaded=OfflineTranslation.Loaded,
                    downloadSource=AppConfig.GetValue("auto","ModelDownloadSource"),layoutModel=DocumentModelStore.SelectedLayout};
            case "models.layout.select":
                await DocumentModelStore.SelectLayoutAsync(Text(p,"id",40),ct);MainWindowChanged();return new{layoutModel=DocumentModelStore.SelectedLayout};
            case "models.source":
                string source=Text(p,"source",24);
                if(source is not("auto" or "modelscope" or "huggingface" or "hf-mirror" or "github"))throw new ArgumentException("未知下载来源。");
                AppConfig.SetValue(source,"ModelDownloadSource");return new{source};
            case "models.download":
                string id=Text(p,"id",40);var model=Array.Find(DlcModelStore.Catalog,m=>m.Id==id)??throw new ArgumentException("未知模型。");
                string preferred=AppConfig.GetValue("auto","ModelDownloadSource")!;
                if(preferred!="auto"&&!ModelDownloadSources.For(id,model.Url).Any(s=>s.Id==preferred&&s.Url is not null))preferred="auto";
                return await DlcModelStore.Current.StartAsync(id,ct,preferred);
            case "models.cancel":return await DlcModelStore.Current.CancelAsync(Text(p,"id",40));
            case "models.verify":return await DlcModelStore.Current.StatusAsync(Text(p,"id",40),true,ct);
            case "models.delete":return await DlcModelStore.Current.DeleteAsync(Text(p,"id",40),ct);
            case "models.import":
                string importId=Text(p,"id",40);
                string? path=p.TryGetProperty("path",out var chosen)?chosen.GetString():null;
                if(string.IsNullOrWhiteSpace(path))path=(importId=="ocr-small"||DocumentModelStore.Contains(importId))?await FileDialogHelper.PickFolderAsync(_window.WindowHandle)
                    :await FileDialogHelper.PickSingleFileAsync(_window.WindowHandle,("模型文件",importId=="index-q5km"?".gguf":".onnx"));
                if(string.IsNullOrWhiteSpace(path))return null;
                if(path.Length>32768)throw new ArgumentException("路径过长。");
                if(importId=="ocr-small"){OcrModelStore.Current.StartImport(path);return await OcrModelStatusAsync(ct);}
                return await DlcModelStore.Current.ImportAsync(importId,path,ct);
            case "models.scan":
                bool folder=p.TryGetProperty("chooseFolder",out var pick)&&pick.GetBoolean();
                if(folder)
                {
                    string? selected=await FileDialogHelper.PickFolderAsync(_window.WindowHandle);
                    return selected is null?new LocalModelFiles.ScanResult([],0,false):await LocalModelFiles.ScanAsync([selected],ct);
                }
                string home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return await LocalModelFiles.ScanAsync(new[]{Path.Combine(AppConfig.UserDataFolder,"Models"),Path.Combine(home,"Downloads"),
                    Path.Combine(home,".cache","huggingface","hub"),Path.Combine(home,".cache","modelscope","hub"),Path.Combine(home,".u2net"),
                    Environment.GetEnvironmentVariable("HF_HOME")??""},ct);
            case "models.translation":
                string backend=Text(p,"backend",16);if(backend is not("api" or "local"))throw new ArgumentException("未知翻译后端");
                if(backend=="local")await DlcModelStore.Current.RequireAsync("index-q5km",ct);
                else await OfflineTranslation.UnloadAsync(ct);
                AppConfig.TranslationBackend=backend;MainWindowChanged();return new{backend};
            case "models.unload":await OfflineTranslation.UnloadAsync(ct);return null;
            default:throw new ArgumentException("未知模型库操作");
        }
    }
    private static void MainWindowChanged()=>Features.ViewHost.MainWindow.BroadcastSettingsChanged();
}
