using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

class Program
{
    // ── Win32 constants ───────────────────────────────────
    const uint MSG_WORKERW = 0x052C;

    const int GWL_STYLE = -16;

    const long WS_VISIBLE = 0x10000000;
    const long WS_CHILD = 0x40000000;
    const long WS_POPUP = 0x80000000;
    const long WS_CAPTION = 0x00C00000;
    const long WS_THICKFRAME = 0x00040000;

    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_FRAMECHANGED = 0x0020;
    const uint SWP_SHOWWINDOW = 0x0040;
    const uint SMTO_NORMAL = 0x0000;

    const int SW_SHOW = 5;

    // ── Win32 API ─────────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    static extern long SetWindowLongPtr(IntPtr hWnd, int nIndex, long dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool IsWindowVisible(IntPtr hWnd);

    // ── Helpers ───────────────────────────────────────────
    static string GetWindowClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, 256);
        return sb.ToString();
    }

    static bool HasChildOfClass(IntPtr parent, string className)
    {
        bool found = false;
        EnumChildWindows(parent, (h, _) =>
        {
            if (GetWindowClass(h) == className) { found = true; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ── Classic algorithm: find WorkerW behind SHELLDLL_DefView ──
    // Step 1: Find top-level window containing SHELLDLL_DefView
    // Step 2: FindWorkerWEx(0, topWindow, "WorkerW", null) → the wallpaper layer behind it
    static IntPtr FindWorkerWClassic()
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            Console.WriteLine("[desktop_host] Progman not found");
            return IntPtr.Zero;
        }
        Console.WriteLine("[desktop_host] Progman: 0x" + progman.ToString("X"));

        // Send 0x052C to trigger WorkerW creation
        SendMessageTimeout(progman, MSG_WORKERW, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 2000, out _);
        System.Threading.Thread.Sleep(200);

        // Find the top-level window that contains SHELLDLL_DefView
        IntPtr defViewParent = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (HasChildOfClass(h, "SHELLDLL_DefView") && IsWindowVisible(h))
            {
                defViewParent = h;
                Console.WriteLine("[desktop_host] SHELLDLL_DefView parent: 0x" + h.ToString("X") + " class=" + GetWindowClass(h));
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (defViewParent != IntPtr.Zero)
        {
            // Find WorkerW behind the DefView parent
            IntPtr workerW = FindWindowEx(IntPtr.Zero, defViewParent, "WorkerW", null);
            if (workerW != IntPtr.Zero)
            {
                Console.WriteLine("[desktop_host] WorkerW behind DefView: 0x" + workerW.ToString("X"));
                return workerW;
            }
            Console.WriteLine("[desktop_host] No WorkerW found behind DefView parent");
        }
        else
        {
            Console.WriteLine("[desktop_host] No visible SHELLDLL_DefView parent found");
        }

        return IntPtr.Zero;
    }

    // ── Fallback: find any WorkerW without DefView ────────
    static IntPtr FindWorkerWFallback()
    {
        var candidates = new List<IntPtr>();

        EnumWindows((h, _) =>
        {
            if (GetWindowClass(h) == "WorkerW")
                candidates.Add(h);
            return true;
        }, IntPtr.Zero);

        Console.WriteLine("[desktop_host] EnumWindows found " + candidates.Count + " WorkerW(s)");

        // Prefer WorkerW without DefView (wallpaper layer)
        foreach (var w in candidates)
        {
            if (!HasChildOfClass(w, "SHELLDLL_DefView"))
            {
                Console.WriteLine("[desktop_host] Fallback WorkerW (no DefView): 0x" + w.ToString("X"));
                return w;
            }
        }

        // Take first visible WorkerW
        foreach (var w in candidates)
        {
            if (IsWindowVisible(w))
            {
                Console.WriteLine("[desktop_host] Fallback first visible WorkerW: 0x" + w.ToString("X"));
                return w;
            }
        }

        // Last resort: first WorkerW
        if (candidates.Count > 0)
        {
            Console.WriteLine("[desktop_host] Fallback first WorkerW: 0x" + candidates[0].ToString("X"));
            return candidates[0];
        }

        return IntPtr.Zero;
    }

    // ── Embed window ──────────────────────────────────────
    static bool EmbedWindow(IntPtr hwnd, IntPtr parent, int x, int y, int w, int h)
    {
        // 1. Read current style
        long style = GetWindowLongPtr(hwnd, GWL_STYLE);
        Console.WriteLine("[desktop_host] Old style: 0x" + style.ToString("X"));

        // 2. Reparent
        IntPtr oldParent = SetParent(hwnd, parent);
        if (oldParent == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine("[desktop_host] SetParent FAILED, error=" + err);
            return false;
        }
        Console.WriteLine("[desktop_host] SetParent ok, old=0x" + oldParent.ToString("X"));

        // 3. Adjust style: add WS_CHILD|WS_VISIBLE, remove WS_POPUP|WS_CAPTION|WS_THICKFRAME
        style |= (WS_CHILD | WS_VISIBLE);
        style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME);
        SetWindowLongPtr(hwnd, GWL_STYLE, style);
        Console.WriteLine("[desktop_host] New style: 0x" + style.ToString("X"));

        // 4. Show the window
        ShowWindow(hwnd, SW_SHOW);

        // 5. Position with SWP_FRAMECHANGED (tells system style changed) + SWP_SHOWWINDOW
        SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
            SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        Console.WriteLine("[desktop_host] SetWindowPos: " + x + "," + y + " " + w + "x" + h);

        return true;
    }

    // ── Main ─────────────────────────────────────────────
    static int Main(string[] args)
    {
        // Parse positional args (first 5) and optional --parent flag
        var positional = new List<string>();
        string parentMode = "workerw"; // default

        foreach (var a in args)
        {
            if (a == "--parent" || a == "-parent")
                continue; // next arg is the value, handled below
            if (a.StartsWith("--parent="))
            {
                parentMode = a.Substring("--parent=".Length);
                continue;
            }
            positional.Add(a);
        }

        // Also handle --parent <value> two-arg form
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--parent")
                parentMode = args[i + 1];
        }

        if (positional.Count < 5)
        {
            Console.Error.WriteLine("Usage: desktop_host.exe <hwnd> <x> <y> <width> <height> [--parent workerw|progman]");
            return 1;
        }

        if (!long.TryParse(positional[0], out long hwndLong) ||
            !int.TryParse(positional[1], out int x) ||
            !int.TryParse(positional[2], out int y) ||
            !int.TryParse(positional[3], out int w) ||
            !int.TryParse(positional[4], out int h))
        {
            Console.Error.WriteLine("[desktop_host] Invalid numeric arguments");
            return 1;
        }

        IntPtr electronHwnd = new IntPtr(hwndLong);
        Console.WriteLine("[desktop_host] HWND=0x" + electronHwnd.ToString("X") + " bounds=" + x + "," + y + " " + w + "x" + h + " parent=" + parentMode);

        // Find parent window
        IntPtr parent = IntPtr.Zero;

        if (parentMode == "progman")
        {
            parent = FindWindow("Progman", null);
            if (parent != IntPtr.Zero)
                Console.WriteLine("[desktop_host] Using Progman: 0x" + parent.ToString("X"));
            else
                Console.WriteLine("[desktop_host] Progman not found");
        }
        else // workerw (default)
        {
            // Priority A: classic algo — WorkerW behind SHELLDLL_DefView
            parent = FindWorkerWClassic();

            // Priority B: fallback WorkerW without DefView
            if (parent == IntPtr.Zero)
                parent = FindWorkerWFallback();

            // Priority C: Progman
            if (parent == IntPtr.Zero)
            {
                parent = FindWindow("Progman", null);
                if (parent != IntPtr.Zero)
                    Console.WriteLine("[desktop_host] Fallback Progman: 0x" + parent.ToString("X"));
            }
        }

        if (parent == IntPtr.Zero)
        {
            Console.Error.WriteLine("[desktop_host] No parent window found");
            return 2;
        }

        Console.WriteLine("[desktop_host] Embedding into parent 0x" + parent.ToString("X"));

        bool ok = EmbedWindow(electronHwnd, parent, x, y, w, h);
        Console.WriteLine("[desktop_host] " + (ok ? "Embedding complete" : "Embedding FAILED"));
        return ok ? 0 : 3;
    }
}
