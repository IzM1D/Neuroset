using Android.Graphics;
using Neuroset.Application;

namespace Neuroset.App.Droid;

public sealed class AndroidImageDecoder : IPresentationImageDecoder
{
    public async Task<byte[]> ToPngAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
        await BitmapFactory.DecodeByteArrayAsync(bytes, 0, bytes.Length, bounds).ConfigureAwait(false);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0 || (long)bounds.OutWidth * bounds.OutHeight > 32_000_000)
            throw new InvalidDataException("The internet image is empty or exceeds 32 megapixels.");
        var sample = 1;
        while (Math.Max(bounds.OutWidth, bounds.OutHeight) / (sample * 2) >= 1600) sample *= 2;
        using var options = new BitmapFactory.Options { InSampleSize = sample };
        using var bitmap = await BitmapFactory.DecodeByteArrayAsync(bytes, 0, bytes.Length, options).ConfigureAwait(false)
            ?? throw new InvalidDataException("The image could not be decoded.");
        var scale = Math.Min(1d, 1600d / Math.Max(bitmap.Width, bitmap.Height));
        using var output = new MemoryStream();
        if (scale < 1)
        {
            using var resized = Bitmap.CreateScaledBitmap(bitmap, Math.Max(1, (int)Math.Round(bitmap.Width * scale)), Math.Max(1, (int)Math.Round(bitmap.Height * scale)), true)!;
            await resized.CompressAsync(Bitmap.CompressFormat.Png!, 100, output).ConfigureAwait(false);
        }
        else await bitmap.CompressAsync(Bitmap.CompressFormat.Png!, 100, output).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return output.ToArray();
    }
}
