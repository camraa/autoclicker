using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoClic;

// =====================================================================================
//  Petite boîte à outils d'interface façon macOS : fenêtre arrondie avec pastilles,
//  boutons capsule, interrupteurs, menus déroulants, champs arrondis, cartes groupées.
//  Suit le thème clair / sombre de Windows.
// =====================================================================================

internal static class Theme
{
    public static readonly bool Dark = DetectDark();

    static bool DetectDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    static Color C(int rgb) => Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

    public static readonly Color Window = Dark ? C(0x1E1E20) : C(0xEFEFF2);
    public static readonly Color TitleBar = Dark ? C(0x2A2A2D) : C(0xF7F7F9);
    public static readonly Color Sidebar = Dark ? C(0x262628) : C(0xE6E6EA);
    public static readonly Color Card = Dark ? C(0x2C2C2F) : C(0xFFFFFF);
    public static readonly Color CardBorder = Dark ? C(0x3A3A3D) : C(0xDCDCE0);
    public static readonly Color Separator = Dark ? C(0x3A3A3D) : C(0xE6E6EB);
    public static readonly Color Text = Dark ? C(0xF2F2F7) : C(0x1D1D1F);
    public static readonly Color Secondary = Dark ? C(0x98989F) : C(0x6E6E73);
    public static readonly Color Tertiary = Dark ? C(0x636368) : C(0xAEAEB2);
    public static readonly Color Accent = Dark ? C(0x0A84FF) : C(0x007AFF);
    public static readonly Color AccentHover = Dark ? C(0x409CFF) : C(0x1A88FF);
    public static readonly Color AccentPressed = Dark ? C(0x0066CC) : C(0x0062CC);
    public static readonly Color Field = Dark ? C(0x1C1C1E) : C(0xFFFFFF);
    public static readonly Color FieldBorder = Dark ? C(0x4A4A4E) : C(0xC9C9CF);
    public static readonly Color Button = Dark ? C(0x48484C) : C(0xFFFFFF);
    public static readonly Color ButtonHover = Dark ? C(0x545458) : C(0xF4F4F6);
    public static readonly Color ButtonPressed = Dark ? C(0x3A3A3E) : C(0xE4E4E8);
    public static readonly Color Hover = Dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0);
    public static readonly Color ToggleOff = Dark ? C(0x3F3F43) : C(0xE2E2E6);
    public static readonly Color Menu = Dark ? C(0x2E2E31) : C(0xF8F8FA);
    public static readonly Color Red = Dark ? C(0xFF453A) : C(0xFF3B30);
    public static readonly Color Orange = Dark ? C(0xFF9F0A) : C(0xFF9500);
    public static readonly Color Green = Dark ? C(0x30D158) : C(0x34C759);
    public static readonly Color Purple = Dark ? C(0xBF5AF2) : C(0xAF52DE);
    public static readonly Color Teal = Dark ? C(0x40C8E0) : C(0x30B0C7);
    public static readonly Color Indigo = Dark ? C(0x5E5CE6) : C(0x5856D6);
    public static readonly Color Pink = Dark ? C(0xFF375F) : C(0xFF2D55);
    public static readonly Color Gray = Dark ? C(0x8E8E93) : C(0x8E8E93);

    public static readonly string Family = PickFamily();
    public static readonly Font Body = new(Family, 9.75f);
    public static readonly Font BodyBold = new(Family, 9.75f, FontStyle.Bold);
    public static readonly Font Small = new(Family, 8.5f);
    public static readonly Font SmallBold = new(Family, 8.5f, FontStyle.Bold);
    public static readonly Font Title = new(Family, 9.75f, FontStyle.Bold);
    public static readonly Font Large = new(Family, 15f, FontStyle.Bold);
    public static readonly Font Icons = new(PickIconFamily(), 10.5f);
    public static readonly Font IconsSmall = new(PickIconFamily(), 9f);

    static string PickFamily()
    {
        foreach (var name in new[] { "Segoe UI Variable Text", "Segoe UI" })
            using (var f = new Font(name, 10f)) if (f.Name == name) return name;
        return SystemFonts.MessageBoxFont.Name;
    }

    static string PickIconFamily()
    {
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            using (var f = new Font(name, 10f)) if (f.Name == name) return name;
        return "Segoe UI Symbol";
    }

    // Glyphes (Segoe MDL2 / Fluent)
    public const string IconAdd = "", IconDelete = "", IconSettings = "", IconKeyboard = "",
                        IconPlay = "", IconTimer = "", IconPhone = "", IconCamera = "",
                        IconEye = "", IconCheck = "", IconWarning = "", IconFolder = "",
                        IconSend = "", IconClose = "", IconMinimize = "";
}

