using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace TiaOpennessWorker
{
    /// <summary>
    /// 从 TIA Portal 安装目录解析 Siemens.Engineering.* 程序集。
    /// 对应文章避坑要点:C# 工程引用 Openness DLL 时 Copy Local 必须为 False,
    /// 运行时通过 AssemblyResolve 从 TIA 安装目录解析,不要把 DLL 拷到输出目录分发。
    /// </summary>
    public static class AssemblyResolver
    {
        private static string _opennessDir;

        /// <summary>解析到的 Openness 程序集目录(未解析时为 null)。</summary>
        public static string OpennessDir => _opennessDir;

        /// <summary>
        /// 注册 AssemblyResolve 处理器。优先 --openness-dir 参数 / 环境变量 TIA_OPENNESS_DIR,
        /// 否则探测常见安装位置。成功返回 true。
        /// </summary>
        public static bool TryResolve(string overrideDir, out string error)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(overrideDir))
                candidates.Add(Path.GetFullPath(overrideDir));

            var env = Environment.GetEnvironmentVariable("TIA_OPENNESS_DIR");
            if (!string.IsNullOrWhiteSpace(env))
                candidates.Add(env);

            // 注册表探测 TIA 安装路径(标准安装)
            candidates.AddRange(ProbeRegistryInstallDirs());

            // 常见默认安装位置(V15 ~ V21)
            foreach (var v in new[] { "Portal V21", "Portal V20", "Portal V19", "Portal V18" })
            {
                candidates.Add($@"C:\Program Files\Siemens\Automation\{v}\PublicAPI\{v.Split(' ')[1]}\net48");
                candidates.Add($@"C:\Program Files (x86)\Siemens\Automation\{v}\PublicAPI\{v.Split(' ')[1]}\net48");
            }

            error = null;
            foreach (var dir in candidates)
            {
                if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "Siemens.Engineering.Base.dll")))
                {
                    _opennessDir = dir;
                    AppDomain.CurrentDomain.AssemblyResolve += ResolveFromOpennessDir;
                    Console.Error.WriteLine($"[info] Openness 程序集目录: {dir}");
                    return true;
                }
            }

            error = "未找到 TIA Portal Openness 程序集目录(需含 Siemens.Engineering.Base.dll)。"
                  + "请用 --openness-dir <目录> 参数或环境变量 TIA_OPENNESS_DIR 指定。";
            return false;
        }

        /// <summary>从注册表读取 TIA Portal 安装目录(HKLM,32/64 位视图)。</summary>
        private static IEnumerable<string> ProbeRegistryInstallDirs()
        {
            var roots = new[] { @"SOFTWARE\Siemens\Automation", @"SOFTWARE\WOW6432Node\Siemens\Automation" };
            var keys = new[] { @"Portal V21", @"TIA Portal V21", @"Portal V20", @"TIA Portal V20" };
            var result = new List<string>();
            foreach (var root in roots)
            {
                foreach (var key in keys)
                {
                    try
                    {
                        using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root + "\\" + key))
                        {
                            var dir = k?.GetValue("InstallPath")?.ToString() ?? k?.GetValue("InstallationPath")?.ToString();
                            if (!string.IsNullOrWhiteSpace(dir))
                                result.Add(Path.Combine(dir, "PublicAPI", "V21", "net48"));
                        }
                    }
                    catch { }
                }
            }
            return result;
        }

        private static Assembly ResolveFromOpennessDir(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            if (!name.StartsWith("Siemens.Engineering")) return null;

            var path = Path.Combine(_opennessDir, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
    }
}
