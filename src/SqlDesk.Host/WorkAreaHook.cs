using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SqlDesk.Host;

/// <summary>
/// Janela sem moldura (WindowStyle=None) maximiza sobre a barra de tarefas. Responde a WM_GETMINMAXINFO
/// limitando a área maximizada à área de trabalho do monitor onde a janela está.
/// </summary>
internal static class WorkAreaHook
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 2;

    public static void Install(Window window)
    {
        var source = (HwndSource)PresentationSource.FromVisual(window)!;
        source.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                mmi.ptMaxPosition.x = info.rcWork.left - info.rcMonitor.left;
                mmi.ptMaxPosition.y = info.rcWork.top - info.rcMonitor.top;
                mmi.ptMaxSize.x = info.rcWork.right - info.rcWork.left;
                mmi.ptMaxSize.y = info.rcWork.bottom - info.rcWork.top;
                Marshal.StructureToPtr(mmi, lParam, true);
            }
        }
        handled = false; // o WPF ainda aplica os limites mínimos da janela
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public int dwFlags;
    }
}
