using System.Numerics;
using Microsoft.UI.Composition;

namespace Neuroset.App.Services;

internal static class CompositionTransforms
{
    public static void SetCenteredOrientation(Visual visual, Quaternion orientation)
    {
#if ANDROID
        // Uno 6.7 applies Orientation about (0, 0, 0), ignoring CenterPoint.
        // Supply WinUI's centered rotation explicitly. Offset and parent scaling
        // remain separate, so resizing does not move the layer's rotation pivot.
        visual.Orientation = Quaternion.Identity;
        var center = visual.CenterPoint;
        visual.TransformMatrix = Matrix4x4.CreateTranslation(-center)
            * Matrix4x4.CreateFromQuaternion(orientation)
            * Matrix4x4.CreateTranslation(center);
#else
        visual.Orientation = orientation;
#endif
    }
}
