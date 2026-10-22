using System;
using System.Drawing;

namespace Starshot.Features.Screenshot;

// Both renderers return source-relative physical pixels, not DIP or screen coordinates.
internal static class SubtitleSelectionRegion
{
    internal static Rectangle ToDesktop(Rectangle source,int virtualX,int virtualY)
    {
        if(source.Width<80||source.Height<16||(long)source.Width*source.Height>4_194_304)
            throw new InvalidOperationException("请将字幕选区调整为至少80×16、最多约400万像素；只框住字幕即可。");
        return new(checked(source.X+virtualX),checked(source.Y+virtualY),source.Width,source.Height);
    }
}
