using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WhitelistMute
{
    /// <summary>
    /// 白名单持久化：保存在 exe 所在目录的 whitelist.conf，
    /// 每行一个进程名，# 开头为注释。简单文本存储，无额外框架依赖。
    /// </summary>
    public sealed class ConfigManager
    {
        private readonly string _configPath;

        public ConfigManager()
        {
            // exe 所在目录（单文件 exe 解压后 BaseDirectory 仍指向 exe 目录）
            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "whitelist.conf");
        }

        /// <summary>读取白名单；文件不存在或损坏时返回空集合。</summary>
        public HashSet<string> Load()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(_configPath))
                {
                    return set;
                }

                foreach (var raw in File.ReadAllLines(_configPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                    {
                        continue;
                    }

                    set.Add(line);
                }
            }
            catch
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            return set;
        }

        /// <summary>整体覆盖写入白名单。</summary>
        public void Save(IEnumerable<string> processNames)
        {
            var ordered = processNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            try
            {
                File.WriteAllLines(_configPath, ordered);
            }
            catch
            {
                // 目录不可写时静默失败，勾选状态只在本次运行内生效
            }
        }
    }
}