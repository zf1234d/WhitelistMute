using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using CSCore.CoreAudioAPI;

namespace WhitelistMute
{
    /// <summary>
    /// 直接读取系统"应用音量合成器"（WASAPI 音频会话）。
    /// 注意：CSCore 的 AudioSessionManager2 要求从 MTA 线程发起
    /// （RegisterSessionNotification must be called from an MTA-thread），
    /// 而 WinForms 主线程是 STA，因此所有枚举一律在专用常驻 MTA 线程内执行。
    /// </summary>
    public sealed class AudioSessionService : IDisposable
    {
        private readonly BlockingCollection<Action> _jobs = new();
        private readonly Thread _mtaThread;
        private bool _disposed;

        public AudioSessionService()
        {
            _mtaThread = new Thread(() =>
            {
                foreach (var job in _jobs.GetConsumingEnumerable())
                {
                    job();
                }
            })
            {
                IsBackground = true,
                Name = "AudioSession-MTA",
            };
            _mtaThread.SetApartmentState(ApartmentState.MTA);
            _mtaThread.Start();
        }

        /// <summary>遍历每个音频进程（去重）。回调 (pid, 进程名)。</summary>
        public void ForEachProcess(Action<int, string> onProcess)
        {
            RunInMta(() =>
            {
                ForEachDeviceSession((pid, name, _) =>
                {
                    onProcess(pid, name);
                });
            });
        }

        /// <summary>遍历每个活跃音频会话，回调 (pid, 进程名, 会话音量)。</summary>
        public void ForEachSession(Action<int, string, SimpleAudioVolume> onSession)
        {
            RunInMta(() =>
            {
                ForEachDeviceSession((pid, name, volume) =>
                {
                    onSession(pid, name, volume);
                });
            });
        }

        /// <summary>将某个指定 PID 的所有会话设为 静音/不静音。</summary>
        public void SetMuteByPid(int pid, bool mute)
        {
            if (pid <= 0)
            {
                return;
            }

            ForEachSession((_pid, _name, volume) =>
            {
                if (_pid == pid)
                {
                    volume.IsMuted = mute;
                }
            });
        }

        /// <summary>当前所有活跃音频会话的进程名（去重）。</summary>
        public IReadOnlyCollection<string> GetActiveProcessNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ForEachProcess((_pid, name) =>
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            });
            return names;
        }

        // -------- 以下为必须在 MTA 线程内执行的底层访问 --------

        private static IReadOnlyList<MMDevice> EnumerateDevices()
        {
            // 急切物化：枚举器在此方法内释放，但返回的 MMDeviceCollection 已对设备持有引用
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumAudioEndpoints(DataFlow.Render, DeviceState.Active).ToList();
        }

        private static AudioSessionEnumerator? TryGetSessions(MMDevice device, out string error)
        {
            error = string.Empty;
            try
            {
                using var manager = AudioSessionManager2.FromMMDevice(device);
                return manager.GetSessionEnumerator();
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        private static void ForEachDeviceSession(Action<int, string, SimpleAudioVolume> onSession)
        {
            foreach (var device in EnumerateDevices())
            {
                using (device)
                {
                    AudioSessionEnumerator? sessions = null;
                    try { sessions = TryGetSessions(device, out _); }
                    catch { }

                    if (sessions == null)
                    {
                        continue;
                    }

                    using (sessions)
                    {
                        foreach (var session in sessions)
                        {
                            if (session == null ||
                                session.SessionState != AudioSessionState.AudioSessionStateActive ||
                                !TryGetSessionInfo(session, out int pid, out string name))
                            {
                                continue;
                            }

                            try
                            {
                                using var volume = GetVolume(session);
                                if (volume == null)
                                {
                                    continue;
                                }

                                onSession(pid, name, volume);
                            }
                            catch
                            {
                                // 单个会话出错不影响其余会话
                            }
                        }
                    }
                }
            }
        }

        private static SimpleAudioVolume? GetVolume(AudioSessionControl session)
        {
            try
            {
                return session.QueryInterface<SimpleAudioVolume>();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 从会话中稳定取出 (pid, 进程名)。兼容 CSCore 枚举元素为 AudioSessionControl2
        /// 或泛型 AudioSessionControl 两种情况（先强转、失败再 QueryInterface）。
        /// </summary>
        private static bool TryGetSessionInfo(AudioSessionControl session, out int pid, out string name)
        {
            pid = 0;
            name = string.Empty;

            AudioSessionControl2? c2 = session as AudioSessionControl2;
            bool needDispose = false;

            try
            {
                if (c2 == null)
                {
                    try
                    {
                        c2 = session.QueryInterface<AudioSessionControl2>();
                        needDispose = true;
                    }
                    catch
                    {
                        return false;
                    }
                }

                if (c2.IsSystemSoundSession)
                {
                    return false;
                }

                int p = c2.ProcessID;
                if (p <= 0)
                {
                    return false;
                }

                string n = c2.Process?.ProcessName ?? SafeProcessName((uint)p);
                if (string.IsNullOrWhiteSpace(n))
                {
                    return false;
                }

                pid = p;
                name = n;
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (needDispose)
                {
                    c2?.Dispose();
                }
            }
        }

        private static string SafeProcessName(uint pid)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                return p.ProcessName;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>在常驻 MTA 线程上执行 action（阻塞等待完成；必须从 MTA 线程调用 CSCore 合并操作）。</summary>
        private void RunInMta(Action action)
        {
            if (_disposed)
            {
                return;
            }

            using var done = new ManualResetEventSlim(false);
            Exception? error = null;

            _jobs.Add(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                finally
                {
                    done.Set();
                }
            });

            done.Wait();

            if (error != null)
            {
                throw error;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _jobs.CompleteAdding(); // 让 MTA 线程退出消费循环
        }
    }
}