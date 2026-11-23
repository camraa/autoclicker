using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace AutoClic;

/// <summary>
/// Une touche ou une combinaison (« F10 », « Ctrl+Maj+S », « Alt »), lisible en français ou en anglais.
/// </summary>
internal sealed class KeyCombo
{
    const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B;

    /// <summary>Touches dans l'ordre d'appui : modificateurs puis touche principale.</summary>
    public Input.Key[] Keys { get; }
    public ushort Main { get; }
    public bool Ctrl { get; }
    public bool Alt { get; }
    public bool Shift { get; }
    public bool Win { get; }

    KeyCombo(ushort main, bool ctrl, bool alt, bool shift, bool win)
    {
        Main = main;
        Ctrl = ctrl && main != VK_CONTROL;
        Alt = alt && main != VK_MENU;
        Shift = shift && main != VK_SHIFT;
        Win = win && main != VK_LWIN;
        var keys = new List<Input.Key>();
        if (Ctrl) keys.Add(new Input.Key(VK_CONTROL));
        if (Alt) keys.Add(new Input.Key(VK_MENU));
        if (Shift) keys.Add(new Input.Key(VK_SHIFT));
        if (Win) keys.Add(new Input.Key(VK_LWIN, true));
        keys.Add(new Input.Key(main, IsExtended(main)));
        Keys = keys.ToArray();
    }

    public static KeyCombo FromKeys(Keys keyData)
    {
        var code = keyData & System.Windows.Forms.Keys.KeyCode;
        ushort vk = code switch
        {
            System.Windows.Forms.Keys.ShiftKey or System.Windows.Forms.Keys.LShiftKey or System.Windows.Forms.Keys.RShiftKey => VK_SHIFT,
            System.Windows.Forms.Keys.ControlKey or System.Windows.Forms.Keys.LControlKey or System.Windows.Forms.Keys.RControlKey => VK_CONTROL,
            System.Windows.Forms.Keys.Menu or System.Windows.Forms.Keys.LMenu or System.Windows.Forms.Keys.RMenu => VK_MENU,
            System.Windows.Forms.Keys.LWin or System.Windows.Forms.Keys.RWin => VK_LWIN,
            _ => (ushort)code,
        };
        return new KeyCombo(vk,
            (keyData & System.Windows.Forms.Keys.Control) != 0,
            (keyData & System.Windows.Forms.Keys.Alt) != 0,
            (keyData & System.Windows.Forms.Keys.Shift) != 0, false);
    }

    public static bool TryParse(string text, out KeyCombo combo)
    {
        combo = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool ctrl = false, alt = false, shift = false, win = false;
        ushort? main = null;
        foreach (var raw in text.Split('+'))
        {
            string part = TextFinder.Normalize(raw);
            if (part.Length == 0) return false;
            if (!Names.TryGetValue(part, out ushort vk)) return false;
            if (main.HasValue)
            {
                // L'ancienne touche principale était en fait un modificateur
                if (!MarkModifier(main.Value, ref ctrl, ref alt, ref shift, ref win)) return false;
            }
            main = vk;
        }
        if (!main.HasValue) return false;
        combo = new KeyCombo(main.Value, ctrl, alt, shift, win);
        return true;
    }

    static bool MarkModifier(ushort vk, ref bool ctrl, ref bool alt, ref bool shift, ref bool win)
    {
        switch (vk)
        {
            case VK_CONTROL: ctrl = true; return true;
            case VK_MENU: alt = true; return true;
            case VK_SHIFT: shift = true; return true;
            case VK_LWIN: win = true; return true;
            default: return false;
        }
    }

    /// <summary>Liste « Alt ; Maj+Z ; Inser » → combinaisons. Renvoie false si un élément est illisible.</summary>
    public static bool TryParseList(string text, out List<KeyCombo> combos, out string bad)
    {
        combos = [];
        bad = null;
        foreach (var part in (text ?? "").Split(';', ',').Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            if (!TryParse(part, out var c)) { bad = part; return false; }
            combos.Add(c);
        }
        return true;
    }

