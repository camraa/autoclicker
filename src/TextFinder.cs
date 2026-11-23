using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace AutoClic;

/// <summary>Résultat d'une recherche de texte.</summary>
internal sealed class TextHit(bool found, Rectangle area, string read, string error = null)
{
    public bool Found { get; } = found;
    /// <summary>Emplacement du texte trouvé, en coordonnées écran.</summary>
    public Rectangle Area { get; } = area;
    public Point Center => new(Area.X + Area.Width / 2, Area.Y + Area.Height / 2);
    /// <summary>Tout ce que l'OCR a lu dans la zone (pour aider à régler).</summary>
    public string Read { get; } = read;
    /// <summary>Message si la lecture est impossible (OCR absent…).</summary>
    public string Error { get; } = error;
}

/// <summary>
/// Lit le texte à l'écran avec l'OCR intégré à Windows et trouve où se trouve un mot / une phrase.
/// Tolère les petites erreurs de lecture, la casse et les accents.
/// Une instance par fil (le moteur et le bouton « Test » ont chacun la leur).
/// </summary>
internal sealed class TextFinder
{
    public const string MissingOcr =
        "Lecture de texte indisponible : installe la langue française (ou anglaise) avec la " +
        "« Reconnaissance optique de caractères » dans Paramètres > Heure et langue > Langue.";

    readonly OcrEngine _engine;

    public TextFinder()
    {
        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                      ?? OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(e => e != null);
        }
        catch (Exception ex) { Log.Error("OCR", ex); }
    }

    public bool Available => _engine != null;

    /// <summary>Cherche <paramref name="query"/> dans la zone <paramref name="area"/> (coordonnées écran) de la capture.</summary>
    public TextHit Find(ScreenShot shot, Rectangle area, string query)
    {
        if (_engine == null) return new TextHit(false, Rectangle.Empty, "", MissingOcr);

        // Zone dans la capture, élargie un peu (capture grossière / texte qui dépasse)
        int padX = Math.Max(20, area.Width / 4), padY = Math.Max(12, area.Height / 4);
        var region = Rectangle.Inflate(area, padX, padY);
        region.Offset(-shot.Bounds.X, -shot.Bounds.Y);
        region.Intersect(new Rectangle(0, 0, shot.Full.Width, shot.Full.Height));
        if (region.Width < 4 || region.Height < 4) return new TextHit(false, Rectangle.Empty, "");

        // Petits textes : agrandis pour que l'OCR les lise mieux
        int scale = region.Height < 120 ? 3 : region.Height < 300 ? 2 : 1;
        var words = Read(shot.Full, region, scale);
        string read = string.Join(" ", words.Select(w => w.Text));

        var best = BestMatch(words, query);
        if (best == null) return new TextHit(false, Rectangle.Empty, read);

        var r = best.Value;
        var screenArea = new Rectangle(shot.Bounds.X + region.X + r.X / scale, shot.Bounds.Y + region.Y + r.Y / scale,
                                       Math.Max(1, r.Width / scale), Math.Max(1, r.Height / scale));
        return new TextHit(true, screenArea, read);
    }

    sealed class Word(string text, Rectangle box, int line)
    {
        public string Text { get; } = text;
        public string Norm { get; } = Normalize(text);
        public Rectangle Box { get; } = box;
        public int Line { get; } = line;
    }

    List<Word> Read(GrayImage img, Rectangle region, int scale)
    {
        int w = region.Width * scale, h = region.Height * scale;
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int src = (region.Y + y / scale) * img.Width + region.X;
            int dst = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                byte g = img.Pixels[src + x / scale];
                int i = dst + x * 4;
                bgra[i] = g; bgra[i + 1] = g; bgra[i + 2] = g; bgra[i + 3] = 255;
            }
        }

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Ignore);
        var result = _engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();

        var words = new List<Word>();
        int line = 0;
        foreach (var l in result.Lines)
        {
            foreach (var word in l.Words)
            {
                var b = word.BoundingRect;
                words.Add(new Word(word.Text, new Rectangle((int)b.X, (int)b.Y, (int)Math.Ceiling(b.Width), (int)Math.Ceiling(b.Height)), line));
            }
            line++;
        }
        return words;
    }

    /// <summary>Les mots consécutifs (sur une même ligne) qui ressemblent le plus au texte cherché.</summary>
    static Rectangle? BestMatch(List<Word> words, string query)
    {
        string q = Normalize(query);
        if (q.Length == 0) return null;
        // Mots courts : il faut l'avoir exactement ; plus long : on tolère ~1 erreur de lecture sur 5 lettres
        double minScore = q.Length <= 3 ? 1.0 : 0.8;

        double bestScore = 0;
        Rectangle? best = null;
        for (int i = 0; i < words.Count; i++)
        {
            string joined = "";
            var box = words[i].Box;
            for (int j = i; j < words.Count && words[j].Line == words[i].Line; j++)
            {
                joined += words[j].Norm;
                if (j > i) box = Rectangle.Union(box, words[j].Box);
                double score = joined.Contains(q) ? 1.0 : Similarity(joined, q);
                if (score > bestScore) { bestScore = score; best = box; }
                if (joined.Length > q.Length + 3) break;
            }
        }
        return bestScore >= minScore ? best : null;
    }

    /// <summary>Minuscules, sans accents, sans espaces ni ponctuation.</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (char c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>1 = identique, 0 = rien à voir (distance d'édition).</summary>
    static double Similarity(string a, string b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0 || m == 0) return 0;
        var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return 1.0 - (double)d[n, m] / Math.Max(n, m);
    }
}
