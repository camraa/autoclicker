using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace AutoClic;

/// <summary>Une image à détecter, prête pour le moteur.</summary>
internal sealed class DetectTarget(int number, Template template, ImageButton img)
{
    public int Number { get; } = number;
    public Template Template { get; } = template;
    /// <summary>Après une action sur ce bouton, secondes avant de pouvoir recommencer.</summary>
    public double Delay { get; } = img.Delay;
    /// <summary>Si rempli : on agit sur ce texte, lu dans la zone trouvée (et pas ailleurs).</summary>
    public string Text { get; } = string.IsNullOrWhiteSpace(img.Text) ? null : img.Text.Trim();
    public string Action { get; } = img.ActionOrDefault;
    public KeyCombo Key { get; } = KeyCombo.TryParse(img.Key, out var k) ? k : null;
    /// <summary>Zone de l'écran où chercher, ou null pour tout l'écran.</summary>
    public Rectangle? Zone { get; } = img.Zone?.ToScreen();
    public bool Alert { get; } = img.Alert;
    public string File { get; } = img.File;
    /// <summary>Enchaînement : fichier du bouton après lequel celui-ci devient actif (null = toujours actif).</summary>
    public string After { get; } = string.IsNullOrEmpty(img.After) || img.After == img.File ? null : img.After;
    public double AfterDelay { get; } = Math.Max(0, img.AfterDelay);
    public double AfterWindow { get; } = img.AfterWindowOrDefault;

    public string Describe() => Action switch
    {
        ImageButton.ActionDouble => "double-clic",
        ImageButton.ActionRight => "clic droit",
        ImageButton.ActionKey => $"touche {Key}",
        _ => "clic",
    };
}

/// <summary>Réglages du moteur (copie figée, remplacée d'un bloc quand on change quelque chose).</summary>
internal sealed class EngineSettings
{
    public KeyCombo HotkeyStart { get; set; }
    public KeyCombo HotkeyMenu { get; set; }
    public bool Macro { get; set; } = true;
    public KeyCombo[] MacroKeys { get; set; } = [];
    public double MacroKeysEvery { get; set; } = 10;
    public double MacroMouseEvery { get; set; } = 3;
    public int MacroMouseDistance { get; set; } = 10;
    public bool Detect { get; set; }
    public double MinSimilarity { get; set; } = Config.DefaultSimilarity;
    public int StopAfterMin { get; set; }
    public TimeSpan? StopAt { get; set; }
    public bool Photos { get; set; }
    public int IdleMin { get; set; }
    public AlertSettings Alerts { get; set; } = new();
    public int MonitorMin { get; set; }
    public bool MonitorWhenStopped { get; set; }
    public bool PauseOutsideGame { get; set; }
    public string GameProcess { get; set; }

    static AlertSettings WithRemote(AlertSettings a, bool remote) { a.RemoteButtons = remote; return a; }

    public static EngineSettings From(Config c) => new()
    {
        HotkeyStart = c.HotkeyStart,
        HotkeyMenu = c.HotkeyMenu,
        Macro = c.Macro,
        MacroKeys = c.MacroKeys.ToArray(),
        MacroKeysEvery = c.MacroKeysEvery,
        MacroMouseEvery = c.MacroMouseEvery,
        MacroMouseDistance = c.MacroMouseDistance,
        Detect = c.Detection,
        MinSimilarity = c.Similarity,
        StopAfterMin = c.StopAfterMin,
        StopAt = c.StopAt,
        Photos = c.Photos,
        IdleMin = c.IdleMin,
        Alerts = WithRemote(c.Alerts.Clone(), c.Remote),
        MonitorMin = c.MonitorMin,
        MonitorWhenStopped = c.MonitorWhenStopped,
        PauseOutsideGame = c.PauseOutsideGame && !string.IsNullOrWhiteSpace(c.GameProcess),
        GameProcess = c.GameProcess,
    };
}

