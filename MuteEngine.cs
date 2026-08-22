using System;
using System.Collections.Generic;
using System.Linq;

namespace WhitelistMute
{
    /// <summary>
    /// 核心静音引擎：仅对白名单内的应用生效——
    /// 若某白名单应用是当前前台窗口 → 取消静音；否则 → 静音。
    /// 白名单外的应用完全不受影响。
    /// </summary>
    public sealed class MuteEngine
    {
        private readonly AudioSessionService _audio;
        private volatile HashSet<string> _whitelist; // 引用字段跨线程读写，volatile 保证可见性

        public MuteEngine(AudioSessionService audio, IEnumerable<string> initialWhitelist)
        {
            _audio = audio;
            _whitelist = new HashSet<string>(initialWhitelist ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>白名单快照（进程名，去重）。</summary>
        public HashSet<string> Whitelist => new(_whitelist, StringComparer.OrdinalIgnoreCase);

        /// <summary>整体替换白名单并立即应用。</summary>
        public void ReplaceWhitelist(IEnumerable<string> names)
        {
            _whitelist = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>取消指定进程名的所有会话静音（用于把应用移出白名单时恢复其声音）。</summary>
        public void UnmuteProcess(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                return;
            }

            _audio.ForEachSession((_pid, name, volume) =>
            {
                if (string.Equals(name, processName, StringComparison.OrdinalIgnoreCase))
                {
                    volume.Mute = false;
                }
            });
        }

        /// <summary>把所有白名单内应用全部取消静音（用于程序退出时释放声音）。</summary>
        public void UnmuteAll()
        {
            var list = _whitelist; // 取一份本地快照，回调中统一使用
            if (list.Count == 0)
            {
                return;
            }

            _audio.ForEachSession((_pid, name, volume) =>
            {
                if (list.Contains(name))
                {
                    volume.Mute = false;
                }
            });
        }

        /// <summary>
        /// 遍历当前活跃音频会话，对白名单内的进程断言其静音状态：
        /// 前台则 Mute=false，后台则 Mute=true。反复调用具备自愈能力。
        /// </summary>
        public void Apply()
        {
            var list = _whitelist; // 取一份本地快照，回调中统一使用
            if (list.Count == 0)
            {
                return;
            }

            string foreground = ForegroundMonitor.GetForegroundProcessName();

            _audio.ForEachSession((_pid, name, volume) =>
            {
                if (string.IsNullOrWhiteSpace(name) || !list.Contains(name))
                {
                    return; // 非白名单，不触碰
                }

                bool shouldMute = !string.Equals(name, foreground, StringComparison.OrdinalIgnoreCase);
                volume.Mute = shouldMute;
            });
        }
    }
}