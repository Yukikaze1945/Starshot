using System;
using System.Collections.Generic;
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
            case "models.list":
                var models=new List<DlcModelStore.Status>();foreach(var m in DlcModelStore.Catalog)models.Add(await DlcModelStore.Current.StatusAsync(m.Id,false,ct));
                return new{items=models,ocr=await OcrModelStatusAsync(ct,false),translationBackend=AppConfig.TranslationBackend,localLoaded=OfflineTranslation.Loaded};
            case "models.download":return await DlcModelStore.Current.StartAsync(Text(p,"id",40),ct);
            case "models.cancel":return await DlcModelStore.Current.CancelAsync(Text(p,"id",40));
            case "models.verify":return await DlcModelStore.Current.StatusAsync(Text(p,"id",40),true,ct);
            case "models.delete":return await DlcModelStore.Current.DeleteAsync(Text(p,"id",40),ct);
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