/// <summary>
/// Le moteur : clics + détection d'images + macro + raccourcis clavier.
/// Il tourne sur ses propres fils, séparés de l'interface : une fenêtre bloquée, un menu
/// ouvert ou une erreur n'arrêtent jamais les clics. Chaque tour de boucle est protégé :
/// une erreur est écrite dans le journal et la boucle continue.
/// </summary>
internal sealed class Engine
{
    const double StatusEvery = 600;          // une ligne « en cours » dans le journal toutes les 10 min
    const double StartDelay = 0.5;           // le temps que le menu et les ronds disparaissent
    const double DetectEvery = 0.4;          // une capture d'écran toutes les 0,4 s
    const double DetectMaxAge = 1.5;         // une image vue il y a plus longtemps n'est plus utilisée
    const double PhotoEvery = 5;             // au plus une photo toutes les 5 s par bouton
    const double PhoneEvery = 60;            // au plus une alerte par minute pour un même évènement

    /// <summary>Levés depuis les fils du moteur : l'interface doit repasser sur son propre fil.</summary>
    public event Action<bool> RunningChanged;
    public event Action MenuKeyPressed;
    public event Action NothingToDo;
    public event Action<string> Notice;      // petite notification à l'écran

    readonly Stopwatch _clock = Stopwatch.StartNew();   // ne saute jamais, contrairement à l'heure Windows
    readonly Reporter _reporter = new();
    volatile ClickPoint[] _points = [];
    volatile DetectTarget[] _targets = [];
    volatile EngineSettings _settings = new();
    volatile bool _running, _quit;
    double _pausedUntil;
    volatile bool _gamePaused;
    double _nextGameCheck;
    readonly Dictionary<int, string> _processNames = [];
    int _toggleRequest;
    long _clicks, _workerBeat;
    Thread _worker, _keys, _detector;

    // État d'une session (utilisé uniquement par le fil des clics, sauf RunDetection partagé)
    ClickPoint[] _run = [];
    volatile RunDetection _runDetect;
    int _index;
    double _nextClick, _nextKeys, _nextMouse, _nextStatus, _stopAfter, _lastActivity, _runStart;
    double _nextMonitor = double.MaxValue;
    int _monitorMinSeen = -1;
    DateTime? _stopAtWall;
    bool _inputRefused, _idleReported, _wasPaused;
    readonly Dictionary<string, double> _lastPhone = [];

    /// <summary>Résultat d'une capture : bouton vu à tel endroit, capture commencée à tel moment.</summary>
    sealed class Sighting(Point center, double seenAt, double similarity)
    {
        public Point Center { get; } = center;
        public double SeenAt { get; } = seenAt;
        public double Similarity { get; } = similarity;
    }

    /// <summary>Partagé entre le fil de détection (qui trouve) et le fil des clics (qui agit).</summary>
    sealed class RunDetection(DetectTarget[] targets)
    {
        public DetectTarget[] Targets { get; } = targets;
        public Sighting[] Seen { get; } = new Sighting[targets.Length];
        public double[] LastClick { get; } = Enumerable.Repeat(double.MinValue, targets.Length).ToArray();
        public double[] LastPhoto { get; } = Enumerable.Repeat(double.MinValue, targets.Length).ToArray();
        public double[] CooldownUntil { get; } = new double[targets.Length];
        /// <summary>Pour ne noter qu'une fois « image vue mais texte pas lu ».</summary>
        public bool[] TextMissLogged { get; } = new bool[targets.Length];
        /// <summary>Pour chaque bouton enchaîné : l'index du bouton précédent (-1 = toujours actif).</summary>
        public int[] AfterIndex { get; } = targets.Select(t => t.After == null ? -1 : Array.FindIndex(targets, o => o.File == t.After)).ToArray();

        /// <summary>Un bouton enchaîné n'est actif que juste après l'action sur le bouton précédent.</summary>
        public bool Active(int i, double now)
        {
            int a = AfterIndex[i];
            if (a < 0) return true;
            double last = Volatile.Read(ref LastClick[a]);
            if (last == double.MinValue) return false;
            double dt = now - last;
            return dt >= Targets[i].AfterDelay && dt <= Targets[i].AfterDelay + Targets[i].AfterWindow;
        }
    }

