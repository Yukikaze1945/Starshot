using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Starshot.Helpers;

/// <summary>Turns OCR lines into editable paragraphs without changing the recognized words.</summary>
internal static class OcrTextFormatter
{
    public static string Lines(IReadOnlyList<OcrLine> lines) =>
        string.Join(Environment.NewLine, lines.Select(line => line.Text.Trim()));

    public static string Paragraphs(IReadOnlyList<OcrLine> lines)
    {
        if (lines.Count == 0) return "";
        var output = new StringBuilder();
        OcrLine? previous = null;
        foreach (OcrLine line in lines)
        {
            string text = line.Text.Trim();
            if (text.Length == 0) continue;
            if (previous is not null)
            {
                double lineHeight = Math.Max(1, Math.Max(previous.Rect.Height, line.Rect.Height));
                double gap = line.Rect.Top - (previous.Rect.Top + previous.Rect.Height);
                double indent = Math.Abs(line.Rect.Left - previous.Rect.Left);
                bool separate = gap > lineHeight * 0.65 ||
                    indent > lineHeight * 2.5 ||
                    line.Rect.Top < previous.Rect.Top - lineHeight * 0.4;
                if (separate) output.AppendLine().AppendLine();
                else if (output.Length > 0 && output[^1] == '-' &&
                    char.IsLetter(text[0]))
                    output.Length--;
                else if (NeedsSpace(output, text))
                    output.Append(' ');
            }
            output.Append(text);
            previous = line;
        }
        return output.ToString();
    }

    private static bool NeedsSpace(StringBuilder output, string next)
    {
        if (output.Length == 0 || next.Length == 0) return false;
        char left = output[^1], right = next[0];
        return IsLatinOrDigit(left) && IsLatinOrDigit(right);
    }

    private static bool IsLatinOrDigit(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
}