    /// <summary>La combinaison est-elle enfoncée en ce moment ?</summary>
    public bool IsDown() =>
        Native.IsDown(Main)
        && (!Ctrl || Native.IsDown(VK_CONTROL))
        && (!Alt || Native.IsDown(VK_MENU))
        && (!Shift || Native.IsDown(VK_SHIFT))
        && (!Win || Native.IsDown(VK_LWIN) || Native.IsDown(0x5C));

    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Maj");
        if (Win) parts.Add("Win");
        parts.Add(DisplayName(Main));
        return string.Join("+", parts);
    }

    public static string ListToString(IEnumerable<KeyCombo> combos) => string.Join(" ; ", combos.Select(c => c.ToString()));

    static bool IsExtended(ushort vk) =>
        vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5;

    // ---------- Noms des touches ----------
    static readonly Dictionary<string, ushort> Names = BuildNames();
    static readonly Dictionary<ushort, string> Display = new()
    {
        [VK_CONTROL] = "Ctrl", [VK_MENU] = "Alt", [VK_SHIFT] = "Maj", [VK_LWIN] = "Win",
        [0x0D] = "Entrée", [0x1B] = "Échap", [0x20] = "Espace", [0x09] = "Tab", [0x08] = "Retour",
        [0x2D] = "Inser", [0x2E] = "Suppr", [0x24] = "Début", [0x23] = "Fin",
        [0x21] = "PageHaut", [0x22] = "PageBas", [0x26] = "Haut", [0x28] = "Bas", [0x25] = "Gauche", [0x27] = "Droite",
        [0x13] = "Pause", [0x2C] = "ImprÉcran", [0x91] = "ArrêtDéfil", [0x14] = "VerrMaj",
    };

    static string DisplayName(ushort vk)
    {
        if (Display.TryGetValue(vk, out var name)) return name;
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        if (vk is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x60 and <= 0x69) return "Pavé" + (vk - 0x60);
        return "Touche" + vk;
    }

    static Dictionary<string, ushort> BuildNames()
    {
        var d = new Dictionary<string, ushort>();
        void Add(ushort vk, params string[] names) { foreach (var n in names) d[TextFinder.Normalize(n)] = vk; }
        Add(VK_CONTROL, "ctrl", "control", "ctl", "contrôle");
        Add(VK_MENU, "alt");
        Add(VK_SHIFT, "shift", "maj", "majuscule");
        Add(VK_LWIN, "win", "windows");
        Add(0x0D, "entrée", "enter", "return");
        Add(0x1B, "échap", "esc", "escape", "echap");
        Add(0x20, "espace", "space");
        Add(0x09, "tab", "tabulation");
        Add(0x08, "retour", "backspace", "retourarrière");
        Add(0x2D, "inser", "insert", "ins", "insertion");
        Add(0x2E, "suppr", "delete", "del", "supprimer");
        Add(0x24, "début", "home", "origine");
        Add(0x23, "fin", "end");
        Add(0x21, "pagehaut", "pageup", "pgup", "pageprécédente");
        Add(0x22, "pagebas", "pagedown", "pgdn", "pagesuivante");
        Add(0x26, "haut", "up", "flèchehaut");
        Add(0x28, "bas", "down", "flèchebas");
        Add(0x25, "gauche", "left", "flèchegauche");
        Add(0x27, "droite", "right", "flèchedroite");
        Add(0x13, "pause");
        Add(0x2C, "imprécran", "printscreen", "impr");
        Add(0x91, "arrêtdéfil", "scrolllock");
        Add(0x14, "verrmaj", "capslock");
        for (int i = 1; i <= 24; i++) Add((ushort)(0x6F + i), "f" + i);
        for (char c = 'A'; c <= 'Z'; c++) Add(c, c.ToString());
        for (char c = '0'; c <= '9'; c++) Add(c, c.ToString());
        for (int i = 0; i <= 9; i++) Add((ushort)(0x60 + i), "pavé" + i, "numpad" + i);
        return d;
    }
}

/// <summary>Case où l'on appuie sur une touche (ou une combinaison) pour la choisir.</summary>
internal sealed class KeyBox : TextBox
{
    KeyCombo _combo;
    Keys _pendingModifier = Keys.None;

    public event Action Changed;

    public KeyBox()
    {
        ReadOnly = true;
        BackColor = System.Drawing.SystemColors.Window;
        Cursor = Cursors.Hand;
        TextAlign = HorizontalAlignment.Center;
        ShortcutsEnabled = false;
    }

    public KeyCombo Combo
    {
        get => _combo;
        set { _combo = value; Text = value?.ToString() ?? ""; }
    }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Prend aussi Tab, Entrée, Échap, F10… au lieu de les laisser à la fenêtre
        const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        if (msg.Msg is WM_KEYDOWN or WM_SYSKEYDOWN) { Take(keyData); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        Take(e.KeyData);
        e.Handled = e.SuppressKeyPress = true;
    }

    void Take(Keys keyData)
    {
        var code = keyData & Keys.KeyCode;
        if (code is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin)
        {
            _pendingModifier = keyData;          // modificateur seul : décidé au relâchement
            Text = KeyCombo.FromKeys(keyData).ToString() + "+…";
            return;
        }
        _pendingModifier = Keys.None;
        Combo = KeyCombo.FromKeys(keyData);
        Changed?.Invoke();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_pendingModifier != Keys.None)
        {
            Combo = KeyCombo.FromKeys(_pendingModifier & Keys.KeyCode);
            _pendingModifier = Keys.None;
            Changed?.Invoke();
        }
        e.Handled = true;
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        BackColor = System.Drawing.Color.LightYellow;
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        BackColor = System.Drawing.SystemColors.Window;
        if (_pendingModifier != Keys.None) { _pendingModifier = Keys.None; Text = _combo?.ToString() ?? ""; }
    }
}
