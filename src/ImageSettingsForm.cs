using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AutoClic;

/// <summary>Réglages d'un bouton à détecter (⚙) : action, zone de recherche, taille, alerte.</summary>
internal sealed class ImageSettingsForm : MacForm
{
    static readonly string[] Actions = ["Clic gauche", "Double-clic", "Clic droit", "Appuyer sur une touche"];
    static readonly string[] ActionCodes = [null, ImageButton.ActionDouble, ImageButton.ActionRight, ImageButton.ActionKey];

    readonly ImageButton _img;
    readonly Func<Task<Rectangle?>> _pickZone;
    readonly MacPopup _action = new(Actions);
    readonly MacKeyField _key = new();
    readonly MacPopup _zonePopup = new();
    readonly MacNumber _size;
    readonly MacToggle _alert = new();
    readonly MacPopup _after = new();
    readonly MacNumber _afterDelay, _afterWindow;
    readonly List<ImageButton> _others;
    readonly Image _preview;
    ZoneRect _zone;
    bool _picking;

    public ImageSettingsForm(ImageButton img, int number, IList<ImageButton> all, Func<Task<Rectangle?>> pickZone)
    {
        _others = all.Where(o => o != img).ToList();
        _img = img;
        _pickZone = pickZone;
        _zone = img.Zone;
        Text = $"Bouton {number}";
        TopMost = true;
        ShowInTaskbar = false;
        int w = D(440);

        // En-tête : miniature + ce qu'il fait
        var thumb = new MacThumb { Size = new Size(D(96), D(54)), Location = new Point(D(22), TitleHeight + D(16)) };
        try { _preview = Template.LoadBitmap(img.FullPath); thumb.Image = _preview; } catch { }
        Controls.Add(thumb);
        var title = MacCard.Label($"Bouton {number}", Theme.Large, Theme.Text);
        title.SetBounds(thumb.Right + D(14), thumb.Top + D(2), w - thumb.Right - D(36), D(28));
        Controls.Add(title);
        var sub = MacCard.Label(string.IsNullOrWhiteSpace(img.Text) ? "Agit au centre de la zone trouvée" : $"Agit sur le texte « {img.Text.Trim()} »",
                                Theme.Small, Theme.Secondary);
        sub.SetBounds(thumb.Right + D(14), thumb.Top + D(30), w - thumb.Right - D(36), D(20));
        Controls.Add(sub);

        int cw = w - D(44);
        _action.Size = new Size(D(190), D(28));
        _action.SelectedIndex = Math.Max(0, Array.IndexOf(ActionCodes, img.ActionOrDefault == ImageButton.ActionClick ? null : img.ActionOrDefault));
        _action.SelectedIndexChanged += (_, _) => _key.Enabled = _action.SelectedIndex == 3;
        _key.Size = new Size(D(150), D(28));
        if (KeyCombo.TryParse(img.Key, out var k)) _key.Key.Combo = k;
        _key.Key.Changed += () => _action.SelectedIndex = 3;
        _key.Enabled = _action.SelectedIndex == 3;
        var actionCard = new MacCard(cw)
            .Row("Quand il est détecté", null, _action)
            .Row("Touche", "Va à la fenêtre au premier plan", _key);
        actionCard.Location = new Point(D(22), thumb.Bottom + D(18));
        Controls.Add(actionCard);

        _zonePopup.Size = new Size(D(190), D(28));
        RefreshZonePopup();
        _zonePopup.SelectedIndexChanged += async (_, _) => await OnZoneChosen();
        _size = new MacNumber(20, 400, (decimal)Math.Round(img.EffectiveScale() * 100), 0, 5) { Size = new Size(D(76), D(28)) };
        _alert.Checked = img.Alert;
        var searchCard = new MacCard(cw)
            .Row("Où chercher", "Une zone : plus rapide, et rien ailleurs", _zonePopup)
            .Row("Taille à l'écran", "Ajustée seule si la résolution change", _size, Unit("%"))
            .Row("Alerte sur le téléphone", "Quand ce bouton est détecté", _alert);
        searchCard.Location = new Point(D(22), actionCard.Bottom + D(14));
        Controls.Add(searchCard);

        // Enchaînement : actif seulement juste après un autre bouton
        var items = new List<string> { "Toujours" };
        items.AddRange(_others.Select(o => $"Juste après le bouton {all.IndexOf(o) + 1}"));
        int selected = string.IsNullOrEmpty(img.After) ? 0 : _others.FindIndex(o => o.File == img.After) + 1;
        _after.SetItems(items, Math.Max(0, selected));
        _after.Size = new Size(D(210), D(28));
        _afterDelay = new MacNumber(0, 600, (decimal)Math.Max(0, img.AfterDelay), 1, 0.5m) { Size = new Size(D(76), D(28)) };
        _afterWindow = new MacNumber(1, 3600, (decimal)img.AfterWindowOrDefault, 0, 5) { Size = new Size(D(76), D(28)) };
        _after.SelectedIndexChanged += (_, _) => UpdateChain();
        var chainCard = new MacCard(cw)
            .Row("Actif", "Enchaînement", _after)
            .Row("Attendre d'abord", "Après l'action sur ce bouton-là", _afterDelay, Unit("s"))
            .Row("Puis actif pendant", "Ensuite il redevient inactif", _afterWindow, Unit("s"));
        chainCard.Location = new Point(D(22), searchCard.Bottom + D(14));
        Controls.Add(chainCard);
        UpdateChain();

        int y = chainCard.Bottom + D(18);
        var ok = new MacButton("OK", MacButtonStyle.Primary) { Size = new Size(D(90), D(30)) };
        ok.Click += (_, _) => { if (Apply()) DialogResult = DialogResult.OK; };
        var cancel = new MacButton("Annuler") { Size = new Size(D(90), D(30)), DialogResult = DialogResult.Cancel };
        ok.Location = new Point(w - D(22) - ok.Width, y);
        cancel.Location = new Point(ok.Left - D(8) - cancel.Width, y);
        Controls.Add(ok);
        Controls.Add(cancel);
        CancelButton = cancel;
        ClientSize = new Size(w, y + D(48));
    }

