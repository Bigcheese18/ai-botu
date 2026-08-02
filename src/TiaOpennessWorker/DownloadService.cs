using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Collections;
using Siemens.Engineering;
using Siemens.Engineering.Download;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;

namespace TiaOpennessWorker
{
    /// <summary>
    /// 下载 PLC 软件到 S7-PLCSIM 仿真器:编译 → 启动 PLCSIM → 选目标接口 → 下载。
    /// V21 下载走 DownloadProvider 服务,Download 方法为 4 参反射调用
    /// (IConfiguration, pre, post, DownloadOptions)——参数类型 IConfiguration 不公开,须反射。
    /// </summary>
    public static class DownloadService
    {
        /// <summary>PLCSIM 可执行文件路径:环境变量 TIA_PLCSIM_DIR 优先,否则常见安装位置。</summary>
        private static string PlcsimExe
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("TIA_PLCSIM_DIR");
                if (!string.IsNullOrWhiteSpace(env)) return Path.Combine(env, "S7PLCSIMV21.exe");
                foreach (var dir in new[]
                {
                    @"C:\Program Files\Siemens\Automation\S7-PLCSIM V21",
                    @"C:\Program Files (x86)\Siemens\Automation\S7-PLCSIM V21",
                    @"C:\Program Files\Siemens\Automation\PLCSIM V21",
                })
                {
                    var p = Path.Combine(dir, "S7PLCSIMV21.exe");
                    if (File.Exists(p)) return p;
                }
                return @"C:\Program Files\Siemens\Automation\S7-PLCSIM V21\S7PLCSIMV21.exe";
            }
        }

        /// <summary>启动 S7-PLCSIM(已运行则跳过),等待就绪。</summary>
        public static void StartPlcsim()
        {
            var running = Process.GetProcessesByName("S7PLCSIMV21").Length > 0;
            if (running)
            {
                Console.Error.WriteLine("[info] S7-PLCSIM 已在运行");
                return;
            }
            if (!File.Exists(PlcsimExe)) throw new FileNotFoundException($"PLCSIM 不存在: {PlcsimExe}");
            Process.Start(new ProcessStartInfo(PlcsimExe) { WorkingDirectory = Path.GetDirectoryName(PlcsimExe) });
            Console.Error.WriteLine("[info] 已启动 S7-PLCSIM,等待就绪...");
            // PLCSIM 启动(许可检查/界面初始化)可能较慢,最多等 60 秒
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                if (Process.GetProcessesByName("S7PLCSIMV21").Length > 0)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(8)); // 进程出现后还需初始化
                    Console.Error.WriteLine("[info] S7-PLCSIM 就绪");
                    return;
                }
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
            throw new InvalidOperationException("S7-PLCSIM 启动超时(60s)");
        }

        /// <summary>
        /// 给 PLC 的 PROFINET 接口配置 IP(下载前提:DownloadProvider 需要接口有地址)。
        /// 遍历工程所有设备,找到带 IPAddress 属性的接口(以太网接口),未配置时设置默认 IP。
        /// </summary>
        public static void EnsureIp(Project project)
        {
            try
            {
                foreach (var device in project.Devices)
                {
                    // 网络接口服务可能挂在树的任意层级(参考仓库全树遍历),递归找
                    foreach (var item in device.DeviceItems)
                        if (TryEnsureNodeIp(item)) return;
                }
                Console.Error.WriteLine("[warn] 未找到带 IPAddress 属性的以太网接口");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[warn] PLC IP 配置失败(可能已配置): {ProjectOperations.Unwrap(ex).Message}");
            }
        }

        /// <summary>递归遍历 DeviceItem 找网络接口对象,配置 Node 的 IP。命中返回 true。</summary>
        private static bool TryEnsureNodeIp(DeviceItem item)
        {
            object netIf = null;
            try { netIf = item.GetService<NetworkInterface>(); } catch { }
            if (netIf == null)
            {
                // 备选:NetworkPort 服务的 Interface 属性(端口对象上的服务)
                try
                {
                    var mi = item.GetType().GetMethods()
                        .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                    var portType = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                        .FirstOrDefault(t => t.Name.EndsWith("NetworkPort") && t.IsInterface && t.IsPublic);
                    if (mi != null && portType != null)
                    {
                        var port = mi.MakeGenericMethod(portType).Invoke(item, null);
                        netIf = port?.GetType().GetProperty("Interface")?.GetValue(port);
                    }
                }
                catch { }
            }

            if (netIf != null)
            {
                try
                {
                    var nodes = netIf.GetType().GetProperty("Nodes")?.GetValue(netIf) as IEnumerable;
                    if (nodes != null)
                    {
                        foreach (var node in nodes)
                        {
                            var get = node.GetType().GetMethod("GetAttribute", new[] { typeof(string) });
                            var set = node.GetType().GetMethod("SetAttribute", new[] { typeof(string), typeof(object) });
                            var ip = (get?.Invoke(node, new object[] { "IPAddress" }) ?? "").ToString();
                            if (ip == "0.0.0.0")
                            {
                                set?.Invoke(node, new object[] { "IPAddress", "192.168.0.1" });
                                set?.Invoke(node, new object[] { "IPSubnetMask", "255.255.255.0" });
                                Console.Error.WriteLine($"[info] 已配置 PLC 接口 IP 192.168.0.1 ({item.Name})");
                            }
                            else
                            {
                                Console.Error.WriteLine($"[info] PLC 接口 {item.Name} IP: {ip}");
                            }
                            return true;
                        }
                    }
                }
                catch { }
            }

            foreach (var child in item.DeviceItems)
                if (TryEnsureNodeIp(child)) return true;
            return false;
        }

        /// <summary>下载 PLC 软件到 PLCSIM。返回 DownloadResult 状态描述。</summary>
        public static string DownloadToPlc(PlcSoftware plcSoftware)
        {
            var provider = plcSoftware.GetService<DownloadProvider>();
            if (provider == null)
                throw new InvalidOperationException("DownloadProvider 服务不可用(PLC 未配置网络/硬件)");
            var configuration = provider.Configuration;
            if (configuration == null)
                throw new InvalidOperationException("没有可用的连接配置:请先在硬件组态中给 PLC 的 PROFINET 接口配置 IP 地址");

            // 目标接口:优先选择 PLCSIM 虚拟以太网(ConfigurationTargetInterface 反射)
            object downloadConfig = TrySelectTargetInterface(configuration) ?? configuration;

            DownloadConfigurationDelegate pre = config =>
            {
                try
                {
                    // 通过属性名设置下载选项(不同版本属性名略有差异,容错)
                    SetOpt(config, "KeepActualValues", true);
                    SetOpt(config, "StartAfterDownload", true);
                    SetOpt(config, "StopBeforeDownload", true);
                    SetOpt(config, "ConsistentBlocksOnly", true);
                }
                catch { }
            };

            var method = provider.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Download"
                    && m.GetParameters().Length == 4
                    && m.GetParameters()[1].ParameterType.Name == "DownloadConfigurationDelegate");
            if (method == null)
                throw new InvalidOperationException("Download(IConfiguration,...) 方法未找到,TIA 版本不匹配?");

            var raw = method.Invoke(provider, new[] { downloadConfig, pre, (DownloadConfigurationDelegate)(c => { }), DownloadOptions.Software });
            if (!(raw is DownloadResult result))
                throw new InvalidOperationException($"下载返回了意外的结果类型: {raw?.GetType().Name}");

            var state = result.State.ToString();
            var errors = result.ErrorCount;
            var warnings = result.WarningCount;
            Console.Error.WriteLine($"[info] 下载完成: State={state}, Errors={errors}, Warnings={warnings}");
            if (errors > 0)
            {
                var msg = string.Join("; ", result.Messages.Take(5).Select(m =>
                {
                    try { return (m.GetAttribute("Description") ?? m.GetAttribute("Text") ?? "").ToString(); }
                    catch { return m.ToString(); }
                }));
                throw new InvalidOperationException($"下载未成功({state}, {errors} 个错误): {msg}");
            }
            return state;
        }

        /// <summary>从连接配置的 PG/PC 接口里挑目标接口(PLCSIM 虚拟网卡优先),应用路由后返回。</summary>
        private static object TrySelectTargetInterface(object configuration)
        {
            try
            {
                // ConnectionConfiguration.ApplyConfiguration(ConfigurationTargetInterface) -> bool
                var configType = configuration.GetType();
                var applyMethod = configType.GetMethods().FirstOrDefault(m =>
                    m.Name == "ApplyConfiguration" && m.GetParameters().Length == 1);
                if (applyMethod == null) return null;

                // Modes -> PcInterfaces -> TargetInterfaces
                var modes = configType.GetProperty("Modes")?.GetValue(configuration);
                if (modes == null) return null;
                var pcInterfaces = modes.GetType().GetProperty("PcInterfaces")?.GetValue(modes);
                if (pcInterfaces == null) return null;
                var targets = pcInterfaces.GetType().GetProperty("TargetInterfaces")?.GetValue(pcInterfaces);
                if (!(targets is System.Collections.IEnumerable enumerable)) return null;

                foreach (var target in enumerable)
                {
                    var name = target.GetType().GetProperty("Name")?.GetValue(target)?.ToString() ?? "";
                    var isPlcsim = name.IndexOf("PLCSIM", StringComparison.OrdinalIgnoreCase) >= 0;
                    try
                    {
                        var ok = applyMethod.Invoke(configuration, new[] { target });
                        if (ok is bool b && b)
                        {
                            Console.Error.WriteLine($"[info] 已选择下载目标接口: {name}");
                            return configuration; // 路由已应用到原配置
                        }
                    }
                    catch { }
                    if (isPlcsim) break; // PLCSIM 接口应用失败就不再找别的
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[warn] 目标接口选择失败: {ProjectOperations.Unwrap(ex).Message}");
            }
            return null;
        }

        private static void SetOpt(object config, string prop, object value)
        {
            var p = config.GetType().GetProperty(prop);
            if (p != null && p.CanWrite) p.SetValue(config, value);
        }
    }
}
