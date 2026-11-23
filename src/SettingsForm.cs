using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AutoClic;

/// <summary>Barre latérale des réglages (icônes colorées arrondies, comme les Réglages Système).</summary>
internal sealed class MacSidebar : MacControl
{
    readonly List<(string Icon, Color Color, string Text)> _items = [];
    int _selected, _hover = -1;

    public event EventHandler SelectedIndexChanged;

    public void Add(string icon, Color color, string text) { _items.Add((icon, color, text)); Invalidate(); }

    public int SelectedIndex
    {
        get => _selected;
        set { if (value == _selected) return; _selected = value; Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); }
    }

    int ItemHeight => D(34);
    Rectangle ItemRect(int i) => new(D(10), D(12) + i * ItemHeight, Width - D(20), ItemHeight - D(2));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = -1;
        for (int i = 0; i < _items.Count; i++) if (ItemRect(i).Contains(e.Location)) h = i;
        if (h != _hover) { _hover = h; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        for (int i = 0; i < _items.Count; i++) if (ItemRect(i).Contains(e.Location)) SelectedIndex = i;
        base.OnMouseDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var b = new SolidBrush(Theme.Sidebar)) g.FillRectangle(b, ClientRectangle);
        Gfx.Smooth(g);
        for (int i = 0; i < _items.Count; i++)
        {
            var r = ItemRect(i);
            bool sel = i == _selected;
            if (sel) Gfx.Fill(g, r, D(7), Theme.Accent);
            else if (i == _hover) Gfx.Fill(g, r, D(7), Theme.Hover);
            var icon = new Rectangle(r.X + D(8), r.Y + (r.Height - D(22)) / 2, D(22), D(22));
            Gfx.Fill(g, icon, D(6), _items[i].Color);
            Gfx.Text(g, _items[i].Icon, Theme.IconsSmall, icon, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            Gfx.Text(g, _items[i].Text, Theme.Body, new Rectangle(icon.Right + D(10), r.Y, r.Right - icon.Right - D(14), r.Height),
                sel ? Color.White : Theme.Text);
        }
        using var pen = new Pen(Theme.Separator);
        g.DrawLine(pen, Width - 1, 0, Width - 1, Height);
    }
}

/// <summary>Fenêtre « Réglages » : touches, macro, arrêt auto, téléphone, surveillance, photos.</summary>
internal sealed class SettingsForm : MacForm
{
    readonly Config _config;
    readonly MacSidebar _sidebar = new();
    readonly List<MacScroll> _pages = [];

    // Touches
    readonly MacKeyField _keyStart = new(), _keyMenu = new(), _keyAdd = new();
    // Macro
    readonly MacField _macroKeys = new();
    Label _macroError;
    MacNumber _macroEvery, _mouseEvery, _mouseDist;
    // Arrêt auto
    readonly MacToggle _chkAfter = new(), _chkAt = new();
    MacNumber _afterH, _afterM, _atH, _atM;
    // Téléphone
    readonly MacPopup _service = new("Aucun", "ntfy (le plus simple)", "Telegram", "Discord");
    readonly MacField _ntfyTopic = new(), _ntfyServer = new(), _tgToken = new(), _tgChat = new(), _discord = new();
    readonly MacToggle _phonePhoto = new(), _onStop = new(), _onIdle = new(), _onProblem = new();
    MacCard _cardService, _cardNtfy, _cardTelegram, _cardDiscord, _cardEvents, _cardTest;
    Label _topicWarning, _testResult;
    // Surveillance
    readonly MacToggle _monitor = new(), _monitorStopped = new();
    MacNumber _monitorMin;
    Label _captureResult, _monitorWarning;
    // Photos
    readonly MacToggle _photos = new();
    MacNumber _idle;
    // Télécommande
    readonly MacToggle _remote = new();
    MacCard _cardRemote;
    Label _remoteNote;
    // Jeu
    readonly MacToggle _gamePause = new();
    readonly MacPopup _gameWindow = new();
    readonly List<(string Process, string Title)> _gameItems = [];

    static readonly string[] Services = [AlertSettings.None, AlertSettings.Ntfy, AlertSettings.Telegram, AlertSettings.Discord];
    int SidebarWidth => D(190);
    int FooterHeight => D(56);
    int PageTop => D(58);

