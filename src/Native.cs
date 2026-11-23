using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AutoClic;

internal static class Native
{
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);

    public const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    /// <summary>Texte gris d'exemple dans une case vide (disparaît quand on tape).</summary>
    public static void SetCueBanner(System.Windows.Forms.TextBox box, string text)
    {
        const int EM_SETCUEBANNER = 0x1501;
        if (box.IsHandleCreated) SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text);
        else box.HandleCreated += (_, _) => SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text);
    }
}

/// <summary>Clics souris et touches clavier envoyés à Windows (donc à la fenêtre au premier plan, quelle qu'elle soit).</summary>
internal static class Input
{
    const uint MOUSE_MOVE = 0x1, MOUSE_LEFTDOWN = 0x2, MOUSE_LEFTUP = 0x4;
    const uint KEY_EXTENDED = 0x1, KEY_UP = 0x2;
    static readonly int Size = Marshal.SizeOf(typeof(Native.INPUT));

    public readonly struct Key(ushort vk, bool extended = false)
    {
        public ushort Vk { get; } = vk;
        public bool Extended { get; } = extended;
    }

    public static readonly Key Alt = new(0x12), Shift = new(0x10), Z = new(0x5A), Insert = new(0x2D, true);

    static bool Send(Native.INPUT input) => Native.SendInput(1, [input], Size) == 1;

    static Native.INPUT Mouse(uint flags, int dx = 0, int dy = 0) => new()
    {
        type = 0,
        u = new Native.InputUnion { mi = new Native.MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags } }
    };

    static Native.INPUT Keyboard(Key k, bool up) => new()
    {
        type = 1,
        u = new Native.InputUnion
        {
            ki = new Native.KEYBDINPUT
            {
                wVk = k.Vk,
                wScan = (ushort)Native.MapVirtualKey(k.Vk, 0),
                dwFlags = (k.Extended ? KEY_EXTENDED : 0) | (up ? KEY_UP : 0)
            }
        }
    };

    const uint MOUSE_RIGHTDOWN = 0x8, MOUSE_RIGHTUP = 0x10;

    /// <summary>Place la souris puis fait un clic gauche. Renvoie false si Windows a refusé.</summary>
    public static bool Click(int x, int y) => Press(x, y, MOUSE_LEFTDOWN, MOUSE_LEFTUP);

    public static bool RightClick(int x, int y) => Press(x, y, MOUSE_RIGHTDOWN, MOUSE_RIGHTUP);

    public static bool DoubleClick(int x, int y)
    {
        bool ok = Press(x, y, MOUSE_LEFTDOWN, MOUSE_LEFTUP);
        Thread.Sleep(40);
        ok &= Send(Mouse(MOUSE_LEFTDOWN));
        Thread.Sleep(30);
        ok &= Send(Mouse(MOUSE_LEFTUP));
        return ok;
    }

    static bool Press(int x, int y, uint down, uint up)
    {
        bool ok = Native.SetCursorPos(x, y);
        Thread.Sleep(30);                       // laisse le jeu voir la souris arriver avant le clic
        ok &= Send(Mouse(down));
        Thread.Sleep(30);
        ok &= Send(Mouse(up));                  // toujours relâché, même si l'appui a échoué
        return ok;
    }

    /// <summary>Touches enfoncées dans l'ordre, relâchées dans l'ordre inverse, 50 ms entre chaque.</summary>
    public static bool Combo(params Key[] keys)
    {
        bool ok = true;
        int pressed = 0;
        try
        {
            foreach (var k in keys)
            {
                ok &= Send(Keyboard(k, false));
                pressed++;
                Thread.Sleep(50);
            }
        }
        finally
        {
            // Quoi qu'il arrive, aucune touche ne reste enfoncée
            for (int i = pressed - 1; i >= 0; i--)
            {
                ok &= Send(Keyboard(keys[i], true));
                if (i > 0) Thread.Sleep(50);
            }
        }
        return ok;
    }

    /// <summary>Souris décalée de quelques pixels puis remise en place.</summary>
    public static bool Jiggle(int distance)
    {
        bool ok = Send(Mouse(MOUSE_MOVE, distance));
        Thread.Sleep(100);
        ok &= Send(Mouse(MOUSE_MOVE, -distance));
        return ok;
    }
}
