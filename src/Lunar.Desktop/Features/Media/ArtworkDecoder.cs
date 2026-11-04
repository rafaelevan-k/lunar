using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Lunar.Desktop.Features.MediaControls;

/// <summary>
/// Turns the artwork bytes of a media session into an image for the page (FE-17). Artwork is untrusted data from another app:
/// it is decoded small (the page shows it at 72 DIP), frozen, and any file that cannot be decoded is simply no artwork,
/// never an error. Use on the UI thread.
/// </summary>
internal static class ArtworkDecoder
{
    /// <summary>Decoded width in pixels; enough for 72 DIP at 200% scaling.</summary>
    internal const int DecodeWidth = 144;

    public static ImageSource? Decode(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.IsEmpty)
            return null;

        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad; // read everything now, so the stream can go
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = DecodeWidth;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or IOException or InvalidOperationException
                                       or COMException or FileFormatException or OutOfMemoryException)
        {
            return null; // not an image, or a broken one: the page shows its placeholder
        }
    }
}
