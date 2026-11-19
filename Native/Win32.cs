using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipFlux.Native;

public static class Win32
{
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const int WM_HOTKEY = 0x0312;
    public const int WM_SETTINGCHANGE = 0x001A;

    // Hotkey Modifiers
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    // Clipboard Formats
    public const uint CF_TEXT = 1;
    public const uint CF_BITMAP = 2;
    public const uint CF_DIB = 8;
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_HDROP = 15;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    public const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>
    /// Clears the clipboard and atomically writes multiple clipboard formats in one session.
    /// </summary>
    public static bool SetClipboardFormats(IReadOnlyList<(uint FormatId, byte[] Data)> formats)
    {
        if (formats == null || formats.Count == 0) return false;

        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    if (!EmptyClipboard()) return false;

                    bool anyWritten = false;
                    foreach (var (formatId, data) in formats)
                    {
                        if (formatId == 0 || data == null || data.Length == 0) continue;

                        IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
                        if (hGlobal == IntPtr.Zero) continue;

                        IntPtr target = GlobalLock(hGlobal);
                        if (target == IntPtr.Zero)
                        {
                            GlobalFree(hGlobal);
                            continue;
                        }

                        try
                        {
                            Marshal.Copy(data, 0, target, data.Length);
                        }
                        finally
                        {
                            GlobalUnlock(hGlobal);
                        }

                        if (SetClipboardData(formatId, hGlobal) == IntPtr.Zero)
                        {
                            GlobalFree(hGlobal);
                        }
                        else
                        {
                            anyWritten = true;
                        }
                    }

                    return anyWritten;
                }
                finally
                {
                    CloseClipboard();
                }
            }
            System.Threading.Thread.Sleep(20);
        }
        return false;
    }

    public static bool SetClipboardPlainText(string text)
    {
        byte[] bytes = Encoding.Unicode.GetBytes((text ?? string.Empty) + "\0");
        return SetClipboardFormats(new[] { (CF_UNICODETEXT, bytes) });
    }

    /// <summary>
    /// Reads a string clipboard format while the caller already holds OpenClipboard.
    /// </summary>
    public static string? TryReadOpenClipboardStringFormat(uint formatId, bool isUnicode)
    {
        if (formatId == 0 || !IsClipboardFormatAvailable(formatId)) return null;

        try
        {
            IntPtr hMem = GetClipboardData(formatId);
            if (hMem == IntPtr.Zero) return null;

            int byteLen = (int)Math.Min(GlobalSize(hMem).ToUInt64(), 8192UL);
            if (byteLen <= 0) return null;

            IntPtr ptr = GlobalLock(hMem);
            if (ptr == IntPtr.Zero) return null;

            try
            {
                byte[] buffer = new byte[byteLen];
                Marshal.Copy(ptr, buffer, 0, byteLen);
                string decoded = isUnicode
                    ? Encoding.Unicode.GetString(buffer)
                    : Encoding.UTF8.GetString(buffer);
                return decoded.TrimEnd('\0').Trim();
            }
            finally
            {
                GlobalUnlock(hMem);
            }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll")]
    public static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(uint dwProcessId);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    public delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public const uint GW_HWNDNEXT = 2;
    private const int DWMWA_CLOAKED = 14;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public const int INPUT_KEYBOARD = 1;

    public const byte VK_RETURN = 0x0D;
    public const byte VK_SHIFT = 0x10;
    public const byte VK_CONTROL = 0x11;
    public const byte VK_MENU = 0x12; // Alt
    public const byte VK_V = 0x56;
    public const byte VK_LWIN = 0x5B;
    public const byte VK_RWIN = 0x5C;
    public const byte VK_LSHIFT = 0xA0;
    public const byte VK_RSHIFT = 0xA1;
    public const byte VK_LMENU = 0xA4;
    public const byte VK_RMENU = 0xA5;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public int type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    public static bool IsKeyPhysicallyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public static bool IsShellOrTrayWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return true;
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, 256);
        string cls = sb.ToString();
        return cls is "Shell_TrayWnd"
            or "Shell_SecondaryTrayWnd"
            or "Progman"
            or "WorkerW"
            or "NotifyIconOverflowWindow"
            or "TopLevelWindowForOverflowXamlIsland"
            or "TaskListThumbnailWnd"
            or "MultitaskingViewFrame"
            or "Windows.UI.Core.CoreWindow";
    }

    private static bool IsWindowCloaked(IntPtr hWnd)
    {
        try
        {
            return DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Walks Z-order from the top window to find a visible non-shell, non-current-process window
    /// as a fallback when no previous foreground window was captured (e.g. opened via tray click).
    /// </summary>
    public static IntPtr FindFallbackForegroundWindow(IntPtr excludeHwnd)
    {
        uint currentPid = (uint)Environment.ProcessId;
        IntPtr candidate = excludeHwnd != IntPtr.Zero ? GetWindow(excludeHwnd, GW_HWNDNEXT) : GetTopWindow(IntPtr.Zero);
        if (candidate == IntPtr.Zero) candidate = GetTopWindow(IntPtr.Zero);

        int guard = 0;
        while (candidate != IntPtr.Zero && guard++ < 512)
        {
            if (candidate != excludeHwnd &&
                IsWindowVisible(candidate) &&
                !IsIconic(candidate) &&
                !IsShellOrTrayWindow(candidate) &&
                !IsWindowCloaked(candidate) &&
                GetWindowTextLength(candidate) > 0)
            {
                GetWindowThreadProcessId(candidate, out uint pid);
                if (pid != 0 && pid != currentPid)
                {
                    return candidate;
                }
            }
            candidate = GetWindow(candidate, GW_HWNDNEXT);
        }

        return IntPtr.Zero;
    }

    public static bool ForceForegroundWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd) || IsShellOrTrayWindow(hWnd)) return false;

        IntPtr foregroundHwnd = GetForegroundWindow();
        if (foregroundHwnd == hWnd) return true;

        uint currentThreadId = GetCurrentThreadId();
        uint targetThreadId = GetWindowThreadProcessId(hWnd, out uint targetPid);
        uint foregroundThreadId = GetWindowThreadProcessId(foregroundHwnd, out _);

        bool attached1 = false;
        bool attached2 = false;

        try
        {
            if (targetPid != 0)
            {
                AllowSetForegroundWindow(targetPid);
            }

            if (currentThreadId != targetThreadId && targetThreadId != 0)
            {
                attached1 = AttachThreadInput(currentThreadId, targetThreadId, true);
            }
            if (foregroundThreadId != targetThreadId && foregroundThreadId != 0 && currentThreadId != foregroundThreadId)
            {
                attached2 = AttachThreadInput(currentThreadId, foregroundThreadId, true);
            }

            if (IsIconic(hWnd))
            {
                ShowWindow(hWnd, 9); // SW_RESTORE
            }

            BringWindowToTop(hWnd);
            return SetForegroundWindow(hWnd);
        }
        catch
        {
            return SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached1) AttachThreadInput(currentThreadId, targetThreadId, false);
            if (attached2) AttachThreadInput(currentThreadId, foregroundThreadId, false);
        }
    }

    private static INPUT CreateKeyInput(byte vk, bool keyUp)
    {
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = (ushort)MapVirtualKey(vk, 0),
                    dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };
    }

    private static void SendKeyLegacy(byte vk, bool keyUp)
    {
        byte scan = (byte)MapVirtualKey(vk, 0);
        keybd_event(vk, scan, keyUp ? KEYEVENTF_KEYUP : 0, UIntPtr.Zero);
    }

    /// <summary>
    /// Simulates pressing Ctrl+V into the target active window using properly aligned SendInput with keybd_event fallback.
    /// </summary>
    public static void SimulatePaste()
    {
        var list = new List<INPUT>(10);

        byte[] modifiersToRelease = { VK_LSHIFT, VK_RSHIFT, VK_SHIFT, VK_LMENU, VK_RMENU, VK_MENU, VK_LWIN, VK_RWIN };
        foreach (byte mod in modifiersToRelease)
        {
            if (IsKeyPhysicallyDown(mod))
            {
                list.Add(CreateKeyInput(mod, keyUp: true));
            }
        }

        list.Add(CreateKeyInput(VK_CONTROL, keyUp: false));
        list.Add(CreateKeyInput(VK_V, keyUp: false));
        list.Add(CreateKeyInput(VK_V, keyUp: true));
        list.Add(CreateKeyInput(VK_CONTROL, keyUp: true));

        INPUT[] inputs = list.ToArray();
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == 0)
        {
            foreach (byte mod in modifiersToRelease)
            {
                if (IsKeyPhysicallyDown(mod)) SendKeyLegacy(mod, keyUp: true);
            }
            SendKeyLegacy(VK_CONTROL, keyUp: false);
            SendKeyLegacy(VK_V, keyUp: false);
            SendKeyLegacy(VK_V, keyUp: true);
            SendKeyLegacy(VK_CONTROL, keyUp: true);
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    public static extern int EmptyWorkingSet(IntPtr hwProc);

    public static void TrimWorkingSet()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch { }
    }

    public static string GetForegroundWindowInfo(out string processName)
    {
        return GetForegroundWindowInfo(out processName, out _);
    }

    public static string GetForegroundWindowInfo(out string processName, out IntPtr hwnd)
    {
        processName = "System";
        hwnd = IntPtr.Zero;
        try
        {
            hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return string.Empty;

            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, 256);
            string title = sb.ToString();

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0)
            {
                using var proc = Process.GetProcessById((int)pid);
                processName = proc.ProcessName;
            }

            return title;
        }
        catch
        {
            return string.Empty;
        }
    }

    // Window Sizing & Hit Testing
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_GETMINMAXINFO = 0x0024;

    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public class MONITORINFO
    {
        public int cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        public RECT rcMonitor = new RECT();
        public RECT rcWork = new RECT();
        public int dwFlags = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, MONITORINFO lpmi);

    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    public static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam, int minWidth = 750, int minHeight = 480)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        IntPtr hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMonitor != IntPtr.Zero)
        {
            var mi = new MONITORINFO();
            if (GetMonitorInfo(hMonitor, mi))
            {
                var rcWork = mi.rcWork;
                var rcMonitor = mi.rcMonitor;
                mmi.ptMaxPosition.x = Math.Abs(rcWork.left - rcMonitor.left);
                mmi.ptMaxPosition.y = Math.Abs(rcWork.top - rcMonitor.top);
                mmi.ptMaxSize.x = Math.Abs(rcWork.right - rcWork.left);
                mmi.ptMaxSize.y = Math.Abs(rcWork.bottom - rcWork.top);
            }
        }
        mmi.ptMinTrackSize.x = minWidth;
        mmi.ptMinTrackSize.y = minHeight;
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr hObject);

    // DPAPI Secret Protection (CurrentUser scope)
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public uint cbData;
        public IntPtr pbData;
    }

    private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>
    /// Encrypts a sensitive string under the current Windows user account using DPAPI and returns Base64.
    /// </summary>
    public static string ProtectString(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;

        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        var inBlob = new DATA_BLOB
        {
            cbData = (uint)plainBytes.Length,
            pbData = Marshal.AllocHGlobal(plainBytes.Length)
        };
        var outBlob = default(DATA_BLOB);

        try
        {
            Marshal.Copy(plainBytes, 0, inBlob.pbData, plainBytes.Length);
            if (!CryptProtectData(ref inBlob, "ClipFlux", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
            {
                return string.Empty;
            }

            byte[] cipherBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, cipherBytes, 0, cipherBytes.Length);
            return Convert.ToBase64String(cipherBytes);
        }
        finally
        {
            Array.Clear(plainBytes, 0, plainBytes.Length);
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    /// <summary>
    /// Decrypts a DPAPI-protected Base64 string back to plaintext for the current Windows user account.
    /// </summary>
    public static string UnprotectString(string protectedBase64)
    {
        if (string.IsNullOrWhiteSpace(protectedBase64)) return string.Empty;

        byte[] cipherBytes;
        try
        {
            cipherBytes = Convert.FromBase64String(protectedBase64.Trim());
        }
        catch
        {
            return string.Empty;
        }
        if (cipherBytes.Length == 0) return string.Empty;

        var inBlob = new DATA_BLOB
        {
            cbData = (uint)cipherBytes.Length,
            pbData = Marshal.AllocHGlobal(cipherBytes.Length)
        };
        var outBlob = default(DATA_BLOB);

        try
        {
            Marshal.Copy(cipherBytes, 0, inBlob.pbData, cipherBytes.Length);
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
            {
                return string.Empty;
            }

            byte[] plainBytes = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, plainBytes, 0, plainBytes.Length);
            try
            {
                return Encoding.UTF8.GetString(plainBytes);
            }
            finally
            {
                Array.Clear(plainBytes, 0, plainBytes.Length);
            }
        }
        finally
        {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}
