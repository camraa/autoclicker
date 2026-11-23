using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace AutoClic;

/// <summary>Où ranger boutons.json et AutoClic.log.</summary>
internal static class Paths
{
    public static string DataDir { get; private set; } = AppDomain.CurrentDomain.BaseDirectory;
    public static string ConfigFile => Path.Combine(DataDir, "boutons.json");
    public static string LogFile => Path.Combine(DataDir, "AutoClic.log");
    /// <summary>Les images des boutons à détecter.</summary>
    public static string ImagesDir => Path.Combine(DataDir, "images");
    /// <summary>Le journal en photos (captures d'écran).</summary>
    public static string PhotosDir => Path.Combine(DataDir, "captures");

    /// <summary>
    /// À côté de AutoClic.exe (version portable). Si ce dossier est protégé (ex. Program Files),
    /// on utilise %LOCALAPPDATA%\AutoClic. « --dossier &lt;chemin&gt; » force un autre dossier.
    /// </summary>
    public static void Init(string[] args)
    {
        string dir = null;
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], "--dossier", StringComparison.OrdinalIgnoreCase)) dir = args[i + 1];

        if (dir == null)
        {
            // Mode portable si des réglages sont déjà à côté de l'exe (comme les anciennes versions),
            // sinon rangés dans %LOCALAPPDATA%\AutoClic (rien ne s'éparpille dans Téléchargements ou sur le Bureau)
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            dir = File.Exists(Path.Combine(exeDir, "boutons.json")) && IsWritable(exeDir)
                ? exeDir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoClic");
        }
        Directory.CreateDirectory(dir);
        DataDir = Path.GetFullPath(dir);
    }

    static bool IsWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".autoclic-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}

/// <summary>Journal texte. Ne lève jamais d'erreur, utilisable depuis n'importe quel fil.</summary>
internal static class Log
{
    static readonly object Gate = new();
    const long MaxSize = 1_000_000;

    public static void Info(string message) => Write(message);

    public static void Error(string where, Exception ex) =>
        Write($"ERREUR ({where}) : {ex?.ToString() ?? "inconnue"}");

    static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                string path = Paths.LogFile;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxSize)
                {
                    string old = Path.ChangeExtension(path, ".old.log");
                    File.Delete(old);
                    File.Move(path, old);
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}\r\n", Encoding.UTF8);
            }
        }
        catch { /* le journal ne doit jamais faire planter le programme */ }
    }
}

[DataContract]
internal sealed class ClickPoint
{
    [DataMember(Order = 1)] public int X { get; set; }
    [DataMember(Order = 2)] public int Y { get; set; }
    /// <summary>Secondes à attendre après le clic sur ce bouton.</summary>
    [DataMember(Order = 3)] public double Delay { get; set; } = Config.DefaultDelay;

    public ClickPoint Clone() => new() { X = X, Y = Y, Delay = Delay };
}

/// <summary>Un bouton à détecter : son image (dans le dossier images) et la pause après chaque clic.</summary>
[DataContract]
internal sealed class ImageButton
{
    [DataMember(Name = "Fichier", Order = 1)] public string File { get; set; }
    [DataMember(Name = "Delay", Order = 2)] public double Delay { get; set; } = Config.DefaultImageDelay;
    /// <summary>Texte sur lequel cliquer dans la zone trouvée (vide = cliquer au centre de l'image).</summary>
    [DataMember(Name = "Texte", Order = 3, EmitDefaultValue = false)] public string Text { get; set; }
    /// <summary>clic (défaut), double, droit ou touche.</summary>
    [DataMember(Name = "Action", Order = 4, EmitDefaultValue = false)] public string Action { get; set; }
    /// <summary>Touche à appuyer si Action = touche (ex. « Entrée »).</summary>
    [DataMember(Name = "Touche", Order = 5, EmitDefaultValue = false)] public string Key { get; set; }
    /// <summary>Zone où chercher, en fractions de l'écran principal (null = tout l'écran).</summary>
    [DataMember(Name = "Zone", Order = 6, EmitDefaultValue = false)] public ZoneRect Zone { get; set; }
    /// <summary>Taille de l'image à l'écran par rapport au fichier (1 = même taille), valable pour <see cref="CaptureScreen"/>.</summary>
    [DataMember(Name = "Echelle", Order = 7, EmitDefaultValue = false)] public double? Scale { get; set; }
    /// <summary>Résolution de l'écran principal au moment de la capture (« 1920x1080 »).</summary>
    [DataMember(Name = "EcranCapture", Order = 8, EmitDefaultValue = false)] public string CaptureScreen { get; set; }
    /// <summary>Prévenir sur le téléphone quand ce bouton est détecté.</summary>
    [DataMember(Name = "Alerte", Order = 9, EmitDefaultValue = false)] public bool Alert { get; set; }
    /// <summary>Enchaînement : actif seulement juste après ce bouton-là (son fichier image), ou null = toujours.</summary>
    [DataMember(Name = "Apres", Order = 10, EmitDefaultValue = false)] public string After { get; set; }
    /// <summary>Secondes à attendre après l'action sur le bouton précédent.</summary>
    [DataMember(Name = "ApresDelai", Order = 11, EmitDefaultValue = false)] public double AfterDelay { get; set; }
    /// <summary>Pendant combien de secondes (après le délai) ce bouton reste actif.</summary>
    [DataMember(Name = "ApresFenetre", Order = 12, EmitDefaultValue = false)] public double AfterWindow { get; set; }

