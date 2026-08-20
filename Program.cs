using System;
using System.IO;
using System.Windows.Forms;

namespace WhitelistMute
{
    internal static class Program
    {
        private static string LogPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "_error.log");

        [STAThread]
        private static void Main()
        {
            // 启动崩溃诊断：写到 exe 目录 _error.log，便于排查（发布版可移除）
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                LogError(e.ExceptionObject as Exception ?? new Exception("未知未处理异常"));
            Application.ThreadException += (_, e) => LogError(e.Exception);

            try
            {
                using var mutex = new System.Threading.Mutex(true, "WhitelistMute_SingleInstance", out bool isNewInstance);
                if (!isNewInstance)
                {
                    MessageBox.Show("白名单静音已在运行中。", "WhitelistMute",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                Application.Run(new TrayApplicationContext());
            }
            catch (Exception ex)
            {
                LogError(ex);
                MessageBox.Show(ex.ToString(), "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return; // 已弹窗说明原因，不再继续
            }
        }

        private static void LogError(Exception? ex)
        {
            // 统一写到 exe 所在目录
            try
            {
                File.WriteAllText(LogPath, ex?.ToString() ?? "null");
            }
            catch
            {
            }
        }
    }
}