    public SettingsForm(Config config)
    {
        _config = config;
        Text = "Réglages";
        TopMost = true;
        ShowInTaskbar = false;
        ClientSize = new Size(D(700), D(560));

        _sidebar.SetBounds(1, TitleHeight, SidebarWidth, ClientSize.Height - TitleHeight - D(6));
        _sidebar.Add(Theme.IconKeyboard, Theme.Gray, "Touches");
        _sidebar.Add(Theme.IconPlay, Theme.Purple, "Macro");
        _sidebar.Add(Theme.IconTimer, Theme.Orange, "Arrêt auto");
        _sidebar.Add(Theme.IconPhone, Theme.Green, "Téléphone");
        _sidebar.Add(Theme.IconEye, Theme.Indigo, "Surveillance");
        _sidebar.Add(Theme.IconCamera, Theme.Teal, "Photos");
        _sidebar.Add("\uE7FC", Theme.Pink, "Jeu");
        _sidebar.SelectedIndexChanged += (_, _) => ShowPage(_sidebar.SelectedIndex);
        Controls.Add(_sidebar);

        BuildKeys();
        BuildMacro();
        BuildStop();
        BuildPhone();
        BuildMonitor();
        BuildPhotos();
        BuildGame();

        var ok = new MacButton("OK", MacButtonStyle.Primary) { Size = new Size(D(90), D(30)) };
        ok.Click += (_, _) => { if (Apply()) DialogResult = DialogResult.OK; };
        var cancel = new MacButton("Annuler") { Size = new Size(D(90), D(30)), DialogResult = DialogResult.Cancel };
        int y = ClientSize.Height - FooterHeight + (FooterHeight - D(30)) / 2;
        ok.Location = new Point(ClientSize.Width - D(16) - ok.Width, y);
        cancel.Location = new Point(ok.Left - D(8) - cancel.Width, y);
        Controls.Add(ok);
        Controls.Add(cancel);
        CancelButton = cancel;

        LoadValues();
        ShowPage(0);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int y = ClientSize.Height - FooterHeight;
        using var pen = new Pen(Theme.Separator);
        e.Graphics.DrawLine(pen, SidebarWidth, y, ClientSize.Width, y);
    }

    // ---------- Pages ----------
    (MacScroll Page, int Width) NewPage(string title)
    {
        var page = new MacScroll();
        page.SetBounds(SidebarWidth + 1, TitleHeight, ClientSize.Width - SidebarWidth - 2, ClientSize.Height - TitleHeight - FooterHeight);
        page.Visible = false;
        Controls.Add(page);
        _pages.Add(page);
        var t = MacCard.Label(title, Theme.Large, Theme.Text);
        t.SetBounds(D(22), D(14), page.ContentWidth - D(44), D(32));
        page.Content.Controls.Add(t);
        return (page, page.ContentWidth - D(44));
    }

    int Add(MacScroll page, Control c, int y, int gap = 14)
    {
        c.Location = new Point(D(22), y);
        page.Content.Controls.Add(c);
        return y + c.Height + D(gap);
    }

    Label PageNote(MacScroll page, string text, ref int y, int width, Color? color = null, int minLines = 0)
    {
        var l = MacCard.Label(text, Theme.Small, color ?? Theme.Secondary);
        l.AutoEllipsis = false;
        l.TextAlign = ContentAlignment.TopLeft;
        int h;
        using (var g = CreateGraphics())
            h = TextRenderer.MeasureText(g, text, Theme.Small, new Size(width, 0), TextFormatFlags.WordBreak).Height + D(4);
        l.Size = new Size(width, Math.Max(h, D(16 * minLines + 4)));
        l.Location = new Point(D(26), y);
        page.Content.Controls.Add(l);
        y += l.Height + D(12);
        return l;
    }

    void ShowPage(int index)
    {
        for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = i == index;
        _sidebar.SelectedIndex = index;
    }

    MacNumber Number(decimal min, decimal max, int width = 76, int decimals = 0, decimal step = 1) =>
        new(min, max, min, decimals, step) { Size = new Size(D(width), D(28)) };

    void BuildKeys()
    {
        var (page, w) = NewPage("Touches");
        _keyStart.Size = _keyMenu.Size = new Size(D(150), D(28));
        var card = new MacCard(w)
            .Row("Lancer / arrêter", null, _keyStart)
            .Row("Afficher / cacher le menu", null, _keyMenu);
        int y = Add(page, card, PageTop);
        PageNote(page, "Clique dans une case, puis appuie sur la touche ou la combinaison voulue (ex. F8, Ctrl+F9). " +
                       "Elles marchent même quand le jeu est au premier plan.", ref y, w);
        page.SetContentHeight(y);
    }

