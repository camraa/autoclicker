# Auto Clic
# Insert = afficher / cacher le menu (glisser les boutons)  ·  F10 = lancer / arrêter

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
public static class Native {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
// Fenêtre qui ne vole pas le focus et n'apparaît pas dans Alt+Tab
public class MarkerForm : Form {
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams {
        get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80 | 0x8; return cp; }
    }
}
"@

[Native]::SetProcessDPIAware() | Out-Null
[System.Windows.Forms.Application]::EnableVisualStyles()

# Une seule copie du programme à la fois
$created = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\AutoClic', [ref]$created)
if (-not $created) {
    [System.Windows.Forms.MessageBox]::Show("Auto Clic est déjà lancé.`nAppuie sur Insert pour ouvrir le menu.", 'Auto Clic') | Out-Null
    return
}

# ---------- Réglages ----------
$SaveFile     = Join-Path $PSScriptRoot 'boutons.json'   # boutons gardés pour la prochaine fois
$R            = 20                                       # rayon des ronds
$DefaultDelay = 1.0                                      # temps par défaut après un bouton (s)

# Macro (reprise de « anti afk.ahk »), sur n'importe quelle fenêtre
$MacroKeysEvery  = 10   # secondes : Alt, puis Shift+Z, puis Insert
$MacroMouseEvery = 3    # secondes : souris 10 px à droite puis retour
$MacroMouseDist  = 10
$MacroCombos = New-Object System.Collections.ArrayList
[void]$MacroCombos.Add(@(@{ Vk = 0x12; Ext = $false }))                               # Alt
[void]$MacroCombos.Add(@(@{ Vk = 0x10; Ext = $false }, @{ Vk = 0x5A; Ext = $false }))  # Shift + Z
[void]$MacroCombos.Add(@(@{ Vk = 0x2D; Ext = $true }))                                # Insert

$points  = New-Object System.Collections.ArrayList     # { X, Y, Delay }
$markers = New-Object System.Collections.ArrayList
$state   = @{
    Running       = $false
    Quitting      = $false
    F10Prev       = $false
    InsPrev       = $false
    Index         = 0
    Clicks        = 0
    NextClick     = [DateTime]::Now
    KeysNext      = [DateTime]::Now
    MouseNext     = [DateTime]::Now
    Drag          = $null
    PendingDelete = $null
}

# ---------- Menu ----------
$form = New-Object System.Windows.Forms.Form
$form.Text            = 'Auto Clic'
$form.ClientSize      = New-Object System.Drawing.Size(280, 420)
$form.FormBorderStyle = 'FixedSingle'
$form.MaximizeBox     = $false
$form.TopMost         = $true
$form.StartPosition   = 'CenterScreen'
$form.Font            = New-Object System.Drawing.Font('Segoe UI', 9)

$btnAdd = New-Object System.Windows.Forms.Button
$btnAdd.Text = '+  Ajouter un bouton'
$btnAdd.Font = New-Object System.Drawing.Font('Segoe UI', 10, [System.Drawing.FontStyle]::Bold)
$btnAdd.SetBounds(10, 10, 260, 38)
$form.Controls.Add($btnAdd)

$lblRows = New-Object System.Windows.Forms.Label
$lblRows.Text = 'Temps à attendre après chaque bouton :'
$lblRows.SetBounds(10, 56, 260, 18)
$form.Controls.Add($lblRows)

$rows = New-Object System.Windows.Forms.Panel
$rows.SetBounds(10, 76, 260, 200)
$rows.AutoScroll  = $true
$rows.BorderStyle = 'FixedSingle'
$form.Controls.Add($rows)

$chkMacro = New-Object System.Windows.Forms.CheckBox
$chkMacro.Text = 'Macro anti-AFK'
$chkMacro.Checked = $true
$chkMacro.SetBounds(10, 284, 260, 22)
$form.Controls.Add($chkMacro)

