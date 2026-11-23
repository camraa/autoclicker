using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace AutoClic;

/// <summary>Le programme lui-même : icône près de l'horloge, menu, moteur, notifications.</summary>
internal sealed class TrayApp : ApplicationContext
{
    const double PauseSeconds = 5 * 60;

    readonly Control _ui;                       // sert à repasser sur le fil de l'interface
    readonly Config _config;
    readonly Engine _engine;
    readonly MenuForm _menu;
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _itemMenu, _itemToggle, _itemPause, _itemSettings;
    readonly System.Windows.Forms.Timer _uiTimer;
    readonly RegisteredWaitHandle _showWait;
    readonly RemoteControl _remote;
    bool _quitting;

    public TrayApp(EventWaitHandle showEvent)
    {
        _ui = new Control();
        _ui.CreateControl();
        _ = _ui.Handle;

        _config = Config.Load();
        _engine = new Engine();
        _menu = new MenuForm(_config, _engine, Quit);     // donne aussi les réglages au moteur

        _itemMenu = new ToolStripMenuItem("Afficher le menu", null, (_, _) => ShowMenu());
        _itemToggle = new ToolStripMenuItem("Lancer", null, (_, _) => _engine.RequestToggle("menu de l'icône"));
        _itemPause = new ToolStripMenuItem("Pause 5 min", null, (_, _) => TogglePause());
        _itemSettings = new ToolStripMenuItem("Paramètres…", null, (_, _) => _menu.OpenSettings());
        var menu = MacMenu.Create();
        menu.Items.Add(_itemMenu);
        menu.Items.Add(_itemToggle);
        menu.Items.Add(_itemPause);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_itemSettings);
        if (Config.HasPreset(Config.DefaultPreset))
            menu.Items.Add(new ToolStripMenuItem($"Charger la config « {Config.DefaultPreset} »", null, (_, _) => _menu.ApplyPreset(Config.DefaultPreset)));
        menu.Items.Add(new ToolStripMenuItem("Envoyer une capture au téléphone", null, (_, _) => SendCapture()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Aide (mode d'emploi)", null, (_, _) => OpenHelp()));
        menu.Items.Add(new ToolStripMenuItem("Ouvrir le dossier des réglages", null, (_, _) => SettingsForm.OpenFolder(Paths.DataDir)));
        menu.Items.Add(new ToolStripMenuItem("Ouvrir le journal", null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripMenuItem("Ouvrir les photos", null, (_, _) => SettingsForm.OpenFolder(Paths.PhotosDir)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quitter", null, (_, _) => Quit()));
        menu.Opening += (_, _) => UpdateTray();

        _tray = new NotifyIcon { Icon = AppIcon.Small, ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => ShowMenu();
        UpdateTray();

        _engine.RunningChanged += running => Post(() => OnRunningChanged(running));
        _engine.MenuKeyPressed += () => Post(() => { if (!_engine.Running) _menu.Toggle(); });
        _engine.NothingToDo += () => Post(() => Toast.Pop($"Ajoute d'abord un bouton ({_config.HotkeyMenu})"));
        _engine.Notice += text => Post(() => Toast.Pop(text, 2500));
        _engine.Start();

        // Télécommande depuis le téléphone (les commandes arrivent sur un autre fil)
        _remote = new RemoteControl(cmd => _engine.Command(cmd));
        _remote.Configure(_config.Alerts, _config.Remote);

        // Relancer AutoClic.exe alors qu'il tourne déjà = afficher le menu
        _showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Post(ShowMenu), null, -1, false);

        _uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _uiTimer.Tick += (_, _) =>
        {
            try { UpdateTray(); _engine.Watchdog(); _remote.Configure(_config.Alerts, _config.Remote); }
            catch (Exception ex) { Log.Error("minuterie", ex); }
        };
        _uiTimer.Start();

        if (Config.FirstRun)
        {
            // Première fois : on ouvre directement le menu pour montrer quoi faire
            Toast.Pop($"Bienvenue ! {_config.HotkeyMenu} = ce menu · {_config.HotkeyStart} = lancer / arrêter · aide : clic droit sur l'icône près de l'horloge", 7000);
            Post(ShowMenu);
        }
        else Toast.Pop($"Auto Clic prêt  ·  {_config.HotkeyMenu} = menu  ·  {_config.HotkeyStart} = lancer", 3000);
    }

    /// <summary>Exécute sur le fil de l'interface (les événements du moteur arrivent d'un autre fil).</summary>
    void Post(Action action)
    {
        if (_quitting) return;
        try { _ui.BeginInvoke(action); }
        catch (Exception ex) { Log.Error("interface", ex); }
    }

    void ShowMenu()
    {
        if (_engine.Running) { Toast.Pop($"Arrête d'abord avec {_config.HotkeyStart}"); return; }
        _menu.ShowMenu();
    }

    void TogglePause()
    {
        if (!_engine.Running) return;
        if (_engine.PausedRemaining > 0)
        {
            _engine.Resume();
            Toast.Pop("Auto Clic : reprise");
        }
        else
        {
            _engine.Pause(PauseSeconds);
            Toast.Pop("Auto Clic : pause 5 min (clic droit sur l'icône > Reprendre)", 2500);
        }
        UpdateTray();
    }

    void OnRunningChanged(bool running)
    {
        if (running)
        {
            _menu.HideMenu();                  // on cache le menu et les ronds pour ne pas cliquer dessus
            Toast.Pop($"Auto Clic : ACTIVÉ  ({_config.HotkeyStart} = arrêter)");
        }
        else
        {
            Toast.Pop($"Auto Clic : ARRÊTÉ  ({_engine.Clicks} clics)");
        }
        UpdateTray();
    }

    void UpdateTray()
    {
        bool running = _engine.Running;
        double paused = _engine.PausedRemaining;
        string text = !running ? $"Auto Clic - {_config.HotkeyMenu} = menu, {_config.HotkeyStart} = lancer"
                    : paused > 0 ? $"Auto Clic - en pause ({Math.Ceiling(paused / 60)} min)"
                    : _engine.GamePaused ? "Auto Clic - en pause (jeu pas au premier plan)"
                    : $"Auto Clic - en cours, {_engine.Clicks} clics";
        _tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        _itemMenu.Text = $"Afficher le menu ({_config.HotkeyMenu})";
        _itemMenu.Enabled = !running;
        _itemToggle.Text = running ? $"Arrêter ({_config.HotkeyStart})" : $"Lancer ({_config.HotkeyStart})";
        _itemPause.Enabled = running;
        _itemPause.Text = paused > 0 ? $"Reprendre (pause : encore {Math.Ceiling(paused / 60)} min)" : "Pause 5 min";
        _itemSettings.Enabled = !running;
    }

    void SendCapture()
    {
        if (!_config.Alerts.Enabled) { Toast.Pop("Choisis d'abord un service dans Réglages > Téléphone", 3000); return; }
        _engine.SendCapture("Capture demandée");
        Toast.Pop("Capture envoyée au téléphone");
    }

    /// <summary>Mode d'emploi intégré à l'exe, écrit dans le dossier des réglages puis ouvert.</summary>
    void OpenHelp()
    {
        try
        {
            using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("AutoClic.LISEZ-MOI.txt");
            if (stream == null) return;
            string text = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8).ReadToEnd();
            int dev = text.IndexOf("POUR LE DÉVELOPPEUR", StringComparison.Ordinal);
            if (dev > 0) text = text.Substring(0, dev).TrimEnd() + "\r\n";
            string path = System.IO.Path.Combine(Paths.DataDir, "LISEZ-MOI.txt");
            System.IO.File.WriteAllText(path, text, new System.Text.UTF8Encoding(true));
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("aide", ex); }
    }

    void OpenLog()
    {
        try
        {
            if (!System.IO.File.Exists(Paths.LogFile)) Log.Info("Journal ouvert");
            Process.Start(new ProcessStartInfo(Paths.LogFile) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("ouverture du journal", ex); }
    }

    void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _uiTimer.Stop();
        _remote.Stop();
        _engine.Shutdown();
        _showWait.Unregister(null);
        _config.Save();
        _menu.CloseForGood();
        _tray.Visible = false;
        _tray.Dispose();
        Log.Info("Programme quitté");
        ExitThread();
    }
}