    void BuildMacro()
    {
        var (page, w) = NewPage("Macro anti-AFK");
        _macroKeys.Size = new Size(w - D(28), D(28));
        _macroKeys.TextChanged += (_, _) => ValidateMacro();
        _keyAdd.Size = new Size(D(120), D(28));
        var add = new MacButton("Ajouter", MacButtonStyle.Secondary, Theme.IconAdd) { Size = new Size(D(96), D(28)) };
        add.Click += (_, _) =>
        {
            if (_keyAdd.Key.Combo == null) return;
            _macroKeys.Text = (_macroKeys.Text.Trim().Length == 0 ? "" : _macroKeys.Text.Trim() + " ; ") + _keyAdd.Key.Combo;
        };
        var card = new MacCard(w).Row("Touches envoyées", "Dans l'ordre, séparées par « ; »").Full(_macroKeys, D(40), separator: false);
        _macroError = card.NoteLabel("", null, 1);
        card.Row("Ajouter une touche", null, _keyAdd, add);
        int y = Add(page, card, PageTop);

        _macroEvery = Number(1, 3600, 80, 1, 1);
        _mouseDist = Number(0, 200, 70);
        _mouseEvery = Number(0.5m, 3600, 80, 1, 0.5m);
        var rhythm = new MacCard(w)
            .Row("Envoyer les touches toutes les", null, _macroEvery, Unit("s"))
            .Row("Bouger la souris de", "0 = ne pas bouger la souris", _mouseDist, Unit("px"))
            .Row("Bouger la souris toutes les", null, _mouseEvery, Unit("s"));
        y = Add(page, rhythm, y);
        PageNote(page, "Noms acceptés : Alt, Ctrl, Maj, Win, Entrée, Échap, Espace, Tab, Inser, Suppr, Haut, Bas, Gauche, " +
                       "Droite, F1 à F24, lettres, chiffres… Combinaison : Maj+Z.", ref y, w);
        page.SetContentHeight(y);
    }

    void BuildStop()
    {
        var (page, w) = NewPage("Arrêt automatique");
        _afterH = Number(0, 168, 64); _afterM = Number(0, 59, 64);
        _atH = Number(0, 23, 64); _atM = Number(0, 59, 64);
        var card = new MacCard(w)
            .Row("Arrêter après une durée", "Comptée à chaque lancement", _chkAfter)
            .Row("Durée", null, _afterH, Unit("h"), _afterM, Unit("min"));
        int y = Add(page, card, PageTop);
        var card2 = new MacCard(w)
            .Row("Arrêter à une heure précise", "Heure de l'horloge du PC", _chkAt)
            .Row("Heure", null, _atH, Unit(":"), _atM);
        y = Add(page, card2, y);
        PageNote(page, "Si les alertes sont réglées (Téléphone), tu es prévenu à l'arrêt, avec une capture de l'écran.", ref y, w);
        page.SetContentHeight(y);
    }

