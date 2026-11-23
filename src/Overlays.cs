using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AutoClic;

/// <summary>Fenêtre qui ne vole pas le focus et n'apparaît ni dans la barre des tâches ni dans Alt+Tab.</summary>
internal class OverlayForm : Form
{
    const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }
}

/// <summary>Rond rouge numéroté à glisser là où il faut cliquer. Clic droit = supprimer.</summary>
internal sealed class MarkerForm : OverlayForm
{
    public event Action<int, Point> Moved;
    public event Action<int> DeleteRequested;

    readonly int _index, _radius;
    bool _dragging;
    Point _dragStart, _origin;

    public MarkerForm(int index, Point center, int radius)
    {
        _index = index;
        _radius = radius;
        BackColor = Color.Crimson;
        Bounds = new Rectangle(center.X - radius, center.Y - radius, 2 * radius, 2 * radius);
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(0, 0, 2 * radius, 2 * radius);
            Region = new Region(path);
        }

        var label = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            Text = (index + 1).ToString(),
            Cursor = Cursors.SizeAll,
        };
        label.MouseDown += OnMouseDown;
        label.MouseMove += OnMouseMove;
        label.MouseUp += OnMouseUp;
        Controls.Add(label);
    }

    void OnMouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _dragStart = Cursor.Position;
        _origin = Location;
    }

    void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var c = Cursor.Position;
        Location = new Point(_origin.X + c.X - _dragStart.X, _origin.Y + c.Y - _dragStart.Y);
    }

    void OnMouseUp(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { DeleteRequested?.Invoke(_index); return; }
        if (!_dragging) return;
        _dragging = false;
        Moved?.Invoke(_index, new Point(Left + _radius, Top + _radius));
    }
}

/// <summary>Petite notification en haut à gauche de l'écran, qui disparaît toute seule.</summary>
internal sealed class Toast : OverlayForm
{
    static Toast _current;
    readonly Timer _timer, _fade;
    readonly string _title, _text;
    readonly Image _icon;
    float S => DeviceDpi / 96f;
    int D(float v) => (int)Math.Round(v * S);

    /// <summary>Petite notification façon macOS, en haut à droite de l'écran.</summary>
    public static void Pop(string text, int milliseconds = 2200)
    {
        try { _current?.Close(); } catch { }
        _current = new Toast(text, milliseconds);
        _current.Show();
    }

    Toast(string text, int milliseconds)
    {
        // « Auto Clic : ACTIVÉ » → titre « Auto Clic », texte « ACTIVÉ »
        _title = "Auto Clic";
        _text = text.StartsWith("Auto Clic : ") ? text.Substring(12) : text.StartsWith("Auto Clic ") ? text.Substring(10) : text;
        _icon = AppIcon.Large.ToBitmap();
        BackColor = Theme.Dark ? Color.FromArgb(0x2C, 0x2C, 0x2F) : Color.FromArgb(0xF7, 0xF7, 0xF9);
        Opacity = 0;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        int w = D(360);
        int textH;
        using (var g = CreateGraphics())
            textH = TextRenderer.MeasureText(g, _text, Theme.Small, new Size(w - D(74), 0), TextFormatFlags.WordBreak).Height;
        int h = Math.Max(D(64), D(36) + textH + D(12));
        var area = Screen.PrimaryScreen.WorkingArea;
        Bounds = new Rectangle(area.Right - w - D(14), area.Y + D(14), w, h);
        using (var path = Gfx.Rounded(new RectangleF(0, 0, w, h), D(16))) Region = new Region(path);

        _fade = new Timer { Interval = 15 };
        _fade.Tick += (_, _) =>
        {
            Opacity = Math.Min(0.97, Opacity + 0.12);
            if (Opacity >= 0.97) _fade.Stop();
        };
        _fade.Start();
        _timer = new Timer { Interval = milliseconds };
        _timer.Tick += (_, _) => { _timer.Stop(); Close(); };
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Gfx.Smooth(g);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Gfx.Stroke(g, r, D(16), Theme.Dark ? Color.FromArgb(0x48, 0x48, 0x4C) : Color.FromArgb(0xD4, 0xD4, 0xDA));
        g.DrawImage(_icon, D(14), (Height - D(36)) / 2, D(36), D(36));
        Gfx.Text(g, _title, Theme.BodyBold, new Rectangle(D(60), D(12), Width - D(130), D(20)), Theme.Text);
        Gfx.Text(g, "maintenant", Theme.Small, new Rectangle(Width - D(90), D(12), D(76), D(20)), Theme.Secondary, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, _text, Theme.Small, new Rectangle(D(60), D(33), Width - D(74), Height - D(38)), Theme.Text,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        _fade.Dispose();
        _icon.Dispose();
        if (_current == this) _current = null;
        base.OnFormClosed(e);
    }
}

/// <summary>Cadre de couleur affiché un instant autour d'une zone trouvée (bouton « Test »).</summary>
internal sealed class HighlightForm : OverlayForm
{
    public HighlightForm(Rectangle area, int thickness, Color color)
    {
        BackColor = color;
        var outer = Rectangle.Inflate(area, thickness, thickness);
        Bounds = outer;
        using var path = new GraphicsPath(FillMode.Alternate);
        path.AddRectangle(new Rectangle(0, 0, outer.Width, outer.Height));
        path.AddRectangle(new Rectangle(thickness, thickness, area.Width, area.Height));
        Region = new Region(path);
    }
}

/// <summary>
/// Écran figé assombri : l'utilisateur entoure à la souris le bouton à détecter.
/// Échap ou clic droit = annuler.
/// </summary>
internal sealed class CaptureForm : Form
{
    readonly Bitmap _shot, _dim;
    readonly Rectangle _screen;
    readonly Font _font;
    Point _start;
    Rectangle _sel;
    bool _dragging;