    public double AfterWindowOrDefault => AfterWindow > 0 ? AfterWindow : 30;

    public string FullPath => Path.Combine(Paths.DataDir, File ?? "");

    public const string ActionClick = "clic", ActionDouble = "double", ActionRight = "droit", ActionKey = "touche";
    public string ActionOrDefault => Action is ActionDouble or ActionRight or ActionKey ? Action : ActionClick;

    public static string CurrentScreen
    {
        get { var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds; return $"{b.Width}x{b.Height}"; }
    }

    /// <summary>
    /// Taille à utiliser maintenant : l'échelle enregistrée, ajustée si la résolution a changé
    /// depuis (ex. bouton capturé en 1920x1080, PC actuel en 2560x1440 → ×1,33).
    /// </summary>
    public double EffectiveScale()
    {
        double s = Scale is > 0.1 and < 10 ? Scale.Value : 1;
        if (TryParseScreen(CaptureScreen, out int capH))
        {
            int curH = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;
            if (capH > 0 && curH != capH) s *= (double)curH / capH;
        }
        return Math.Abs(s - 1) < 0.01 ? 1 : s;
    }

    static bool TryParseScreen(string s, out int height)
    {
        height = 0;
        var parts = (s ?? "").Split('x');
        return parts.Length == 2 && int.TryParse(parts[1], out height);
    }
}

/// <summary>Rectangle en fractions de l'écran principal (s'adapte à une autre résolution).</summary>
[DataContract]
internal sealed class ZoneRect
{
    [DataMember(Order = 1)] public double X { get; set; }
    [DataMember(Order = 2)] public double Y { get; set; }
    [DataMember(Order = 3)] public double W { get; set; }
    [DataMember(Order = 4)] public double H { get; set; }

    public static ZoneRect FromScreen(System.Drawing.Rectangle r)
    {
        var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        return new ZoneRect
        {
            X = (double)(r.X - b.X) / b.Width, Y = (double)(r.Y - b.Y) / b.Height,
            W = (double)r.Width / b.Width, H = (double)r.Height / b.Height,
        };
    }

    public System.Drawing.Rectangle ToScreen()
    {
        var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        return new System.Drawing.Rectangle(b.X + (int)(X * b.Width), b.Y + (int)(Y * b.Height),
                                            Math.Max(1, (int)(W * b.Width)), Math.Max(1, (int)(H * b.Height)));
    }
}

/// <summary>Alertes sur le téléphone.</summary>
[DataContract]
internal sealed class AlertSettings
{
    public const string None = "aucun", Ntfy = "ntfy", Telegram = "telegram", Discord = "discord";
    public const string DefaultNtfyServer = "https://ntfy.sh";

    [DataMember(Name = "Service", Order = 1)] public string Service { get; set; } = None;
    [DataMember(Name = "NtfyServeur", Order = 2, EmitDefaultValue = false)] public string NtfyServer { get; set; }
    [DataMember(Name = "NtfySujet", Order = 3, EmitDefaultValue = false)] public string NtfyTopic { get; set; }
    [DataMember(Name = "TelegramToken", Order = 4, EmitDefaultValue = false)] public string TelegramToken { get; set; }
    [DataMember(Name = "TelegramChat", Order = 5, EmitDefaultValue = false)] public string TelegramChat { get; set; }
    [DataMember(Name = "DiscordWebhook", Order = 6, EmitDefaultValue = false)] public string DiscordWebhook { get; set; }
    [DataMember(Name = "Photo", Order = 7)] bool? _photo = true;
    [DataMember(Name = "SurArret", Order = 8)] bool? _onStop = true;
    [DataMember(Name = "SurInactivite", Order = 9)] bool? _onIdle = true;
    [DataMember(Name = "SurProbleme", Order = 10)] bool? _onProblem = true;

