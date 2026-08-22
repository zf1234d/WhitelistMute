using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WhitelistMute
{
    /// <summary>
    /// 程序宿主：纯系统托盘，无主窗口。
    /// 「后台播放」「白名单」以内联可勾选子菜单呈现。
    /// </summary>
    public sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon _tray;
        private readonly ConfigManager _config;
        private readonly AudioSessionService _audio;
        private readonly MuteEngine _engine;
        private readonly System.Windows.Forms.Timer _timer;

        private ToolStripMenuItem _bgMenu = null!;
        private ToolStripMenuItem _effectMenu = null!;

        private IReadOnlyCollection<string>? _cachedRunning; // 正在播放应用缓存
        private bool _refreshing;                             // 防止并发刷新

        public TrayApplicationContext()
        {
            _config = new ConfigManager();
            _audio = new AudioSessionService();
            _engine = new MuteEngine(_audio, _config.Load());

            _tray = new NotifyIcon
            {
                Icon = CreateTrayIcon(),
                Text = "白名单静音 · 前台放行 / 后台静音",
                Visible = true,
                ContextMenuStrip = BuildMenu(),
            };
            _tray.DoubleClick += (_, _) => RefreshMenusNow();

            // 周期刷新：前台切换后 ~800ms 内生效
            _timer = new System.Windows.Forms.Timer { Interval = 800 };
            _timer.Tick += (_, _) =>
            {
                try
                {
                    _engine.Apply();
                }
                catch
                {
                    // 音频设备拔插等瞬时错误忽略，下个周期自愈
                }
            };
            _timer.Start();

            // 启动立即应用一次，不等首个 tick（保证开机自启后白名单立刻被接管）
            try
            {
                _engine.Apply();
            }
            catch
            {
            }

            Application.ApplicationExit += (_, _) => Shutdown();
        }

        private ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();

            _bgMenu = new ToolStripMenuItem("后台播放");
            _bgMenu.DropDownOpening += (_, _) => PopulateBackgroundMenu();

            _effectMenu = new ToolStripMenuItem("白名单");
            _effectMenu.DropDownOpening += (_, _) => PopulateEffectMenu();

            _bgMenu.DropDownItems.Add(new ToolStripMenuItem("正在加载…") { Enabled = false });

            var miExit = new ToolStripMenuItem("退出");
            miExit.Click += (_, _) => ExitThread();

            var miAutostart = new ToolStripMenuItem("开机自启")
            {
                CheckOnClick = true,
                Checked = IsAutostartEnabled(),
            };
            miAutostart.Click += (_, _) => SetAutostart(miAutostart.Checked);

            // 展开整个菜单时也顺手刷新一次两个子列表
            menu.Opening += (_, _) => RefreshMenusNow();

            menu.Items.Add(_bgMenu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_effectMenu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miAutostart);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miExit);

            return menu;
        }

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "WhitelistMute";

        private static bool IsAutostartEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(RunValueName) is string value &&
                       string.Equals(value, GetExePath(), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void SetAutostart(bool enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null)
            {
                return;
            }

            try
            {
                if (enabled)
                {
                    key.SetValue(RunValueName, GetExePath(), RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(RunValueName, false);
                }
            }
            catch
            {
                // 注册表写入失败忽略；菜单勾选状态下次打开菜单时按实际值刷新
            }
        }

        private static string GetExePath()
        {
            return System.Reflection.Assembly.GetExecutingAssembly().Location;
        }

        /// <summary>「后台播放」：列出当前正在播放声音的应用，勾选=加入白名单，取消=移出。仅显示进程名。</summary>
        private void PopulateBackgroundMenu()
        {
            _bgMenu.DropDownItems.Clear();

            if (_cachedRunning != null)
            {
                // 渲染缓存，并在后台刷新缓存
                RenderBackgroundItems(_cachedRunning);
                RefreshCacheAsync();
            }
            else
            {
                // 无缓存则同步枚举初始化
                try
                {
                    _cachedRunning = _audio.GetActiveProcessNames();
                    RenderBackgroundItems(_cachedRunning);
                }
                catch (Exception ex)
                {
                    RenderBackgroundItems(Array.Empty<string>(), $"枚举出错: {ex.Message}");
                }
            }
        }

        /// <summary>后台刷新正在播放应用缓存；仅更新字段，不改动已显示的子菜单项。</summary>
        private void RefreshCacheAsync()
        {
            if (_refreshing)
            {
                return;
            }
            _refreshing = true;

            Task.Run(() => _audio.GetActiveProcessNames())
                .ContinueWith(t =>
                {
                    _refreshing = false;
                    if (!t.IsFaulted && t.Result != null)
                    {
                        _cachedRunning = t.Result; // 仅更新缓存字段
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void RenderBackgroundItems(IEnumerable<string> running, string? error = null)
        {
            _bgMenu.DropDownItems.Clear();

            var whitelist = _engine.Whitelist;
            bool any = false;

            foreach (string name in running.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                var item = new ToolStripMenuItem(name)
                {
                    CheckOnClick = true,
                    Checked = whitelist.Contains(name),
                };
                item.Click += (_, _) => ToggleWhitelist(name, item);
                _bgMenu.DropDownItems.Add(item);
                any = true;
            }

            if (!any)
            {
                var text = error ?? "（当前没有正在播放音频的应用）";
                _bgMenu.DropDownItems.Add(new ToolStripMenuItem(text)
                {
                    Enabled = false,
                    ForeColor = error != null ? Color.Red : Color.Empty,
                });
            }
        }

        private void ToggleWhitelist(string name, ToolStripMenuItem item)
        {
            var whitelist = _engine.Whitelist;

            if (item.Checked && !whitelist.Contains(name))
            {
                whitelist.Add(name);
                _engine.ReplaceWhitelist(whitelist);
                _config.Save(whitelist);
                _engine.Apply();
            }
            else if (!item.Checked)
            {
                // 取消勾选 = 移出白名单并恢复声音
                RemoveFromWhitelist(name);
            }
        }

        /// <summary>「白名单」：展示所有勾选过的应用（白名单全集，含当前未播放的）；取消勾选=移出并恢复声音。</summary>
        private void PopulateEffectMenu()
        {
            _effectMenu.DropDownItems.Clear();

            var whitelist = _engine.Whitelist;
            foreach (string name in whitelist.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            {
                var item = new ToolStripMenuItem(name)
                {
                    CheckOnClick = true,
                    Checked = true, // 在白名单即勾选
                };
                var processName = name; // 捕获循环变量
                item.Click += (_, _) =>
                {
                    if (!item.Checked)
                    {
                        RemoveFromWhitelist(processName);
                        PopulateEffectMenu(); // 移出后刷新列表
                    }
                };
                _effectMenu.DropDownItems.Add(item);
            }

            if (whitelist.Count == 0)
            {
                _effectMenu.DropDownItems.Add(new ToolStripMenuItem("（尚未勾选任何应用）")
                {
                    Enabled = false,
                });
            }
        }

        /// <summary>把指定应用移出白名单并立即恢复其声音。</summary>
        private void RemoveFromWhitelist(string name)
        {
            _engine.UnmuteProcess(name);

            var whitelist = _engine.Whitelist;
            whitelist.Remove(name);
            _engine.ReplaceWhitelist(whitelist);
            _config.Save(whitelist);
            _engine.Apply();
        }

        private void RefreshMenusNow()
        {
            if (_bgMenu.DropDownItems.Count <= 0)
            {
                return;
            }
            PopulateBackgroundMenu();
            PopulateEffectMenu();
        }

        private Icon CreateTrayIcon()
        {
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var brush = new SolidBrush(Color.FromArgb(0, 120, 215));
                g.FillEllipse(brush, 3, 3, 26, 26);

                var font = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var txt = new SolidBrush(Color.White);
                g.DrawString("M", font, txt, new RectangleF(2, 5, 28, 22),
                    new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            }
            var hIcon = bmp.GetHicon();
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }

        private void Shutdown()
        {
            // 退出前释放：把所有白名单应用取消静音，避免退出后仍处于静音
            try
            {
                _engine.UnmuteAll();
            }
            catch
            {
            }

            _timer?.Stop();
            _tray?.Dispose();
            _audio?.Dispose();
        }
    }
}