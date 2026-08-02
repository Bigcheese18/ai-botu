using System;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;

namespace TiaOpennessWorker
{
    /// <summary>
    /// 工程操作:新建带 S7-1500 CPU 的测试工程、打开已有工程、取 PLC 软件、保存归档。
    /// 对应文章能力:OpenProject / SaveAndArchive。V21 API:Create(DirectoryInfo, name)。
    /// </summary>
    public static class ProjectOperations
    {
        // S7-1500 CPU 订货号搜索词(按优先级排列)。
        // 注意:V21 的 TypeIdentifier 是 "OrderNumber:6ES7 511-1AK02-0AB0/V2.9" 格式
        // (带前缀、订货号含空格、带固件版本),硬编码纯订货号会报 Unknown TypeIdentifer,
        // 必须通过 HardwareCatalog.Find 获取真实 TypeIdentifier。
        private static readonly string[] CpuOrderNumberPatterns =
        {
            "6ES7511-1AK02-0AB0", // CPU 1511-1 PN(FW 2.x)
            "6ES7511-1AK00-0AB0",
            "6ES7511-1AL03-0AB0",
            "6ES7511-1CK00-0AB0",
        };

        // 设备类型名候选(V21 CreateWithItem 第 3 参)
        private static readonly string[] CpuTypeNames = { "CPU 1511-1 PN", "Device_1" };

        /// <summary>在指定目录新建测试工程并添加 S7-1500 CPU,返回工程。</summary>
        public static Project CreateTestProject(TiaPortal tiaPortal, string projectName, DirectoryInfo projectDir)
        {
            // Openness 要求绝对路径且目录已存在:GetFullPath 解析 + 预创建
            var absoluteDir = new DirectoryInfo(Path.GetFullPath(projectDir.FullName));
            if (!absoluteDir.Exists) absoluteDir.Create();
            Console.Error.WriteLine($"[info] 工程目录(绝对): {absoluteDir.FullName}");
            var project = tiaPortal.Projects.Create(absoluteDir, projectName);
            Console.Error.WriteLine($"[info] 已创建工程: {absoluteDir.FullName}\\{projectName}");

            AddCpuDevice(project);
            return project;
        }

        /// <summary>向工程添加 S7-1500 CPU(供新建工程与用户 Attach 场景共用)。</summary>
        public static Device AddCpuDevice(Project project)
        {
            var tiaPortal = project.Parent as TiaPortal;
            Exception lastError = null;
            foreach (var pattern in CpuOrderNumberPatterns)
            {
                // 通过硬件目录 Find 获取真实 TypeIdentifier(取最新固件版本)
                string typeIdentifier = null;
                try
                {
                    var entries = tiaPortal.HardwareCatalog.Find(pattern);
                    if (entries.Count > 0)
                        typeIdentifier = (entries[entries.Count - 1].GetAttribute("TypeIdentifier") ?? "").ToString();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[warn] 目录查询 {pattern} 失败: {Unwrap(ex).Message}");
                }

                foreach (var typeName in CpuTypeNames)
                {
                    try
                    {
                        var device = project.Devices.CreateWithItem(
                            typeIdentifier ?? pattern, "PLC_1", typeName);
                        Console.Error.WriteLine($"[info] 已添加 CPU {typeIdentifier ?? pattern} -> 设备 {device.Name}");
                        return device;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        Console.Error.WriteLine($"[warn] 创建设备 {typeIdentifier ?? pattern}/{typeName} 失败: {Unwrap(ex).Message}");
                    }
                }
            }

            throw new InvalidOperationException(
                $"无法添加 S7-1500 CPU(已尝试 {string.Join(", ", CpuOrderNumberPatterns)}): {Unwrap(lastError)?.Message}");
        }

        /// <summary>打开已有工程文件(.ap21)。V21 API:ProjectComposition.Open(FileInfo)。</summary>
        public static Project OpenProject(TiaPortal tiaPortal, string projectFile)
        {
            var file = new FileInfo(projectFile);
            if (!file.Exists) throw new FileNotFoundException($"工程文件不存在: {projectFile}");
            var project = tiaPortal.Projects.Open(file);
            Console.Error.WriteLine($"[info] 已打开工程: {projectFile}");
            return project;
        }

        /// <summary>
        /// 取 PLC 设备的软件对象(导入/编译的入口)。
        /// V21 方式:DeviceItem 没有 PlcSoftware 属性,要通过 GetService&lt;SoftwareContainer&gt;
        /// 拿到软件容器再取 .Software(社区 V21 项目验证过的模式)。
        /// 注意:DeviceItems[0] 是机架(导轨_0),CPU 是子项,需遍历查找带 PLC 软件的那一个。
        /// </summary>
        public static PlcSoftware GetPlcSoftware(Device device)
        {
            foreach (var deviceItem in device.DeviceItems)
            {
                var container = deviceItem.GetService<SoftwareContainer>();
                if (container?.Software is PlcSoftware plcSoftware)
                    return plcSoftware;
            }
            throw new InvalidOperationException($"无法从设备 {device.Name} 获取 PlcSoftware 对象");
        }

        /// <summary>
        /// 保存工程并归档为 .zap21,返回实际生成的文件路径。
        /// V21 API:Save() 就地保存;Archive(DirectoryInfo, name, mode) 生成归档。
        /// 注意:Openness 要求绝对路径(相对路径会报错),统一 GetFullPath。
        /// </summary>
        public static Tuple<string, string> SaveAndArchive(Project project, string outputDir, string name)
        {
            var absoluteOut = Path.GetFullPath(outputDir);
            Directory.CreateDirectory(absoluteOut);
            project.Save();
            Console.Error.WriteLine("[info] 工程已保存");

            var archiveDir = new DirectoryInfo(absoluteOut);
            // V21 Archive 拒绝覆盖,且落盘名 = 传入名(不自动补扩展名):
            // 统一用 {name}_arch.zap21,归档前清理全部旧归档(AI 修复循环会重复保存)
            var archiveName = name + "_arch.zap21";
            foreach (var old in Directory.GetFiles(absoluteOut, name + "_arch*"))
            {
                try { File.Delete(old); } catch { }
            }
            project.Archive(archiveDir, archiveName, ProjectArchivationMode.Compressed);

            // 实际路径以落盘为准:V21 的 Project.Path 直接返回工程文件(FileInfo),
            // 打开已有工程时最可靠;不存在时回退到本 Worker 的命名规则 outDir/projects/<name>。
            var projectFile = project.Path;
            var ap21 = (projectFile != null && projectFile.Exists)
                           ? projectFile.FullName
                           : Directory.GetFiles(absoluteOut, "*.ap21", SearchOption.AllDirectories)
                                     .FirstOrDefault();
            if (ap21 == null)
                ap21 = Path.Combine(Path.GetFullPath(Path.Combine(absoluteOut, "projects", name)), name + ".ap21");
            var zap21 = Path.Combine(absoluteOut, archiveName);
            if (!File.Exists(zap21))
                zap21 = Directory.GetFiles(absoluteOut, name + "_arch*", SearchOption.TopDirectoryOnly).FirstOrDefault()
                        ?? zap21;
            Console.Error.WriteLine($"[info] 工程文件: {ap21}");
            Console.Error.WriteLine($"[info] 归档文件: {zap21}");
            return Tuple.Create(ap21, zap21);
        }

        /// <summary>解包西门子异常链,取最内层真实错误消息。</summary>
        internal static Exception Unwrap(Exception ex)
        {
            var inner = ex;
            while (inner.InnerException != null) inner = inner.InnerException;
            return inner;
        }
    }
}
