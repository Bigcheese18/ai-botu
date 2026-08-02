using System;
using System.IO;
using System.Text;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.ExternalSources;

namespace TiaOpennessWorker
{
    /// <summary>
    /// 导入 SCL 块源并生成块。对应文章能力:ImportBlocks。
    /// V21 API:外部 SCL 源走 PlcExternalSource.CreateFromFile + GenerateBlocksFromSource。
    /// 关键:导入成功不等于工程正确 —— SCL 语法错误在 GenerateBlocksFromSource(生成块)时暴露,
    /// 这也是将来"AI 修复回路"拿到错误反馈的位置。
    /// </summary>
    public static class BlockImporter
    {
        /// <summary>
        /// 创建外部 SCL 源并生成块。生成失败(语法错误)抛出 BlockGenerationException,内含编译器错误描述。
        /// </summary>
        public static void ImportAndGenerate(PlcSoftware plcSoftware, string sclFile)
        {
            var file = new FileInfo(sclFile);
            if (!file.Exists) throw new FileNotFoundException($"SCL 文件不存在: {sclFile}");

            var sourceName = Path.GetFileNameWithoutExtension(sclFile);

            // AI 修复循环会重复导入同名 SCL(失败后修复再导):先删掉同名旧源,否则 "name is not unique"
            try
            {
                var existing = plcSoftware.ExternalSourceGroup.ExternalSources.Find(sourceName);
                if (existing != null)
                {
                    existing.Delete();
                    Console.Error.WriteLine($"[info] 已删除旧外部源: {sourceName}");
                }
            }
            catch { } // 旧 API 无 Find 或删除失败时忽略

            // 中文注释支持:TIA 读 .scl 按 BOM 识别编码,UTF-8 无 BOM 的中文会被按 GBK 误解析
            // 报语法错。统一转成带 BOM 的 UTF-8 临时文件再导入。
            var text = File.ReadAllText(file.FullName, Encoding.UTF8);
            var prepared = Path.Combine(Path.GetTempPath(), "tia_scl_import_" + Guid.NewGuid().ToString("N") + ".scl");
            File.WriteAllText(prepared, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            var source = plcSoftware.ExternalSourceGroup.ExternalSources.CreateFromFile(sourceName, prepared);
            Console.Error.WriteLine($"[info] 已创建外部 SCL 源: {source.Name} ({file.FullName})");

            try
            {
                source.GenerateBlocksFromSource();
                Console.Error.WriteLine($"[info] 已从 SCL 源生成块: {source.Name}");
            }
            catch (Exception ex)
            {
                throw new BlockGenerationException(
                    "从 SCL 块源生成块失败(SCL 语法错误),详情: " + ProjectOperations.Unwrap(ex).Message, ex);
            }
        }
    }

    /// <summary>SCL 块源生成(语法检查)失败。</summary>
    public sealed class BlockGenerationException : Exception
    {
        public BlockGenerationException(string message, Exception inner) : base(message, inner) { }
    }
}