internal static class Gfx
{
    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }

    public static void Fill(Graphics g, RectangleF r, float radius, Color c)
    {
        using var p = Rounded(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, p);
    }

    public static void Stroke(Graphics g, RectangleF r, float radius, Color c, float width = 1)
    {
        using var p = Rounded(r, radius);
        using var pen = new Pen(c, width);
        g.DrawPath(pen, p);
    }

    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static Color Faded(Color c, float alpha) => Color.FromArgb((int)(c.A * alpha), c);

    public static void Text(Graphics g, string text, Font font, Rectangle r, Color c,
        TextFormatFlags align = TextFormatFlags.Left | TextFormatFlags.VerticalCenter)
        => TextRenderer.DrawText(g, text, font, r, c, align | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

    /// <summary>Double chevron des menus déroulants macOS (⌃⌄).</summary>
    public static void Chevrons(Graphics g, PointF center, float size, Color c)
    {
        using var pen = new Pen(c, Math.Max(1.4f, size / 4.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float w = size / 2.6f, h = size / 4.2f, gap = size / 5f;
        g.DrawLines(pen, new[] { new PointF(center.X - w, center.Y - gap), new PointF(center.X, center.Y - gap - h), new PointF(center.X + w, center.Y - gap) });
        g.DrawLines(pen, new[] { new PointF(center.X - w, center.Y + gap), new PointF(center.X, center.Y + gap + h), new PointF(center.X + w, center.Y + gap) });
    }
}

/// <summary>Base des contrôles dessinés : double tampon, fond transparent, échelle d'affichage.</summary>
internal abstract class MacControl : Control
{
    protected bool Hovered, Pressed;

    protected MacControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        ForeColor = Theme.Text;
        Font = Theme.Body;
    }

    protected float S => DeviceDpi / 96f;
    protected int D(float v) => (int)Math.Round(v * S);

    protected override void OnMouseEnter(EventArgs e) { Hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hovered = false; Pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
}

internal enum MacButtonStyle { Secondary, Primary, Destructive, Plain, PlainDanger }

/// <summary>Bouton capsule (principal bleu, secondaire, destructif, ou simple icône).</summary>
internal sealed class MacButton : MacControl, IButtonControl
{
    public MacButtonStyle Style { get; set; }
    /// <summary>Glyphe d'icône (Segoe MDL2) affiché avant le texte, ou seul.</summary>
    public string Icon { get; set; }
    public DialogResult DialogResult { get; set; }
    public Color? IconColor { get; set; }

    public MacButton(string text = "", MacButtonStyle style = MacButtonStyle.Secondary, string icon = null)
    {
        Text = text;
        Style = style;
        Icon = icon;
        TabStop = true;
        Size = new Size(D(90), D(28));
    }

    public void NotifyDefault(bool value) { }

    public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (DialogResult != DialogResult.None && FindForm() is Form f) f.DialogResult = DialogResult;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { PerformClick(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Gfx.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        float radius = r.Height / 2;
        float alpha = Enabled ? 1 : 0.4f;
        Color text = Theme.Text;

        switch (Style)
        {
            case MacButtonStyle.Primary:
                Gfx.Fill(g, r, radius, Gfx.Faded(Pressed ? Theme.AccentPressed : Hovered ? Theme.AccentHover : Theme.Accent, alpha));
                text = Color.White;
                break;
            case MacButtonStyle.Secondary:
            case MacButtonStyle.Destructive:
                Gfx.Fill(g, r, radius, Gfx.Faded(Pressed ? Theme.ButtonPressed : Hovered ? Theme.ButtonHover : Theme.Button, alpha));
                if (!Theme.Dark) Gfx.Stroke(g, r, radius, Gfx.Faded(Theme.FieldBorder, alpha));
                text = Style == MacButtonStyle.Destructive ? Theme.Red : Theme.Text;
                break;
            default:
                if (Hovered || Pressed) Gfx.Fill(g, r, Math.Min(radius, D(7)), Pressed ? Gfx.Faded(Theme.Hover, 2) : Theme.Hover);
                text = Style == MacButtonStyle.PlainDanger ? Theme.Red : Theme.Accent;
                break;
        }
        if (Focused && ShowFocusCues) Gfx.Stroke(g, RectangleF.Inflate(r, -1, -1), radius, Gfx.Faded(Theme.Accent, 0.6f), 2);

        text = Gfx.Faded(text, alpha);
        var iconColor = IconColor.HasValue ? Gfx.Faded(IconColor.Value, alpha) : text;
        bool hasIcon = !string.IsNullOrEmpty(Icon), hasText = !string.IsNullOrEmpty(Text);
        var font = Style == MacButtonStyle.Primary ? Theme.BodyBold : Theme.Body;
        if (hasIcon && !hasText)
        {
            Gfx.Text(g, Icon, Theme.Icons, ClientRectangle, iconColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        var textSize = TextRenderer.MeasureText(g, Text, font, Size.Empty, TextFormatFlags.NoPadding);
        int iconW = hasIcon ? D(18) : 0;
        int x = (Width - textSize.Width - iconW) / 2;
        if (hasIcon) Gfx.Text(g, Icon, Theme.IconsSmall, new Rectangle(x, 0, iconW, Height), iconColor);
        Gfx.Text(g, Text, font, new Rectangle(x + iconW, 0, textSize.Width + 2, Height), text);
    }
}

/// <summary>Interrupteur macOS (avec libellé à gauche si Text est rempli).</summary>
internal sealed class MacToggle : MacControl
{
    bool _checked;
    float _pos;
    readonly Timer _anim = new() { Interval = 15 };

    public event EventHandler CheckedChanged;

    public MacToggle(string text = "")
    {
        Text = text;
        TabStop = true;
        Size = new Size(D(40), D(24));
        _anim.Tick += (_, _) =>
        {
            float target = _checked ? 1 : 0;
            _pos += Math.Sign(target - _pos) * 0.2f;
            if (Math.Abs(_pos - target) < 0.2f) { _pos = target; _anim.Stop(); }
            Invalidate();
        };
    }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            if (IsHandleCreated && Visible) _anim.Start(); else _pos = value ? 1 : 0;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Gfx.Smooth(g);
        float alpha = Enabled ? 1 : 0.4f;
        int sw = D(38), sh = D(22);
        var r = new RectangleF(Width - sw - 0.5f, (Height - sh) / 2f, sw, sh);
        if (!string.IsNullOrEmpty(Text))
            Gfx.Text(g, Text, Font, new Rectangle(0, 0, Width - sw - D(10), Height), Gfx.Faded(Theme.Text, alpha));

        Gfx.Fill(g, r, sh / 2f, Gfx.Faded(Gfx.Mix(Theme.ToggleOff, Theme.Accent, _pos), alpha));
        float knob = sh - D(4);
        float x = r.X + D(2) + (sw - knob - D(4)) * _pos;
        var k = new RectangleF(x, r.Y + D(2), knob, knob);
        using (var shadow = new SolidBrush(Color.FromArgb((int)(40 * alpha), 0, 0, 0)))
            g.FillEllipse(shadow, k.X, k.Y + 0.8f, k.Width, k.Height);
        using (var b = new SolidBrush(Gfx.Faded(Color.White, alpha))) g.FillEllipse(b, k);
        if (Focused && ShowFocusCues) Gfx.Stroke(g, RectangleF.Inflate(r, 1.5f, 1.5f), sh / 2f + 1.5f, Gfx.Faded(Theme.Accent, 0.5f), 2);
    }

    protected override void Dispose(bool disposing) { if (disposing) _anim.Dispose(); base.Dispose(disposing); }
}

/// <summary>Champ de texte arrondi (une vraie TextBox sans bordure à l'intérieur).</summary>
internal class MacField : MacControl
{
    public TextBox Box { get; }
    protected virtual int RightReserve => 0;

    public MacField(TextBox box = null, string placeholder = null)
    {
        Box = box ?? new TextBox();
        Box.BorderStyle = BorderStyle.None;
        Box.BackColor = Theme.Field;
        Box.ForeColor = Theme.Text;
        Box.Font = Theme.Body;
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Controls.Add(Box);
        Size = new Size(D(160), D(28));
        Cursor = Cursors.IBeam;
        if (placeholder != null) Native.SetCueBanner(Box, placeholder);
    }

    public override string Text { get => Box.Text; set => Box.Text = value; }

    public new event EventHandler TextChanged { add => Box.TextChanged += value; remove => Box.TextChanged -= value; }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Box == null) return;                   // appelé par le constructeur de base, avant la création de la case
        int h = Box.PreferredHeight;
        Box.SetBounds(D(8), Math.Max(0, (Height - h) / 2), Math.Max(10, Width - D(16) - RightReserve), h);
    }

    protected override void OnMouseDown(MouseEventArgs e) { Box.Focus(); base.OnMouseDown(e); }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Box.Enabled = Enabled;
        Box.BackColor = Theme.Field;
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Gfx.Smooth(g);
        var r = new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f);
        float alpha = Enabled ? 1 : 0.5f;
        Gfx.Fill(g, r, D(6), Gfx.Faded(Theme.Field, alpha));
        if (Box.Focused) Gfx.Stroke(g, RectangleF.Inflate(r, 0.5f, 0.5f), D(6.5f), Gfx.Faded(Theme.Accent, 0.85f), 2.2f);
        else Gfx.Stroke(g, r, D(6), Gfx.Faded(Theme.FieldBorder, alpha));
        PaintExtra(g);
    }

    protected virtual void PaintExtra(Graphics g) { }
}