    /// <summary>Zone choisie, relative à la capture.</summary>
    public Rectangle Selection { get; private set; }

    readonly string _instruction;

    public CaptureForm(Bitmap shot, Rectangle screen,
                       string instruction = "Entoure le bouton à détecter (glisse avec la souris)   ·   Échap = annuler")
    {
        _shot = shot;
        _screen = screen;
        _instruction = instruction;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        Bounds = screen;
        _font = new Font(Theme.Family, 12f, FontStyle.Bold);

        _dim = new Bitmap(shot.Width, shot.Height);
        using var g = Graphics.FromImage(_dim);
        g.DrawImageUnscaled(shot, 0, 0);
        using var shade = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
        g.FillRectangle(shade, 0, 0, shot.Width, shot.Height);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImageUnscaled(_dim, 0, 0);
        if (_sel.Width > 0 && _sel.Height > 0)
        {
            g.DrawImage(_shot, _sel, _sel, GraphicsUnit.Pixel);
            using var pen = new Pen(Theme.Accent, 2);
            g.DrawRectangle(pen, _sel);
        }

        // Consigne en haut de l'écran principal
        string text = _instruction;
        var size = TextRenderer.MeasureText(text, _font);
        var primary = Screen.PrimaryScreen.Bounds;
        var box = new Rectangle(primary.X - _screen.X + (primary.Width - size.Width) / 2 - 24,
                                primary.Y - _screen.Y + 30, size.Width + 48, size.Height + 20);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Gfx.Fill(g, box, box.Height / 2f, Color.FromArgb(235, 30, 30, 32));
        TextRenderer.DrawText(g, text, _font, box, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; return; }
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _start = e.Location;
        _sel = Rectangle.Empty;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        _sel = Rectangle.FromLTRB(Math.Min(_start.X, e.X), Math.Min(_start.Y, e.Y),
                                  Math.Max(_start.X, e.X), Math.Max(_start.Y, e.Y));
        _sel.Intersect(new Rectangle(0, 0, _shot.Width, _shot.Height));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_dragging || e.Button != MouseButtons.Left) return;
        _dragging = false;
        if (_sel.Width >= 8 && _sel.Height >= 8)
        {
            Selection = _sel;
            DialogResult = DialogResult.OK;
        }
        else
        {
            _sel = Rectangle.Empty;      // trop petit : on recommence
            Invalidate();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
        base.OnKeyDown(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _dim.Dispose(); _font.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>Icône du programme (intégrée dans l'exe).</summary>
internal static class AppIcon
{
    public static readonly Icon Small = Load(SystemInformation.SmallIconSize);
    public static readonly Icon Large = Load(SystemInformation.IconSize);

    static Icon Load(Size size)
    {
        try
        {
            using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("AutoClic.app.ico");
            return new Icon(stream, size);
        }
        catch { return SystemIcons.Application; }
    }
}
