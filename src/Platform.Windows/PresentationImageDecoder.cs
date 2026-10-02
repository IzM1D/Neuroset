using System.Runtime.InteropServices.WindowsRuntime;
using Neuroset.Application;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Neuroset.Platform.Windows;

public sealed class PresentationImageDecoder : IPresentationImageDecoder
{
    public async Task<byte[]> ToPngAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
        }
        var decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken).ConfigureAwait(false);
        if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 || (long)decoder.PixelWidth * decoder.PixelHeight > 32_000_000)
            throw new InvalidDataException("The internet image is empty or exceeds 32 megapixels.");
        var scale = Math.Min(1d, 1600d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var width = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * scale));
        var height = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * scale));
        var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant };
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken).ConfigureAwait(false);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask(cancellationToken).ConfigureAwait(false);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, width, height, 96, 96, pixels.DetachPixelData());
        await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size).AsTask(cancellationToken).ConfigureAwait(false);
        var png = new byte[output.Size];
        reader.ReadBytes(png);
        return png;
    }
}
