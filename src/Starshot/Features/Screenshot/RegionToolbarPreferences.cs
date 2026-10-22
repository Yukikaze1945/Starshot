using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Starshot.Features.Screenshot;

internal sealed record RegionToolbarCustomization(RegionToolbarCommand[] Main, RegionToolbarCommand[] Secondary)
{
    internal RegionToolbarCustomization Copy() => new((RegionToolbarCommand[])Main.Clone(), (RegionToolbarCommand[])Secondary.Clone());
}

/// <summary>Data-only preferences shared by both renderers and the CPU editor.</summary>
internal static class RegionToolbarPreferences
{
    private static readonly object Gate = new();
    private static RegionToolbarCustomization _current = Defaults();
    private static Action<string>? _write;
    internal static RegionToolbarCommand[] Available => Enum.GetValues<RegionToolbarCommand>()
        .Where(Editable).ToArray();
    internal static bool Editable(RegionToolbarCommand c) => Enum.IsDefined(c)
        && c is not (RegionToolbarCommand.More or RegionToolbarCommand.Help or RegionToolbarCommand.EditToolbar);
    internal static RegionToolbarCustomization Defaults() => new(RegionToolbarCatalog.Main
        .Where(c => c != RegionToolbarCommand.More).ToArray(),
        [RegionToolbarCommand.PageLayout, RegionToolbarCommand.QuickSave, RegionToolbarCommand.QrCode, RegionToolbarCommand.Translate, RegionToolbarCommand.RecordGif,
         RegionToolbarCommand.LongCapture, RegionToolbarCommand.Subtitles, RegionToolbarCommand.TranslateInPlace, RegionToolbarCommand.HdrAnalysis, RegionToolbarCommand.GamutAnalysis, RegionToolbarCommand.Reselect]);
    internal static RegionToolbarCustomization Current { get { lock (Gate) return _current.Copy(); } }
    internal static void Configure(string? json, Action<string> write)
    { lock (Gate) { _current = Parse(json); _write = write; } }
    internal static void Save(RegionToolbarCustomization value)
    {
        value = Normalize(value);
        string json = Serialize(value);
        lock (Gate) { _write?.Invoke(json); _current = value; }
    }
    internal static RegionToolbarCustomization Normalize(RegionToolbarCustomization value)
    {
        var used = new HashSet<RegionToolbarCommand>();
        RegionToolbarCommand[] Clean(IEnumerable<RegionToolbarCommand>? list) => (list ?? [])
            .Where(c => Editable(c) && used.Add(c)).Take(64).ToArray();
        return new(Clean(value.Main), Clean(value.Secondary));
    }
    internal static string Serialize(RegionToolbarCustomization value) => JsonSerializer.Serialize(new {
        version = 5, main = value.Main.Select(c => c.ToString()).ToArray(), secondary = value.Secondary.Select(c => c.ToString()).ToArray()
    });
    internal static RegionToolbarCustomization Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 16000) return Defaults();
        try
        {
            using var doc = JsonDocument.Parse(json);
            int version = doc.RootElement.GetProperty("version").GetInt32();
            if (version is not (1 or 2 or 3 or 4 or 5)) return Defaults();
            RegionToolbarCommand[] Read(string key) => doc.RootElement.GetProperty(key).EnumerateArray()
                .Select(v => v.ValueKind == JsonValueKind.String && Enum.TryParse<RegionToolbarCommand>(v.GetString(), out var c) && Enum.IsDefined(c) ? c : (RegionToolbarCommand)(-1)).ToArray();
            var parsed = Normalize(new(Read("main"), Read("secondary")));
            // Offer the newly introduced tool once to existing configurations. V2 removals stay removed.
            if(version == 1 && !parsed.Main.Contains(RegionToolbarCommand.QrCode) && !parsed.Secondary.Contains(RegionToolbarCommand.QrCode))
                parsed = parsed with { Secondary = parsed.Secondary.Append(RegionToolbarCommand.QrCode).ToArray() };
            if(version<3){if(!parsed.Main.Contains(RegionToolbarCommand.Formula)&&!parsed.Secondary.Contains(RegionToolbarCommand.Formula)){var list=parsed.Main.ToList();int i=list.IndexOf(RegionToolbarCommand.Ocr);if(i>=0)list.Insert(i+1,RegionToolbarCommand.Formula);else parsed=parsed with{Secondary=parsed.Secondary.Append(RegionToolbarCommand.Formula).ToArray()};parsed=parsed with{Main=list.ToArray()};}if(!parsed.Main.Contains(RegionToolbarCommand.PageLayout)&&!parsed.Secondary.Contains(RegionToolbarCommand.PageLayout))parsed=parsed with{Secondary=parsed.Secondary.Append(RegionToolbarCommand.PageLayout).ToArray()};}
            if(version<4&&!parsed.Main.Contains(RegionToolbarCommand.GamutAnalysis)&&!parsed.Secondary.Contains(RegionToolbarCommand.GamutAnalysis))
                parsed=parsed with{Secondary=parsed.Secondary.Append(RegionToolbarCommand.GamutAnalysis).ToArray()};
            if(version<5&&!parsed.Main.Contains(RegionToolbarCommand.Subtitles)&&!parsed.Secondary.Contains(RegionToolbarCommand.Subtitles))
                parsed=parsed with{Secondary=parsed.Secondary.Append(RegionToolbarCommand.Subtitles).ToArray()};
            return parsed;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return Defaults(); }
    }
}

internal sealed class RegionToolbarEditModel
{
    internal List<RegionToolbarCommand> Main { get; } = [];
    internal List<RegionToolbarCommand> Secondary { get; } = [];
    internal RegionToolbarEditModel(RegionToolbarCustomization value) => Replace(value);
    internal void Replace(RegionToolbarCustomization value)
    { value = RegionToolbarPreferences.Normalize(value); Main.Clear(); Main.AddRange(value.Main); Secondary.Clear(); Secondary.AddRange(value.Secondary); }
    internal List<RegionToolbarCommand> List(int side) => side == 0 ? Main : Secondary;
    internal RegionToolbarCommand[] Unused() => RegionToolbarPreferences.Available.Except(Main).Except(Secondary).ToArray();
    internal bool Move(int side, int index, int destination, int position)
    {
        var from = List(side); var to = List(destination);
        if (index < 0 || index >= from.Count) return false;
        var c = from[index]; from.RemoveAt(index);
        if (ReferenceEquals(from, to) && position > index) position--;
        to.Insert(Math.Clamp(position, 0, to.Count), c); return true;
    }
    internal RegionToolbarCustomization Snapshot() => new(Main.ToArray(), Secondary.ToArray());
}
