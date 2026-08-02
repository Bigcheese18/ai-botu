using System;
using System.Collections.Generic;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Tags;

namespace TiaOpennessWorker
{
    /// <summary>
    /// 变量表操作:建表、加标签。对应文章能力:PLC Tag 进入 TIA。
    /// V21 API:plcSoftware.TagTableGroup.TagTables.Create(name) 建表,
    /// table.Tags.Create(name, dataTypeName, logicalAddress) 加标签。
    /// </summary>
    public static class TagOperations
    {
        /// <summary>建变量表(已存在则复用),返回表对象。</summary>
        public static PlcTagTable CreateTagTable(PlcSoftware plcSoftware, string tableName)
        {
            var existing = plcSoftware.TagTableGroup.TagTables.Find(tableName);
            if (existing != null)
            {
                Console.Error.WriteLine($"[info] 变量表已存在,复用: {tableName}");
                return existing;
            }
            var table = plcSoftware.TagTableGroup.TagTables.Create(tableName);
            Console.Error.WriteLine($"[info] 已创建变量表: {tableName}");
            return table;
        }

        /// <summary>批量添加标签,返回实际添加的标签列表。</summary>
        public static List<PlcTag> AddTags(PlcSoftware plcSoftware, string tableName, IEnumerable<TagSpec> tags)
        {
            var table = CreateTagTable(plcSoftware, tableName);
            var created = new List<PlcTag>();
            foreach (var spec in tags)
            {
                if (string.IsNullOrWhiteSpace(spec.Name))
                    throw new InvalidOperationException("标签名不能为空");
                if (string.IsNullOrWhiteSpace(spec.DataType))
                    throw new InvalidOperationException($"标签 {spec.Name} 缺少数据类型(如 Bool/Int/Real)");

                try
                {
                    var tag = table.Tags.Create(spec.Name, spec.DataType, spec.Address ?? "");
                    created.Add(tag);
                    Console.Error.WriteLine($"[info] 已添加标签: {spec.Name} ({spec.DataType}{(string.IsNullOrEmpty(spec.Address) ? "" : " @" + spec.Address)})");
                }
                catch (Exception ex)
                {
                    // 单个标签失败不中断批量(如重名),记入错误后继续
                    Console.Error.WriteLine($"[warn] 标签 {spec.Name} 添加失败: {ProjectOperations.Unwrap(ex).Message}");
                }
            }
            return created;
        }
    }

    /// <summary>标签定义。</summary>
    public sealed class TagSpec
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public string Address { get; set; }
    }
}
