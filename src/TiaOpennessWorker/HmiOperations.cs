using System;
using System.IO;
using System.Linq;
using System.Text;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Communication;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.CommunicationConnections;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;

namespace TiaOpennessWorker
{
    /// <summary>
    /// HMI(WinCC Comfort)操作:添加 HMI 设备、取 HmiTarget、导入画面 XML。
    /// V21 API:HMI 设备经 HardwareCatalog 添加;软件对象走 SoftwareContainer
    /// (与 PlcSoftware 同模式,Software as HmiTarget);画面经 ScreenComposition.Import。
    /// </summary>
    public static class HmiOperations
    {
        // HMI 面板订货号搜索词(按优先级)。本机装了 WinCC Comfort,应可搜到 KTP 系列。
        private static readonly string[] HmiOrderNumberPatterns =
        {
            "6AV2123-2GB03-0AX0", // KTP700 Basic PN
            "KTP700", "KTP900", "KTP400",
        };

        private static readonly string[] HmiTypeNames = { "KTP700 Basic PN", "HMI_1" };

        /// <summary>向工程添加 HMI 设备,返回设备。</summary>
        public static Device AddHmiDevice(Project project)
        {
            var tiaPortal = project.Parent as TiaPortal;
            Exception lastError = null;
            foreach (var pattern in HmiOrderNumberPatterns)
            {
                string typeIdentifier = null;
                try
                {
                    var entries = tiaPortal.HardwareCatalog.Find(pattern);
                    if (entries.Count > 0)
                        typeIdentifier = (entries[entries.Count - 1].GetAttribute("TypeIdentifier") ?? "").ToString();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[warn] 目录查询 {pattern} 失败: {ProjectOperations.Unwrap(ex).Message}");
                }

                foreach (var typeName in HmiTypeNames)
                {
                    try
                    {
                        var device = project.Devices.CreateWithItem(
                            typeIdentifier ?? pattern, "HMI_1", typeName);
                        Console.Error.WriteLine($"[info] 已添加 HMI {typeIdentifier ?? pattern} -> 设备 {device.Name}");
                        return device;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        Console.Error.WriteLine($"[warn] 创建 HMI {typeIdentifier ?? pattern}/{typeName} 失败: {ProjectOperations.Unwrap(ex).Message}");
                    }
                }
            }
            throw new InvalidOperationException($"无法添加 HMI 设备(已尝试 {string.Join(", ", HmiOrderNumberPatterns)}): {ProjectOperations.Unwrap(lastError)?.Message}");
        }

        /// <summary>取 HMI 设备的软件对象(HmiTarget)。</summary>
        public static HmiTarget GetHmiTarget(Device device)
        {
            foreach (var deviceItem in device.DeviceItems)
            {
                var container = deviceItem.GetService<SoftwareContainer>();
                if (container?.Software is HmiTarget hmi)
                    return hmi;
            }
            throw new InvalidOperationException($"无法从设备 {device.Name} 获取 HmiTarget 对象");
        }

        /// <summary>
        /// 建立 HMI → PLC 的连接:V21 Hmi 连接组合无 Create,只能 XML 导入
        /// (Hmi.Connection.Connection)。已有同名连接则复用。返回连接名。
        /// </summary>
        public static string AddHmiConnection(Project project, HmiTarget hmiTarget, string xmlFile)
        {
            foreach (var existing in hmiTarget.Connections)
            {
                if (existing.Name == "HMI_Connection_1") return existing.Name;
            }
            var file = new FileInfo(xmlFile);
            if (!file.Exists) throw new FileNotFoundException($"连接 XML 不存在: {xmlFile}");
            var imported = hmiTarget.Connections.Import(EnsureBom(file), ImportOptions.Override);
            var count = imported?.Count ?? 0;
            Console.Error.WriteLine($"[info] 已导入 {count} 个 HMI 连接: {xmlFile}");
            return "HMI_Connection_1";
        }

        /// <summary>导入 HMI 变量表 XML(Hmi.Tag.TagTable),返回导入的变量数。</summary>
        public static int ImportHmiTagTable(HmiTarget hmiTarget, string xmlFile)
        {
            var file = new FileInfo(xmlFile);
            if (!file.Exists) throw new FileNotFoundException($"变量表 XML 不存在: {xmlFile}");
            var imported = hmiTarget.TagFolder.TagTables.Import(EnsureBom(file), ImportOptions.Override);
            var count = imported?.Count ?? 0;
            Console.Error.WriteLine($"[info] 已导入 {count} 个 HMI 变量表: {xmlFile}");
            return count;
        }

        /// <summary>导入画面 XML(SimaticML),返回导入的画面数量。</summary>
        public static int ImportScreen(HmiTarget hmiTarget, string xmlFile)
        {
            var file = new FileInfo(xmlFile);
            if (!file.Exists) throw new FileNotFoundException($"画面 XML 不存在: {xmlFile}");
            var imported = hmiTarget.ScreenFolder.Screens.Import(EnsureBom(file), ImportOptions.Override);
            var count = imported?.Count ?? 0;
            Console.Error.WriteLine($"[info] 已导入 {count} 个画面: {xmlFile}");
            return count;
        }

        /// <summary>无 BOM 时补写 UTF-8 BOM(TIA 靠 BOM 识别编码,否则中文乱码)。</summary>
        private static FileInfo EnsureBom(FileInfo file)
        {
            var bytes = File.ReadAllBytes(file.FullName);
            var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            if (hasBom) return file;
            var text = File.ReadAllText(file.FullName, Encoding.UTF8);
            var tmp = Path.Combine(Path.GetTempPath(), "tia_hmi_import_" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(tmp, text, new UTF8Encoding(true));
            return new FileInfo(tmp);
        }
    }
}
