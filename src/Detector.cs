using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AutoClic;

/// <summary>Image en niveaux de gris (un octet par pixel).</summary>
internal sealed class GrayImage(int width, int height, byte[] pixels)
{
    const int EdgeThreshold = 40;

    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;

    public static GrayImage FromBitmap(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            var raw = new byte[stride * h];
            Marshal.Copy(data.Scan0, raw, 0, raw.Length);
            var gray = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                int row = y * stride, o = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    gray[o + x] = (byte)((raw[i + 2] * 77 + raw[i + 1] * 150 + raw[i] * 29) >> 8);
                }
            }
            return new GrayImage(w, h, gray);
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>Réduit l'image k fois (moyenne de chaque carré k×k) pour une recherche rapide.</summary>
    public GrayImage Downscale(int k)
    {
        if (k <= 1) return this;
        int w = Width / k, h = Height / k, kk = k * k;
        var p = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int dy = 0; dy < k; dy++)
                {
                    int o = (y * k + dy) * Width + x * k;
                    for (int dx = 0; dx < k; dx++) sum += Pixels[o + dx];
                }
                p[y * w + x] = (byte)(sum / kk);
            }
        return new GrayImage(w, h, p);
    }

    /// <summary>
    /// Contours (1 = changement net de gris avec le voisin de droite ou du dessous).
    /// <paramref name="widen"/> : contours élargis d'un pixel, pour tolérer un léger décalage.
    /// </summary>
    public byte[] Edges(bool widen)
    {
        int w = Width, h = Height;
        var p = Pixels;
        var e = new byte[w * h];
        for (int y = 0; y < h - 1; y++)
        {
            int o = y * w;
            for (int x = 0; x < w - 1; x++)
            {
                int i = o + x;
                int g = Math.Abs(p[i + 1] - p[i]) + Math.Abs(p[i + w] - p[i]);
                if (g >= EdgeThreshold) e[i] = 1;
            }
        }
        if (!widen) return e;

        var hz = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int o = y * w;
            for (int x = 0; x < w; x++)
                hz[o + x] = (byte)(e[o + x] | (x > 0 ? e[o + x - 1] : 0) | (x < w - 1 ? e[o + x + 1] : 0));
        }
        var res = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int o = y * w;
            for (int x = 0; x < w; x++)
                res[o + x] = (byte)(hz[o + x] | (y > 0 ? hz[o + x - w] : 0) | (y < h - 1 ? hz[o + x + w] : 0));
        }
        return res;
    }
}

/// <summary>
/// Une image à comparer, avec le poids de chaque pixel.
/// La capture peut être grossière : le fond autour du bouton (la marge) peut changer dans le jeu.
/// On estime la couleur de ce fond à partir des bords de la capture ; les pixels qui lui
/// ressemblent comptent peu, ceux du bouton (texte, dessin, couleur) comptent beaucoup.
/// Les contours du bouton (lettres, bords) doivent aussi se retrouver à l'écran :
/// une zone unie de la même couleur ne passe plus pour le bouton.
/// </summary>
internal sealed class Pattern
{
    public const int ContentWeight = 5, BackgroundWeight = 1, EdgeWeight = 5;
    const int BackgroundTolerance = 30;

    public GrayImage Image { get; }
    public byte[] Weights { get; }
    /// <summary>1 = contour du bouton qui doit se retrouver à l'écran.</summary>
    public byte[] EdgeMask { get; }
    public int TotalWeight { get; }
    /// <summary>Part des pixels considérés comme « le bouton » (0-1).</summary>
    public double ContentRatio { get; }

    public Pattern(GrayImage image)
    {
        Image = image;
        int w = image.Width, h = image.Height;
        var p = image.Pixels;

        // Couleur du fond = médiane des pixels du bord de la capture
        var border = new List<byte>(2 * (w + h));
        for (int x = 0; x < w; x++) { border.Add(p[x]); border.Add(p[(h - 1) * w + x]); }
        for (int y = 1; y < h - 1; y++) { border.Add(p[y * w]); border.Add(p[y * w + w - 1]); }
        border.Sort();
        int background = border[border.Count / 2];

        int content = 0;
        for (int i = 0; i < p.Length; i++)
            if (Math.Abs(p[i] - background) > BackgroundTolerance) content++;
        ContentRatio = (double)content / p.Length;
        bool uniform = ContentRatio < 0.03;           // presque rien ne se détache : tout compte pareil

        var edges = image.Edges(false);
        Weights = new byte[p.Length];
        EdgeMask = new byte[p.Length];
        int total = 0;
        for (int i = 0; i < p.Length; i++)
        {
            bool isContent = uniform || Math.Abs(p[i] - background) > BackgroundTolerance;
            // Un contour touchant le bouton compte aussi (bord entre le bouton et le fond)
            bool edgeOfContent = edges[i] != 0 && (isContent || (i + 1 < p.Length && Math.Abs(p[i + 1] - background) > BackgroundTolerance)
                                                             || (i + w < p.Length && Math.Abs(p[i + w] - background) > BackgroundTolerance));
            Weights[i] = (byte)(isContent ? ContentWeight : BackgroundWeight);
            total += Weights[i];
            if (edgeOfContent) { EdgeMask[i] = 1; total += EdgeWeight; }
        }
        TotalWeight = total;
    }
}