$lblMacro = New-Object System.Windows.Forms.Label
$lblMacro.Text = 'Alt, Shift+Z, Insert / 10 s  ·  souris / 3 s'
$lblMacro.ForeColor = [System.Drawing.Color]::DimGray
$lblMacro.SetBounds(28, 306, 245, 18)
$form.Controls.Add($lblMacro)

$btnClear = New-Object System.Windows.Forms.Button
$btnClear.Text = 'Tout effacer'
$btnClear.SetBounds(10, 332, 127, 28)
$form.Controls.Add($btnClear)

$btnQuit = New-Object System.Windows.Forms.Button
$btnQuit.Text = 'Quitter le programme'
$btnQuit.SetBounds(143, 332, 127, 28)
$form.Controls.Add($btnQuit)

$lblHelp = New-Object System.Windows.Forms.Label
$lblHelp.Text = "Glisse les ronds là où il faut cliquer (clic droit = supprimer).`nInsert = afficher / cacher ce menu  ·  F10 = lancer / arrêter"
$lblHelp.ForeColor = [System.Drawing.Color]::DimGray
$lblHelp.SetBounds(10, 368, 265, 48)
$form.Controls.Add($lblHelp)

# ---------- Sauvegarde ----------
function Save-Config {
    try {
        [pscustomobject]@{
            Macro   = $chkMacro.Checked
            Boutons = [object[]]$points.ToArray()
        } | ConvertTo-Json -Depth 4 | Set-Content -Path $SaveFile -Encoding UTF8
    } catch { }
}

function Load-Config {
    if (-not (Test-Path $SaveFile)) { return }
    try {
        $d = Get-Content -Path $SaveFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $fallback = if ($d.Pause) { [double]$d.Pause } else { $DefaultDelay }   # ancien format
        foreach ($b in @($d.Boutons)) {
            if ($null -eq $b) { continue }
            $delay = if ($b.Delay) { [double]$b.Delay } else { $fallback }
            [void]$points.Add([pscustomobject]@{ X = [int]$b.X; Y = [int]$b.Y; Delay = [Math]::Max([double]0.1, [Math]::Min([double]3600, [double]$delay)) })
        }
        if ($null -ne $d.Macro)   { $chkMacro.Checked = [bool]$d.Macro }
        if ($null -ne $d.AntiAfk) { $chkMacro.Checked = [bool]$d.AntiAfk }
    } catch { }
}

# ---------- Lignes « Bouton N : attendre X s » ----------
function Refresh-Rows {
    $rows.SuspendLayout()
    foreach ($c in @($rows.Controls)) { $c.Dispose() }
    $rows.Controls.Clear()
    if ($points.Count -eq 0) {
        $l = New-Object System.Windows.Forms.Label
        $l.Text = "Aucun bouton.`nClique sur « Ajouter un bouton »."
        $l.ForeColor = [System.Drawing.Color]::DimGray
        $l.SetBounds(8, 8, 230, 40)
        $rows.Controls.Add($l)
    }
    for ($i = 0; $i -lt $points.Count; $i++) {
        $y = 6 + $i * 30
        $l = New-Object System.Windows.Forms.Label
        $l.Text = "Bouton $($i + 1)  →  attendre"
        $l.SetBounds(6, $y + 3, 125, 20)
        $rows.Controls.Add($l)

        $n = New-Object System.Windows.Forms.NumericUpDown
        $n.DecimalPlaces = 1; $n.Increment = 0.1; $n.Minimum = 0.1; $n.Maximum = 3600
        $n.Value = [decimal]$points[$i].Delay
        $n.Tag = $i
        $n.SetBounds(132, $y, 62, 24)
        $n.add_ValueChanged({ param($s, $e) $points[[int]$s.Tag].Delay = [double]$s.Value; Save-Config })
        $rows.Controls.Add($n)

        $u = New-Object System.Windows.Forms.Label
        $u.Text = 's'
        $u.SetBounds(197, $y + 3, 12, 20)
        $rows.Controls.Add($u)

        $x = New-Object System.Windows.Forms.Button
        $x.Text = '✕'
        $x.Tag = $i
        $x.SetBounds(212, $y, 26, 24)
        $x.add_Click({ param($s, $e) $state.PendingDelete = [int]$s.Tag })   # traité dans la boucle
        $rows.Controls.Add($x)
    }
    $rows.ResumeLayout()
}

