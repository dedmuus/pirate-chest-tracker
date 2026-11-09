using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Threading;
using System.Windows.Input;
using System.Windows.Threading;

namespace ChestCounter;

public enum InputKind { None = 0, Key = 1, Mouse = 2, Controller = 3 }

public enum MouseBtn { Left = 0, Right = 1, Middle = 2, X1 = 3, X2 = 4 }

/// <summary>A single keyboard key or mouse button.</summary>
public readonly record struct InputBinding(InputKind Kind, int Code)
{
    public static readonly InputBinding None = new(InputKind.None, 0);

    [JsonIgnore] public bool IsNone => Kind == InputKind.None;

    [JsonIgnore] public bool IsEscape => Kind == InputKind.Key && Code == 0x1B;

    [JsonIgnore]
    public string DisplayName => Kind switch
    {
        InputKind.Key => KeyName(Code),
        InputKind.Mouse => (MouseBtn)Code switch
        {
            MouseBtn.Left => "Left Click",
            MouseBtn.Right => "Right Click",
            MouseBtn.Middle => "Middle Click",
            MouseBtn.X1 => "Mouse 4",
            MouseBtn.X2 => "Mouse 5",
            _ => "Mouse ?"
        },
        InputKind.Controller => ControllerName(Code),
        _ => "Not set"
    };

    static string ControllerName(int button) => button switch
    {
        0x0001 => "D-pad Up",
        0x0002 => "D-pad Down",
        0x0004 => "D-pad Left",
        0x0008 => "D-pad Right",
        0x0010 => "Menu (Start)",
        0x0020 => "View (Back)",
        0x0040 => "Left Stick Click",
        0x0080 => "Right Stick Click",
        0x0100 => "LB",
        0x0200 => "RB",
        0x1000 => "A",
        0x2000 => "B",
        0x4000 => "X",
        0x8000 => "Y",
        InputHook.LeftTrigger => "LT",
        InputHook.RightTrigger => "RT",
        _ => "Pad ?"
    };

    static string KeyName(int vk)
    {
        Key key = KeyInterop.KeyFromVirtualKey(vk);
        return key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "Num " + (int)(key - Key.NumPad0),
            Key.OemTilde => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.LeftShift => "Left Shift",
            Key.RightShift => "Right Shift",
            Key.LeftCtrl => "Left Ctrl",
            Key.RightCtrl => "Right Ctrl",
            Key.LeftAlt => "Left Alt",
            Key.RightAlt => "Right Alt",
            Key.CapsLock => "Caps Lock",
            Key.Enter => "Enter",
            Key.Back => "Backspace",
            Key.PageUp => "Page Up",
            Key.PageDown => "Page Down",
            Key.Multiply => "Num *",
            Key.Add => "Num +",
            Key.Subtract => "Num -",
            Key.Divide => "Num /",
            Key.Decimal => "Num .",
            Key.None => $"Key {vk}",
            _ => key.ToString()
        };
    }
}

/// <summary>
/// Global low-level keyboard + mouse hook. It only listens: every input is passed on
/// to the game untouched. Runs on its own thread so it never adds input lag.
/// </summary>
public sealed class InputHook : IDisposable
{
    public event Action<InputBinding>? Pressed;

    readonly Dispatcher ui;
    readonly HashSet<int> heldKeys = new();
    HookProc? keyboardProc;
    HookProc? mouseProc;
    Thread? thread;
    uint threadId;

    public InputHook(Dispatcher ui) => this.ui = ui;

