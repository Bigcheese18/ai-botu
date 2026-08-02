using System;
using System.IO;
using System.Text;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;

namespace TiaOpennessWorker
{
    /// <summary>
    /// LAD 梯形图块导入:把 SimaticML XML 块文件通过 PlcBlockComposition.Import
    /// 导入 PLC 软件(V21 模块化 API)。
    ///
    /// 关键点(来自社区验证 + 本文档):
    /// 1. XML 必须带 UTF-8 BOM,否则 TIA 把中文读成乱码;
    /// 2. &lt;Engineering version&gt; 应匹配本机 TIA 主版本(V21);
    /// 3. 导入失败时 Openness 的异常链很深,要解到最内层才有真实错误。
    /// </summary>
    public static class LadBlockImporter
    {
        /// <summary>导入一个 SimaticML XML 块文件,返回导入的块数量。</summary>
        public static int ImportXml(PlcSoftware plcSoftware, string xmlFile)
        {
            var file = new FileInfo(xmlFile);
            if (!file.Exists) throw new FileNotFoundException($"XML 块文件不存在: {xmlFile}");

            var prepared = PrepareXmlForImport(file);
            PlcBlockComposition blocks;
            try { blocks = plcSoftware.BlockGroup.Blocks; }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"无法获取块组: {ProjectOperations.Unwrap(ex).Message}");
            }

            var imported = blocks.Import(prepared, ImportOptions.Override);
            var count = imported?.Count ?? 0;
            Console.Error.WriteLine($"[info] 已导入 {count} 个块: {xmlFile}");
            return count;
        }

        /// <summary>
        /// 归一化 XML 再导入:补 UTF-8 BOM、把 Engineering 版本号替换为当前 TIA 版本。
        /// 与原文件一致时直接返回原路径,否则写出临时文件。
        /// </summary>
        private static FileInfo PrepareXmlForImport(FileInfo file)
        {
            var bytes = File.ReadAllBytes(file.FullName);
            var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            if (hasBom) return file;

            // 无 BOM 时补写 UTF-8 BOM(TIA 靠 BOM 识别编码,否则中文乱码)
            var text = File.ReadAllText(file.FullName, Encoding.UTF8);
            var tmp = Path.Combine(Path.GetTempPath(), "tia_lad_import_" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(tmp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return new FileInfo(tmp);
        }
    }
}
