using System;
using Starshot.Features.Screenshot;

const int width = 420, height = 360;
Check("vertical 87px", Page(0, 0), Page(0, 87), false, 87);
Check("horizontal 53px", Page(0, 0), Page(53, 0), true, 53);
Check("vertical with fixed header", Page(0, 0, true), Page(0, 87, true), false, 87);
Check("unchanged", Page(0, 0), Page(0, 0), false, 0);
Check("repeated rows", new byte[width * height * 4], new byte[width * height * 4], false, 0);
CheckReverse("vertical back 87px", Page(0, 87), Page(0, 0), false, 87);
CheckReverse("horizontal back 53px", Page(53, 0), Page(0, 0), true, 53);
CheckReverse("unchanged back", Page(0, 0), Page(0, 0), false, 0);
CheckReason("unchanged frame", Page(0, 0), Page(0, 0), "unchanged");
CheckReason("no shared content", Page(0, 0), Page(9000, 9000), "no-overlap");
CheckReason("periodic stripe shift", StripePage(0), StripePage(200), "ambiguous");
Console.WriteLine("All synthetic long-capture matches passed.");

void CheckReason(string name, byte[] previous, byte[] current, string expected)
{
    var result = ScrollingFrameMatcher.Analyze(previous, current, width, height, false, false);
    Console.WriteLine($"{name}: reason={result.Reason}, shift={result.Shift}");
    if (result.Shift != 0 || result.Reason != expected)
        throw new Exception($"{name}: expected {expected}, got {result.Reason} / {result.Shift}");
}

void Check(string name, byte[] previous, byte[] current, bool horizontal, int expected)
{
    var (shift, error) = ScrollingFrameMatcher.FindShift(previous, current, width, height, horizontal);
    Console.WriteLine($"{name}: shift={shift}, error={error:F2}");
    if (shift != expected) throw new Exception($"{name}: expected {expected}, got {shift}");
}

void CheckReverse(string name, byte[] previous, byte[] current, bool horizontal, int expected)
{
    var (shift, error) = ScrollingFrameMatcher.FindReverseShift(previous, current, width, height, horizontal);
    Console.WriteLine($"{name}: shift={shift}, error={error:F2}");
    if (shift != expected) throw new Exception($"{name}: expected {expected}, got {shift}");
}

byte[] Page(int scrollX, int scrollY, bool fixedHeader = false)
{
    byte[] pixels = new byte[width * height * 4];
    for (int y = 0; y < height; y++)
    for (int x = 0; x < width; x++)
    {
        uint value = Hash((uint)(x + scrollX), (uint)(y + (fixedHeader && y < 48 ? 0 : scrollY)));
        int i = (y * width + x) * 4;
        pixels[i] = (byte)value;
        pixels[i + 1] = (byte)(value >> 8);
        pixels[i + 2] = (byte)(value >> 16);
        pixels[i + 3] = 255;
    }
    return pixels;
}

byte[] StripePage(int scrollY)
{
    byte[] pixels = new byte[width * height * 4];
    for (int y = 0; y < height; y++)
    for (int x = 0; x < width; x++)
    {
        int i = (y * width + x) * 4;
        byte value = (byte)(((y + scrollY) / 90) % 2 == 0 ? 230 : 250);
        pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
        pixels[i + 3] = 255;
    }
    return pixels;
}

static uint Hash(uint x, uint y)
{
    uint value = x * 0x9e3779b1u ^ y * 0x85ebca6bu;
    value ^= value >> 16;
    value *= 0x7feb352du;
    value ^= value >> 15;
    return value;
}
