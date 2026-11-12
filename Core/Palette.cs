using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Piztki.Core;

/// <summary>Finds the color that gives an album cover its character, and tunes it for a black notch.</summary>
public static class Palette
{
    /// <summary>
    /// The cover's dominant vivid color, or null for covers that are essentially black, white, or gray.
    /// Pixels vote for one of twelve hue families, weighted by how colorful and bright they are, and
    /// the winning family's average color is returned. A plain average of the whole image would turn
    /// most covers into the same muddy brown.
    /// </summary>
    public static Color? Dominant(BitmapSource source)
    {
        try
        {
            const int Size = 28;
            var small = new TransformedBitmap(source,
                new ScaleTransform((double)Size / source.PixelWidth, (double)Size / source.PixelHeight));
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);

            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);

            const int Bins = 12;
            var weight = new double[Bins];
            var r = new double[Bins];
            var g = new double[Bins];
            var b = new double[Bins];

            for (int i = 0; i < px.Length; i += 4)
            {
                double pb = px[i], pg = px[i + 1], pr = px[i + 2];
                double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                double value = max / 255, saturation = max <= 0 ? 0 : (max - min) / max;
                if (value < 0.16 || saturation < 0.2) continue; // near-black and gray pixels don't vote

                double hue = Hue(pr, pg, pb, max, min);
                int bin = (int)(hue / 360 * Bins) % Bins;
                double vote = saturation * saturation * value;
                weight[bin] += vote;
                r[bin] += pr * vote;
                g[bin] += pg * vote;
                b[bin] += pb * vote;
            }

            int best = 0;
            for (int i = 1; i < Bins; i++)
                if (weight[i] > weight[best]) best = i;

            // too few colorful pixels: a black-and-white cover gets a neutral silver glow instead
            if (weight[best] < w * h * 0.004) return Color.FromRgb(0xC8, 0xC8, 0xD0);

            return Color.FromRgb(
                (byte)(r[best] / weight[best]),
                (byte)(g[best] / weight[best]),
                (byte)(b[best] / weight[best]));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Up to <paramref name="count"/> of the cover's most prominent colors, strongest first, from
    /// different hue families. Used for the soft color clouds behind the lyrics. A cover with no
    /// real color gives an empty list.
    /// </summary>
    public static List<Color> TopColors(BitmapSource source, int count)
    {
        var result = new List<Color>();
        try
        {
            const int Size = 28, Bins = 12;
            var small = new TransformedBitmap(source,
                new ScaleTransform((double)Size / source.PixelWidth, (double)Size / source.PixelHeight));
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);

            var weight = new double[Bins];
            var r = new double[Bins];
            var g = new double[Bins];
            var b = new double[Bins];
            for (int i = 0; i < px.Length; i += 4)
            {
                double pb = px[i], pg = px[i + 1], pr = px[i + 2];
                double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                double value = max / 255, saturation = max <= 0 ? 0 : (max - min) / max;
                if (value < 0.16 || saturation < 0.2) continue;
                int bin = (int)(Hue(pr, pg, pb, max, min) / 360 * Bins) % Bins;
                double vote = saturation * saturation * value;
                weight[bin] += vote;
                r[bin] += pr * vote;
                g[bin] += pg * vote;
                b[bin] += pb * vote;
            }

            foreach (int bin in Enumerable.Range(0, Bins).OrderByDescending(i => weight[i]).Take(count))
            {
                if (weight[bin] < w * h * 0.003) break; // the rest are too faint to matter
                result.Add(Color.FromRgb((byte)(r[bin] / weight[bin]), (byte)(g[bin] / weight[bin]), (byte)(b[bin] / weight[bin])));
            }
        }
        catch
        {
            // no colors is a fine answer
        }
        return result;
    }

    /// <summary>
    /// Some players (Spotify among them) hand Windows a thumbnail with the real cover sitting on a
    /// plain gray card, with their logo in a strip underneath. This finds the cover inside that card
    /// and returns just the cover. Images that are already a plain cover come back unchanged.
    /// </summary>
    public static BitmapSource CropToCover(BitmapSource source)
    {
        try
        {
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            if (w < 32 || h < 32) return source;
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);

            // the card's color is whatever sits in the bottom-left corner
            int corner = ((h - 2) * w + 1) * 4;
            byte cb = px[corner], cg = px[corner + 1], cr = px[corner + 2], ca = px[corner + 3];

            bool IsCard(int x, int y)
            {
                int i = (y * w + x) * 4;
                if (px[i + 3] < 24) return true; // see-through padding
                if (ca < 24) return false;
                return Math.Abs(px[i] - cb) + Math.Abs(px[i + 1] - cg) + Math.Abs(px[i + 2] - cr) < 30;
            }

            // a row or column belongs to the cover when most of it is not card-colored;
            // the logo strip fails this because the logo covers well under half its width
            bool RowIsCover(int y)
            {
                int card = 0;
                for (int x = 0; x < w; x++) if (IsCard(x, y)) card++;
                return card < w * 0.5;
            }

            int top = 0;
            while (top < h && !RowIsCover(top)) top++;
            int bottom = top;
            while (bottom + 1 < h && RowIsCover(bottom + 1)) bottom++;
            if (top >= h) return source;

            bool ColIsCover(int x)
            {
                int card = 0;
                for (int y = top; y <= bottom; y++) if (IsCard(x, y)) card++;
                return card < (bottom - top + 1) * 0.5;
            }

            int left = 0;
            while (left < w && !ColIsCover(left)) left++;
            int right = w - 1;
            while (right > left && !ColIsCover(right)) right--;

            int cw = right - left + 1, ch = bottom - top + 1;
            bool trimmedSomething = cw < w * 0.96 || ch < h * 0.96;
            bool plausible = cw >= w * 0.4 && ch >= h * 0.4 && cw / (double)ch is > 0.8 and < 1.25;
            if (!trimmedSomething || !plausible) return source;

            var cropped = new CroppedBitmap(source, new System.Windows.Int32Rect(left, top, cw, ch));
            cropped.Freeze();
            return cropped;
        }
        catch
        {
            return source;
        }
    }

    /// <summary>The same hue, light and clear enough for text and thin lines on black.</summary>
    public static Color Bright(Color c)
    {
        ToHsl(c, out double h, out double s, out _);
        if (s < 0.12) return Color.FromRgb(0xE6, 0xE6, 0xEE); // grays stay gray rather than being forced into a hue
        return FromHsl(h, Math.Clamp(s, 0.55, 0.9), 0.74);
    }

    /// <summary>The same hue, rich and darker, for a glow behind white text.</summary>
    public static Color Deep(Color c)
    {
        ToHsl(c, out double h, out double s, out _);
        if (s < 0.12) return Color.FromRgb(0x6A, 0x6A, 0x74);
        return FromHsl(h, Math.Clamp(s, 0.5, 0.85), 0.42);
    }

    static double Hue(double r, double g, double b, double max, double min)
    {
        double d = max - min;
        if (d <= 0) return 0;
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    static void ToHsl(Color c, out double h, out double s, out double l)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;
        double d = max - min;
        s = d <= 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        h = Hue(r, g, b, max, min);
    }

    static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