    public void Start()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "InputHook" };
        thread.Start();
        new Thread(PollControllers) { IsBackground = true, Name = "ControllerPoll" }.Start();
    }

    // ---- controllers (XInput: Xbox controllers, and others through Steam Input / DS4Windows) ----

    public const int LeftTrigger = 0x10000;
    public const int RightTrigger = 0x20000;
    const byte TriggerThreshold = 30;
    volatile bool polling = true;

    void PollControllers()
    {
        var previous = new int[4];
        var connected = new bool[4];
        var nextProbe = new long[4];
        try
        {
            while (polling)
            {
                long now = Environment.TickCount64;
                for (int i = 0; i < 4; i++)
                {
                    // Asking an empty slot is slow, so only re-check unplugged slots every 2 seconds.
                    if (!connected[i] && now < nextProbe[i]) continue;
                    if (XInputGetState(i, out XINPUT_STATE state) != 0)
                    {
                        connected[i] = false;
                        previous[i] = 0;
                        nextProbe[i] = now + 2000;
                        continue;
                    }
                    connected[i] = true;

                    int current = state.Gamepad.wButtons
                        | (state.Gamepad.bLeftTrigger > TriggerThreshold ? LeftTrigger : 0)
                        | (state.Gamepad.bRightTrigger > TriggerThreshold ? RightTrigger : 0);
                    int pressed = current & ~previous[i];
                    previous[i] = current;
                    for (int bit = 1; bit <= RightTrigger; bit <<= 1)
                        if ((pressed & bit) != 0) Raise(new InputBinding(InputKind.Controller, bit));
                }
                Thread.Sleep(16);
            }
        }
        catch (DllNotFoundException)
        {
            // No XInput on this PC: controller support is simply off.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    static extern int XInputGetState(int userIndex, out XINPUT_STATE state);

    void Run()
    {
        threadId = GetCurrentThreadId();
        keyboardProc = KeyboardProc;
        mouseProc = MouseProc;
        IntPtr module = GetModuleHandle(null);
        IntPtr kb = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, module, 0);
        IntPtr ms = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0);

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0) { }

        if (kb != IntPtr.Zero) UnhookWindowsHookEx(kb);
        if (ms != IntPtr.Zero) UnhookWindowsHookEx(ms);
    }

    IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            int vk = Marshal.ReadInt32(lParam);
            int flags = Marshal.ReadInt32(lParam, 8);
            // With NumLock off, numpad keys arrive as Insert/End/arrows etc.
            // Those come without the "extended" flag, so map them back to the numpad key.
            if ((flags & LLKHF_EXTENDED) == 0) vk = NumpadKey(vk);
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                // Holding a key fires repeated key-downs; count it only once.
                if (heldKeys.Add(vk)) Raise(new InputBinding(InputKind.Key, vk));
            }
            else if (msg is WM_KEYUP or WM_SYSKEYUP)
            {
                heldKeys.Remove(vk);
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int? button = (int)wParam switch
            {
                WM_LBUTTONDOWN => (int)MouseBtn.Left,
                WM_RBUTTONDOWN => (int)MouseBtn.Right,
                WM_MBUTTONDOWN => (int)MouseBtn.Middle,
                WM_XBUTTONDOWN => ((Marshal.ReadInt32(lParam, 8) >> 16) & 0xFFFF) == 1
                    ? (int)MouseBtn.X1
                    : (int)MouseBtn.X2,
                _ => null
            };
            if (button is int b) Raise(new InputBinding(InputKind.Mouse, b));
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    static int NumpadKey(int vk) => vk switch
    {
        0x2D => 0x60, // Insert   -> Num 0
        0x23 => 0x61, // End      -> Num 1
        0x28 => 0x62, // Down     -> Num 2
        0x22 => 0x63, // PageDown -> Num 3
        0x25 => 0x64, // Left     -> Num 4
        0x0C => 0x65, // Clear    -> Num 5
        0x27 => 0x66, // Right    -> Num 6
        0x24 => 0x67, // Home     -> Num 7
        0x26 => 0x68, // Up       -> Num 8
        0x21 => 0x69, // PageUp   -> Num 9
        0x2E => 0x6E, // Delete   -> Num .
        _ => vk
    };

    void Raise(InputBinding binding) => ui.InvokeAsync(() => Pressed?.Invoke(binding));

    public void Dispose()
    {
        polling = false;
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    const int WH_KEYBOARD_LL = 13;
    const int WH_MOUSE_LL = 14;
    const int WM_KEYDOWN = 0x0100;
    const int WM_KEYUP = 0x0101;
    const int WM_SYSKEYDOWN = 0x0104;
    const int WM_SYSKEYUP = 0x0105;
    const int WM_LBUTTONDOWN = 0x0201;
    const int WM_RBUTTONDOWN = 0x0204;
    const int WM_MBUTTONDOWN = 0x0207;
    const int WM_XBUTTONDOWN = 0x020B;
    const uint WM_QUIT = 0x0012;
    const int LLKHF_EXTENDED = 0x01;

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();
}
