using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using ClipFlux.Native;

namespace ClipFlux.Services;

public class HotkeyService
{
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private readonly Dictionary<int, Action> _callbacks = new();
    private int _currentId = 9000;

    public void Initialize(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);
    }

    public int Register(uint modifiers, uint vk, Action callback)
    {
        int id = ++_currentId;
        bool ok = Win32.RegisterHotKey(_hwnd, id, modifiers | Win32.MOD_NOREPEAT, vk);
        if (ok)
        {
            _callbacks[id] = callback;
            return id;
        }
        return -1;
    }

    public void UnregisterAll()
    {
        foreach (var id in _callbacks.Keys)
        {
            Win32.UnregisterHotKey(_hwnd, id);
        }
        _callbacks.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_callbacks.TryGetValue(id, out var callback))
            {
                callback?.Invoke();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }
}