    void BuildPhone()
    {
        var (page, w) = NewPage("Alertes sur le téléphone");
        _service.Size = new Size(D(200), D(28));
        _service.SelectedIndexChanged += (_, _) => LayoutPhone();
        _cardService = new MacCard(w).Row("Service", null, _service);
        Add(page, _cardService, PageTop);

        _ntfyTopic.Size = new Size(D(180), D(28));
        _ntfyTopic.TextChanged += (_, _) => UpdateTopicWarning();
        var newTopic = new MacButton("Nouveau") { Size = new Size(D(84), D(28)) };
        newTopic.Click += (_, _) => _ntfyTopic.Text = AlertSettings.NewTopic();
        _ntfyServer.Size = new Size(D(270), D(28));
        _cardNtfy = new MacCard(w).Row("Sujet", null, _ntfyTopic, newTopic).Row("Serveur", null, _ntfyServer);
        _topicWarning = _cardNtfy.NoteLabel("", Theme.Orange, 2);
        _cardNtfy.Note("1. Installe l'appli « ntfy » sur ton téléphone.\n2. Abonne-toi au même sujet (serveur ntfy.sh).\n" +
                       "3. Clique « Envoyer un test ». Le sujet est public : choisis un nom impossible à deviner.");

        _tgToken.Size = new Size(D(270), D(28));
        _tgChat.Size = new Size(D(170), D(28));
        var find = new MacButton("Trouver") { Size = new Size(D(94), D(28)) };
        find.Click += async (_, _) =>
        {
            string token = _tgToken.Text.Trim();
            if (token.Length == 0) { _testResult.Text = "Colle d'abord le code du bot."; return; }
            _testResult.Text = "Recherche…";
            string error = null;
            string chat = await Task.Run(() => Phone.FindTelegramChat(token, out error));
            if (chat != null) { _tgChat.Text = chat; _testResult.Text = "Conversation trouvée. Clique « Envoyer un test »."; }
            else _testResult.Text = error;
        };
        _cardTelegram = new MacCard(w).Row("Code du bot", null, _tgToken).Row("Conversation", null, _tgChat, find)
            .Note("1. Sur Telegram, écris à @BotFather : /newbot, choisis un nom : il te donne un code.\n" +
                  "2. Colle ce code ci-dessus.\n3. Envoie /start à ton bot, puis clique « Trouver ».");

        _discord.Size = new Size(D(270), D(28));
        _cardDiscord = new MacCard(w).Row("Webhook", null, _discord)
            .Note("Sur ton serveur Discord : paramètres du salon > Intégrations > Webhooks > Nouveau webhook > " +
                  "Copier l'URL du webhook, puis colle-la ci-dessus.");

        _cardEvents = new MacCard(w)
            .Row("Joindre une capture de l'écran", null, _phonePhoto)
            .Row("Arrêt automatique", null, _onStop)
            .Row("Plus rien ne se passe", "Délai réglé dans Photos", _onIdle)
            .Row("Problème", "Écran verrouillé, clics refusés…", _onProblem)
            .Note("Pour un bouton précis : ⚙ à côté de lui dans le menu, « Alerte sur le téléphone ».");

        var test = new MacButton("Envoyer un test", MacButtonStyle.Primary, Theme.IconSend) { Size = new Size(D(150), D(30)) };
        test.Click += async (_, _) =>
        {
            var alerts = ReadAlerts();
            if (!alerts.Enabled) { _testResult.Text = "Choisis d'abord un service."; return; }
            test.Enabled = false;
            _testResult.Text = "Envoi…";
            string error = await Task.Run(() => Reporter.SendTest(alerts));
            _testResult.Text = error == null ? "Envoyé ! Regarde ton téléphone." : "Échec : " + error;
            test.Enabled = true;
        };
        _remote.CheckedChanged += (_, _) => UpdateTopicWarning();
        _cardRemote = new MacCard(w).Row("Commander depuis le téléphone", "Pause, capture, arrêt… à distance", _remote);
        _remoteNote = _cardRemote.NoteLabel("", null, 4);

        _cardTest = new MacCard(w).Row("Vérifier", null, test);
        _testResult = _cardTest.NoteLabel("", null, 2);

        foreach (var c in new[] { _cardNtfy, _cardTelegram, _cardDiscord, _cardEvents, _cardRemote, _cardTest }) page.Content.Controls.Add(c);
    }

    void LayoutPhone()
    {
        var page = _pages[3];
        string s = Services[Math.Max(0, _service.SelectedIndex)];
        int y = PageTop + _cardService.Height + D(14);
        foreach (var (card, name) in new[] { (_cardNtfy, AlertSettings.Ntfy), (_cardTelegram, AlertSettings.Telegram), (_cardDiscord, AlertSettings.Discord) })
        {
            bool show = s == name;               // (Visible vaut false tant que la fenêtre n'est pas affichée)
            card.Visible = show;
            if (show) { card.Location = new Point(D(22), y); y += card.Height + D(14); }
        }
        bool any = s != AlertSettings.None;
        _cardEvents.Visible = _cardTest.Visible = any;
        bool remote = s is AlertSettings.Ntfy or AlertSettings.Telegram;
        _cardRemote.Visible = remote;
        if (any)
        {
            _cardEvents.Location = new Point(D(22), y); y += _cardEvents.Height + D(14);
            if (remote) { _cardRemote.Location = new Point(D(22), y); y += _cardRemote.Height + D(14); }
            _cardTest.Location = new Point(D(22), y); y += _cardTest.Height + D(14);
        }
        page.SetContentHeight(y);
        UpdateMonitorWarning();
    }

