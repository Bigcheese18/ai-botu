using System;
using System.Collections.Generic;
using System.Linq;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.SW;

namespace TiaOpennessWorker
{
    /// <summary>
    /// 编译 PLC 软件并导出结构化诊断。对应文章能力:CompileSoftware / ExportDiagnostics。
    /// V21 API:plcSoftware.GetService&lt;ICompilable&gt;().Compile() -> CompilerResult。
    /// 关键:CompilerResult.Messages 是嵌套树,真正的错误在叶子节点(容器/汇总节点 Description 为空),
    /// 必须递归遍历(社区 V21 项目验证过的模式)。输出的 JSON 形状 { state, block, description }
    /// 即为将来回传给 AI 修复的输入格式。
    /// </summary>
    public static class CompileDiagnostics
    {
        public static CompileReport Compile(PlcSoftware plcSoftware)
        {
            var compilable = plcSoftware.GetService<ICompilable>();
            var result = compilable.Compile();

            var leaves = new List<DiagnosticMessage>();
            WalkMessages(result.Messages, leaves);

            var report = new CompileReport
            {
                CompilationState = result.State.ToString(),
                Errors = leaves.Count(m => m.State == "Error"),
                Warnings = leaves.Count(m => m.State == "Warning"),
                Messages = leaves,
            };
            Console.Error.WriteLine($"[info] 编译完成: state={report.CompilationState}, errors={report.Errors}, warnings={report.Warnings}");
            return report;
        }

        /// <summary>递归遍历编译消息树,收集叶子诊断(跳过汇总与容器节点)。</summary>
        private static void WalkMessages(IEnumerable<CompilerResultMessage> messages, List<DiagnosticMessage> leaves)
        {
            if (messages == null) return;
            foreach (var m in messages)
            {
                var desc = (m.Description ?? string.Empty).Trim();
                var hasChildren = m.Messages != null && m.Messages.Count > 0;

                // 叶子节点(无子节点)或带描述的非汇总节点才是真正的诊断
                if (!IsSummary(desc) && (!hasChildren || desc.Length > 0))
                {
                    leaves.Add(new DiagnosticMessage
                    {
                        State = m.State.ToString(),
                        Block = m.Path ?? string.Empty,
                        Description = desc,
                    });
                }

                if (hasChildren)
                    WalkMessages(m.Messages, leaves);
            }
        }

        private static bool IsSummary(string desc)
        {
            return desc.StartsWith("Compiling finished", StringComparison.OrdinalIgnoreCase)
                || desc.StartsWith("Compilation finished", StringComparison.OrdinalIgnoreCase)
                || desc.StartsWith("Kompilierung beendet", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>编译报告。</summary>
    public sealed class CompileReport
    {
        public string CompilationState { get; set; }
        public int Errors { get; set; }
        public int Warnings { get; set; }
        public List<DiagnosticMessage> Messages { get; set; } = new List<DiagnosticMessage>();
    }

    /// <summary>单条编译诊断。</summary>
    public sealed class DiagnosticMessage
    {
        public string State { get; set; }
        public string Block { get; set; }
        public string Description { get; set; }
    }
}
