using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace AutoClic;

/// <summary>Envoie une notification sur le téléphone (ntfy, Telegram ou Discord).</summary>
internal static class Phone
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AutoClic/2");
        return http;
    }

    /// <summary>Renvoie null si c'est parti, sinon le message d'erreur.</summary>
    public static string Send(AlertSettings s, string title, string message, byte[] jpeg)
    {
        try
        {
            HttpResponseMessage response = s.Service switch
            {
                AlertSettings.Ntfy => SendNtfy(s, title, message, jpeg),
                AlertSettings.Telegram => SendTelegram(s, title, message, jpeg),
                AlertSettings.Discord => SendDiscord(s, title, message, jpeg),
                _ => null,
            };
            if (response == null) return "aucun service choisi";
            using (response)
            {
                if (response.IsSuccessStatusCode) return null;
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return $"{(int)response.StatusCode} {response.ReasonPhrase} {Shorten(body)}";
            }
        }
        catch (Exception ex) { return ex.GetBaseException().Message; }
    }

    static HttpResponseMessage SendNtfy(AlertSettings s, string title, string message, byte[] jpeg)
    {
        string server = string.IsNullOrWhiteSpace(s.NtfyServer) ? AlertSettings.DefaultNtfyServer : s.NtfyServer.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(s.NtfyTopic)) throw new InvalidOperationException("sujet ntfy vide");
        // Titre et texte dans l'adresse (accepte les accents), photo en pièce jointe
        string url = $"{server}/{Uri.EscapeDataString(s.NtfyTopic.Trim())}?title={Uri.EscapeDataString(title)}";
        // Télécommande : boutons « Pause 15 min / Capture / Arrêter » directement dans la notification
        if (s.RemoteButtons && s.NtfyCommandTopic != null) url += "&actions=" + Uri.EscapeDataString(RemoteControl.NtfyActions(s));
        if (jpeg == null)
            return Http.PostAsync(url, new StringContent(message, Encoding.UTF8, "text/plain")).GetAwaiter().GetResult();
        url += $"&message={Uri.EscapeDataString(message)}&filename=ecran.jpg";
        return Http.PutAsync(url, new ByteArrayContent(jpeg)).GetAwaiter().GetResult();
    }

    static HttpResponseMessage SendTelegram(AlertSettings s, string title, string message, byte[] jpeg)
    {
        if (string.IsNullOrWhiteSpace(s.TelegramToken) || string.IsNullOrWhiteSpace(s.TelegramChat))
            throw new InvalidOperationException("code du bot ou conversation Telegram manquant");
        string api = $"https://api.telegram.org/bot{s.TelegramToken.Trim()}";
        string text = $"{title}\n{message}";
        if (jpeg == null)
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["chat_id"] = s.TelegramChat.Trim(), ["text"] = text });
            return Http.PostAsync(api + "/sendMessage", form).GetAwaiter().GetResult();
        }
        var multi = new MultipartFormDataContent
        {
            { new StringContent(s.TelegramChat.Trim()), "chat_id" },
            { new StringContent(text), "caption" },
            { new ByteArrayContent(jpeg), "photo", "ecran.jpg" },
        };
        return Http.PostAsync(api + "/sendPhoto", multi).GetAwaiter().GetResult();
    }

    static HttpResponseMessage SendDiscord(AlertSettings s, string title, string message, byte[] jpeg)
    {
        if (string.IsNullOrWhiteSpace(s.DiscordWebhook)) throw new InvalidOperationException("adresse du webhook Discord vide");
        string json = "{\"content\":\"" + JsonEscape($"**{title}**\n{message}") + "\"}";
        var multi = new MultipartFormDataContent { { new StringContent(json, Encoding.UTF8, "application/json"), "payload_json" } };
        if (jpeg != null) multi.Add(new ByteArrayContent(jpeg), "files[0]", "ecran.jpg");
        return Http.PostAsync(s.DiscordWebhook.Trim(), multi).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Telegram : retrouve la conversation à partir du dernier message envoyé au bot.
    /// Renvoie l'identifiant, ou null avec un message d'erreur.
    /// </summary>
    public static string FindTelegramChat(string token, out string error)
    {
        error = null;
        try
        {
            string json = Http.GetStringAsync($"https://api.telegram.org/bot{token.Trim()}/getUpdates").GetAwaiter().GetResult();
            var ids = Regex.Matches(json, "\"chat\"\\s*:\\s*\\{\\s*\"id\"\\s*:\\s*(-?\\d+)");
            if (ids.Count == 0) { error = "Aucun message reçu : envoie d'abord un message (ex. /start) à ton bot sur Telegram."; return null; }
            return ids[ids.Count - 1].Groups[1].Value;
        }
        catch (Exception ex)
        {
            error = "Code du bot refusé ou pas d'Internet : " + ex.GetBaseException().Message;
            return null;
        }
    }

    static string JsonEscape(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    static string Shorten(string s) => string.IsNullOrEmpty(s) ? "" : s.Length > 120 ? s.Substring(0, 120) + "…" : s;
}

/// <summary>Un évènement à garder en photo et / ou à envoyer sur le téléphone.</summary>
internal sealed class Report
{
    public string Title { get; set; }
    public string Message { get; set; }
    /// <summary>Nom court pour le fichier photo (ex. « bouton-2 », « inactivite »).</summary>
    public string Tag { get; set; }
    /// <summary>Capture prise au moment de l'évènement (sinon prise à l'envoi).</summary>
    public Bitmap Photo { get; set; }
    /// <summary>Point à entourer sur la photo (là où on a cliqué), en coordonnées écran.</summary>
    public Point? Mark { get; set; }
    public bool KeepPhoto { get; set; }
    public bool SendToPhone { get; set; }
    /// <summary>Toujours joindre la capture (surveillance), même si l'option générale est coupée.</summary>
    public bool ForcePhoto { get; set; }
    /// <summary>Jamais de capture (simple réponse texte à une commande).</summary>
    public bool NoPhoto { get; set; }
}

/// <summary>
/// Fil à part qui enregistre les photos et envoie les alertes : le réseau ou le disque
/// ne ralentissent jamais les clics.
/// </summary>
internal sealed class Reporter
{
    const int MaxPhotos = 300;
    readonly BlockingCollection<Report> _queue = new(new ConcurrentQueue<Report>(), 50);
    volatile AlertSettings _alerts = new();
    Thread _thread;

    public AlertSettings Alerts { get => _alerts; set => _alerts = value?.Clone() ?? new AlertSettings(); }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "AutoClic.Rapports", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public bool IsAlive => _thread?.IsAlive ?? false;

    /// <summary>Ajoute un évènement (ignoré si la file est pleine : on ne bloque jamais l'appelant).</summary>
    public void Add(Report report)
    {
        if (report == null || (!report.KeepPhoto && !report.SendToPhone)) { report?.Photo?.Dispose(); return; }
        if (!_queue.TryAdd(report)) report.Photo?.Dispose();
    }

    public void Stop() => _queue.CompleteAdding();

    void Loop()
    {
        try
        {
            foreach (var r in _queue.GetConsumingEnumerable())
            {
                try { Handle(r); }
                catch (Exception ex) { Log.Error("rapport", ex); }
                finally { r.Photo?.Dispose(); }
            }
        }
        catch (Exception ex) { Log.Error("rapports", ex); }
    }

    void Handle(Report r)
    {
        var alerts = _alerts;
        bool send = r.SendToPhone && alerts.Enabled;
        bool withPhoto = !r.NoPhoto && (alerts.Photo || r.ForcePhoto);
        bool needPhoto = r.KeepPhoto || (send && withPhoto);
        if (needPhoto && r.Photo == null)
        {
            try { r.Photo = ScreenShot.CaptureBitmap(SystemInformation.VirtualScreen); }
            catch (Exception ex) { Log.Error("photo", ex); }
        }
        if (r.Photo != null && r.Mark.HasValue) DrawMark(r.Photo, r.Mark.Value);

        if (r.KeepPhoto && r.Photo != null) SavePhoto(r.Photo, r.Tag);

        if (send)
        {
            byte[] jpeg = withPhoto && r.Photo != null ? ToJpeg(r.Photo, 1280, 70) : null;
            string error = Phone.Send(alerts, r.Title, r.Message, jpeg);
            if (error != null) Log.Info($"Alerte téléphone pas envoyée ({r.Title}) : {error}");
        }
    }

    /// <summary>Envoie un message de test tout de suite (depuis la fenêtre des paramètres).</summary>
    public static string SendTest(AlertSettings alerts)
    {
        byte[] jpeg = null;
        if (alerts.Photo)
        {
            try
            {
                using var bmp = ScreenShot.CaptureBitmap(Screen.PrimaryScreen.Bounds);
                jpeg = ToJpeg(bmp, 1280, 70);
            }
            catch { }
        }
        return Phone.Send(alerts, "Auto Clic : test", "Les alertes arrivent bien sur ton téléphone.", jpeg);
    }

    /// <summary>Capture de tout l'écran envoyée tout de suite (bouton « Envoyer maintenant »).</summary>
    public static string SendCaptureNow(AlertSettings alerts)
    {
        byte[] jpeg;
        try
        {
            using var bmp = ScreenShot.CaptureBitmap(SystemInformation.VirtualScreen);
            jpeg = ToJpeg(bmp, 1280, 70);
        }
        catch (Exception ex) { return "capture impossible : " + ex.GetBaseException().Message; }
        return Phone.Send(alerts, "Auto Clic : capture d'écran", $"Capture demandée à {DateTime.Now:HH:mm}.", jpeg);
    }

    static void DrawMark(Bitmap bmp, Point screenPoint)
    {
        var v = SystemInformation.VirtualScreen;
        int x = screenPoint.X - v.X, y = screenPoint.Y - v.Y;
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.Red, 4);
        g.DrawEllipse(pen, x - 22, y - 22, 44, 44);
        g.DrawLine(pen, x - 34, y, x - 26, y);
        g.DrawLine(pen, x + 26, y, x + 34, y);
    }

    static void SavePhoto(Bitmap bmp, string tag)
    {
        try
        {
            Directory.CreateDirectory(Paths.PhotosDir);
            string name = $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{tag}.jpg";
            File.WriteAllBytes(Path.Combine(Paths.PhotosDir, name), ToJpeg(bmp, 1920, 75));

            // On ne garde que les plus récentes
            var old = new DirectoryInfo(Paths.PhotosDir).GetFiles("*.jpg")
                .OrderByDescending(f => f.CreationTimeUtc).Skip(MaxPhotos).ToList();
            foreach (var f in old) { try { f.Delete(); } catch { } }
        }
        catch (Exception ex) { Log.Error("enregistrement de la photo", ex); }
    }

    static byte[] ToJpeg(Bitmap bmp, int maxWidth, long quality)
    {
        Bitmap scaled = null;
        try
        {
            var src = bmp;
            if (bmp.Width > maxWidth)
            {
                scaled = new Bitmap(maxWidth, (int)((long)bmp.Height * maxWidth / bmp.Width));
                using var g = Graphics.FromImage(scaled);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(bmp, 0, 0, scaled.Width, scaled.Height);
                src = scaled;
            }
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var p = new EncoderParameters(1);
            p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            using var ms = new MemoryStream();
            src.Save(ms, codec, p);
            return ms.ToArray();
        }
        finally { scaled?.Dispose(); }
    }
}
