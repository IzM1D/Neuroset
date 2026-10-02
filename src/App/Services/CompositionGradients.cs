using System.Numerics;
using Microsoft.UI.Composition;

namespace Neuroset.App.Services;

internal static class CompositionGradients
{
    public static void SetRadialCenter(CompositionRadialGradientBrush brush, Vector2 center)
    {
        brush.MappingMode = CompositionMappingMode.Relative;
        brush.EllipseCenter = center;
#if ANDROID
        // WinUI treats GradientOriginOffset as an offset from EllipseCenter.
        // Uno Skia 6.7 treats it as an absolute point. Its default (0,0) selects
        // a broken conical-gradient path, losing shading and transparent halos.
        // Coincident centers use the supported radial shader with the same look.
        brush.GradientOriginOffset = center;
#endif
    }
}
