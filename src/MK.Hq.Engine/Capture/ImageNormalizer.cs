using System.Drawing;
using System.Drawing.Imaging;
using MK.Hq.Domain;

namespace MK.Hq.Engine.Capture;

/// <summary>
/// Turns whatever the scanner or phone produced (PNG, JPEG, BMP, TIFF, GIF; EXIF-rotated phone photos) into an upright JPEG
/// no larger than the model needs. Scanner BMPs are not accepted by Google's API and multi-megabyte phone photos only
/// slow the upload, so every page is normalised once, on arrival.
/// </summary>
public static class ImageNormalizer
{
    public const int MaxEdge = 2400;
    public const long MaxInputBytes = 25 * 1024 * 1024;

    public static byte[] Normalize(byte[] input, int extraRotation = 0, int maxEdge = MaxEdge)
    {
        Guard.Require(input.Length is > 0 and <= (int)MaxInputBytes, "IMAGE_SIZE", "Choose an image up to 25 MB");
        // GDI+ reports a damaged or oversized picture as OutOfMemoryException (its generic "cannot decode" status) or
        // ExternalException, not only ArgumentException. Those escaped as a 500 on upload. The header is read first
        // (validateImageData: false) so a small file claiming huge dimensions is refused before anything is decoded.
        try { return NormalizeCore(input, extraRotation, maxEdge); }
        catch (Exception e) when (e is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            throw HqException.Invalid("IMAGE_TYPE", "That file is not a picture this PC can read (use JPEG, PNG or BMP; export PDF/HEIC as images)");
        }
    }

    /// <summary>Largest picture accepted, in pixels (a 108-megapixel phone camera is about 12000 × 9000).</summary>
    public const long MaxPixels = 120_000_000;

    private static byte[] NormalizeCore(byte[] input, int extraRotation, int maxEdge)
    {
        var source = Image.FromStream(new MemoryStream(input), useEmbeddedColorManagement: false, validateImageData: false);
        using (source)
        {
            Guard.Require((long)source.Width * source.Height <= MaxPixels, "IMAGE_SIZE", "That picture is too large; send a normal phone photo or scan");
            ApplyExifOrientation(source);
            var rotate = ((extraRotation % 360) + 360) % 360;
            if (rotate == 90) source.RotateFlip(RotateFlipType.Rotate90FlipNone);
            else if (rotate == 180) source.RotateFlip(RotateFlipType.Rotate180FlipNone);
            else if (rotate == 270) source.RotateFlip(RotateFlipType.Rotate270FlipNone);

            var scale = Math.Min(1.0, (double)maxEdge / Math.Max(source.Width, source.Height));
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            using var canvas = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            canvas.SetResolution(150, 150);
            using (var g = Graphics.FromImage(canvas))
            {
                g.Clear(Color.White); // transparent PNG areas become paper-white, not black
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(source, 0, 0, width, height);
            }
            var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
            using var output = new MemoryStream();
            canvas.Save(output, encoder, parameters);
            return output.ToArray();
        }
    }

    /// <summary>
    /// Whether the writing runs vertically. Lines of text (and ruled paper) make the ink profile across rows alternate
    /// sharply between "text line" and "gap"; across columns it is smooth. For a sideways page it is the other way round.
    /// Deterministic, instant, no API call.
    /// </summary>
    public static bool LooksSideways(byte[] image)
    {
        var (rows, cols) = LineStructure(image);
        return cols > rows * 1.25;
    }

    /// <summary>Normalised high-frequency energy of the dark-pixel profile along rows and along columns.</summary>
    public static (double Rows, double Cols) LineStructure(byte[] image)
    {
        using var source = Image.FromStream(new MemoryStream(image));
        const int size = 600;
        var scale = (double)size / Math.Max(source.Width, source.Height);
        int w = Math.Max(1, (int)(source.Width * scale)), h = Math.Max(1, (int)(source.Height * scale));
        using var small = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small)) { g.Clear(Color.White); g.DrawImage(source, 0, 0, w, h); }
        var data = small.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var luma = new byte[w * h];
        try
        {
            var stride = data.Stride;
            var buffer = new byte[stride * h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = y * stride + x * 3;
                    luma[y * w + x] = (byte)((buffer[i] * 29 + buffer[i + 1] * 150 + buffer[i + 2] * 77) >> 8);
                }
        }
        finally { small.UnlockBits(data); }
        var mean = luma.Average(b => (double)b);
        var threshold = mean * 0.75; // ink is clearly darker than the paper
        // Ignore a 6% border (scanner edges, punch holes, page shadows).
        int mx = w * 6 / 100, my = h * 6 / 100;
        var rowInk = new double[h];
        var colInk = new double[w];
        for (var y = my; y < h - my; y++)
            for (var x = mx; x < w - mx; x++)
                if (luma[y * w + x] < threshold) { rowInk[y]++; colInk[x]++; }
        static double Energy(double[] p, int from, int to)
        {
            double diff = 0, total = 0;
            for (var i = from + 1; i < to; i++) { diff += Math.Abs(p[i] - p[i - 1]); total += p[i]; }
            return total <= 0 ? 0 : diff / total;
        }
        return (Energy(rowInk, my, h - my), Energy(colInk, mx, w - mx));
    }

    private static void ApplyExifOrientation(Image image)
    {
        const int OrientationTag = 0x0112;
        if (!image.PropertyIdList.Contains(OrientationTag)) return;
        var value = image.GetPropertyItem(OrientationTag)?.Value;
        if (value is null || value.Length == 0) return;
        var flip = value[0] switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        };
        if (flip != RotateFlipType.RotateNoneFlipNone) image.RotateFlip(flip);
        image.RemovePropertyItem(OrientationTag);
    }
}