    void UpdateTopicWarning()
    {
        string t = _ntfyTopic.Text.Trim();
        bool weak = t.Length < 10 || !t.Any(char.IsDigit);
        _topicWarning.Text = t.Length == 0 ? "Choisis un sujet (ou clique « Nouveau »)."
                           : weak ? "Ce sujet est facile à deviner : n'importe qui pourrait lire tes alertes et voir tes captures."
                           : "Sujet difficile à deviner : parfait.";
        _topicWarning.ForeColor = t.Length > 0 && !weak ? Theme.Green : Theme.Orange;
        if (_remoteNote == null) return;
        bool tg = Services[Math.Max(0, _service.SelectedIndex)] == AlertSettings.Telegram;
        _remoteNote.Text = !_remote.Checked ? "Coupé : le téléphone ne peut rien commander."
            : tg ? "Écris à ton bot : lancer, stop, pause (ou pause 20), reprendre, capture, état. Seule ta conversation est écoutée."
            : $"Boutons « Pause 15 min · Capture · Arrêter » dans chaque notification. Ou abonne-toi aussi au sujet « {(t.Length > 0 ? t + "-cmd" : "…-cmd")} » " +
              "et écris-y : lancer, stop, pause 20, reprendre, capture, état.";
    }

    void BuildMonitor()
    {
        var (page, w) = NewPage("Surveillance");
        _monitorMin = Number(1, 1440, 76);
        var card = new MacCard(w)
            .Row("Capture d'écran régulière", "Envoyée sur le téléphone, avec l'état des clics", _monitor)
            .Row("Toutes les", null, _monitorMin, Unit("min"))
            .Row("Aussi quand les clics sont arrêtés", null, _monitorStopped);
        int y = Add(page, card, PageTop);

        var send = new MacButton("Envoyer maintenant", MacButtonStyle.Primary, Theme.IconCamera) { Size = new Size(D(170), D(30)) };
        send.Click += async (_, _) =>
        {
            var alerts = ReadAlerts();
            if (!alerts.Enabled) { _captureResult.Text = "Choisis d'abord un service dans « Téléphone »."; return; }
            send.Enabled = false;
            _captureResult.Text = "Capture et envoi…";
            string error = await Task.Run(() => Reporter.SendCaptureNow(alerts));
            _captureResult.Text = error == null ? "Capture envoyée ! Regarde ton téléphone." : "Échec : " + error;
            send.Enabled = true;
        };
        var card2 = new MacCard(w).Row("Capture tout de suite", null, send);
        _captureResult = card2.NoteLabel("", null, 1);
        y = Add(page, card2, y);
        _monitorWarning = PageNote(page, "", ref y, w, Theme.Orange, 1);
        PageNote(page, "La capture est prise en arrière-plan, sans la touche Impr. écran : ni le PC ni la macro ne " +
                       "sont interrompus. Elle part avec le service choisi dans « Téléphone ».", ref y, w);
        page.SetContentHeight(y);
    }

    void UpdateMonitorWarning()
    {
        if (_monitorWarning == null) return;
        _monitorWarning.Text = Services[Math.Max(0, _service.SelectedIndex)] == AlertSettings.None
            ? "Aucun service choisi dans « Téléphone » : rien ne sera envoyé." : "";
    }

    void BuildPhotos()
    {
        var (page, w) = NewPage("Photos");
        _idle = Number(0, 1440, 76);
        var open = new MacButton("Ouvrir", MacButtonStyle.Secondary, Theme.IconFolder) { Size = new Size(D(100), D(28)) };
        open.Click += (_, _) => OpenFolder(Paths.PhotosDir);
        var card = new MacCard(w)
            .Row("Garder des photos de l'écran", "Bouton détecté, inactivité, arrêt, problème", _photos)
            .Row("« Plus rien ne se passe » après", "Aucun bouton détecté · 0 = jamais", _idle, Unit("min"))
            .Row("Dossier des photos", "Les 300 dernières sont gardées", open);
        int y = Add(page, card, PageTop);
        PageNote(page, "Pratique pour comprendre ce qui s'est passé pendant ton absence.", ref y, w);
        page.SetContentHeight(y);
    }