/// <summary>Champ numérique avec petites flèches (stepper) à droite.</summary>
internal sealed class MacNumber : MacField
{
    decimal _value, _min, _max = 100;
    bool _updating;
    readonly Timer _repeat = new() { Interval = 350 };
    int _repeatDir;

    public decimal Increment { get; set; } = 1;
    public int DecimalPlaces { get; set; }
    public event EventHandler ValueChanged;
    protected override int RightReserve => D(16);

    public MacNumber(decimal min, decimal max, decimal value, int decimals = 0, decimal increment = 1) : base()
    {
        _min = min;
        _max = max;
        DecimalPlaces = decimals;
        Increment = increment;
        Box.TextAlign = HorizontalAlignment.Right;
        Size = new Size(D(76), D(28));
        Cursor = Cursors.Default;
        _value = Clamp(value);
        ShowValue();
        Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Up) { Step(1); e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { Step(-1); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { Commit(); e.Handled = e.SuppressKeyPress = true; }
        };
        Box.Leave += (_, _) => Commit();
        Box.TextChanged += (_, _) => { if (!_updating && TryParse(Box.Text, out var v) && v >= _min && v <= _max) SetValue(v, false); };
        Box.MouseWheel += (_, e) => { if (Box.Focused) Step(Math.Sign(e.Delta)); };
        _repeat.Tick += (_, _) => { _repeat.Interval = 60; Step(_repeatDir); };
    }

