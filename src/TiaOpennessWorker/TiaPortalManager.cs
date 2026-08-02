using System;
using System.Diagnostics;
using System.Threading;
using Siemens.Engineering;

namespace TiaOpennessWorker
{
    /// <summary>
    /// 管理 TIA Portal 实例:优先连接已运行的实例,否则启动新实例。
    /// V21 API:GetProcesses() 是静态方法,TiaPortalProcess.Attach() 绑定已有实例。
    /// 第一次连接会弹出 Openness firewall 授权框,需手动点一次"始终允许"
    /// (连续拒绝 3 次会抛 EngineeringSecurityException)。
    /// </summary>
    public sealed class TiaPortalManager : IDisposable
    {
        public TiaPortal TiaPortal { get; private set; }

        /// <summary>由本管理器启动的实例(而非外部已运行的),Dispose 时关闭。</summary>
        private bool _ownsInstance;

        /// <summary>本管理器启动的 TIA 主进程 PID(用于 Dispose 后清理残留)。</summary>
        private int? _ownedProcessId;

        /// <summary>
        /// 连接 TIA Portal。
        /// preferFresh=true 时(run 命令):等待残留实例退出后启动全新实例,
        /// 避免 "Another project is already open"(TIA 有工程打开时 Dispose 不一定终止进程)。
        /// </summary>
        public void Connect(TiaPortalMode mode, bool preferFresh = false)
        {
            var processes = TiaPortal.GetProcesses();
            if (preferFresh && processes.Count > 0)
            {
                Console.Error.WriteLine($"[info] 检测到 {processes.Count} 个已运行的 TIA Portal 实例,等待其退出...");
                if (!WaitForExit(processes.Count, TimeSpan.FromSeconds(90)))
                {
                    throw new InvalidOperationException(
                        "存在残留的 TIA Portal 实例且未在 90 秒内退出(通常因未保存工程)。"
                        + "请手动关闭 TIA Portal 后重试,或先用 import/compile 命令连接现有实例。");
                }
                processes = TiaPortal.GetProcesses();
            }

            if (processes.Count > 0)
            {
                TiaPortal = processes[0].Attach();
                Console.Error.WriteLine("[info] 已连接当前运行的 TIA Portal 实例");
            }
            else
            {
                TiaPortal = new TiaPortal(mode);
                _ownsInstance = true;
                try { _ownedProcessId = TiaPortal.GetCurrentProcess().Id; } catch { }
                Console.Error.WriteLine($"[info] 已启动新的 TIA Portal 实例(mode={mode})");
            }
        }

        /// <summary>按进程 ID Attach 指定的 TIA Portal 实例(多实例场景切换)。</summary>
        public void AttachTo(int processId)
        {
            foreach (var p in TiaPortal.GetProcesses())
            {
                if (p.Id == processId)
                {
                    TiaPortal = p.Attach();
                    Console.Error.WriteLine($"[info] 已 Attach 到 TIA 实例 PID {processId}");
                    return;
                }
            }
            throw new InvalidOperationException($"未找到 PID {processId} 的 TIA 实例");
        }

        /// <summary>
        /// 关闭 TIA Portal 并确保实例退出。
        /// TIA 是"主进程 + 工作进程"结构,Dispose 后退出是异步的且不可靠(实测 120 秒仍未退出),
        /// 残留进程会锁住工程文件、导致下次 run 失败,因此等待后按 PID 强制结束自己启动的实例。
        /// </summary>
        public void Dispose()
        {
            if (_ownsInstance && TiaPortal != null)
            {
                TiaPortal.Dispose();

                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        if (TiaPortal.GetProcesses().Count == 0) break;
                    }
                    catch { break; } // 枚举失败视为已退出
                    Thread.Sleep(2000);
                }

                if (_ownedProcessId.HasValue)
                {
                    try
                    {
                        var proc = Process.GetProcessById(_ownedProcessId.Value);
                        if (!proc.HasExited)
                        {
                            Console.Error.WriteLine($"[warn] TIA 实例未随 Dispose 退出(PID {_ownedProcessId}),强制结束");
                            proc.Kill();
                            proc.WaitForExit(15000);
                        }
                    }
                    catch (ArgumentException) { } // 进程已不存在,正常
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[warn] 结束 TIA 进程失败: {ex.Message}");
                    }

                    // TIA 是主+工作多进程结构,主进程结束后清掉同名残留
                    // (run 走 preferFresh 已保证启动前环境干净,此处不会误杀用户手动打开的实例)
                    try
                    {
                        foreach (var p in Process.GetProcessesByName("Siemens.Automation.Portal"))
                        {
                            if (!p.HasExited)
                            {
                                Console.Error.WriteLine($"[warn] 清理残留 TIA 工作进程 PID {p.Id}");
                                p.Kill();
                                p.WaitForExit(15000);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[warn] 清理 TIA 残留进程失败: {ex.Message}");
                    }
                }
                Console.Error.WriteLine("[info] 已关闭 TIA Portal 实例");
            }
        }

        /// <summary>轮询等待运行中的 TIA Portal 实例数量降为 targetCount。</summary>
        private static bool WaitForExit(int targetCount, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (TiaPortal.GetProcesses().Count <= targetCount) return true;
                }
                catch
                {
                    return true; // 进程枚举失败视为已退出
                }
                Thread.Sleep(2000);
            }
            return false;
        }
    }
}
