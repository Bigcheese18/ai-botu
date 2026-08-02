using System;
using System.IO;
using System.Text;
using Siemens.Engineering.SW;

namespace TiaOpennessWorker
{
    /// <summary>
    /// 工程报告生成:把 PLC 软件的块列表、变量表、I/O 分配汇总为 Markdown 文档,
    /// 用于程序文档化/评审/交接。
    /// </summary>
    public static class ReportGenerator
    {
        public static string Generate(PlcSoftware plc, string outDir, string projectName)
        {
            var sb = new StringBuilder(4096);
            sb.Append($"# {projectName} 工程报告\n\n");
            sb.Append($"> 由 TIA Openness Worker 生成 · {DateTime.Now:yyyy-MM-dd HH:mm}\n\n");

            // 块清单
            sb.Append("## 程序块\n\n");
            sb.Append("| 块名 | 类型 | 编号 | 语言 |\n");
            sb.Append("|---|---|---|---|\n");
            foreach (var b in ProjectReader.ListBlocks(plc))
                sb.Append($"| {b.Name} | {b.Type} | {b.Number} | {b.Language} |\n");

            // 变量表全量
            sb.Append("\n## 变量表\n\n");
            sb.Append("| 表 | 变量名 | 数据类型 | 地址 |\n");
            sb.Append("|---|---|---|---|\n");
            var tagCount = 0;
            try
            {
                foreach (var table in plc.TagTableGroup.TagTables)
                {
                    foreach (var tag in table.Tags)
                    {
                        sb.Append($"| {table.Name} | {tag.Name} | {tag.DataTypeName} | {tag.LogicalAddress} |\n");
                        tagCount++;
                    }
                }
            }
            catch { }
            if (tagCount == 0) sb.Append("_(无变量表标签)_\n");

            sb.Append("\n---\n");
            sb.Append("_报告结束_");

            Directory.CreateDirectory(Path.GetFullPath(outDir));
            var path = Path.Combine(Path.GetFullPath(outDir), projectName + "_report.md");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Console.Error.WriteLine($"[info] 报告已生成: {path}");
            return path;
        }
    }
}
