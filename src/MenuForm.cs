using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AutoClic;

/// <summary>
/// Le menu (touche Inser par défaut), façon macOS : boutons à position fixe (ronds à glisser),
/// boutons à détecter (images), macro anti-AFK.
/// </summary>
internal sealed class MenuForm : MacForm
{
    readonly Config _config;
    readonly Engine _engine;
    readonly Action _quit;
    readonly MacScroll _scroll;
    readonly List<MarkerForm> _markers = [];
    readonly List<Image> _thumbnails = [];
    readonly Dictionary<string, Template> _templates = new(StringComparer.OrdinalIgnoreCase);
    CaptureForm _capture;
    TextFinder _textFinder;
    bool _closingForGood, _busy;

    int Radius => D(20);
    int FooterHeight => D(58);

    public MenuForm(Config config, Engine engine, Action quit)
    {
        _config = config;
        _engine = engine;
        _quit = quit;

        Text = "Auto Clic";
        TopMost = true;
        ShowInTaskbar = true;
        CanMinimize = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(D(390), D(640));

        _scroll = new MacScroll();
        Controls.Add(_scroll);

        // Barre du bas
        var settings = new MacButton("Paramètres", MacButtonStyle.Secondary, Theme.IconSettings) { Size = new Size(D(118), D(30)) };
        settings.Click += (_, _) => OpenSettings();
        var clear = new MacButton("Tout effacer", MacButtonStyle.PlainDanger) { Size = new Size(D(100), D(30)) };
        clear.Click += (_, _) => ClearAll();
        var quitButton = new MacButton("Quitter") { Size = new Size(D(84), D(30)) };
        quitButton.Click += (_, _) => _quit();
        foreach (var b in new Control[] { settings, clear, quitButton }) { b.Tag = "footer"; Controls.Add(b); }

        SyncEngine();
        Rebuild();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_scroll == null) return;
        _scroll.SetBounds(1, TitleHeight, ClientSize.Width - 2, ClientSize.Height - TitleHeight - FooterHeight);
        var footer = Controls.OfType<MacButton>().Where(c => (string)c.Tag == "footer").ToList();
        if (footer.Count == 3)
        {
            int y = ClientSize.Height - FooterHeight + (FooterHeight - D(30)) / 2;
            footer[0].Location = new Point(D(16), y);
            footer[1].Location = new Point(footer[0].Right + D(6), y);
            footer[2].Location = new Point(ClientSize.Width - D(16) - footer[2].Width, y);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        int y = ClientSize.Height - FooterHeight;
        using (var b = new SolidBrush(Theme.TitleBar)) e.Graphics.FillRectangle(b, 1, y, ClientSize.Width - 2, FooterHeight - 1);
        using (var pen = new Pen(Theme.Separator)) e.Graphics.DrawLine(pen, 0, y, ClientSize.Width, y);
    }