    public bool Running => _running;
    public long Clicks => Interlocked.Read(ref _clicks);
    /// <summary>Vrai pendant qu'on choisit une touche dans les paramètres : les raccourcis ne font rien.</summary>
    public volatile bool SuspendHotkeys;
    public EngineSettings Settings
    {
        get => _settings;
        set { _settings = value ?? new EngineSettings(); _reporter.Alerts = _settings.Alerts; }
    }
    double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>Secondes de pause restantes (0 = pas en pause).</summary>
    public double PausedRemaining => _running ? Math.Max(0, Volatile.Read(ref _pausedUntil) - Now) : 0;
    /// <summary>En pause parce que le jeu n'est pas au premier plan.</summary>
    public bool GamePaused => _running && _gamePaused;

    public void SetPoints(IEnumerable<ClickPoint> points) => _points = points.Select(p => p.Clone()).ToArray();
    public void SetTargets(IEnumerable<DetectTarget> targets) => _targets = targets.ToArray();

    /// <summary>Lancer / arrêter, comme la touche (utilisé par le menu de l'icône).</summary>
    public void RequestToggle(string reason = null) { _toggleReason = reason; Interlocked.Exchange(ref _toggleRequest, 1); }
    volatile string _toggleReason;

    public void Pause(double seconds)
    {
        if (!_running) return;
        Volatile.Write(ref _pausedUntil, Now + seconds);
        Log.Info($"Pause de {seconds / 60:0} min");
    }

    public void Resume()
    {
        if (PausedRemaining <= 0) return;
        Volatile.Write(ref _pausedUntil, 0);
        Log.Info("Reprise après pause");
    }

    public void Start()
    {
        _quit = false;
        _reporter.Start();
        StartWorker();
        StartKeys();
        StartDetector();
    }