    public decimal Value { get => _value; set => SetValue(value, true); }
    public decimal Minimum { get => _min; set { _min = value; Value = _value; } }
    public decimal Maximum { get => _max; set { _max = value; Value = _value; } }

    decimal Clamp(decimal v) => Math.Max(_min, Math.Min(_max, Math.Round(v, DecimalPlaces)));

    void SetValue(decimal v, bool show)
    {
        v = Clamp(v);
        bool changed = v != _value;
        _value = v;
        if (show) ShowValue();
        if (changed) ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    void ShowValue()
    {
        _updating = true;
        Box.Text = _value.ToString("F" + DecimalPlaces, CultureInfo.CurrentCulture);
        _updating = false;
    }

    void Commit()
    {
        if (TryParse(Box.Text, out var v)) SetValue(v, true);
        else ShowValue();
    }

    static bool TryParse(string s, out decimal v) =>
        decimal.TryParse((s ?? "").Replace('.', ',').Replace(" ", ""), NumberStyles.Number, CultureInfo.GetCultureInfo("fr-FR"), out v);

    void Step(int dir) => SetValue(_value + dir * Increment, true);

    Rectangle Stepper => new(Width - D(19), D(4), D(14), Height - D(8));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Stepper.Contains(e.Location))
        {
            _repeatDir = e.Y < Height / 2 ? 1 : -1;
            Step(_repeatDir);
            _repeat.Interval = 350;
            _repeat.Start();
            return;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _repeat.Stop(); base.OnMouseUp(e); }
    protected override void OnMouseLeave(EventArgs e) { _repeat.Stop(); base.OnMouseLeave(e); }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        Step(Math.Sign(e.Delta));
        base.OnMouseWheel(e);
    }

    protected override void PaintExtra(Graphics g)
    {
        var s = Stepper;
        Gfx.Chevrons(g, new PointF(s.X + s.Width / 2f, s.Y + s.Height / 2f), D(11), Gfx.Faded(Theme.Secondary, Enabled ? 1 : 0.4f));
    }

    protected override void Dispose(bool disposing) { if (disposing) _repeat.Dispose(); base.Dispose(disposing); }
}

/// <summary>Menu déroulant macOS (« pop-up button ») : libellé + double chevron, menu arrondi.</summary>
internal sealed class MacPopup : MacControl
{
    readonly List<string> _items = [];
    int _selected = -1;

    public event EventHandler SelectedIndexChanged;

    public MacPopup(params string[] items)
    {
        _items.AddRange(items);
        if (_items.Count > 0) _selected = 0;
        TabStop = true;
        Size = new Size(D(170), D(28));
    }

    public IList<string> Items => _items;

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            value = Math.Max(-1, Math.Min(_items.Count - 1, value));
            if (value == _selected) return;
            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string SelectedItem => _selected >= 0 && _selected < _items.Count ? _items[_selected] : "";