    public bool Photo { get => _photo ?? true; set => _photo = value; }
    public bool OnStop { get => _onStop ?? true; set => _onStop = value; }
    public bool OnIdle { get => _onIdle ?? true; set => _onIdle = value; }
    public bool OnProblem { get => _onProblem ?? true; set => _onProblem = value; }
    public bool Enabled => Service is Ntfy or Telegram or Discord;

    public AlertSettings Clone() => (AlertSettings)MemberwiseClone();

    /// <summary>Sujet ntfy où le téléphone envoie ses commandes (« &lt;sujet&gt;-cmd »).</summary>
    public string NtfyCommandTopic => string.IsNullOrWhiteSpace(NtfyTopic) ? null : NtfyTopic.Trim() + "-cmd";

    public string NtfyServerOrDefault => string.IsNullOrWhiteSpace(NtfyServer) ? DefaultNtfyServer : NtfyServer.Trim().TrimEnd('/');

    /// <summary>Télécommande active (copié depuis la config, pour ajouter les boutons aux notifications).</summary>
    public bool RemoteButtons { get; set; }

    public static string NewTopic()
    {
        const string chars = "abcdefghijkmnpqrstuvwxyz23456789";
        var rnd = new Random(Guid.NewGuid().GetHashCode());
        return "autoclic-" + new string(Enumerable.Range(0, 12).Select(_ => chars[rnd.Next(chars.Length)]).ToArray());
    }
}

/// <summary>Contenu de boutons.json (compatible avec l'ancienne version).</summary>
[DataContract]
internal sealed class Config
{
    public const double DefaultDelay = 1.0, MinDelay = 0.1, MaxDelay = 3600;
    public const double DefaultImageDelay = 2.0, MinImageDelay = 0.5;
    // Ressemblance = % (pondéré) des pixels qui correspondent ; 75 laisse de la place aux marges grossières
    public const int DefaultSimilarity = 75, MinSimilarity = 40, MaxSimilarity = 99;

    [DataMember(Name = "Macro", Order = 1)] bool? _macro = true;
    [DataMember(Name = "Boutons", Order = 2)] List<ClickPoint> _boutons = [];
    [DataMember(Name = "Detection", Order = 3)] bool? _detection = false;
    [DataMember(Name = "Seuil", Order = 4)] int? _similarity = DefaultSimilarity;
    [DataMember(Name = "Images", Order = 5)] List<ImageButton> _images = [];
    [DataMember(Name = "ToucheLancer", Order = 6)] string _hotkeyStart = DefaultHotkeyStart;
    [DataMember(Name = "ToucheMenu", Order = 7)] string _hotkeyMenu = DefaultHotkeyMenu;
    [DataMember(Name = "MacroTouches", Order = 8)] string _macroKeys = DefaultMacroKeys;
    [DataMember(Name = "MacroToutes", Order = 9)] double? _macroKeysEvery = 10;
    [DataMember(Name = "SourisToutes", Order = 10)] double? _macroMouseEvery = 3;
    [DataMember(Name = "SourisDistance", Order = 11)] int? _macroMouseDistance = 10;
    [DataMember(Name = "ArretApresMin", Order = 12)] int? _stopAfterMin = 0;
    [DataMember(Name = "ArretA", Order = 13, EmitDefaultValue = false)] string _stopAt;
    [DataMember(Name = "Photos", Order = 14)] bool? _photos = true;
    [DataMember(Name = "InactiviteMin", Order = 15)] int? _idleMin = 10;
    [DataMember(Name = "Alertes", Order = 16)] AlertSettings _alerts = new();
    [DataMember(Name = "CaptureToutesMin", Order = 17)] int? _monitorMin = 0;
    [DataMember(Name = "CaptureSiArret", Order = 18)] bool? _monitorWhenStopped = false;
    [DataMember(Name = "Telecommande", Order = 19)] bool? _remote = false;
    [DataMember(Name = "PauseHorsJeu", Order = 20)] bool? _pauseOutsideGame = false;
    [DataMember(Name = "JeuProcessus", Order = 21, EmitDefaultValue = false)] string _gameProcess;
    [DataMember(Name = "JeuTitre", Order = 22, EmitDefaultValue = false)] string _gameTitle;