    // ---------- Contenu ----------
    /// <summary>Reconstruit tout le contenu (après ajout / suppression, ou changement de réglages).</summary>
    void Rebuild()
    {
        var content = _scroll.Content;
        content.SuspendLayout();
        foreach (Control c in content.Controls.Cast<Control>().ToList()) c.Dispose();
        content.Controls.Clear();
        foreach (var img in _thumbnails) img.Dispose();
        _thumbnails.Clear();

        int pad = D(16), w = _scroll.ContentWidth - 2 * pad, y = D(14);

        // ---- Boutons à position fixe ----
        var fixedCard = new MacCard(w);
        var add = new MacButton("Ajouter", MacButtonStyle.Secondary, Theme.IconAdd) { Size = new Size(D(96), D(28)) };
        add.Click += (_, _) => AddPoint();
        fixedCard.Row("Boutons à position fixe", "Ronds rouges : glisse-les là où cliquer", add);
        for (int i = 0; i < _config.Boutons.Count; i++)
        {
            int index = i;
            var delay = new MacNumber((decimal)Config.MinDelay, (decimal)Config.MaxDelay, (decimal)Config.ClampDelay(_config.Boutons[i].Delay), 1, 0.1m)
                { Size = new Size(D(80), D(28)) };
            delay.ValueChanged += (_, _) =>
            {
                if (index >= _config.Boutons.Count) return;
                _config.Boutons[index].Delay = (double)delay.Value;
                Changed();
            };
            var remove = new MacButton("", MacButtonStyle.PlainDanger, Theme.IconDelete) { Size = new Size(D(28), D(28)) };
            remove.Click += (_, _) => BeginInvoke(new Action(() => DeletePoint(index)));
            fixedCard.Row(new MacBadge((i + 1).ToString(), Theme.Red), "Puis attendre", null, delay, Unit("s"), remove);
        }
        y = Place(content, fixedCard, pad, y) + D(18);

        // ---- Détection auto ----
        var detectCard = new MacCard(w);
        var detect = new MacToggle { Checked = _config.Detection };
        detect.CheckedChanged += (_, _) => { _config.Detection = detect.Checked; Changed(); };
        detectCard.Row("Détection auto", "Agit dès qu'un de ces boutons apparaît", detect);
        for (int i = 0; i < _config.Images.Count; i++) AddImageRows(detectCard, i);
        var capture = new MacButton("Capturer un bouton à détecter", MacButtonStyle.Plain, Theme.IconAdd) { Size = new Size(w - D(28), D(30)) };
        capture.Click += (_, _) => CaptureImage();
        detectCard.Full(capture, D(42));
        var similarity = new MacNumber(Config.MinSimilarity, Config.MaxSimilarity, _config.Similarity) { Size = new Size(D(70), D(28)) };
        similarity.ValueChanged += (_, _) => { _config.Similarity = (int)similarity.Value; Changed(); };
        detectCard.Row("Ressemblance minimum", $"{Config.DefaultSimilarity} % conseillé · plus haut = plus strict", similarity, Unit("%"));
        y = Place(content, detectCard, pad, y) + D(18);

        // ---- Macro ----
        var macroCard = new MacCard(w);
        var macro = new MacToggle { Checked = _config.Macro };
        macro.CheckedChanged += (_, _) => { _config.Macro = macro.Checked; Changed(); };
        var keys = _config.MacroKeys;
        string desc = (keys.Count > 0 ? KeyCombo.ListToString(keys) + $" / {_config.MacroKeysEvery:0.#} s" : "aucune touche") +
                      (_config.MacroMouseDistance > 0 ? $" · souris / {_config.MacroMouseEvery:0.#} s" : "");
        macroCard.Row("Macro anti-AFK", desc, macro);
        y = Place(content, macroCard, pad, y) + D(12);

        // ---- Aide ----
        var help = MacCard.Label($"{_config.HotkeyMenu} : afficher / cacher ce menu   ·   {_config.HotkeyStart} : lancer / arrêter",
                                 Theme.Small, Theme.Secondary);
        help.TextAlign = ContentAlignment.MiddleCenter;
        help.SetBounds(pad, y, w, D(20));
        content.Controls.Add(help);
        y += D(34);

        content.ResumeLayout();
        _scroll.SetContentHeight(y);

        // La fenêtre prend la hauteur du contenu, sans dépasser l'écran
        int max = Screen.FromControl(this).WorkingArea.Height - D(40);
        int wanted = TitleHeight + y + FooterHeight;
        int height = Math.Min(max, wanted);
        if (ClientSize.Height != height)
        {
            int bottom = Top + Height;
            ClientSize = new Size(ClientSize.Width, height);
            if (Visible && bottom > Screen.FromControl(this).WorkingArea.Bottom) Top = Math.Max(0, Screen.FromControl(this).WorkingArea.Bottom - Height);
        }
        PerformLayout();
    }

