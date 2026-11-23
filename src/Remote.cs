using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AutoClic;

/// <summary>
/// Télécommande : écoute les commandes envoyées depuis le téléphone et les passe au moteur.
/// - ntfy : sujet « &lt;ton sujet&gt;-cmd » (boutons des notifications, ou message écrit dans l'appli)
/// - Telegram : messages envoyés au bot (seulement depuis ta conversation)
/// Commandes : lancer, stop, pause [minutes], reprendre, capture, état, aide.
/// </summary>
internal sealed class RemoteControl
{
    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    readonly Action<string> _execute;
    volatile AlertSettings _settings;
    volatile bool _enabled;
    int _generation;
    Thread _thread;

    public RemoteControl(Action<string> execute) => _execute = execute;

    /// <summary>Change les réglages : relance l'écoute si le service ou le sujet a changé.</summary>
    public void Configure(AlertSettings alerts, bool enabled)
    {
        var old = _settings;
        bool same = old != null && _enabled == enabled && old.Service == alerts.Service && old.NtfyTopic == alerts.NtfyTopic &&
                    old.NtfyServer == alerts.NtfyServer && old.TelegramToken == alerts.TelegramToken && old.TelegramChat == alerts.TelegramChat;
        _settings = alerts.Clone();
        _enabled = enabled;
        if (same && (_thread?.IsAlive ?? false)) return;

        int gen = Interlocked.Increment(ref _generation);
        if (!enabled || !(alerts.Service is AlertSettings.Ntfy or AlertSettings.Telegram)) return;
        _thread = new Thread(() => Loop(gen)) { IsBackground = true, Name = "AutoClic.Telecommande", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
        Log.Info($"Télécommande active ({alerts.Service})");
    }

    public void Stop() => Interlocked.Increment(ref _generation);

    bool Current(int gen) => gen == Volatile.Read(ref _generation);

    void Loop(int gen)
    {
        int failures = 0;
        while (Current(gen))
        {
            try
            {
                var s = _settings;
                if (s.Service == AlertSettings.Ntfy) ListenNtfy(s, gen);
                else if (s.Service == AlertSettings.Telegram) ListenTelegram(s, gen);
                else return;
                failures = 0;
            }
            catch (Exception ex)
            {
                if (!Current(gen)) return;
                if (failures++ == 0) Log.Info("Télécommande : connexion perdue, nouvel essai… (" + ex.GetBaseException().Message + ")");
            }
            // Pause avant de se reconnecter (plus longue si ça échoue souvent)
            for (int i = 0; i < Math.Min(60, 3 + failures * 5) * 10 && Current(gen); i++) Thread.Sleep(100);
        }
    }

    // ---------- ntfy : flux continu des messages du sujet de commandes ----------
    string _ntfySince;

    void ListenNtfy(AlertSettings s, int gen)
    {
        string topic = s.NtfyCommandTopic;
        if (topic == null) throw new InvalidOperationException("sujet ntfy vide");
        // « since » : ne reprend pas les vieilles commandes, mais ne rate pas celles arrivées pendant une coupure
        string since = _ntfySince ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        string url = $"{s.NtfyServerOrDefault}/{Uri.EscapeDataString(topic)}/json?since={Uri.EscapeDataString(since)}";
        using var cts = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // Le serveur envoie un « keepalive » toutes les ~45 s : sans rien pendant 2 min, on se reconnecte
        var watchdog = new Timer(_ => { try { cts.Cancel(); response.Dispose(); } catch { } }, null, 120_000, Timeout.Infinite);
        try
        {
            string line;
            while (Current(gen) && (line = reader.ReadLine()) != null)
            {
                watchdog.Change(120_000, Timeout.Infinite);
                var id = Regex.Match(line, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                if (id.Success) _ntfySince = id.Groups[1].Value;
                if (!Regex.IsMatch(line, "\"event\"\\s*:\\s*\"message\"")) continue;
                var m = Regex.Match(line, "\"message\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                if (m.Success) Handle(Regex.Unescape(m.Groups[1].Value));
            }
        }
        finally { watchdog.Dispose(); }
    }

    // ---------- Telegram : « long polling » des messages envoyés au bot ----------
    long _tgOffset;

#pragma warning disable CS0649 // remplis par la lecture du JSON
    [DataContract] sealed class TgResponse { [DataMember(Name = "ok")] public bool Ok; [DataMember(Name = "result")] public List<TgUpdate> Result; }
    [DataContract] sealed class TgUpdate { [DataMember(Name = "update_id")] public long Id; [DataMember(Name = "message")] public TgMessage Message; }
    [DataContract] sealed class TgMessage { [DataMember(Name = "chat")] public TgChat Chat; [DataMember(Name = "text")] public string Text; [DataMember(Name = "date")] public long Date; }
    [DataContract] sealed class TgChat { [DataMember(Name = "id")] public long Id; }
#pragma warning restore CS0649

    void ListenTelegram(AlertSettings s, int gen)
    {
        if (string.IsNullOrWhiteSpace(s.TelegramToken) || string.IsNullOrWhiteSpace(s.TelegramChat))
            throw new InvalidOperationException("bot Telegram pas configuré");
        long started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var serializer = new DataContractJsonSerializer(typeof(TgResponse));
        while (Current(gen))
        {
            string url = $"https://api.telegram.org/bot{s.TelegramToken.Trim()}/getUpdates?timeout=50&offset={_tgOffset}";
            byte[] json = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            var res = (TgResponse)serializer.ReadObject(new MemoryStream(json));
            if (res?.Result == null) continue;
            foreach (var u in res.Result)
            {
                _tgOffset = Math.Max(_tgOffset, u.Id + 1);
                var msg = u.Message;
                // Seulement ta conversation, et pas les vieux messages d'avant le lancement
                if (msg?.Text == null || msg.Chat?.Id.ToString() != s.TelegramChat.Trim() || msg.Date < started - 5) continue;
                Handle(msg.Text);
            }
        }
    }

    void Handle(string text)
    {
        string cmd = (text ?? "").Trim();
        if (cmd.StartsWith("/")) cmd = cmd.Substring(1);
        int at = cmd.IndexOf('@');                    // « /pause@MonBot » dans les groupes Telegram
        if (at > 0) cmd = cmd.Substring(0, at) + (cmd.IndexOf(' ', at) > 0 ? cmd.Substring(cmd.IndexOf(' ', at)) : "");
        if (cmd.Length == 0) return;
        Log.Info($"Télécommande : « {cmd} »");
        try { _execute(cmd); } catch (Exception ex) { Log.Error("télécommande", ex); }
    }

    /// <summary>Boutons ajoutés aux notifications ntfy (le téléphone envoie la commande d'un simple toucher).</summary>
    public static string NtfyActions(AlertSettings s)
    {
        string url = $"{s.NtfyServerOrDefault}/{s.NtfyCommandTopic}";
        string Button(string label, string body) => $"http, {label}, {url}, method=POST, body={body}, clear=true";
        return string.Join("; ", Button("Pause 15 min", "pause 15"), Button("Capture", "capture"), Button("Arrêter", "stop"));
    }

    public const string Help =
        "Commandes : lancer · stop · pause (ou pause 20 pour 20 min) · reprendre · capture · état · aide";
}
