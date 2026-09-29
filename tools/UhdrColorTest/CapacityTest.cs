using System;
using System.IO;
using Microsoft.Graphics.Canvas;
using Starshot.Features.Codec;
using Starward.Codec.UltraHdr;

internal static class CapacityTest
{
    public static int Run(CanvasDevice device)
    {
        using var image = Harness.MakePatchImage(device);
        foreach (float capacity in new[] { 0f, 2f, 4f, 8f, 12f, 13.8f, 16f, 32f })
        {
            using var stream = new MemoryStream();
            ImageSaver.SaveAsUhdrAsync(image, stream, Harness.MaxCll, 80, capacity).GetAwaiter().GetResult();
            byte[] bytes = stream.ToArray();
            using var probe = UhdrDecoder.Create(bytes);
            float actual = probe.GetGainmapMetadata().HdrCapacityMax;
            if (capacity > 0 && Math.Abs(actual - capacity) > 0.0001)
                throw new Exception($"Capacity {capacity} decoded as {actual}");
            File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, $"capacity-{capacity}.jpg"), bytes);
            Console.WriteLine($"PASS capacity={capacity} decoded={actual}");
        }
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, -1f, 1f, 33f })
        {
            using var stream = new MemoryStream();
            try
            {
                ImageSaver.SaveAsUhdrAsync(image, stream, Harness.MaxCll, 80, invalid).GetAwaiter().GetResult();
                throw new Exception($"Accepted invalid capacity {invalid}");
            }
            catch (ArgumentOutOfRangeException) { }
        }
        Console.WriteLine("PASS invalid values rejected");
        return 0;
    }
}