    public void SetItems(IEnumerable<string> items, int selected)
    {
        _items.Clear();
        _items.AddRange(items);
        _selected = Math.Max(-1, Math.Min(_items.Count - 1, selected));
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && Enabled) Open();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Down) { SelectedIndex++; e.Handled = true; }
        else if (e.KeyCode == Keys.Up) { SelectedIndex = Math.Max(0, _selected - 1); e.Handled = true; }
        else if (e.KeyCode is Keys.Space or Keys.Enter) { Open(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    void Open()
    {
        var menu = MacMenu.Create();
        menu.MinimumSize = new Size(Width, 0);
        for (int i = 0; i < _items.Count; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(_items[i]) { Checked = i == _selected };
            item.Click += (_, _) => SelectedIndex = index;
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => { Pressed = false; Invalidate(); BeginInvoke(new Action(menu.Dispose)); };
        menu.Show(this, new Point(0, Height + D(2)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Gfx.Smooth(g);
        float alpha = Enabled ? 1 : 0.45f;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Gfx.Fill(g, r, D(7), Gfx.Faded(Pressed ? Theme.ButtonPressed : Hovered ? Theme.ButtonHover : Theme.Button, alpha));
        if (!Theme.Dark) Gfx.Stroke(g, r, D(7), Gfx.Faded(Theme.FieldBorder, alpha));
        if (Focused && ShowFocusCues) Gfx.Stroke(g, RectangleF.Inflate(r, -1, -1), D(6), Gfx.Faded(Theme.Accent, 0.6f), 2);
        Gfx.Text(g, SelectedItem, Font, new Rectangle(D(10), 0, Width - D(34), Height), Gfx.Faded(Theme.Text, alpha));
        // Petit carré bleu avec le double chevron (comme les pop-ups de macOS)
        var box = new RectangleF(Width - D(22), (Height - D(18)) / 2f, D(16), D(18));
        Gfx.Fill(g, box, D(5), Gfx.Faded(Theme.Accent, alpha));
        Gfx.Chevrons(g, new PointF(box.X + box.Width / 2, box.Y + box.Height / 2), D(11), Color.White);
    }
}

/// <summary>Menus contextuels arrondis façon macOS (menu de l'icône, menus déroulants).</summary>
internal static class MacMenu
{
    public static ContextMenuStrip Create()
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new MacMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = true,
            Font = Theme.Body,
            Padding = new Padding(0, 5, 0, 5),
            BackColor = Theme.Menu,
            ForeColor = Theme.Text,
        };
        menu.ItemAdded += (_, e) => { e.Item.Padding = new Padding(2, 3, 10, 3); e.Item.ForeColor = Theme.Text; };
        menu.Opened += (_, _) =>
        {
            using var p = Gfx.Rounded(new RectangleF(0, 0, menu.Width, menu.Height), 9);
            menu.Region = new Region(p);
        };
        return menu;
    }

    sealed class MacMenuRenderer : ToolStripProfessionalRenderer
    {
        public MacMenuRenderer() : base(new ProfessionalColorTable()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(Theme.Menu);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var g = e.Graphics;
            Gfx.Smooth(g);
            Gfx.Stroke(g, new RectangleF(0.5f, 0.5f, e.ToolStrip.Width - 1.5f, e.ToolStrip.Height - 1.5f), 9, Theme.CardBorder);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            Gfx.Smooth(g);
            Gfx.Fill(g, new RectangleF(5, 0, e.Item.Width - 10, e.Item.Height), 5, Theme.Accent);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? Theme.Tertiary : e.Item.Selected ? Color.White : Theme.Text;
            e.TextFont = Theme.Body;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var color = e.Item.Selected ? Color.White : Theme.Text;
            Gfx.Text(e.Graphics, Theme.IconCheck, Theme.IconsSmall, e.ImageRectangle, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Theme.Separator);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item.Selected ? Color.White : Theme.Secondary;
            base.OnRenderArrow(e);
        }
    }
}

/// <summary>Carte arrondie (groupe de réglages) avec des lignes séparées par un trait fin.</summary>
internal sealed class MacCard : Panel
{
    readonly List<int> _separators = [];
    int _y;

    float S => DeviceDpi / 96f;
    int D(float v) => (int)Math.Round(v * S);

    public MacCard(int width)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Width = width;
        Height = D(4);
        _y = D(2);
    }

    /// <summary>Ligne : libellé à gauche (avec explication en petit dessous), contrôles alignés à droite.</summary>
    public MacCard Row(string label, string hint = null, params Control[] right) => Row(null, label, hint, right);

    /// <summary>Ligne avec un élément au début (pastille, miniature), puis libellé, puis contrôles à droite.</summary>
    public MacCard Row(Control lead, string label, string hint, params Control[] right)
    {
        int h = hint == null ? D(44) : D(58);
        if (lead != null) h = Math.Max(h, lead.Height + D(16));
        AddSeparator();
        int lx = D(14);
        if (lead != null)
        {
            lead.Location = new Point(lx, _y + (h - lead.Height) / 2);
            Controls.Add(lead);
            lx += lead.Width + D(10);
        }
        var l = Label(label, Theme.Body, Theme.Text);
        int rightWidth = right.Sum(c => c.Width) + Math.Max(0, right.Length - 1) * D(6);
        int labelW = Width - lx - D(14) - rightWidth - D(8);
        if (hint == null)
        {
            l.SetBounds(lx, _y, labelW, h);
        }
        else
        {
            l.SetBounds(lx, _y + (h - D(40)) / 2, labelW, D(20));
            var hl = Label(hint, Theme.Small, Theme.Secondary);
            hl.SetBounds(lx, _y + (h - D(40)) / 2 + D(20), labelW, D(20));
            Controls.Add(hl);
        }
        Controls.Add(l);
        int x = Width - D(14);
        for (int i = right.Length - 1; i >= 0; i--)
        {
            var c = right[i];
            x -= c.Width;
            c.Location = new Point(x, _y + (h - c.Height) / 2);
            Controls.Add(c);
            x -= D(6);
        }
        Advance(h);
        return this;
    }

    /// <summary>Ligne occupant toute la largeur (ex. un champ de texte, un message).</summary>
    public MacCard Full(Control c, int height = 0, bool separator = true)
    {
        int h = height > 0 ? height : c.Height + D(16);
        if (separator) AddSeparator(); else if (_y > D(2)) _y -= D(6);
        c.SetBounds(D(14), _y + (h - c.Height) / 2, Width - D(28), c.Height);
        Controls.Add(c);
        Advance(h);
        return this;
    }

    /// <summary>Texte explicatif en petit gris, sur toute la largeur.</summary>
    public MacCard Note(string text, Color? color = null)
    {
        var l = Label(text, Theme.Small, color ?? Theme.Secondary);
        using (var g = CreateGraphics())
        {
            var size = TextRenderer.MeasureText(g, text, Theme.Small, new Size(Width - D(28), 0), TextFormatFlags.WordBreak);
            l.Height = size.Height + D(4);
        }
        l.Width = Width - D(28);
        return Full(l, l.Height + D(14));
    }

    public Label NoteLabel(string text, Color? color = null, int lines = 2)
    {
        var l = Label(text, Theme.Small, color ?? Theme.Secondary);
        l.Height = D(16 * lines + 2);
        Full(l, l.Height + D(14));
        return l;
    }

    void AddSeparator()
    {
        if (_y > D(2)) _separators.Add(_y);
    }

    void Advance(int h)
    {
        _y += h;
        Height = _y + D(2);
    }

    public static Label Label(string text, Font font, Color color) => new()
    {
        Text = text,
        Font = font,
        ForeColor = color,
        BackColor = Color.Transparent,
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        UseMnemonic = false,
    };

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var b = new SolidBrush(Parent?.BackColor ?? Theme.Window)) g.FillRectangle(b, ClientRectangle);
        Gfx.Smooth(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Gfx.Fill(g, r, D(10), Theme.Card);
        Gfx.Stroke(g, r, D(10), Theme.CardBorder);
        using var pen = new Pen(Theme.Separator);
        foreach (int y in _separators) g.DrawLine(pen, D(14), y, Width - D(14), y);
    }
}