    void UpdateChain() => _afterDelay.Enabled = _afterWindow.Enabled = _after.SelectedIndex > 0;

    void RefreshZonePopup()
    {
        _picking = true;
        if (_zone == null) _zonePopup.SetItems(["Tout l'écran", "Choisir une zone…"], 0);
        else
        {
            var r = _zone.ToScreen();
            _zonePopup.SetItems(["Tout l'écran", $"Zone {r.Width} × {r.Height}", "Choisir une autre zone…"], 1);
        }
        _picking = false;
    }

    async Task OnZoneChosen()
    {
        if (_picking) return;
        int last = _zonePopup.Items.Count - 1;
        if (_zonePopup.SelectedIndex == 0) { _zone = null; RefreshZonePopup(); return; }
        if (_zonePopup.SelectedIndex != last) return;
        // Transparente plutôt que cachée : cacher une fenêtre modale la fermerait
        Opacity = 0;
        var r = await _pickZone();
        Opacity = 1;
        Activate();
        if (r.HasValue) _zone = ZoneRect.FromScreen(r.Value);
        RefreshZonePopup();
    }

    bool Apply()
    {
        if (_action.SelectedIndex == 3 && _key.Key.Combo == null)
        {
            MacAlert.Info(this, "Quelle touche ?", "Clique dans la case « Touche » et appuie sur la touche voulue.");
            return false;
        }
        _img.Action = ActionCodes[Math.Max(0, _action.SelectedIndex)];   // null = clic gauche (défaut)
        _img.Key = _key.Key.Combo?.ToString();
        _img.Zone = _zone;
        double size = (double)_size.Value / 100;
        if (Math.Abs(size - _img.EffectiveScale()) > 0.004)
        {
            _img.Scale = size;
            _img.CaptureScreen = ImageButton.CurrentScreen;
        }
        _img.Alert = _alert.Checked;
        if (_after.SelectedIndex > 0)
        {
            _img.After = _others[_after.SelectedIndex - 1].File;
            _img.AfterDelay = (double)_afterDelay.Value;
            _img.AfterWindow = (double)_afterWindow.Value;
        }
        else { _img.After = null; _img.AfterDelay = 0; _img.AfterWindow = 0; }
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _preview?.Dispose();
        base.Dispose(disposing);
    }
}