# ---------- Ronds à glisser ----------
function Clear-Markers {
    foreach ($m in $markers) { $m.Close(); $m.Dispose() }
    $markers.Clear()
}

function New-Marker($i) {
    $p = $points[$i]
    $m = New-Object MarkerForm
    $m.FormBorderStyle = 'None'
    $m.ShowInTaskbar   = $false
    $m.TopMost         = $true
    $m.StartPosition   = 'Manual'
    $m.BackColor       = [System.Drawing.Color]::Crimson
    $m.Bounds          = New-Object System.Drawing.Rectangle(($p.X - $R), ($p.Y - $R), (2 * $R), (2 * $R))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddEllipse(0, 0, 2 * $R, 2 * $R)
    $m.Region = New-Object System.Drawing.Region($path)

    $lbl = New-Object System.Windows.Forms.Label
    $lbl.Dock      = 'Fill'
    $lbl.TextAlign = 'MiddleCenter'
    $lbl.ForeColor = [System.Drawing.Color]::White
    $lbl.Font      = New-Object System.Drawing.Font('Segoe UI', 12, [System.Drawing.FontStyle]::Bold)
    $lbl.Text      = ($i + 1).ToString()
    $lbl.Tag       = $i
    $lbl.Cursor    = [System.Windows.Forms.Cursors]::SizeAll
    $m.Controls.Add($lbl)

    $lbl.add_MouseDown({ param($s, $e)
        if ($e.Button -eq 'Left') {
            $state.Drag = @{ Index = [int]$s.Tag; Start = [System.Windows.Forms.Cursor]::Position; Orig = $s.Parent.Location }
        }
    })
    $lbl.add_MouseMove({ param($s, $e)
        if ($state.Drag) {
            $c = [System.Windows.Forms.Cursor]::Position
            $s.Parent.Location = New-Object System.Drawing.Point(
                ($state.Drag.Orig.X + $c.X - $state.Drag.Start.X),
                ($state.Drag.Orig.Y + $c.Y - $state.Drag.Start.Y))
        }
    })
    $lbl.add_MouseUp({ param($s, $e)
        if ($e.Button -eq 'Right') { $state.PendingDelete = [int]$s.Tag; return }
        if ($state.Drag) {
            $loc = $s.Parent.Location
            $points[$state.Drag.Index].X = $loc.X + $R
            $points[$state.Drag.Index].Y = $loc.Y + $R
            $state.Drag = $null
            Save-Config
        }
    })

    $m.Show()
    return $m
}

function Show-Markers {
    Clear-Markers
    if ($state.Running -or -not $form.Visible) { return }
    for ($i = 0; $i -lt $points.Count; $i++) { [void]$markers.Add((New-Marker $i)) }
}

function Show-Menu {
    if ($state.Running) { return }
    $form.Show()
    $form.Activate()
    Refresh-Rows
    Show-Markers
}

function Hide-Menu {
    $form.Hide()
    Clear-Markers
}

$btnAdd.add_Click({
    # Nouveau rond au milieu de l'écran (décalé pour ne pas s'empiler)
    $sb = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $n  = $points.Count
    $delay = if ($n -gt 0) { $points[$n - 1].Delay } else { $DefaultDelay }
    [void]$points.Add([pscustomobject]@{
        X     = $sb.X + [int]($sb.Width / 2) + ($n % 5) * 60 - 120
        Y     = $sb.Y + [int]($sb.Height / 2) + ([Math]::Floor($n / 5) % 4) * 60 - 90
        Delay = $delay
    })
    Save-Config
    Refresh-Rows
    Show-Markers
})

