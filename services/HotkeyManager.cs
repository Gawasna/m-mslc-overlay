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
