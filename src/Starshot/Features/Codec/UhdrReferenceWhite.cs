using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Starshot.Features.Codec;

/// <summary>Optional EXIF provenance. UHDR's relative gain map alone has no absolute SDR white.</summary>
internal static class UhdrReferenceWhite
{
    private const string Prefix = "Starshot SDRWhiteNits=";
    public static byte[] CreateExif(float white)
    {
        if (!float.IsFinite(white) || white < 1 || white > 10000) throw new ArgumentOutOfRangeException(nameof(white));
        byte[] text = Encoding.ASCII.GetBytes(Prefix + white.ToString("R", CultureInfo.InvariantCulture) + "\0");
        byte[] exif = new byte[32 + text.Length];
        "Exif\0\0II"u8.CopyTo(exif);
        var tiff = exif.AsSpan(6);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff[2..], 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff[4..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff[8..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff[10..], 0x010e);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff[12..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff[14..], (uint)text.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff[18..], 26);
        text.CopyTo(exif, 32);
        return exif;
    }
    // Compressed-base + gain-map assembly in libultrahdr v2.0.2 preserves
    // the base JPEG's EXIF but ignores encoder.SetExifData. Insert before
    // assembly so MPF offsets are generated from the final base size.
    public static byte[] AttachToBaseJpeg(byte[] jpeg,float white)
    {
        if(jpeg.Length<2 || jpeg[0]!=255 || jpeg[1]!=216)throw new ArgumentException("需要 JPEG 基图");
        byte[] exif=CreateExif(white),output=new byte[jpeg.Length+exif.Length+4];
        jpeg.AsSpan(0,2).CopyTo(output);output[2]=255;output[3]=225;
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4),checked((ushort)(exif.Length+2)));
        exif.CopyTo(output,6);jpeg.AsSpan(2).CopyTo(output.AsSpan(exif.Length+6));return output;
    }
    public static float Read(ReadOnlySpan<byte> exif)
    {
        // Default is a rendering reference, not a claim about the source monitor.
        const float fallback = 203;
        if (exif.StartsWith("Exif\0\0"u8)) exif = exif[6..];
        if (exif.Length < 26 || !exif.StartsWith("II"u8)) return fallback;
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(exif[4..]);
        if (offset > exif.Length - 2) return fallback;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(exif[(int)offset..]);
        for (int i = 0; i < Math.Min(count, 256); i++)
        {
            long pos = offset + 2L + i * 12;
            if (pos + 12 > exif.Length) return fallback;
            var entry = exif.Slice((int)pos, 12);
            if (BinaryPrimitives.ReadUInt16LittleEndian(entry) != 0x010e || BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]) != 2) continue;
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]), start = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            if (length > 128 || length < Prefix.Length || (ulong)start + length > (ulong)exif.Length) return fallback;
            string text = Encoding.ASCII.GetString(exif.Slice((int)start, (int)length)).TrimEnd('\0');
            if (text.StartsWith(Prefix, StringComparison.Ordinal) && float.TryParse(text[Prefix.Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out float white)
                && float.IsFinite(white) && white is >= 1 and <= 10000) return white;
        }
        return fallback;
    }
}