$btnClear.add_Click({
    if ($points.Count -eq 0) { return }
    $r = [System.Windows.Forms.MessageBox]::Show($form, 'Supprimer tous les boutons ?', 'Auto Clic', 'YesNo', 'Question')
    if ($r -eq 'Yes') { $points.Clear(); Save-Config; Refresh-Rows; Show-Markers }
})
$chkMacro.add_CheckedChanged({ Save-Config })

# La croix de la fenêtre cache juste le menu ; « Quitter » arrête vraiment le programme
$form.add_FormClosing({ param($s, $e) if (-not $state.Quitting) { $e.Cancel = $true; Hide-Menu } })

# ---------- Actions ----------
function Invoke-LeftClick($x, $y) {
    [Native]::SetCursorPos($x, $y) | Out-Null
    [Native]::mouse_event(0x02, 0, 0, 0, [UIntPtr]::Zero)
    [Native]::mouse_event(0x04, 0, 0, 0, [UIntPtr]::Zero)
}

function Send-Key($k, $up) {
    $flags = 0
    if ($k.Ext) { $flags = $flags -bor 0x1 }
    if ($up)    { $flags = $flags -bor 0x2 }
    [Native]::keybd_event([byte]$k.Vk, [byte][Native]::MapVirtualKey([uint32]$k.Vk, 0), [uint32]$flags, [UIntPtr]::Zero)
}

# Comme le .ahk : touches enfoncées puis relâchées dans l'ordre inverse, 50 ms entre chaque
function Send-Combo($combo) {
    foreach ($k in $combo) { Send-Key $k $false; [System.Threading.Thread]::Sleep(50) }
    for ($i = $combo.Count - 1; $i -ge 0; $i--) {
        Send-Key $combo[$i] $true
        if ($i -gt 0) { [System.Threading.Thread]::Sleep(50) }
    }
}

function Invoke-Macro($now) {
    if ($now -ge $state.KeysNext) {
        $first = $true
        foreach ($combo in $MacroCombos) {
            if (-not $first) { [System.Threading.Thread]::Sleep(100) }
            Send-Combo $combo
            $first = $false
        }
        $state.KeysNext = [DateTime]::Now.AddSeconds($MacroKeysEvery)
    }
    if ($now -ge $state.MouseNext) {
        [Native]::mouse_event(0x0001, $MacroMouseDist, 0, 0, [UIntPtr]::Zero)
        [System.Threading.Thread]::Sleep(100)
        [Native]::mouse_event(0x0001, -$MacroMouseDist, 0, 0, [UIntPtr]::Zero)
        $state.MouseNext = [DateTime]::Now.AddSeconds($MacroMouseEvery)
    }
}

# Petite notification en haut à gauche
function Show-Toast($text, $ms = 1500) {
    $t = New-Object MarkerForm
    $t.FormBorderStyle = 'None'
    $t.ShowInTaskbar   = $false
    $t.TopMost         = $true
    $t.StartPosition   = 'Manual'
    $t.Opacity         = 0.9
    $t.BackColor       = [System.Drawing.Color]::FromArgb(30, 30, 30)
    $sb = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $t.Bounds = New-Object System.Drawing.Rectangle(($sb.X + 20), ($sb.Y + 20), 380, 40)
    $l = New-Object System.Windows.Forms.Label
    $l.Dock = 'Fill'; $l.TextAlign = 'MiddleCenter'; $l.Text = $text
    $l.ForeColor = [System.Drawing.Color]::White
    $l.Font = New-Object System.Drawing.Font('Segoe UI', 11, [System.Drawing.FontStyle]::Bold)
    $t.Controls.Add($l)
    $t.Show()
    $tm = New-Object System.Windows.Forms.Timer
    $tm.Interval = $ms
    $tm.Tag = $t
    $tm.add_Tick({ param($s, $e) $s.Stop(); $s.Tag.Close(); $s.Tag.Dispose(); $s.Dispose() })
    $tm.Start()
}

