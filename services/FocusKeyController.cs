using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;

namespace m_mslc_overlay.services;

public class FocusKeyController : IDisposable
{
    private readonly Window _window;
    private readonly Dictionary<(Key key, KeyModifiers modifiers), Action> _shortcutActions = new();
    private readonly Dictionary<Key, Action> _fallbackCharActions = new();

    public FocusKeyController(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _window.KeyDown += OnWindowKeyDown;
    }

    /// <summary>
    /// Register a key shortcut with specific modifiers (Ctrl, Alt, Shift).
    /// </summary>
    public void Register(Key key, KeyModifiers modifiers, Action action)
    {
        // Normalize modifiers
        var normModifiers = modifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);
        _shortcutActions[(key, normModifiers)] = action;
    }

    /// <summary>
    /// Register a fallback single key action (like A-Z, Fx) without modifiers.
    /// These will be bypassed if a TextBox has active focus.
    /// </summary>
    public void RegisterFallbackKey(Key key, Action action)
    {
        _fallbackCharActions[key] = action;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var normModifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);

        // 1. Try to match exact shortcut with modifiers (e.g. Ctrl + O, F1 etc)
        if (_shortcutActions.TryGetValue((e.Key, normModifiers), out var shortcutAction))
        {
            // Even if a TextBox is focused, we allow Ctrl + Key shortcuts or Fx keys, 
            // unless it's a standard text box action (like Ctrl+A, Ctrl+C, Ctrl+V, Ctrl+X, Ctrl+Z, Ctrl+Y).
            bool isTextBoxAction = false;
            var focused = TopLevel.GetTopLevel(_window)?.FocusManager?.GetFocusedElement();
            if (focused is TextBox)
            {
                if (normModifiers == KeyModifiers.Control && 
                    (e.Key == Key.A || e.Key == Key.C || e.Key == Key.V || e.Key == Key.X || e.Key == Key.Z || e.Key == Key.Y))
                {
                    isTextBoxAction = true;
                }
            }

            if (!isTextBoxAction)
            {
                shortcutAction();
                e.Handled = true;
                return;
            }
        }

        // 2. Try to match fallback single key action when no modifiers are pressed (e.g. A-Z, Fx)
        if (normModifiers == KeyModifiers.None && _fallbackCharActions.TryGetValue(e.Key, out var fallbackAction))
        {
            // Bypass single character key triggers if user is currently typing in any input TextBox
            var focused = TopLevel.GetTopLevel(_window)?.FocusManager?.GetFocusedElement();
            if (focused is TextBox)
            {
                // Only allow function keys (F1-F12) to bypass TextBox typing blocks
                if (e.Key >= Key.F1 && e.Key <= Key.F12)
                {
                    fallbackAction();
                    e.Handled = true;
                }
                return;
            }

            fallbackAction();
            e.Handled = true;
        }
    }

    public void Dispose()
    {
        _window.KeyDown -= OnWindowKeyDown;
        _shortcutActions.Clear();
        _fallbackCharActions.Clear();
    }
}
