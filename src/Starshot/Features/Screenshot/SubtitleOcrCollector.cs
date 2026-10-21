using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Starshot.Features.Screenshot;

/// <summary>Tracks visible OCR lines and appends each new subtitle once. No Win32 or OCR dependencies.</summary>
internal sealed class SubtitleOcrCollector
{
    internal sealed record Cue(DateTimeOffset CapturedAt, string Text);

    private static readonly TimeSpan ReappearanceWindow = TimeSpan.FromSeconds(6);
    private readonly object _sync = new();
    private readonly HashSet<string> _previous = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.Ordinal);
    private readonly List<Cue> _cues = new();
    private const int MaxCues = 50_000;

    public IReadOnlyList<Cue> Observe(IEnumerable<string> recognizedLines, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(recognizedLines);
        lock (_sync)
        {
            var visible = new HashSet<string>(StringComparer.Ordinal);
            var candidates = new List<(string Key, string Text)>();
            foreach (string? raw in recognizedLines)
            {
                string text = (raw ?? "").Trim();
                if (text.Length == 0) continue;
                if (text.Length > 2048) text = text[..2048];
                string key = Normalize(text);
                if (key.Length == 0 || !visible.Add(key)) continue;
                candidates.Add((key, text));
            }

            var added = new List<Cue>();
            foreach (var (key, text) in candidates)
            {
                bool alreadyVisible = _previous.Contains(key);
                bool recentlySeen = _lastSeen.TryGetValue(key, out var last) &&
                                    at - last < ReappearanceWindow;
                _lastSeen[key] = at;
                if (alreadyVisible || recentlySeen || _cues.Count >= MaxCues) continue;
                var cue = new Cue(at, text);
                _cues.Add(cue);
                added.Add(cue);
            }
            _previous.Clear();
            foreach (string key in visible) _previous.Add(key);

            if (_lastSeen.Count > 256)
            {
                foreach (string stale in _lastSeen.Where(item => at - item.Value > TimeSpan.FromMinutes(2))
                             .Select(item => item.Key).ToArray())
                    _lastSeen.Remove(stale);
            }
            return added;
        }
    }

    public string CopyAll()
    {
        lock (_sync)
            return string.Join(Environment.NewLine, _cues.Select(Format));
    }

    public string CopyTextOnly()
    {
        lock (_sync)
            return string.Join(Environment.NewLine, _cues.Select(cue => cue.Text));
    }

    public static string Format(Cue cue) => $"[{cue.CapturedAt:HH:mm:ss}] {cue.Text}";

    private static string Normalize(string text)
    {
        string normalized = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (char c in normalized)
            if (!char.IsWhiteSpace(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format)
                builder.Append(char.ToUpperInvariant(c));
        return builder.ToString();
    }
}