# ---------- Icône près de l'horloge (pour quitter même menu caché) ----------
$tray = New-Object System.Windows.Forms.NotifyIcon
$tray.Icon = [System.Drawing.SystemIcons]::Application
$tray.Text = 'Auto Clic - Insert = menu, F10 = lancer'
$trayMenu = New-Object System.Windows.Forms.ContextMenuStrip
[void]$trayMenu.Items.Add('Afficher le menu (Insert)', $null, { Show-Menu })
[void]$trayMenu.Items.Add('Quitter', $null, { Quit-App })
$tray.ContextMenuStrip = $trayMenu
$tray.add_DoubleClick({ Show-Menu })
$tray.Visible = $true

function Quit-App {
    $state.Quitting = $true
    $state.Running  = $false
    $timer.Stop()
    Save-Config
    Clear-Markers
    $tray.Visible = $false
    $tray.Dispose()
    $form.Close()
    [System.Windows.Forms.Application]::Exit()
}
$btnQuit.add_Click({ Quit-App })

# ---------- Lancer / arrêter (F10) ----------
function Start-All {
    if ($points.Count -eq 0 -and -not $chkMacro.Checked) { Show-Toast "Ajoute d'abord un bouton (Insert)"; return }
    $now = [DateTime]::Now
    Hide-Menu                          # on cache le menu et les ronds pour ne pas cliquer dessus
    $state.Running   = $true
    $state.Index     = 0
    $state.Clicks    = 0
    $state.NextClick = $now
    $state.KeysNext  = $now.AddSeconds($MacroKeysEvery)
    $state.MouseNext = $now.AddSeconds($MacroMouseEvery)
    $tray.Text = 'Auto Clic - EN COURS (F10 = arrêter)'
    Show-Toast 'Auto Clic : ACTIVÉ  (F10 = arrêter)'
}

function Stop-All {
    $state.Running = $false
    $tray.Text = 'Auto Clic - Insert = menu, F10 = lancer'
    Show-Toast "Auto Clic : ARRÊTÉ  ($($state.Clicks) clics)"
}

# ---------- Boucle (toutes les 20 ms) ----------
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 20
$timer.add_Tick({
    $f10 = ([Native]::GetAsyncKeyState(0x79) -band 0x8000) -ne 0
    $ins = ([Native]::GetAsyncKeyState(0x2D) -band 0x8000) -ne 0
    $f10Pressed = $f10 -and -not $state.F10Prev; $state.F10Prev = $f10
    $insPressed = $ins -and -not $state.InsPrev; $state.InsPrev = $ins

    if ($f10Pressed) { if ($state.Running) { Stop-All } else { Start-All }; return }

    # Insert ouvre / ferme le menu (ignoré pendant que ça tourne : la macro appuie elle-même sur Insert)
    if ($insPressed -and -not $state.Running) {
        if ($form.Visible) { Hide-Menu } else { Show-Menu }
        return
    }

    if ($null -ne $state.PendingDelete) {
        $i = $state.PendingDelete
        $state.PendingDelete = $null
        if (-not $state.Running -and $i -lt $points.Count) { $points.RemoveAt($i); Save-Config; Refresh-Rows; Show-Markers }
        return
    }

    if (-not $state.Running) { return }
    $now = [DateTime]::Now
    if ($points.Count -gt 0 -and $now -ge $state.NextClick) {
        $p = $points[$state.Index]
        Invoke-LeftClick $p.X $p.Y
        $state.Clicks++
        $state.NextClick = [DateTime]::Now.AddSeconds([double]$p.Delay)   # temps propre à ce bouton
        $state.Index = ($state.Index + 1) % $points.Count
    }
    if ($chkMacro.Checked) { Invoke-Macro $now }
})

# ---------- Démarrage : rien à l'écran, juste une notification ----------
Load-Config
Refresh-Rows
$timer.Start()
Show-Toast 'Auto Clic prêt  ·  Insert = menu  ·  F10 = lancer' 3000
[System.Windows.Forms.Application]::Run()
$mutex.ReleaseMutex()
