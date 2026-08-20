using System;
using System.Runtime.InteropServices;

namespace WhitelistMute
{
    /// <summary>
    /// 获取当前前台窗口所属进程的进程名（用于匹配白名单）。
    /// </summary>
    public static class ForegroundMonitor
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        /// <summary>返回当前前台窗口所属进程的进程名；无前台窗口时返回空串。</summary>
        public static string GetForegroundProcessName()
        {
            IntPtr hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd))
            {
                return string.Empty;
            }

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0)
            {
                return string.Empty;
            }

            try
            {
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                return p.ProcessName;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}