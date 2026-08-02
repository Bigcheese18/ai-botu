using System;
using System.Collections.Generic;
using System.IO;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;

namespace TiaOpennessWorker
{
    /// <summary>
    /// 读取/导出工程内容:块列表、块导出(SCL 文本 / SimaticML XML)、变量表全量。
    /// 用于"AI 读懂用户已有程序":导出后直接阅读 SCL/XML 文本。
    /// </summary>
    public static class ProjectReader
    {
        public sealed class BlockInfo
        {
            public string Name;
            public string Type;       // FB/FC/OB/DB...
            public int Number;
            public string Language;   // SCL/LAD/FBD...
        }

        /// <summary>递归收集全部程序块(含用户组)。</summary>
        public static List<BlockInfo> ListBlocks(PlcSoftware plc)
        {
            var result = new List<BlockInfo>();
            CollectBlocks(plc.BlockGroup, result);
            return result;
        }

        private static void CollectBlocks(PlcBlockGroup group, List<BlockInfo> result)
        {
            foreach (var b in group.Blocks)
            {
                result.Add(new BlockInfo
                {
                    Name = b.Name,
                    Number = b.Number,
                    Language = b.ProgrammingLanguage.ToString(),
                    Type = b.GetType().Name.Replace("Plc", "").Replace("Block", ""),
                });
            }
            foreach (var g in group.Groups) CollectBlocks(g, result);
        }

        /// <summary>
        /// 导出块到目录。SCL 块 → .scl 文本(可直接阅读);其他语言 → .xml(SimaticML)。
        /// 返回导出文件路径。
        /// </summary>
        public static string ExportBlock(PlcBlock block, string dir)
        {
            Directory.CreateDirectory(dir);
            var isScl = block.ProgrammingLanguage == ProgrammingLanguage.SCL;
            var path = Path.Combine(dir, block.Name + (isScl ? ".scl" : ".xml"));
            block.Export(new FileInfo(path), ExportOptions.WithDefaults);
            return path;
        }

        /// <summary>把工程内容汇总为 JSON(供 serve read-project 命令直接返回)。</summary>
        public static void WriteProjectJson(PlcSoftware plc, JsonWriter json, string exportDir, out List<string> exported)
        {
            exported = new List<string>();

            json.BeginArray("blocks");
            foreach (var b in ListBlocks(plc))
            {
                json.BeginObject()
                    .Property("name", b.Name)
                    .Property("type", b.Type)
                    .Property("number", b.Number)
                    .Property("language", b.Language)
                .EndObject();
            }
            json.EndArray();

            // 变量表全量(名称/类型/地址)
            json.BeginArray("tags");
            try
            {
                foreach (var table in plc.TagTableGroup.TagTables)
                {
                    foreach (var tag in table.Tags)
                    {
                        json.BeginObject()
                            .Property("table", table.Name)
                            .Property("name", tag.Name)
                            .Property("dataType", tag.DataTypeName)
                            .Property("address", tag.LogicalAddress)
                        .EndObject();
                    }
                }
            }
            catch { } // 部分表读取失败不影响整体
            json.EndArray();

            // 导出全部块(按语言选 .scl/.xml)
            if (!string.IsNullOrEmpty(exportDir))
            {
                try
                {
                    foreach (var b in plc.BlockGroup.Blocks)
                    {
                        try { exported.Add(ExportBlock(b, exportDir)); }
                        catch (Exception ex) { Console.Error.WriteLine($"[warn] 导出 {b.Name} 失败: {Unwrap(ex).Message}"); }
                    }
                }
                catch (Exception ex) { Console.Error.WriteLine($"[warn] 导出块失败: {Unwrap(ex).Message}"); }
            }
        }

        private static Exception Unwrap(Exception ex)
        {
            var inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            return inner;
        }
    }
}