    /// <summary>Deux lignes par bouton détecté : [miniature] Bouton N · Test ⚙ 🗑, puis attendre + texte.</summary>
    void AddImageRows(MacCard card, int i)
    {
        int index = i;
        var img = _config.Images[i];

        var thumb = new MacThumb { Size = new Size(D(58), D(34)) };
        try { var bmp = Template.LoadBitmap(img.FullPath); _thumbnails.Add(bmp); thumb.Image = bmp; } catch { }

        var test = new MacButton("Test") { Size = new Size(D(54), D(26)) };
        test.Click += (_, _) => TestImage(index);
        bool custom = img.ActionOrDefault != ImageButton.ActionClick || img.Zone != null || img.Alert || !string.IsNullOrEmpty(img.After);
        var gear = new MacButton("", MacButtonStyle.Plain, Theme.IconSettings) { Size = new Size(D(28), D(28)), IconColor = custom ? Theme.Accent : Theme.Secondary };
        gear.Click += (_, _) => BeginInvoke(new Action(() => OpenImageSettings(index)));
        var remove = new MacButton("", MacButtonStyle.PlainDanger, Theme.IconDelete) { Size = new Size(D(28), D(28)) };
        remove.Click += (_, _) => BeginInvoke(new Action(() => DeleteImage(index)));
        card.Row(thumb, $"Bouton {i + 1}", Describe(img), test, gear, remove);

        // 2e ligne : attendre + texte à cliquer
        var line = new Panel { BackColor = Color.Transparent, Size = new Size(card.Width - D(28), D(30)) };
        var wait = MacCard.Label("Puis", Theme.Small, Theme.Secondary);
        wait.SetBounds(0, 0, D(30), D(30));
        var delay = new MacNumber((decimal)Config.MinImageDelay, (decimal)Config.MaxDelay, (decimal)Config.ClampImageDelay(img.Delay), 1, 0.5m)
            { Location = new Point(D(30), D(1)), Size = new Size(D(72), D(28)) };
        delay.ValueChanged += (_, _) =>
        {
            if (index >= _config.Images.Count) return;
            _config.Images[index].Delay = (double)delay.Value;
            Changed();
        };
        var s = MacCard.Label("s", Theme.Small, Theme.Secondary);
        s.SetBounds(D(105), 0, D(12), D(30));
        var text = new MacField(placeholder: "Texte à cliquer (optionnel)") { Text = img.Text ?? "" };
        text.SetBounds(D(124), D(1), line.Width - D(124), D(28));
        text.TextChanged += (_, _) =>
        {
            if (index >= _config.Images.Count) return;
            _config.Images[index].Text = string.IsNullOrWhiteSpace(text.Text) ? null : text.Text;
            Changed();
        };
        line.Controls.AddRange([wait, delay, s, text]);
        card.Full(line, D(40), separator: false);
    }

    string Describe(ImageButton img)
    {
        string action = img.ActionOrDefault switch
        {
            ImageButton.ActionDouble => "Double-clic",
            ImageButton.ActionRight => "Clic droit",
            ImageButton.ActionKey => "Touche " + (KeyCombo.TryParse(img.Key, out var k) ? k.ToString() : "?"),
            _ => "Clic",
        };
        int after = string.IsNullOrEmpty(img.After) ? -1 : _config.Images.FindIndex(o => o.File == img.After);
        if (after >= 0) action += $" · après le {after + 1}";
        if (img.Zone != null) action += " · zone";
        if (img.Alert) action += " · alerte";
        return action;
    }

    static int Place(Panel content, Control c, int x, int y)
    {
        c.Location = new Point(x, y);
        content.Controls.Add(c);
        return y + c.Height;
    }

    // ---------- Afficher / cacher ----------
    public void ShowMenu()
    {
        if (_engine.Running || _busy) return;
        Show();
        Activate();
        Rebuild();
        ShowMarkers();
    }

    public void HideMenu()
    {
        Hide();
        ClearMarkers();
        // Lancement pendant une capture : on l'annule pour ne pas cliquer sur l'écran figé
        if (_capture != null) _capture.DialogResult = DialogResult.Cancel;
    }

    public void Toggle()
    {
        if (_busy) return;
        if (Visible) HideMenu();
        else ShowMenu();
    }

    public void CloseForGood()
    {
        _closingForGood = true;
        ClearMarkers();
        Close();
    }