/// <summary>Zone qui défile avec la molette, barre fine façon macOS.</summary>
internal sealed class MacScroll : Control, IMessageFilter
{
    int _offset;
    bool _dragging;
    int _dragStartY, _dragStartOffset;

    public Panel Content { get; }
    float S => DeviceDpi / 96f;
    int D(float v) => (int)Math.Round(v * S);
    int Bar => D(10);

    public MacScroll()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Window;
        Content = new Panel { BackColor = Theme.Window, Location = Point.Empty };
        Controls.Add(Content);
        Application.AddMessageFilter(this);
    }

    public int ContentWidth => Width - Bar;

    public void SetContentHeight(int h)
    {
        Content.SetBounds(0, -_offset, ContentWidth, Math.Max(h, Height));
        ScrollTo(_offset);
    }

    void ScrollTo(int offset)
    {
        _offset = Math.Max(0, Math.Min(Math.Max(0, Content.Height - Height), offset));
        Content.Top = -_offset;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Content.Width = ContentWidth;
        ScrollTo(_offset);
    }

    public bool PreFilterMessage(ref Message m)
    {
        const int WM_MOUSEWHEEL = 0x20A;
        if (m.Msg != WM_MOUSEWHEEL || !Visible || !IsHandleCreated || Content.Height <= Height) return false;
        var screen = new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF));
        if (!RectangleToScreen(ClientRectangle).Contains(screen)) return false;
        // Un champ numérique sélectionné garde la molette pour lui
        if (FromHandle(m.HWnd) is TextBox tb && tb.Parent is MacNumber && tb.Focused) return false;
        int delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
        ScrollTo(_offset - delta / 120 * D(48));
        return true;
    }

    Rectangle Thumb()
    {
        if (Content.Height <= Height) return Rectangle.Empty;
        int h = Math.Max(D(30), Height * Height / Content.Height);
        int y = (int)((Height - h) * (_offset / (double)Math.Max(1, Content.Height - Height)));
        return new Rectangle(Width - Bar + D(2), y, D(6), h);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var t = Thumb();
        if (t.Contains(e.Location)) { _dragging = true; _dragStartY = e.Y; _dragStartOffset = _offset; }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            int track = Height - Thumb().Height;
            ScrollTo(_dragStartOffset + (int)((e.Y - _dragStartY) * (Content.Height - Height) / (double)Math.Max(1, track)));
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
        var t = Thumb();
        if (t.IsEmpty) return;
        Gfx.Smooth(g);
        Gfx.Fill(g, t, D(3), Gfx.Faded(Theme.Secondary, 0.55f));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Application.RemoveMessageFilter(this);
        base.Dispose(disposing);
    }
}