/// <summary>Un bouton à reconnaître (image capturée par l'utilisateur).</summary>
internal sealed class Template
{
    public Pattern Full { get; private set; }
    public Pattern Small { get; private set; }
    public int Scale { get; private set; }
    /// <summary>Écart-type des gris : trop faible = zone unie, impossible à reconnaître.</summary>
    public double Contrast { get; private set; }
    public int Width => Full.Image.Width;
    public int Height => Full.Image.Height;

    public static Template FromBitmap(Bitmap bmp)
    {
        var full = GrayImage.FromBitmap(bmp);
        // Grandes captures : recherche grossière très réduite (rapide), petites : peu réduite
        int k = Math.Max(1, Math.Min(8, Math.Min(full.Width, full.Height) / 8));

        double mean = 0, var2 = 0;
        foreach (var b in full.Pixels) mean += b;
        mean /= full.Pixels.Length;
        foreach (var b in full.Pixels) var2 += (b - mean) * (b - mean);

        return new Template
        {
            Full = new Pattern(full),
            Small = new Pattern(full.Downscale(k)),
            Scale = k,
            Contrast = Math.Sqrt(var2 / full.Pixels.Length),
        };
    }

    /// <summary>Charge l'image d'un bouton, redimensionnée si besoin (autre résolution d'écran).</summary>
    public static Template Load(string path, double scale = 1)
    {
        using var bmp = LoadBitmap(path);
        if (Math.Abs(scale - 1) < 0.01) return FromBitmap(bmp);
        using var resized = Resize(bmp, scale);
        return FromBitmap(resized);
    }

    public static Bitmap Resize(Bitmap bmp, double scale)
    {
        int w = Math.Max(8, (int)Math.Round(bmp.Width * scale)), h = Math.Max(8, (int)Math.Round(bmp.Height * scale));
        var res = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(res);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);   // pas de bord flou
        g.DrawImage(bmp, new Rectangle(0, 0, w, h), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, attrs);
        return res;
    }

    /// <summary>Charge une image sans verrouiller le fichier (on peut le supprimer ensuite).</summary>
    public static Bitmap LoadBitmap(string path)
    {
        using var ms = new MemoryStream(File.ReadAllBytes(path));
        using var img = Image.FromStream(ms);
        return new Bitmap(img);
    }
}

/// <summary>Capture de tout l'écran (tous les moniteurs), en gris, avec ses versions réduites et ses contours.</summary>
internal sealed class ScreenShot
{
    readonly Dictionary<int, (GrayImage Image, byte[] Edges)> _levels = [];

    public Rectangle Bounds { get; }
    public GrayImage Full { get; }

    ScreenShot(Rectangle bounds, GrayImage full)
    {
        Bounds = bounds;
        Full = full;
    }

    public static Bitmap CaptureBitmap(Rectangle area)
    {
        var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            return bmp;
        }
        catch { bmp.Dispose(); throw; }
    }

    public static ScreenShot Capture()
    {
        var area = SystemInformation.VirtualScreen;
        using var bmp = CaptureBitmap(area);
        return new ScreenShot(area, GrayImage.FromBitmap(bmp));
    }

    /// <summary>Image réduite k fois + ses contours élargis (calculés une fois par capture).</summary>
    public (GrayImage Image, byte[] Edges) Level(int k)
    {
        lock (_levels)
        {
            if (!_levels.TryGetValue(k, out var level))
            {
                var img = k <= 1 ? Full : Full.Downscale(k);
                _levels[k] = level = (img, img.Edges(true));
            }
            return level;
        }
    }
}

internal readonly struct Match(bool found, Rectangle area, double similarity)
{
    public bool Found { get; } = found;
    /// <summary>Emplacement trouvé, en coordonnées écran.</summary>
    public Rectangle Area { get; } = area;
    public Point Center => new(Area.X + Area.Width / 2, Area.Y + Area.Height / 2);
    /// <summary>Ressemblance en % (100 = identique).</summary>
    public double Similarity { get; } = similarity;
}

/// <summary>
/// Cherche une image dans une capture d'écran.
/// Ressemblance = part (pondérée) de ce qui correspond : les pixels (à une tolérance près)
/// et les contours du bouton. Une marge qui a changé fait juste perdre quelques %.
/// 1) recherche grossière sur des images réduites (rapide),
/// 2) recherche fine autour des meilleurs endroits, sur un pixel sur deux,
/// 3) calcul exact à la meilleure position.
/// </summary>
internal static class Detector
{
    const int Candidates = 6;
    const int PixelTolerance = 32;          // écart de gris accepté pour dire « ce pixel correspond »
    const int CoarseTolerance = 40;
    const double CoarseSlack = 25;          // l'image réduite est floue : on est plus large au 1er tri

