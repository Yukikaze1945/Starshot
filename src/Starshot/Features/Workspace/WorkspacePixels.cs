using System;

namespace Starshot.Features.Workspace;

/// <summary>Explicit byte-layout boundary for the workspace's premultiplied BGRA contract.</summary>
internal static class WorkspacePixels
{
    internal enum ByteOrder { Bgra8, Rgba8 }

    internal static void CopyBgra8(ReadOnlySpan<byte> source,Span<byte> destination,ByteOrder order)
    {
        if(source.Length%4!=0 || destination.Length!=source.Length)
            throw new ArgumentException("BGRA 像素缓冲区尺寸不符");
        if(order is not(ByteOrder.Bgra8 or ByteOrder.Rgba8))throw new ArgumentOutOfRangeException(nameof(order));
        source.CopyTo(destination);
        if(order==ByteOrder.Rgba8)
            for(int i=0;i<destination.Length;i+=4)
                (destination[i],destination[i+2])=(destination[i+2],destination[i]);
    }
}