    protected override void OnCloseButton() => HideMenu();
    protected override void OnMinimizeButton() => HideMenu();

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Fermer cache juste le menu ; « Quitter » arrête vraiment
        if (!_closingForGood && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideMenu();
        }
        base.OnFormClosing(e);
    }

    // ---------- Modifications ----------
    void Changed()
    {
        _config.Save();
        SyncEngine();
    }

    /// <summary>Donne au moteur les réglages actuels (images chargées une seule fois).</summary>
    public void SyncEngine()
    {
        _engine.SetPoints(_config.Boutons);
        _engine.Settings = EngineSettings.From(_config);

        var targets = new List<DetectTarget>();
        for (int i = 0; i < _config.Images.Count; i++)
        {
            var t = GetTemplate(_config.Images[i]);
            if (t != null) targets.Add(new DetectTarget(i + 1, t, _config.Images[i]));
        }
        _engine.SetTargets(targets);
    }

    /// <summary>
    /// Recharge une config perso intégrée dans l'exe (ex. « AFK config auto farm ») : boutons à position fixe
    /// et macro. Les autres réglages (téléphone, détection, touches…) ne changent pas.
    /// </summary>
    public void ApplyPreset(string name)
    {
        if (_busy || _engine.Running) { Toast.Pop("Arrête d'abord les clics"); return; }
        var preset = Config.LoadPreset(name);
        if (preset == null) { Toast.Pop($"Config « {name} » introuvable"); return; }
        if (!MacAlert.Ask(Visible ? this : null, $"Charger la config « {name} » ?",
                $"Tes boutons à position fixe sont remplacés par les {preset.Boutons.Count} de « {name} » " +
                "(et la macro est réglée comme dans cette config). Le reste ne change pas.", "Charger")) return;
        _config.Boutons.Clear();
        _config.Boutons.AddRange(preset.Boutons);
        _config.Macro = preset.Macro;
        Changed();
        Log.Info($"Config « {name} » chargée ({preset.Boutons.Count} bouton(s))");
        Toast.Pop($"Config « {name} » chargée : {preset.Boutons.Count} boutons");
        if (Visible) { Rebuild(); ShowMarkers(); }
    }

    /// <summary>Fenêtre des paramètres (aussi ouverte depuis l'icône près de l'horloge).</summary>
    public void OpenSettings()
    {
        if (_busy || _engine.Running) return;
        _busy = true;
        _engine.SuspendHotkeys = true;              // sinon choisir F10 lancerait les clics
        try
        {
            using var f = new SettingsForm(_config);
            if (f.ShowDialog(Visible ? this : null) == DialogResult.OK)
            {
                Changed();
                if (Visible) Rebuild();
                Log.Info($"Paramètres modifiés (touches {_config.HotkeyStart} / {_config.HotkeyMenu}, macro « {_config.MacroKeysText} », " +
                         $"capture toutes les {_config.MonitorMin} min)");
            }
        }
        finally
        {
            _engine.SuspendHotkeys = false;
            _busy = false;
        }
    }

    void OpenImageSettings(int index)
    {
        if (_busy || index >= _config.Images.Count) return;
        _busy = true;
        _engine.SuspendHotkeys = true;
        try
        {
            using var f = new ImageSettingsForm(_config.Images[index], index + 1, _config.Images, PickZone);
            if (f.ShowDialog(this) == DialogResult.OK) { Changed(); Rebuild(); }
        }
        finally
        {
            _engine.SuspendHotkeys = false;
            _busy = false;
        }
    }

    /// <summary>Écran figé : l'utilisateur entoure la zone où chercher un bouton.</summary>
    async Task<Rectangle?> PickZone()
    {
        Opacity = 0;                               // transparent plutôt que caché (une fenêtre modale en dépend)
        foreach (var m in _markers) m.Hide();
        try
        {
            await Task.Delay(350);
            var screen = SystemInformation.VirtualScreen;
            using var shot = ScreenShot.CaptureBitmap(screen);
            using var form = new CaptureForm(shot, screen, "Entoure la zone où chercher ce bouton   ·   Échap = annuler");
            if (form.ShowDialog() != DialogResult.OK) return null;
            var r = form.Selection;
            r.Offset(screen.Location);
            return r;
        }
        catch (Exception ex)
        {
            Log.Error("choix de zone", ex);
            return null;
        }
        finally
        {
            Opacity = 1;
            foreach (var m in _markers) m.Show();
        }
    }

    Template GetTemplate(ImageButton img)
    {
        double scale = img.EffectiveScale();
        string key = $"{img.File}@{scale:0.000}";
        if (_templates.TryGetValue(key, out var t)) return t;
        try
        {
            t = Template.Load(img.FullPath, scale);
            _templates[key] = t;
            return t;
        }
        catch (Exception ex)
        {
            Log.Error($"chargement de l'image {img.File}", ex);
            return null;
        }
    }

    void AddPoint()
    {
        var screen = Screen.PrimaryScreen.Bounds;
        int n = _config.Boutons.Count;
        _config.Boutons.Add(new ClickPoint
        {
            // Au milieu de l'écran, décalé pour ne pas s'empiler
            X = screen.X + screen.Width / 2 + D((n % 5) * 60 - 120),
            Y = screen.Y + screen.Height / 2 + D((n / 5 % 4) * 60 - 90),
            Delay = n > 0 ? _config.Boutons[n - 1].Delay : Config.DefaultDelay,
        });
        Changed();
        Rebuild();
        ShowMarkers();
    }

    void DeletePoint(int index)
    {
        if (_engine.Running || index < 0 || index >= _config.Boutons.Count) return;
        _config.Boutons.RemoveAt(index);
        Changed();
        Rebuild();
        ShowMarkers();
    }

    void DeleteImage(int index)
    {
        if (_engine.Running || index < 0 || index >= _config.Images.Count) return;
        var img = _config.Images[index];
        _config.Images.RemoveAt(index);
        foreach (var key in _templates.Keys.Where(k => k.StartsWith(img.File + "@", StringComparison.OrdinalIgnoreCase)).ToList())
            _templates.Remove(key);
        Changed();
        TryDeleteFile(img.FullPath);
        Rebuild();
    }

    void ClearAll()
    {
        if (_config.Boutons.Count == 0 && _config.Images.Count == 0) return;
        if (!MacAlert.Ask(this, "Tout effacer ?", "Les boutons à position fixe et les boutons à détecter seront supprimés.",
                "Tout effacer", destructive: true)) return;
        var files = _config.Images.Select(i => i.FullPath).ToList();
        _config.Boutons.Clear();
        _config.Images.Clear();
        _templates.Clear();
        Changed();
        files.ForEach(TryDeleteFile);
        Rebuild();
        ShowMarkers();
    }

    static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch (Exception ex) { Log.Error("suppression d'image", ex); }
    }

    // ---------- Capture d'un bouton à détecter ----------
    async void CaptureImage()
    {
        if (_busy) return;
        _busy = true;
        Bitmap shot = null;
        try
        {
            HideMenu();
            await Task.Delay(400);                 // le temps que le menu et les ronds disparaissent
            var screen = SystemInformation.VirtualScreen;
            shot = ScreenShot.CaptureBitmap(screen);

            using (_capture = new CaptureForm(shot, screen))
            {
                if (_capture.ShowDialog() != DialogResult.OK || _engine.Running) return;
                using var crop = shot.Clone(_capture.Selection, PixelFormat.Format32bppArgb);
                var template = Template.FromBitmap(crop);
                if (template.Contrast < 6)
                {
                    Toast.Pop("Zone trop unie : entoure le texte ou le dessin du bouton", 3000);
                    return;
                }

                Directory.CreateDirectory(Paths.ImagesDir);
                string name = $"bouton-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png";
                crop.Save(Path.Combine(Paths.ImagesDir, name), ImageFormat.Png);
                var img = new ImageButton
                {
                    File = Path.Combine("images", name),
                    Delay = Config.DefaultImageDelay,
                    CaptureScreen = ImageButton.CurrentScreen,   // pour adapter la taille sur un autre écran
                };
                _templates[$"{img.File}@{1.0:0.000}"] = template;
                _config.Images.Add(img);
                _config.Detection = true;
                Changed();
                Log.Info($"Image {_config.Images.Count} capturée ({crop.Width}x{crop.Height})");
                Toast.Pop($"Bouton {_config.Images.Count} ajouté à la détection");
            }
        }
        catch (Exception ex)
        {
            Log.Error("capture", ex);
            Toast.Pop("Capture impossible (voir le journal)");
        }
        finally
        {
            _capture = null;
            shot?.Dispose();
            _busy = false;
            ShowMenu();
        }
    }

    /// <summary>Cherche l'image à l'écran tout de suite et montre où elle est (cadre vert / bleu).</summary>
    async void TestImage(int index)
    {
        if (_busy || index >= _config.Images.Count) return;
        _busy = true;
        try
        {
            var template = GetTemplate(_config.Images[index]);
            if (template == null) { Toast.Pop("Image introuvable (fichier supprimé ?)"); return; }

            var img = _config.Images[index];
            string text = string.IsNullOrWhiteSpace(img.Text) ? null : img.Text.Trim();
            Rectangle? zone = img.Zone?.ToScreen();
            HideMenu();                             // sinon il trouverait la miniature du menu
            await Task.Delay(400);
            var shot = ScreenShot.Capture();
            int min = _config.Similarity;
            var m = await Task.Run(() => Detector.Find(shot, template, min, zone));

            if (!m.Found)
            {
                // Peut-être que le bouton n'a plus la même taille (autre écran, autre réglage du jeu)
                Toast.Pop($"Bouton {index + 1} pas trouvé à cette taille : essai d'autres tailles…", 8000);
                double current = img.EffectiveScale();
                var (scale, other) = await Task.Run(() => FindOtherSize(shot, img, current, min, zone));
                if (other.Found)
                {
                    img.Scale = scale;
                    img.CaptureScreen = ImageButton.CurrentScreen;
                    Changed();
                    m = other;
                    Log.Info($"Bouton {index + 1} retrouvé à {scale * 100:0} % de sa taille : taille enregistrée");
                    Toast.Pop($"Trouvé à {scale * 100:0} % de la taille capturée : taille adaptée", 2500);
                    await Task.Delay(1200);
                }
                else
                {
                    double best = Math.Max(m.Similarity, other.Similarity);
                    Toast.Pop(best > 0
                        ? $"Bouton {index + 1} pas trouvé (au mieux {best:0} %, minimum {min} %)"
                        : $"Bouton {index + 1} pas à l'écran en ce moment", 2500);
                    await Task.Delay(1500);
                    return;
                }
            }
            TextHit hit = text != null
                ? await Task.Run(() => (_textFinder ??= new TextFinder()).Find(shot, m.Area, text))
                : null;

            // Cadre vert = zone trouvée ; cadre bleu = texte trouvé (c'est là qu'il agira)
            using var frame = new HighlightForm(m.Area, D(3), Theme.Green);
            frame.Show();
            HighlightForm textFrame = null;
            if (hit == null)
            {
                Toast.Pop($"Bouton {index + 1} trouvé : ressemblance {m.Similarity:0} %", 2500);
            }
            else if (hit.Found)
            {
                textFrame = new HighlightForm(hit.Area, D(3), Theme.Accent);
                textFrame.Show();
                Toast.Pop($"Bouton {index + 1} trouvé ({m.Similarity:0} %), texte « {text} » trouvé", 2500);
            }
            else
            {
                Toast.Pop(hit.Error ?? $"Bouton {index + 1} trouvé ({m.Similarity:0} %) mais pas le texte « {text} »  ·  " +
                                       $"lu : « {(hit.Read.Length == 0 ? "rien" : hit.Read.Length > 50 ? hit.Read.Substring(0, 50) + "…" : hit.Read)} »", 4500);
            }
            await Task.Delay(hit != null && !hit.Found ? 3000 : 2000);
            textFrame?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("test d'image", ex);
            Toast.Pop("Test impossible (voir le journal)");
        }
        finally
        {
            _busy = false;
            ShowMenu();
        }
    }

    /// <summary>Cherche le bouton à d'autres tailles (de 50 % à 200 % de la taille actuelle).</summary>
    static (double Scale, Match Match) FindOtherSize(ScreenShot shot, ImageButton img, double current, int min, Rectangle? zone)
    {
        double[] factors = [0.9, 1.1, 0.8, 1.25, 0.7, 1.4, 0.6, 1.6, 0.5, 1.8, 2.0];
        var best = (Scale: current, Match: default(Match));
        foreach (var f in factors)
        {
            double scale = current * f;
            var t = Template.Load(img.FullPath, scale);
            var m = Detector.Find(shot, t, min, zone);
            if (m.Similarity > best.Match.Similarity) best = (scale, m);
            if (m.Found) return (scale, m);
        }
        return best;
    }

    // ---------- Ronds ----------
    void ShowMarkers()
    {
        ClearMarkers();
        if (_engine.Running || !Visible) return;

        bool moved = false;
        for (int i = 0; i < _config.Boutons.Count; i++)
        {
            var p = _config.Boutons[i];
            // Rond hors de tous les écrans (autre résolution, écran débranché) : on le ramène au milieu
            if (!Screen.AllScreens.Any(s => s.Bounds.Contains(p.X, p.Y)))
            {
                var screen = Screen.PrimaryScreen.Bounds;
                Log.Info($"Bouton {i + 1} hors de l'écran ({p.X}, {p.Y}) : ramené au milieu");
                p.X = screen.X + screen.Width / 2 + D(i % 5 * 60 - 120);
                p.Y = screen.Y + screen.Height / 2;
                moved = true;
            }

            var marker = new MarkerForm(i, new Point(p.X, p.Y), Radius);
            marker.Moved += (index, center) =>
            {
                if (index >= _config.Boutons.Count) return;
                _config.Boutons[index].X = center.X;
                _config.Boutons[index].Y = center.Y;
                Changed();
            };
            // Différé : on ne détruit pas le rond pendant qu'il traite son propre clic
            marker.DeleteRequested += index => BeginInvoke(new Action(() => DeletePoint(index)));
            marker.Show();
            _markers.Add(marker);
        }
        if (moved) Changed();
    }

    void ClearMarkers()
    {
        foreach (var m in _markers) m.Close();
        _markers.Clear();
    }
}