    public const string DefaultHotkeyStart = "F10", DefaultHotkeyMenu = "Inser", DefaultMacroKeys = "Alt ; Maj+Z ; Inser";

    public bool Macro { get => _macro ?? true; set => _macro = value; }

    public KeyCombo HotkeyStart
    {
        get => KeyCombo.TryParse(_hotkeyStart, out var k) ? k : Default(DefaultHotkeyStart);
        set => _hotkeyStart = value?.ToString() ?? DefaultHotkeyStart;
    }
    public KeyCombo HotkeyMenu
    {
        get => KeyCombo.TryParse(_hotkeyMenu, out var k) ? k : Default(DefaultHotkeyMenu);
        set => _hotkeyMenu = value?.ToString() ?? DefaultHotkeyMenu;
    }
    static KeyCombo Default(string s) { KeyCombo.TryParse(s, out var k); return k; }

    /// <summary>Touches de la macro anti-AFK, ex. « Alt ; Maj+Z ; Inser ».</summary>
    public string MacroKeysText { get => _macroKeys ?? DefaultMacroKeys; set => _macroKeys = value; }
    public List<KeyCombo> MacroKeys =>
        KeyCombo.TryParseList(MacroKeysText, out var list, out _) ? list : (KeyCombo.TryParseList(DefaultMacroKeys, out var d, out _) ? d : []);
    public double MacroKeysEvery { get => Clamp(_macroKeysEvery ?? 10, 1, 3600); set => _macroKeysEvery = value; }
    public double MacroMouseEvery { get => Clamp(_macroMouseEvery ?? 3, 0.5, 3600); set => _macroMouseEvery = value; }
    public int MacroMouseDistance { get => (int)Clamp(_macroMouseDistance ?? 10, 0, 200); set => _macroMouseDistance = value; }