/// <summary>
/// Fenêtre façon macOS : sans bordure Windows, coins arrondis, ombre, barre de titre
/// avec les trois pastilles (fermer / réduire / agrandir), déplaçable par la barre.
/// </summary>
internal class MacForm : Form
{
    const int WM_NCHITTEST = 0x84, HTCAPTION = 2, CS_DROPSHADOW = 0x20000;
    bool _lightsHovered;

    protected float S { get; }
    protected int D(float v) => (int)Math.Round(v * S);
    public int TitleHeight => D(40);
    /// <summary>Pastille jaune : réduire (cache la fenêtre). Désactivée par défaut.</summary>
    protected bool CanMinimize { get; set; }
    /// <summary>Contenu de la barre de titre (vide = titre centré).</summary>
    protected bool ShowTitle { get; set; } = true;

    protected MacForm()
    {
        S = DeviceDpi / 96f;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Icon = AppIcon.Large;
        DoubleBuffered = true;
        KeyPreview = true;
        MaximizeBox = false;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        using var p = Gfx.Rounded(new RectangleF(0, 0, Width, Height), D(12));
        Region = new Region(p);
    }

    Rectangle Light(int i) => new(D(14) + i * D(20), (TitleHeight - D(12)) / 2, D(12), D(12));

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        Gfx.Smooth(g);
        using (var b = new SolidBrush(Theme.TitleBar)) g.FillRectangle(b, 0, 0, Width, TitleHeight);
        using (var pen = new Pen(Theme.Separator)) g.DrawLine(pen, 0, TitleHeight - 1, Width, TitleHeight - 1);

        Color[] colors = [Color.FromArgb(0xFF, 0x5F, 0x57), Color.FromArgb(0xFE, 0xBC, 0x2E), Color.FromArgb(0x28, 0xC8, 0x40)];
        string[] glyphs = ["", "", ""];
        bool[] active = [true, CanMinimize, false];
        for (int i = 0; i < 3; i++)
        {
            var r = Light(i);
            var c = active[i] && ContainsFocus || active[i] && _lightsHovered ? colors[i] : Theme.Dark ? Color.FromArgb(0x4A, 0x4A, 0x4E) : Color.FromArgb(0xD0, 0xD0, 0xD4);
            if (!active[i]) c = Theme.Dark ? Color.FromArgb(0x45, 0x45, 0x48) : Color.FromArgb(0xD8, 0xD8, 0xDC);
            using (var b = new SolidBrush(c)) g.FillEllipse(b, r);
            if (_lightsHovered && active[i])
                using (var f = new Font(Theme.Icons.FontFamily, 5.5f))
                    Gfx.Text(g, glyphs[i], f, Rectangle.Inflate(r, 2, 2), Color.FromArgb(150, 0, 0, 0), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        if (ShowTitle)
            Gfx.Text(g, Text, Theme.Title, new Rectangle(D(80), 0, Width - D(160), TitleHeight), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        using (var pen = new Pen(Theme.Dark ? Color.FromArgb(0x50, 0x50, 0x55) : Color.FromArgb(0xB8, 0xB8, 0xBE)))
        using (var p = Gfx.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), D(12)))
            g.DrawPath(pen, p);
    }

    protected override void OnActivated(EventArgs e) { Invalidate(new Rectangle(0, 0, D(80), TitleHeight)); base.OnActivated(e); }
    protected override void OnDeactivate(EventArgs e) { Invalidate(new Rectangle(0, 0, D(80), TitleHeight)); base.OnDeactivate(e); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool over = new Rectangle(D(8), 0, D(70), TitleHeight).Contains(e.Location);
        if (over != _lightsHovered) { _lightsHovered = over; Invalidate(new Rectangle(0, 0, D(80), TitleHeight)); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (_lightsHovered) { _lightsHovered = false; Invalidate(new Rectangle(0, 0, D(80), TitleHeight)); }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (Rectangle.Inflate(Light(0), 3, 3).Contains(e.Location)) OnCloseButton();
            else if (CanMinimize && Rectangle.Inflate(Light(1), 3, 3).Contains(e.Location)) OnMinimizeButton();
        }
        base.OnMouseUp(e);
    }

    /// <summary>Pastille rouge : par défaut ferme (ou annule une fenêtre de dialogue).</summary>
    protected virtual void OnCloseButton()
    {
        if (Modal) DialogResult = DialogResult.Cancel;
        else Close();
    }