    /// <param name="zone">Zone de l'écran où chercher (coordonnées écran), ou null pour tout l'écran.</param>
    public static Match Find(ScreenShot shot, Template t, double minSimilarity, Rectangle? zone = null)
    {
        int k = t.Scale;
        var (S, SE) = shot.Level(k);
        var P = t.Small;
        int tw = P.Image.Width, th = P.Image.Height;
        if (tw < 1 || th < 1 || tw > S.Width || th > S.Height) return default;

        // Positions possibles (coin haut-gauche de l'image), limitées à la zone choisie
        int x0 = 0, y0 = 0, x1 = S.Width - tw, y1 = S.Height - th;
        if (zone.HasValue)
        {
            var z = zone.Value;
            x0 = Math.Max(0, (z.X - shot.Bounds.X) / k);
            y0 = Math.Max(0, (z.Y - shot.Bounds.Y) / k);
            x1 = Math.Min(x1, (z.Right - shot.Bounds.X) / k - tw);
            y1 = Math.Min(y1, (z.Bottom - shot.Bounds.Y) / k - th);
            // Zone plus petite que le bouton : on cherche quand même autour
            if (x1 < x0) { int c = (x0 + x1) / 2; x0 = Math.Max(0, c - 2); x1 = Math.Min(S.Width - tw, c + 2); }
            if (y1 < y0) { int c = (y0 + y1) / 2; y0 = Math.Max(0, c - 2); y1 = Math.Min(S.Height - th, c + 2); }
            if (x1 < x0 || y1 < y0) return default;
        }

        // ---- 1. Recherche grossière ----
        int worst = (int)((1 - Math.Max(0, minSimilarity - CoarseSlack) / 100) * P.TotalWeight);
        var cands = new List<(int Miss, int X, int Y)>(Candidates + 1);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int miss = Mismatch(S, SE, P, x, y, worst + 1, 1, CoarseTolerance);
                if (miss > worst) continue;
                cands.Add((miss, x, y));
                if (cands.Count > Candidates)
                {
                    cands.Sort((a, b) => a.Miss.CompareTo(b.Miss));
                    cands.RemoveAt(cands.Count - 1);
                }
                if (cands.Count == Candidates) worst = Math.Min(worst, cands[cands.Count - 1].Miss);
            }
        if (cands.Count == 0) return default;

        // ---- 2. Recherche fine autour des candidats ----
        var (F, FE) = shot.Level(1);
        var PF = t.Full;
        int fw = PF.Image.Width, fh = PF.Image.Height;
        int step = fw * fh > 4000 ? 2 : 1;
        int bestMiss = int.MaxValue, bx = -1, by = -1;
        foreach (var cand in cands)
        {
            int cx = cand.X * k, cy = cand.Y * k;
            for (int y = Math.Max(0, cy - k); y <= Math.Min(F.Height - fh, cy + k); y++)
                for (int x = Math.Max(0, cx - k); x <= Math.Min(F.Width - fw, cx + k); x++)
                {
                    int miss = Mismatch(F, FE, PF, x, y, bestMiss, step, PixelTolerance);
                    if (miss < bestMiss) { bestMiss = miss; bx = x; by = y; }
                }
        }
        if (bx < 0) return default;

        // ---- 3. Calcul exact (et petit ajustement d'un pixel) ----
        int exact = int.MaxValue, ex = bx, ey = by;
        for (int y = Math.Max(0, by - 1); y <= Math.Min(F.Height - fh, by + 1); y++)
            for (int x = Math.Max(0, bx - 1); x <= Math.Min(F.Width - fw, bx + 1); x++)
            {
                int miss = Mismatch(F, FE, PF, x, y, exact, 1, PixelTolerance);
                if (miss < exact) { exact = miss; ex = x; ey = y; }
            }

        double similarity = Math.Max(0, 100.0 * (1 - (double)exact / PF.TotalWeight));
        var area = new Rectangle(shot.Bounds.X + ex, shot.Bounds.Y + ey, fw, fh);
        return new Match(similarity >= minSimilarity, area, similarity);
    }

    /// <summary>
    /// Poids de ce qui ne correspond pas à la position (x, y) : pixels trop différents,
    /// et contours du bouton absents de l'écran. Arrête de compter dès <paramref name="stopAt"/>.
    /// </summary>
    static int Mismatch(GrayImage screen, byte[] screenEdges, Pattern p, int x, int y, int stopAt, int step, int tolerance)
    {
        int tw = p.Image.Width, th = p.Image.Height, W = screen.Width;
        byte[] sp = screen.Pixels, tp = p.Image.Pixels, wp = p.Weights, em = p.EdgeMask;
        int miss = 0;
        for (int r = 0; r < th; r += step)
        {
            int so = (y + r) * W + x, to = r * tw;
            for (int c = 0; c < tw; c += step)
            {
                int v = sp[so + c] - tp[to + c];
                if (v > tolerance || v < -tolerance) miss += wp[to + c];
                if (em[to + c] != 0 && screenEdges[so + c] == 0) miss += Pattern.EdgeWeight;
            }
            if (miss >= stopAt) return int.MaxValue;
        }
        return miss;
    }
}
