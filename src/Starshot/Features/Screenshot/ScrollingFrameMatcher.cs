using System;

namespace Starshot.Features.Screenshot;

/// <summary>Compares the previous frame's trailing content with the next frame's leading content.</summary>
internal static class ScrollingFrameMatcher
{
    internal readonly record struct MatchResult(int Shift, double Error, double Baseline,
        string Reason);

    internal static (int Shift, double Error) FindShift(
        byte[] previous, byte[] current, int width, int height, bool horizontal)
    {
        var result = Analyze(previous, current, width, height, horizontal, reverse: false);
        return (result.Shift, result.Error);
    }

    internal static (int Shift, double Error) FindReverseShift(
        byte[] previous, byte[] current, int width, int height, bool horizontal)
    {
        var result = Analyze(previous, current, width, height, horizontal, reverse: true);
        return (result.Shift, result.Error);
    }

    internal static MatchResult Analyze(
        byte[] previous, byte[] current, int width, int height, bool horizontal, bool reverse)
    {
        int axis = horizontal ? width : height;
        if (width < 32 || height < 32 || axis < 80 ||
            previous.Length != (long)width * height * 4 || current.Length != previous.Length)
            return new(0, double.MaxValue, double.MaxValue, "invalid-frame");
        double baseline = Score(previous, current, width, height, horizontal, 0, reverse);
        if (baseline <= 1.0)
            return new(0, baseline, baseline, "unchanged");
        double best = double.MaxValue;
        int bestShift = 0;
        // Very small overlaps can appear to match a repeated row or card at a
        // distant position. Wait for a slower scroll instead of appending it.
        int maxShift = axis - Math.Max(40, (int)Math.Ceiling(axis * 0.4));
        for (int shift = 8; shift <= maxShift; shift++)
        {
            double error = Score(previous, current, width, height, horizontal, shift, reverse);
            if (error < best) { best = error; bestShift = shift; }
        }
        // A loose match on a page with repeating cards can add the same card
        // twice. Prefer waiting for the next stable frame to corrupting the
        // stitched image during a smooth-scroll animation.
        if (best > 2.5 || best >= baseline * 0.7)
            return new(0, best, baseline, "no-overlap");
        double alternative = double.MaxValue;
        // A one-to-seven-pixel movement is too small to append, but a repeating
        // page can make it look like a much larger shift. Count those candidates
        // when deciding whether the chosen overlap is unique.
        for (int shift = 1; shift < 8; shift++)
            alternative = Math.Min(alternative,
                Score(previous, current, width, height, horizontal, shift, reverse));
        for (int shift = 8; shift <= maxShift; shift += 4)
        {
            if (Math.Abs(shift - bestShift) <= 6) continue;
            alternative = Math.Min(alternative,
                Score(previous, current, width, height, horizontal, shift, reverse));
        }
        if (alternative < best * 1.15 + 0.5)
            return new(0, best, baseline, "ambiguous"); // Repeating content.
        return new(bestShift, best, baseline, "matched");
    }

    private static double Score(byte[] previous, byte[] current,
        int width, int height, bool horizontal, int shift, bool reverse)
    {
        int axis = horizontal ? width : height;
        int cross = horizontal ? height : width;
        int overlap = axis - shift;
        const int axisSamples = 24, crossSamples = 24;
        long difference = 0;
        for (int a = 0; a < axisSamples; a++)
        {
            // Avoid a sticky header/footer inside the selected region.
            int along = (int)((0.25 + (a + 0.5) * 0.60 / axisSamples) * overlap);
            for (int c = 0; c < crossSamples; c++)
            {
                int across = (int)((c + 0.5) * cross / crossSamples);
                int oldX = horizontal ? along + (reverse ? 0 : shift) : across;
                int oldY = horizontal ? across : along + (reverse ? 0 : shift);
                int newX = horizontal ? along + (reverse ? shift : 0) : across;
                int newY = horizontal ? across : along + (reverse ? shift : 0);
                int oldIndex = (oldY * width + oldX) * 4;
                int newIndex = (newY * width + newX) * 4;
                difference += Math.Abs(previous[oldIndex] - current[newIndex]);
                difference += Math.Abs(previous[oldIndex + 1] - current[newIndex + 1]);
                difference += Math.Abs(previous[oldIndex + 2] - current[newIndex + 2]);
            }
        }
        return (double)difference / (axisSamples * crossSamples * 3);
    }
}
