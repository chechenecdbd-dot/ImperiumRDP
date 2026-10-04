using System.Runtime.InteropServices;

namespace ImperiumRDP.Agent.Input;

/// <summary>Внедрение ввода через SendInput (мышь по всему виртуальному рабочему столу, клавиши скан-кодами).</summary>
public sealed class InputInjector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUTUNION u; }

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_MIDDLEDOWN = 0x0020,
        MOUSEEVENTF_MIDDLEUP = 0x0040, MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_ABSOLUTE = 0x8000,
        MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002,
        KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_SCANCODE = 0x0008;
    private const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    // Клавиши, требующие флага EXTENDEDKEY (стрелки, Home/End и т.п.)
    private static readonly HashSet<int> ExtendedKeys = new()
    {
        0x21, 0x22, 0x23, 0x24,          // PageUp/PageDown/End/Home
        0x25, 0x26, 0x27, 0x28,          // стрелки
        0x2D, 0x2E,                      // Insert/Delete
        0x2C,                            // PrintScreen
        0x5B, 0x5C, 0x5D,                // Win left/right, Apps
        0x6F,                            // Numpad /
        0x90,                            // NumLock
        0x92,                            // ScrollLock
    };

    private readonly Rectangle _screen;

    public InputInjector(Rectangle screen) { _screen = screen; }

    private void Send(INPUT[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            throw new IOException($"SendInput failed: {Marshal.GetLastWin32Error()}");
    }

    public void MouseMove(int x, int y)
    {
        int nx = _screen.Width > 1 ? (int)((x - _screen.X) * 65535.0 / (_screen.Width - 1)) : 0;
        int ny = _screen.Height > 1 ? (int)((y - _screen.Y) * 65535.0 / (_screen.Height - 1)) : 0;
        Send(new[] { new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT
            {
                dx = nx, dy = ny,
                dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK
            } }
        } });
    }

    public void MouseButton(int button, bool down)
    {
        uint flag = (button, down) switch
        {
            (0, true) => MOUSEEVENTF_LEFTDOWN,
            (0, false) => MOUSEEVENTF_LEFTUP,
            (1, true) => MOUSEEVENTF_RIGHTDOWN,
            (1, false) => MOUSEEVENTF_RIGHTUP,
            (2, true) => MOUSEEVENTF_MIDDLEDOWN,
            (2, false) => MOUSEEVENTF_MIDDLEUP,
            _ => 0,
        };
        if (flag == 0) return;
        Send(new[] { new INPUT { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = flag } } } });
    }

    public void MouseWheel(int delta)
    {
        Send(new[] { new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT { mouseData = unchecked((uint)delta), dwFlags = MOUSEEVENTF_WHEEL } }
        } });
    }

    public void Key(int vk, bool down)
    {
        ushort scan = (ushort)MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
        if (scan == 0) scan = (ushort)vk;
        uint flags = KEYEVENTF_SCANCODE;
        if (ExtendedKeys.Contains(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        if (!down) flags |= KEYEVENTF_KEYUP;
        Send(new[] { new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT { wScan = scan, dwFlags = flags } }
        } });
    }

    public void Text(string chars)
    {
        var list = new List<INPUT>(chars.Length * 2);
        foreach (var ch in chars)
        {
            list.Add(new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } });
            list.Add(new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
        }
        if (list.Count > 0) Send(list.ToArray());
    }
}