    void StartWorker()
    {
        Volatile.Write(ref _workerBeat, _clock.ElapsedMilliseconds);
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "AutoClic.Clics", Priority = ThreadPriority.AboveNormal };
        _worker.Start();
    }

    void StartKeys()
    {
        _keys = new Thread(KeysLoop) { IsBackground = true, Name = "AutoClic.Touches", Priority = ThreadPriority.AboveNormal };
        _keys.Start();
    }

    void StartDetector()
    {
        _detector = new Thread(DetectLoop) { IsBackground = true, Name = "AutoClic.Detection", Priority = ThreadPriority.BelowNormal };
        _detector.Start();
    }

    /// <summary>Appelé régulièrement par l'interface : relance un fil s'il s'est arrêté.</summary>
    public void Watchdog()
    {
        if (_quit) return;
        long now = _clock.ElapsedMilliseconds;
        if (!_worker.IsAlive)
        {
            Log.Info("Le fil des clics s'est arrêté : redémarrage");
            bool wasRunning = _running;
            _running = false;
            StartWorker();
            if (wasRunning) RequestToggle();   // reprend là où il en était
        }
        else if (now - Volatile.Read(ref _workerBeat) > 30_000)
        {
            Log.Info("Le fil des clics ne répond plus depuis 30 s");
            Volatile.Write(ref _workerBeat, now);
        }
        if (!_keys.IsAlive) { Log.Info("Le fil des touches s'est arrêté : redémarrage"); StartKeys(); }
        if (!_detector.IsAlive) { Log.Info("Le fil de détection s'est arrêté : redémarrage"); StartDetector(); }
        if (!_reporter.IsAlive) { Log.Info("Le fil des rapports s'est arrêté : redémarrage"); _reporter.Start(); }
    }

    public void Shutdown()
    {
        _quit = true;
        _worker?.Join(3000);
        _keys?.Join(1000);
        _detector?.Join(2000);
        _reporter.Stop();
    }

    // ---------- Raccourcis clavier (fil séparé : jamais retardé par un clic ou la macro) ----------
    void KeysLoop()
    {
        EngineSettings seen = null;
        bool startPrev = false, menuPrev = false;
        while (!_quit)
        {
            try
            {
                var s = _settings;
                bool start = s.HotkeyStart?.IsDown() ?? false, menu = s.HotkeyMenu?.IsDown() ?? false;
                if (s != seen) { seen = s; startPrev = start; menuPrev = menu; }   // réglages changés : pas de faux appui
                if (SuspendHotkeys) { startPrev = start; menuPrev = menu; Thread.Sleep(10); continue; }
                if (start && !startPrev) RequestToggle("touche " + s.HotkeyStart);
                // Touche du menu ignorée pendant que ça tourne (la macro peut appuyer dessus)
                if (menu && !menuPrev && !_running) Raise(MenuKeyPressed);
                startPrev = start;
                menuPrev = menu;
            }
            catch (Exception ex) { Log.Error("touches", ex); Thread.Sleep(250); }
            Thread.Sleep(10);
        }
    }

    // ---------- Détection : capture l'écran et cherche les boutons (n'agit pas lui-même) ----------
    void DetectLoop()
    {
        bool failing = false;
        TextFinder text = null;                    // créé sur ce fil, la première fois qu'un texte est demandé
        while (!_quit)
        {
            double wait = DetectEvery;
            try
            {
                var run = _runDetect;
                var s = _settings;
                if (_running && s.Detect && run != null && run.Targets.Length > 0 && PausedRemaining <= 0 && !_gamePaused)
                {
                    double start = Now;
                    var shot = ScreenShot.Capture();
                    if (failing) { Log.Info("La capture d'écran fonctionne de nouveau"); failing = false; }
                    for (int i = 0; i < run.Targets.Length && _running; i++)
                    {
                        if (Now < Volatile.Read(ref run.CooldownUntil[i])) continue;
                        if (!run.Active(i, Now)) { Volatile.Write(ref run.Seen[i], null); continue; }
                        var target = run.Targets[i];
                        var m = Detector.Find(shot, target.Template, s.MinSimilarity, target.Zone);
                        Sighting found = null;
                        if (m.Found && target.Text == null)
                        {
                            found = new Sighting(m.Center, start, m.Similarity);
                        }
                        else if (m.Found)
                        {
                            // Zone trouvée : on lit le texte dedans et on vise pile dessus
                            text ??= new TextFinder();
                            var hit = text.Find(shot, m.Area, target.Text);
                            if (hit.Found)
                            {
                                found = new Sighting(hit.Center, start, m.Similarity);
                                run.TextMissLogged[i] = false;
                            }
                            else if (!run.TextMissLogged[i])
                            {
                                Log.Info(hit.Error ?? $"Bouton {target.Number} vu ({m.Similarity:0} %) mais texte « {target.Text} » pas lu dedans " +
                                                      $"(lu : « {Shorten(hit.Read)} ») : rien fait");
                                run.TextMissLogged[i] = true;
                            }
                        }
                        Volatile.Write(ref run.Seen[i], found);
                    }
                }
            }
            catch (Exception ex)
            {
                // Ex. écran verrouillé : la capture échoue. On le note une fois, puis on réessaie chaque seconde.
                if (!failing)
                {
                    Log.Error("détection", ex);
                    Problem("La capture d'écran ne marche plus (écran verrouillé ?)");
                }
                failing = true;
                wait = 1;
            }
            Thread.Sleep((int)(wait * 1000));
        }
    }

    // ---------- Clics + macro ----------
    void WorkerLoop()
    {
        while (!_quit)
        {
            try
            {
                Volatile.Write(ref _workerBeat, _clock.ElapsedMilliseconds);
                if (Interlocked.Exchange(ref _toggleRequest, 0) == 1)
                {
                    string why = _toggleReason ?? "touche " + (_settings.HotkeyStart?.ToString() ?? "F10");
                    _toggleReason = null;
                    if (_running) StopRun(why, false);
                    else StartRun();
                }
                if (_running) Step();
                Monitor();
            }
            catch (Exception ex) { Log.Error("clics", ex); Thread.Sleep(250); }
            Thread.Sleep(5);
        }
        try { if (_running) StopRun("fermeture du programme", false); }
        catch (Exception ex) { Log.Error("arrêt", ex); }
    }

    void StartRun()
    {
        var s = _settings;
        var points = _points;
        var targets = s.Detect ? _targets : [];
        if (points.Length == 0 && targets.Length == 0 && !s.Macro) { Raise(NothingToDo); return; }

        _run = points;
        _runDetect = new RunDetection(targets);
        _index = 0;
        Interlocked.Exchange(ref _clicks, 0);
        Volatile.Write(ref _pausedUntil, 0);
        _inputRefused = false;
        _idleReported = false;
        _wasPaused = false;
        _gamePaused = false;
        _nextGameCheck = 0;
        _lastPhone.Clear();
        double now = Now;
        _nextClick = now + StartDelay;
        _nextKeys = now + s.MacroKeysEvery;
        _nextMouse = now + s.MacroMouseEvery;
        _nextStatus = now + StatusEvery;
        _lastActivity = now;
        _runStart = now;
        if (s.MonitorMin > 0) _nextMonitor = now + s.MonitorMin * 60.0;
        _stopAfter = s.StopAfterMin > 0 ? now + s.StopAfterMin * 60.0 : double.MaxValue;
        _stopAtWall = null;
        if (s.StopAt.HasValue)
        {
            var at = DateTime.Today + s.StopAt.Value;
            _stopAtWall = at > DateTime.Now ? at : at.AddDays(1);
        }
        // Pas de détection avant que le menu et les ronds aient disparu
        for (int i = 0; i < targets.Length; i++) _runDetect.CooldownUntil[i] = now + StartDelay;

        // Empêche la veille et l'extinction de l'écran tant que ça tourne
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED | Native.ES_DISPLAY_REQUIRED);
        _running = true;
        string stops = (s.StopAfterMin > 0 ? $", arrêt auto dans {s.StopAfterMin} min" : "") +
                       (_stopAtWall.HasValue ? $", arrêt auto à {_stopAtWall:HH:mm}" : "");
        Log.Info($"Lancé : {points.Length} bouton(s) fixe(s), {targets.Length} bouton(s) à détecter, " +
                 $"macro {(s.Macro ? "activée" : "désactivée")}{stops}");
        Raise(RunningChanged, true);
    }

    void StopRun(string reason, bool automatic)
    {
        _running = false;
        Volatile.Write(ref _pausedUntil, 0);
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        Log.Info($"Arrêté ({reason}) après {Clicks} clic(s)");
        if (automatic)
        {
            var s = _settings;
            _reporter.Add(new Report
            {
                Title = "Auto Clic : arrêté",
                Message = $"{reason} après {Clicks} clic(s).",
                Tag = "arret",
                KeepPhoto = s.Photos,
                SendToPhone = s.Alerts.OnStop,
            });
            Raise(Notice, $"Auto Clic : {reason}");
        }
        Raise(RunningChanged, false);
    }

    void Step()
    {
        var s = _settings;

        // Arrêt automatique
        if (Now >= _stopAfter) { StopRun($"arrêt automatique après {s.StopAfterMin} min", true); return; }
        if (_stopAtWall.HasValue && DateTime.Now >= _stopAtWall.Value) { StopRun($"arrêt automatique à {_stopAtWall:HH:mm}", true); return; }

        // Pause : rien du tout (ni clics, ni macro), on reprend ensuite là où on en était
        if (PausedRemaining > 0) { _wasPaused = true; return; }
        if (CheckGame(s)) { _wasPaused = true; return; }
        if (_wasPaused)
        {
            _wasPaused = false;
            double now = Now;
            _nextClick = now;
            _lastActivity = now;
            Log.Info("Fin de la pause");
            Raise(Notice, "Auto Clic : reprise");
        }

        var points = _run;
        if (points.Length > 0 && Now >= _nextClick)
        {
            if (_index >= points.Length) _index = 0;
            var p = points[_index];
            ReportInput(Input.Click(p.X, p.Y));
            Interlocked.Increment(ref _clicks);
            _nextClick = Now + p.Delay;            // temps propre à ce bouton
            _index = (_index + 1) % points.Length;
        }

        ActOnDetected(s);
        CheckIdle(s);

        if (s.Macro)
        {
            if (Now >= _nextKeys)
            {
                bool ok = true;
                for (int i = 0; i < s.MacroKeys.Length; i++)
                {
                    if (i > 0) Thread.Sleep(100);
                    ok &= Input.Combo(s.MacroKeys[i].Keys);
                }
                ReportInput(ok);
                _nextKeys = Now + s.MacroKeysEvery;
            }
            if (Now >= _nextMouse)
            {
                if (s.MacroMouseDistance > 0) ReportInput(Input.Jiggle(s.MacroMouseDistance));
                _nextMouse = Now + s.MacroMouseEvery;
            }
        }

        if (Now >= _nextStatus)
        {
            Log.Info($"En cours : {Clicks} clic(s)");
            _nextStatus = Now + StatusEvery;
        }
    }

    /// <summary>
    /// Surveillance : toutes les X minutes, une capture d'écran part sur le téléphone.
    /// La capture et l'envoi se font sur le fil des rapports (jamais « Impr. écran ») :
    /// les clics et la macro continuent sans interruption.
    /// </summary>
    void Monitor()
    {
        var s = _settings;
        if (s.MonitorMin != _monitorMinSeen)
        {
            _monitorMinSeen = s.MonitorMin;
            _nextMonitor = s.MonitorMin > 0 ? Now + s.MonitorMin * 60.0 : double.MaxValue;
        }
        if (s.MonitorMin <= 0 || Now < _nextMonitor) return;
        _nextMonitor = Now + s.MonitorMin * 60.0;
        if (!_running && !s.MonitorWhenStopped) return;
        SendCapture("Surveillance");
    }

    /// <summary>Envoie une capture de l'écran sur le téléphone, en arrière-plan.</summary>
    public void SendCapture(string why)
    {
        string state;
        if (_running)
        {
            var t = TimeSpan.FromSeconds(Now - _runStart);
            string since = t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00}" : $"{t.Minutes} min";
            state = PausedRemaining > 0 ? $"En pause ({Math.Ceiling(PausedRemaining / 60)} min) · {Clicks} clic(s)"
                  : _gamePaused ? $"En pause : jeu pas au premier plan · {Clicks} clic(s)"
                  : $"En cours depuis {since} · {Clicks} clic(s)";
        }
        else state = "Clics arrêtés";
        _reporter.Add(new Report
        {
            Title = $"Auto Clic : {why.ToLowerInvariant()}",
            Message = $"{state} · {DateTime.Now:HH:mm}",
            Tag = "surveillance",
            ForcePhoto = true,
            SendToPhone = true,
        });
    }

    // ---------- Jeu au premier plan ----------
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);

    /// <summary>Vrai si on doit rester en pause : le jeu choisi n'est pas la fenêtre au premier plan.</summary>
    bool CheckGame(EngineSettings s)
    {
        if (!s.PauseOutsideGame) { if (_gamePaused) _gamePaused = false; return false; }
        if (Now < _nextGameCheck) return _gamePaused;
        _nextGameCheck = Now + 0.25;
        string fg = ForegroundProcess(out int pid);
        // Nos propres fenêtres (notifications) ne comptent pas : on garde l'état actuel
        if (pid == Process.GetCurrentProcess().Id) return _gamePaused;
        bool outside = !string.Equals(fg, s.GameProcess, StringComparison.OrdinalIgnoreCase);
        if (outside != _gamePaused)
        {
            _gamePaused = outside;
            Log.Info(outside ? $"Pause : le jeu n'est plus au premier plan ({fg ?? "?"})" : "Le jeu est revenu au premier plan : reprise");
            Raise(Notice, outside ? "Auto Clic : en pause (le jeu n'est pas au premier plan)" : "Auto Clic : reprise (jeu au premier plan)");
        }
        return _gamePaused;
    }

    string ForegroundProcess(out int pid)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out pid);
        if (pid == 0) return null;
        if (_processNames.TryGetValue(pid, out var name)) return name;
        try { name = Process.GetProcessById(pid).ProcessName; } catch { name = null; }
        if (_processNames.Count > 200) _processNames.Clear();
        _processNames[pid] = name;
        return name;
    }

    // ---------- Commandes reçues du téléphone ----------
    /// <summary>Exécute une commande de la télécommande (« pause 20 », « capture »…).</summary>
    public void Command(string text)
    {
        var parts = (text ?? "").Trim().Split([' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        string cmd = TextFinder.Normalize(parts[0]);
        int.TryParse(parts.Length > 1 ? parts[1] : "", out int minutes);
        switch (cmd)
        {
            case "lancer": case "lance": case "go": case "demarrer": case "run":
                if (_running) Reply("Déjà en cours.");
                else { RequestToggle("téléphone"); Reply("Lancé."); }
                break;
            case "stop": case "arret": case "arreter": case "stopper":
                if (!_running) Reply("Déjà arrêté.");
                else { RequestToggle("téléphone"); Reply($"Arrêté après {Clicks} clic(s)."); }
                break;
            case "pause":
                if (!_running) { Reply("Pas lancé : rien à mettre en pause."); break; }
                minutes = minutes > 0 ? Math.Min(minutes, 24 * 60) : 15;
                Pause(minutes * 60.0);
                Raise(Notice, $"Auto Clic : pause {minutes} min (depuis le téléphone)");
                Reply($"Pause {minutes} min. Envoie « reprendre » pour repartir avant.");
                break;
            case "reprendre": case "resume": case "continuer": case "reprise":
                if (PausedRemaining <= 0) Reply(_running ? "Pas en pause." : "Pas lancé.");
                else { Resume(); Raise(Notice, "Auto Clic : reprise (depuis le téléphone)"); Reply("Reprise."); }
                break;
            case "capture": case "photo": case "screen": case "ecran":
                SendCapture("Capture demandée");
                break;
            case "etat": case "status": case "statut": case "info":
                SendCapture("État");
                break;
            default:
                Reply(RemoteControl.Help);
                break;
        }
    }

    void Reply(string text) => _reporter.Add(new Report
    {
        Title = "Auto Clic",
        Message = text,
        Tag = "commande",
        SendToPhone = true,
        NoPhoto = true,
    });

    /// <summary>Agit sur les boutons que le fil de détection vient de voir à l'écran.</summary>
    void ActOnDetected(EngineSettings s)
    {
        var run = _runDetect;
        if (run == null || !s.Detect) return;
        for (int i = 0; i < run.Targets.Length; i++)
        {
            var seen = Volatile.Read(ref run.Seen[i]);
            if (seen == null) continue;
            double now = Now;
            // Vu avant la dernière action (capture déjà en cours), trop ancien, ou en pause : on ignore
            if (seen.SeenAt <= run.LastClick[i] || now - seen.SeenAt > DetectMaxAge || now < run.CooldownUntil[i]) continue;
            if (!run.Active(i, now)) continue;
            var target = run.Targets[i];

            // Photo juste avant d'agir (le bouton est encore à l'écran)
            bool phone = target.Alert && s.Alerts.Enabled && CanPhone("bouton" + i);
            bool photo = (s.Photos && now - run.LastPhoto[i] >= PhotoEvery) || phone;
            Bitmap shot = null;
            if (photo)
            {
                try { shot = ScreenShot.CaptureBitmap(SystemInformation.VirtualScreen); run.LastPhoto[i] = now; }
                catch (Exception ex) { Log.Error("photo", ex); }
            }

            bool ok = target.Action switch
            {
                ImageButton.ActionDouble => Input.DoubleClick(seen.Center.X, seen.Center.Y),
                ImageButton.ActionRight => Input.RightClick(seen.Center.X, seen.Center.Y),
                ImageButton.ActionKey when target.Key != null => Input.Combo(target.Key.Keys),
                _ => Input.Click(seen.Center.X, seen.Center.Y),
            };
            ReportInput(ok);
            Interlocked.Increment(ref _clicks);
            Volatile.Write(ref run.LastClick[i], Now);
            _lastActivity = Now;
            _idleReported = false;
            Volatile.Write(ref run.CooldownUntil[i], Now + target.Delay);
            Volatile.Write(ref run.Seen[i], null);

            string what = (target.Text == null ? "" : $", texte « {target.Text} »") +
                          (run.AfterIndex[i] >= 0 ? $", après le bouton {run.Targets[run.AfterIndex[i]].Number}" : "");
            string action = target.Describe();
            Log.Info($"Bouton {target.Number} détecté ({seen.Similarity:0} %{what}) : {action} en {seen.Center.X}, {seen.Center.Y}");
            _reporter.Add(new Report
            {
                Title = $"Auto Clic : bouton {target.Number} détecté",
                Message = $"{char.ToUpper(action[0]) + action.Substring(1)} fait (ressemblance {seen.Similarity:0} %{what}).",
                Tag = $"bouton-{target.Number}",
                Photo = shot,
                Mark = target.Action == ImageButton.ActionKey ? null : seen.Center,
                KeepPhoto = shot != null && s.Photos,
                SendToPhone = phone,
            });
        }
    }

    /// <summary>Aucun bouton détecté depuis longtemps : photo + alerte (une fois, jusqu'au prochain bouton).</summary>
    void CheckIdle(EngineSettings s)
    {
        var run = _runDetect;
        if (run == null || !s.Detect || run.Targets.Length == 0 || s.IdleMin <= 0 || _idleReported) return;
        if (Now - _lastActivity < s.IdleMin * 60.0) return;
        _idleReported = true;
        Log.Info($"Aucun bouton détecté depuis {s.IdleMin} min");
        _reporter.Add(new Report
        {
            Title = "Auto Clic : plus rien ne se passe",
            Message = $"Aucun bouton détecté depuis {s.IdleMin} min ({Clicks} clic(s) au total).",
            Tag = "inactivite",
            KeepPhoto = s.Photos,
            SendToPhone = s.Alerts.OnIdle,
        });
    }

    /// <summary>Note dans le journal quand Windows refuse les clics (écran verrouillé…) et quand ça repart.</summary>
    void ReportInput(bool ok)
    {
        if (!ok && !_inputRefused)
        {
            Log.Info($"Windows refuse les clics / touches (écran verrouillé ?) après {Clicks} clic(s)");
            Problem("Windows refuse les clics (écran verrouillé ?)");
        }
        if (ok && _inputRefused) Log.Info("Les clics passent de nouveau");
        _inputRefused = !ok;
    }

    void Problem(string message)
    {
        var s = _settings;
        _reporter.Add(new Report
        {
            Title = "Auto Clic : problème",
            Message = message,
            Tag = "probleme",
            KeepPhoto = s.Photos,
            SendToPhone = s.Alerts.OnProblem && CanPhone("probleme"),
        });
    }

    /// <summary>Limite les alertes : une par minute pour un même évènement.</summary>
    bool CanPhone(string key)
    {
        lock (_lastPhone)
        {
            double now = Now;
            if (_lastPhone.TryGetValue(key, out var last) && now - last < PhoneEvery) return false;
            _lastPhone[key] = now;
            return true;
        }
    }

    static string Shorten(string s) => string.IsNullOrEmpty(s) ? "rien" : s.Length > 60 ? s.Substring(0, 60) + "…" : s;

    static void Raise(Action handler)
    {
        try { handler?.Invoke(); } catch (Exception ex) { Log.Error("événement", ex); }
    }

    static void Raise<T>(Action<T> handler, T value)
    {
        try { handler?.Invoke(value); } catch (Exception ex) { Log.Error("événement", ex); }
    }
}