    protected virtual void OnMinimizeButton() => Hide();

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_NCHITTEST && (int)m.Result == 1)
        {
            var p = PointToClient(new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF)));
            if (p.Y < TitleHeight && p.X > D(76)) m.Result = (IntPtr)HTCAPTION;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && Modal) { DialogResult = DialogResult.Cancel; return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Petit libellé d'unité (« s », « % », « min ») à côté d'un champ.</summary>
    protected Label Unit(string text)
    {
        var l = MacCard.Label(text, Theme.Body, Theme.Secondary);
        l.AutoEllipsis = false;
        l.Size = new Size(D(6 + 8 * text.Length), D(28));
        return l;
    }

    /// <summary>Titre de section au-dessus d'une carte (petit, gras, gris).</summary>
    protected Label SectionTitle(string text) => MacCard.Label(text, Theme.SmallBold, Theme.Secondary);
}

/// <summary>Boîte de dialogue façon macOS (icône, message, boutons capsule).</summary>
internal sealed class MacAlert : MacForm
{
    MacAlert(string title, string message, string ok, string cancel, bool destructive)
    {
        Text = "";
        ShowTitle = false;
        TopMost = true;
        ShowInTaskbar = false;
        int w = D(300);
        var icon = new PictureBox { Image = AppIcon.Large.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
        icon.SetBounds((w - D(56)) / 2, D(28), D(56), D(56));
        Controls.Add(icon);

        var t = MacCard.Label(title, Theme.Title, Theme.Text);
        t.TextAlign = ContentAlignment.MiddleCenter;
        t.SetBounds(D(20), D(92), w - D(40), D(24));
        Controls.Add(t);

        int y = D(118);
        if (!string.IsNullOrEmpty(message))
        {
            var m = MacCard.Label(message, Theme.Small, Theme.Secondary);
            m.TextAlign = ContentAlignment.TopCenter;
            m.AutoEllipsis = false;
            using var g = CreateGraphics();
            int h = TextRenderer.MeasureText(g, message, Theme.Small, new Size(w - D(40), 0), TextFormatFlags.WordBreak).Height + D(6);
            m.SetBounds(D(20), y, w - D(40), h);
            Controls.Add(m);
            y += h + D(6);
        }
        y += D(10);
        var okButton = new MacButton(ok, destructive ? MacButtonStyle.Destructive : MacButtonStyle.Primary) { DialogResult = DialogResult.OK };
        if (cancel != null)
        {
            var cancelButton = new MacButton(cancel) { DialogResult = DialogResult.Cancel };
            cancelButton.SetBounds(D(20), y, (w - D(50)) / 2, D(30));
            okButton.SetBounds(D(30) + (w - D(50)) / 2, y, (w - D(50)) / 2, D(30));
            Controls.Add(cancelButton);
            CancelButton = cancelButton;
        }
        else okButton.SetBounds(D(20), y, w - D(40), D(30));
        Controls.Add(okButton);
        AcceptButton = okButton;
        ClientSize = new Size(w, y + D(48));
    }

    public static bool Ask(IWin32Window owner, string title, string message, string ok, string cancel = "Annuler", bool destructive = false)
    {
        using var a = new MacAlert(title, message, ok, cancel, destructive);
        return a.ShowDialog(owner) == DialogResult.OK;
    }

    public static void Info(IWin32Window owner, string title, string message)
    {
        using var a = new MacAlert(title, message, "OK", null, false);
        a.ShowDialog(owner);
    }
}

/// <summary>Pastille ronde numérotée (couleur au choix).</summary>
internal sealed class MacBadge : MacControl
{
    public Color Color { get; set; }

    public MacBadge(string text, Color color)
    {
        Text = text;
        Color = color;
        Font = Theme.SmallBold;
        Size = new Size(D(22), D(22));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Gfx.Smooth(g);
        using (var b = new SolidBrush(Color)) g.FillEllipse(b, 0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Gfx.Text(g, Text, Font, ClientRectangle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>Miniature arrondie d'une image.</summary>
internal sealed class MacThumb : MacControl
{
    public Image Image { get; set; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Gfx.Smooth(g);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Gfx.Fill(g, r, D(6), Theme.Dark ? Color.FromArgb(0x1A, 0x1A, 0x1C) : Color.FromArgb(0xF2, 0xF2, 0xF4));
        if (Image != null)
        {
            float scale = Math.Min((Width - 4f) / Image.Width, (Height - 4f) / Image.Height);
            float w = Image.Width * scale, h = Image.Height * scale;
            using var clip = Gfx.Rounded(r, D(6));
            g.SetClip(clip);
            g.DrawImage(Image, (Width - w) / 2, (Height - h) / 2, w, h);
            g.ResetClip();
        }
        else Gfx.Text(g, "?", Theme.Body, ClientRectangle, Theme.Red, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        Gfx.Stroke(g, r, D(6), Theme.CardBorder);
    }
}

/// <summary>Champ « appuie sur une touche » au style des autres champs.</summary>
internal sealed class MacKeyField : MacField
{
    public KeyBox Key => (KeyBox)Box;

    public MacKeyField() : base(new KeyBox())
    {
        Size = new Size(D(130), D(28));
        Box.BackColor = Theme.Field;
        Box.ForeColor = Theme.Text;
    }
}