    /// <summary>Arrêt automatique après ce nombre de minutes (0 = jamais).</summary>
    public int StopAfterMin { get => (int)Clamp(_stopAfterMin ?? 0, 0, 7 * 24 * 60); set => _stopAfterMin = value; }
    /// <summary>Arrêt automatique à cette heure (« 23:30 »), ou null.</summary>
    public TimeSpan? StopAt
    {
        get => TimeSpan.TryParse(_stopAt, out var t) && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1) ? t : null;
        set => _stopAt = value?.ToString(@"hh\:mm");
    }
    /// <summary>Garder des photos de l'écran (dossier captures) à chaque bouton détecté, inactivité, arrêt.</summary>
    public bool Photos { get => _photos ?? true; set => _photos = value; }
    /// <summary>Prévenir si aucun bouton n'a été détecté depuis ce nombre de minutes (0 = jamais).</summary>
    public int IdleMin { get => (int)Clamp(_idleMin ?? 10, 0, 24 * 60); set => _idleMin = value; }
    public AlertSettings Alerts { get => _alerts ??= new AlertSettings(); set => _alerts = value; }
    /// <summary>Surveillance : envoyer une capture d'écran sur le téléphone toutes les X minutes (0 = jamais).</summary>
    public int MonitorMin { get => (int)Clamp(_monitorMin ?? 0, 0, 24 * 60); set => _monitorMin = value; }
    /// <summary>Surveillance aussi quand les clics sont arrêtés.</summary>
    public bool MonitorWhenStopped { get => _monitorWhenStopped ?? false; set => _monitorWhenStopped = value; }
    /// <summary>Commander le programme depuis le téléphone (ntfy ou Telegram).</summary>
    public bool Remote { get => _remote ?? false; set => _remote = value; }
    /// <summary>Mettre en pause quand le jeu n'est pas la fenêtre au premier plan.</summary>
    public bool PauseOutsideGame { get => _pauseOutsideGame ?? false; set => _pauseOutsideGame = value; }
    /// <summary>Nom du programme du jeu (ex. « FortniteClient-Win64-Shipping »).</summary>
    public string GameProcess { get => _gameProcess; set => _gameProcess = value; }
    /// <summary>Titre de sa fenêtre au moment du choix (pour l'affichage).</summary>
    public string GameTitle { get => _gameTitle; set => _gameTitle = value; }

    static double Clamp(double v, double min, double max) => double.IsNaN(v) ? min : Math.Max(min, Math.Min(max, v));
    public List<ClickPoint> Boutons => _boutons ??= [];
    /// <summary>Détection auto : cliquer dès qu'une des images apparaît à l'écran.</summary>
    public bool Detection { get => _detection ?? false; set => _detection = value; }
    /// <summary>Ressemblance minimum en % pour considérer une image comme trouvée.</summary>
    public int Similarity
    {
        get => Math.Max(MinSimilarity, Math.Min(MaxSimilarity, _similarity ?? DefaultSimilarity));
        set => _similarity = value;
    }
    public List<ImageButton> Images => _images ??= [];

    public static double ClampDelay(double d) =>
        double.IsNaN(d) || d <= 0 ? DefaultDelay : Math.Max(MinDelay, Math.Min(MaxDelay, d));

    public static double ClampImageDelay(double d) =>
        double.IsNaN(d) || d <= 0 ? DefaultImageDelay : Math.Max(MinImageDelay, Math.Min(MaxDelay, d));

    /// <summary>
    /// Config perso intégrée dans l'exe (dossier presets), utilisée au premier lancement,
    /// ou null s'il n'y en a pas (édition client). Le nom vient du fichier intégré.
    /// </summary>
    public static string DefaultPreset { get; } = typeof(Config).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("AutoClic.presets.") && n.EndsWith(".json"))
        .Select(n => n.Substring("AutoClic.presets.".Length, n.Length - "AutoClic.presets.".Length - ".json".Length))
        .FirstOrDefault();

    /// <summary>Vrai si boutons.json vient d'être créé (premier lancement sur ce PC).</summary>
    public static bool FirstRun { get; private set; }

    /// <summary>La config perso est-elle dans cet exe ? (absente de l'édition client)</summary>
    public static bool HasPreset(string name) =>
        name != null && typeof(Config).Assembly.GetManifestResourceInfo($"AutoClic.presets.{name}.json") != null;

    public static Config Load()
    {
        string path = Paths.ConfigFile;
        if (!File.Exists(path))
        {
            // Premier lancement sur ce PC : on part de la config perso intégrée
            FirstRun = true;
            var preset = HasPreset(DefaultPreset) ? LoadPreset(DefaultPreset) : null;
            var config = preset ?? new Config();
            config.Save();
            Log.Info(preset != null ? $"Premier lancement : config « {DefaultPreset} » chargée ({preset.Boutons.Count} bouton(s))"
                                    : "Premier lancement : nouvelle configuration");
            return config;
        }
        try
        {
            // ReadAllText enlève l'éventuel BOM écrit par l'ancienne version
            return Parse(new UTF8Encoding(false).GetBytes(File.ReadAllText(path, Encoding.UTF8)));
        }
        catch (Exception ex)
        {
            Log.Error("lecture de boutons.json", ex);
            try { File.Copy(path, path + ".illisible", true); } catch { }
            return new Config();
        }
    }

    static Config Parse(byte[] json)
    {
        using var ms = new MemoryStream(json);
        var config = (Config)new DataContractJsonSerializer(typeof(Config)).ReadObject(ms) ?? new Config();
        config.Boutons.RemoveAll(b => b == null);
        foreach (var b in config.Boutons) b.Delay = ClampDelay(b.Delay);
        config.Images.RemoveAll(i => i == null || string.IsNullOrEmpty(i.File));
        foreach (var i in config.Images) i.Delay = ClampImageDelay(i.Delay);
        return config;
    }

    /// <summary>Lit une config intégrée dans l'exe (dossier presets), ou null si absente.</summary>
    public static Config LoadPreset(string name)
    {
        try
        {
            using var stream = typeof(Config).Assembly.GetManifestResourceStream($"AutoClic.presets.{name}.json");
            if (stream == null) return null;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return Parse(ms.ToArray());
        }
        catch (Exception ex)
        {
            Log.Error($"config intégrée {name}", ex);
            return null;
        }
    }

    public void Save()
    {
        try
        {
            using var ms = new MemoryStream();
            using (var writer = JsonReaderWriterFactory.CreateJsonWriter(ms, new UTF8Encoding(false), false, true, "    "))
                new DataContractJsonSerializer(typeof(Config)).WriteObject(writer, this);

            // Écriture dans un fichier temporaire puis remplacement : jamais de fichier à moitié écrit
            string path = Paths.ConfigFile, tmp = path + ".tmp";
            File.WriteAllBytes(tmp, ms.ToArray());
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
        catch (Exception ex) { Log.Error("sauvegarde de boutons.json", ex); }
    }
}
