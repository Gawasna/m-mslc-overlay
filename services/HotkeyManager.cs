using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace m_mslc_overlay.services;

public class HotkeyManager : IDisposable
{
    // Modifier keys flags
    public const uint MOD_NONE = 0x0000;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    private const uint WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Subclassing API
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc callback, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc callback, IntPtr uIdSubclass);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    private readonly Window _window;
    private IntPtr _hwnd = IntPtr.Zero;
    private readonly SubclassProc _subclassProc;
    private readonly Dictionary<int, Action> _callbacks = new();
    private bool _isSubclassed = false;

    public HotkeyManager(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _subclassProc = new SubclassProc(WndProc); // Keep delegate reference alive to prevent GC reclamation
    }

    public void Initialize()
    {
        if (_hwnd != IntPtr.Zero) return;

        var platformHandle = _window.TryGetPlatformHandle();
        if (platformHandle != null)
        {
            _hwnd = platformHandle.Handle;
        }

        if (_hwnd != IntPtr.Zero)
        {
            _isSubclassed = SetWindowSubclass(_hwnd, _subclassProc, new IntPtr(1002), IntPtr.Zero);
        }
    }

    public bool Register(int id, uint modifiers, uint vk, Action callback)
    {
        if (_hwnd == IntPtr.Zero)
        {
            Initialize();
        }

        if (_hwnd == IntPtr.Zero) return false;

        // Try to unregister first to avoid duplicated key registration issues
        UnregisterHotKey(_hwnd, id);

        bool success = RegisterHotKey(_hwnd, id, modifiers | MOD_NOREPEAT, vk);
        if (success)
        {
            _callbacks[id] = callback;
        }
        return success;
    }

    public void Unregister(int id)
    {
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, id);
        }
        _callbacks.Remove(id);
    }

    public void UnregisterAll()
    {
        if (_hwnd != IntPtr.Zero)
        {
            foreach (var id in _callbacks.Keys)
            {
                UnregisterHotKey(_hwnd, id);
            }
        }
        _callbacks.Clear();
        _actionToIdMap.Clear();
    }

    private readonly Dictionary<string, int> _actionToIdMap = new();
    private int _nextId = 1000;

    public static bool TryParseWin32(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(gesture)) return false;
        try
        {
            var keyGesture = Avalonia.Input.KeyGesture.Parse(gesture);
            if (keyGesture.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt)) modifiers |= MOD_ALT;
            if (keyGesture.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) modifiers |= MOD_CONTROL;
            if (keyGesture.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)) modifiers |= MOD_SHIFT;
            if (keyGesture.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta)) modifiers |= MOD_WIN;
            
            vk = VirtualKeyFromKey(keyGesture.Key);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static uint VirtualKeyFromKey(Avalonia.Input.Key key)
    {
        if (key >= Avalonia.Input.Key.A && key <= Avalonia.Input.Key.Z)
            return (uint)(key - Avalonia.Input.Key.A + 0x41);
        if (key >= Avalonia.Input.Key.D0 && key <= Avalonia.Input.Key.D9)
            return (uint)(key - Avalonia.Input.Key.D0 + 0x30);
        if (key >= Avalonia.Input.Key.NumPad0 && key <= Avalonia.Input.Key.NumPad9)
            return (uint)(key - Avalonia.Input.Key.NumPad0 + 0x60);
        if (key >= Avalonia.Input.Key.F1 && key <= Avalonia.Input.Key.F24)
            return (uint)(key - Avalonia.Input.Key.F1 + 0x70);
        
        return key switch
        {
            Avalonia.Input.Key.Up => 0x26,
            Avalonia.Input.Key.Down => 0x28,
            Avalonia.Input.Key.Left => 0x25,
            Avalonia.Input.Key.Right => 0x27,
            Avalonia.Input.Key.Escape => 0x1B,
            Avalonia.Input.Key.Space => 0x20,
            Avalonia.Input.Key.Enter => 0x0D,
            Avalonia.Input.Key.Back => 0x08,
            Avalonia.Input.Key.Tab => 0x09,
            _ => (uint)key // Fallback
        };
    }

    public bool TryRegister(string actionId, string gesture, Action callback, out string error)
    {
        error = string.Empty;
        if (!TryParseWin32(gesture, out uint modifiers, out uint vk))
        {
            error = "Invalid key gesture format.";
            return false;
        }

        if (_hwnd == IntPtr.Zero) Initialize();
        if (_hwnd == IntPtr.Zero)
        {
            error = "Window handle is not ready.";
            return false;
        }

        if (_actionToIdMap.TryGetValue(actionId, out int oldId))
        {
            Unregister(oldId);
            _actionToIdMap.Remove(actionId);
        }

        int newId = _nextId++;
        bool success = RegisterHotKey(_hwnd, newId, modifiers | MOD_NOREPEAT, vk);
        if (success)
        {
            _callbacks[newId] = callback;
            _actionToIdMap[actionId] = newId;
            return true;
        }
        
        error = "The shortcut may be in use by another application or Windows.";
        return false;
    }

    private IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_callbacks.TryGetValue(id, out var callback))
            {
                callback();
                return IntPtr.Zero; // Message handled
            }
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void Dispose()
    {
        UnregisterAll();
        if (_hwnd != IntPtr.Zero && _isSubclassed)
        {
            RemoveWindowSubclass(_hwnd, _subclassProc, new IntPtr(1002));
            _isSubclassed = false;
        }
        _hwnd = IntPtr.Zero;
    }
}