    void BuildGame()
    {
        var (page, w) = NewPage("Fenêtre du jeu");
        _gameWindow.Size = new Size(D(270), D(28));
        _gameWindow.SelectedIndexChanged += (_, _) => { if (_gameWindow.SelectedIndex > 0) _gamePause.Checked = true; };
        var refresh = new MacButton("Actualiser la liste", MacButtonStyle.Secondary, "\uE72C") { Size = new Size(D(160), D(28)) };
        refresh.Click += (_, _) => FillGameWindows(SelectedGame().Process, SelectedGame().Title);
        var card = new MacCard(w)
            .Row("Pause quand le jeu n'est pas au premier plan", "Les clics reprennent quand tu reviens sur le jeu", _gamePause)
            .Row("Jeu", null, _gameWindow)
            .Row("Le jeu n'est pas dans la liste ?", "Lance-le, puis actualise", refresh);
        int y = Add(page, card, PageTop);
        PageNote(page, "Évite de cliquer sur le bureau ou une autre fenêtre quand tu changes d'application. " +
                       "Le jeu est reconnu par son programme (pas par le titre de sa fenêtre).", ref y, w);
        page.SetContentHeight(y);
    }

    (string Process, string Title) SelectedGame() =>
        _gameWindow.SelectedIndex > 0 && _gameWindow.SelectedIndex <= _gameItems.Count ? _gameItems[_gameWindow.SelectedIndex - 1] : (null, null);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>Liste les fenêtres ouvertes (une par programme) pour choisir celle du jeu.</summary>
    void FillGameWindows(string selectedProcess, string selectedTitle)
    {
        _gameItems.Clear();
        int me = Process.GetCurrentProcess().Id;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
            int len = GetWindowTextLength(h);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            GetWindowThreadProcessId(h, out int pid);
            if (pid == me) return true;
            string proc;
            try { proc = Process.GetProcessById(pid).ProcessName; } catch { return true; }
            if (proc is "explorer" or "TextInputHost" or "ApplicationFrameHost" or "SystemSettings" || !seen.Add(proc)) return true;
            _gameItems.Add((proc, sb.ToString()));
            return true;
        }, IntPtr.Zero);
        _gameItems.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
        if (!string.IsNullOrEmpty(selectedProcess) && !_gameItems.Any(g => string.Equals(g.Process, selectedProcess, StringComparison.OrdinalIgnoreCase)))
            _gameItems.Insert(0, (selectedProcess, (selectedTitle ?? selectedProcess) + " (fermé)"));
        int index = _gameItems.FindIndex(g => string.Equals(g.Process, selectedProcess, StringComparison.OrdinalIgnoreCase));
        var labels = new List<string> { "Aucun" };
        labels.AddRange(_gameItems.Select(g => g.Title.Length > 40 ? g.Title.Substring(0, 40) + "…" : g.Title));
        bool wasChecked = _gamePause.Checked;
        _gameWindow.SetItems(labels, index + 1);
        _gamePause.Checked = wasChecked;
    }

    // ---------- Valeurs ----------
    void LoadValues()
    {
        _keyStart.Key.Combo = _config.HotkeyStart;
        _keyMenu.Key.Combo = _config.HotkeyMenu;

        _macroKeys.Text = _config.MacroKeysText;
        _macroEvery.Value = (decimal)_config.MacroKeysEvery;
        _mouseEvery.Value = (decimal)_config.MacroMouseEvery;
        _mouseDist.Value = _config.MacroMouseDistance;

        int after = _config.StopAfterMin;
        _afterH.Value = Math.Min(168, after / 60);
        _afterM.Value = after % 60;
        _chkAfter.Checked = after > 0;
        var at = _config.StopAt ?? new TimeSpan(23, 0, 0);
        _atH.Value = at.Hours;
        _atM.Value = at.Minutes;
        _chkAt.Checked = _config.StopAt.HasValue;
        // Changer une valeur active l'option correspondante
        _afterH.ValueChanged += (_, _) => _chkAfter.Checked = true;
        _afterM.ValueChanged += (_, _) => _chkAfter.Checked = true;
        _atH.ValueChanged += (_, _) => _chkAt.Checked = true;
        _atM.ValueChanged += (_, _) => _chkAt.Checked = true;

        var a = _config.Alerts;
        _service.SelectedIndex = Math.Max(0, Array.IndexOf(Services, a.Service));
        _ntfyTopic.Text = string.IsNullOrWhiteSpace(a.NtfyTopic) ? AlertSettings.NewTopic() : a.NtfyTopic;
        _ntfyServer.Text = string.IsNullOrWhiteSpace(a.NtfyServer) ? AlertSettings.DefaultNtfyServer : a.NtfyServer;
        _tgToken.Text = a.TelegramToken ?? "";
        _tgChat.Text = a.TelegramChat ?? "";
        _discord.Text = a.DiscordWebhook ?? "";
        _phonePhoto.Checked = a.Photo;
        _onStop.Checked = a.OnStop;
        _onIdle.Checked = a.OnIdle;
        _onProblem.Checked = a.OnProblem;

        _monitorMin.Value = _config.MonitorMin > 0 ? _config.MonitorMin : 30;
        _monitor.Checked = _config.MonitorMin > 0;
        _monitorStopped.Checked = _config.MonitorWhenStopped;
        _monitorMin.ValueChanged += (_, _) => _monitor.Checked = true;

        _photos.Checked = _config.Photos;
        _idle.Value = _config.IdleMin;
        _remote.Checked = _config.Remote;
        FillGameWindows(_config.GameProcess, _config.GameTitle);
        _gamePause.Checked = _config.PauseOutsideGame;
        LayoutPhone();
        UpdateTopicWarning();
        ValidateMacro();
    }

    AlertSettings ReadAlerts() => new()
    {
        Service = Services[Math.Max(0, _service.SelectedIndex)],
        NtfyTopic = _ntfyTopic.Text.Trim(),
        NtfyServer = _ntfyServer.Text.Trim(),
        TelegramToken = _tgToken.Text.Trim(),
        TelegramChat = _tgChat.Text.Trim(),
        DiscordWebhook = _discord.Text.Trim(),
        Photo = _phonePhoto.Checked,
        OnStop = _onStop.Checked,
        OnIdle = _onIdle.Checked,
        OnProblem = _onProblem.Checked,
    };

    bool Apply()
    {
        if (!ValidateMacro())
        {
            ShowPage(1);
            MacAlert.Info(this, "Touche non reconnue", _macroError.Text);
            return false;
        }
        if (_keyStart.Key.Combo != null && _keyMenu.Key.Combo != null && _keyStart.Key.Combo.ToString() == _keyMenu.Key.Combo.ToString())
        {
            ShowPage(0);
            MacAlert.Info(this, "Même touche deux fois", "Lancer / arrêter et le menu doivent avoir des touches différentes.");
            return false;
        }

        _config.HotkeyStart = _keyStart.Key.Combo;
        _config.HotkeyMenu = _keyMenu.Key.Combo;
        _config.MacroKeysText = _macroKeys.Text.Trim();
        _config.MacroKeysEvery = (double)_macroEvery.Value;
        _config.MacroMouseEvery = (double)_mouseEvery.Value;
        _config.MacroMouseDistance = (int)_mouseDist.Value;
        _config.StopAfterMin = _chkAfter.Checked ? (int)(_afterH.Value * 60 + _afterM.Value) : 0;
        _config.StopAt = _chkAt.Checked ? new TimeSpan((int)_atH.Value, (int)_atM.Value, 0) : null;
        _config.Alerts = ReadAlerts();
        _config.MonitorMin = _monitor.Checked ? (int)_monitorMin.Value : 0;
        _config.MonitorWhenStopped = _monitorStopped.Checked;
        _config.Photos = _photos.Checked;
        _config.IdleMin = (int)_idle.Value;
        _config.Remote = _remote.Checked;
        var game = SelectedGame();
        if (_gamePause.Checked && game.Process == null)
        {
            ShowPage(6);
            MacAlert.Info(this, "Quel jeu ?", "Choisis la fenêtre du jeu dans la liste (lance-le d'abord, puis « Actualiser la liste »).");
            return false;
        }
        _config.PauseOutsideGame = _gamePause.Checked;
        if (game.Process != null) { _config.GameProcess = game.Process; _config.GameTitle = game.Title.Replace(" (fermé)", ""); }
        return true;
    }

    bool ValidateMacro()
    {
        bool ok = KeyCombo.TryParseList(_macroKeys.Text, out var list, out string bad);
        _macroError.Text = ok ? (list.Count == 0 ? "Aucune touche : la macro ne fera que bouger la souris." : "") : $"« {bad} » n'est pas reconnu.";
        _macroError.ForeColor = ok ? Theme.Secondary : Theme.Red;
        return ok;
    }

    public static void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Error("ouverture du dossier", ex); }
    }
}
