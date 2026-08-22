using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace WhitelistMute
{
    /// <summary>
    /// 直接读取系统"应用音量合成器"（WASAPI 音频会话）。
    /// NAudio 的 AudioSessionManager/SessionCollection 需要从 MTA 线程访问，
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
                ForEachDeviceSession(onSession);
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
            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        }

        private static void ForEachDeviceSession(Action<int, string, SimpleAudioVolume> onSession)
        {
            foreach (var device in EnumerateDevices())
            {
                using (device)
                {
                    AudioSessionManager manager;
                    try
                    {
                        manager = device.AudioSessionManager;
                    }
                    catch
                    {
                        continue;
                    }

                    try
                    {
                        SessionCollection sessions;
                        try
                        {
                            sessions = manager.Sessions;
                        }
                        catch
                        {
                            continue;
                        }

                        for (int i = 0; i < sessions.Count; i++)
                        {
                            try
                            {
                                using var session = sessions[i];
                                if (session == null ||
                                    session.State != AudioSessionState.AudioSessionStateActive ||
                                    session.IsSystemSoundsSession ||
                                    !TryGetSessionInfo(session, out int pid, out string name))
                                {
                                    continue;
                                }

                                using var volume = session.SimpleAudioVolume;
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
                    finally
                    {
                        manager.Dispose();
                    }
                }
            }
        }

        private static bool TryGetSessionInfo(AudioSessionControl session, out int pid, out string name)
        {
            pid = 0;
            name = string.Empty;

            try
            {
                uint p = session.GetProcessID;
                if (p <= 0)
                {
                    return false;
                }

                name = SafeProcessName(p);
                if (string.IsNullOrWhiteSpace(name))
                {
                    return false;
                }

                pid = (int)p;
                return true;
            }
            catch
            {
                return false;
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

        /// <summary>在常驻 MTA 线程上执行 action（阻塞等待完成；会话访问必须在 MTA 线程）。</summary>
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