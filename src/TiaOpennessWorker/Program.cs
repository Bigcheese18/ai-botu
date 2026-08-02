using System;
using System.Collections.Generic;
using System.IO;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;

namespace TiaOpennessWorker
{
    /// <summary>
    /// TIA Portal Openness Worker 命令行入口。
    ///
    /// 用法:
    ///   TiaOpennessWorker run     [--scl <file>] [--out <dir>] [--openness-dir <dir>]
    ///   TiaOpennessWorker import  --project <ap21> --scl <file> [--openness-dir <dir>]
    ///   TiaOpennessWorker compile --project <ap21> [--openness-dir <dir>]
    ///   TiaOpennessWorker open    --project <ap21> [--openness-dir <dir>]
    ///
    /// 退出码: 0=成功且 0 编译错误; 1=编译有错误; 2=运行失败/参数错误。
    /// 进度日志输出到 stderr,结果 JSON 输出到 stdout(供上游编排层/后续 HTTP API 消费)。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // 统一 UTF-8 输入输出:子进程(stdin/stdout 是管道)没有控制台句柄时
            // Console.OutputEncoding 赋值会抛异常,直接包装原始标准流最可靠
            // (UTF8Encoding(false) 不带 BOM,避免污染 JSON 协议首行)
            var utf8 = new System.Text.UTF8Encoding(false);
            try { Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true }); } catch { }
            try { Console.SetError(new System.IO.StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true }); } catch { }
            try { Console.SetIn(new System.IO.StreamReader(Console.OpenStandardInput(), utf8)); } catch { }

            // 关键顺序:Main 自身不能引用任何 Siemens 类型(JIT 编译 Main 时就会加载程序集,
            // 那时 AssemblyResolve 尚未注册会 FileNotFound),必须先注册再分发到 RunCommand。
            var opts = ParseArgs(args);
            if (!AssemblyResolver.TryResolve(opts.Get("openness-dir"), out var resolveError))
            {
                Console.Error.WriteLine($"[error] {resolveError}");
                return 2;
            }
            return RunCommand(args, opts);
        }

        private static int RunCommand(string[] args, Dictionary<string, string> opts)
        {
            try
            {
                if (args.Length == 0)
                {
                    PrintUsage();
                    return 2;
                }

                var command = args[0];

                switch (command)
                {
                    case "run":    return RunFullLoop(opts);
                    case "import": return ImportIntoProject(opts);
                    case "compile":return CompileProject(opts);
                    case "open":   return OpenProjectAndList(opts);
                    case "serve":  return ServeMode.Run();
                    case "add-tags": return AddTagsCommand(opts);
                    case "import-block": return ImportBlockCommand(opts);
                    case "gen-lad": return GenLadCommand(opts);
                    default:
                        Console.Error.WriteLine($"[error] 未知命令: {command}");
                        PrintUsage();
                        return 2;
                }
            }
            catch (EngineeringSecurityException)
            {
                // 文章避坑:连续拒绝 3 次 Openness firewall 授权会抛此异常
                Console.Error.WriteLine("[error] Openness firewall 授权被拒绝。请在 TIA Portal 弹窗中选择\"始终允许\"后重试。");
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[error] {ProjectOperations.Unwrap(ex).Message}");
                return 2;
            }
        }

        // ---------- 命令实现 ----------

        /// <summary>run:新建工程 → 加 CPU → 导入 SCL → 生成块 → 编译 → 保存归档(完整闭环)。</summary>
        private static int RunFullLoop(Dictionary<string, string> opts)
        {
            var scl = opts.Get("scl") ?? Path.Combine(Directory.GetCurrentDirectory(), "samples", "GoodSample.scl");
            var outDir = opts.Get("out") ?? Path.Combine(Directory.GetCurrentDirectory(), "output");
            var projectName = "TiaOpennessTest_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

            using (var manager = new TiaPortalManager())
            {
                manager.Connect(TiaPortalMode.WithUserInterface, preferFresh: true);

                var projectDir = new DirectoryInfo(Path.Combine(outDir, "projects"));
                var project = ProjectOperations.CreateTestProject(manager.TiaPortal, projectName, projectDir);
                try
                {
                    var plcSoftware = ProjectOperations.GetPlcSoftware((Device)project.Devices[0]);

                    BlockImporter.ImportAndGenerate(plcSoftware, scl);
                    var report = CompileDiagnostics.Compile(plcSoftware);
                    var files = ProjectOperations.SaveAndArchive(project, outDir, projectName);

                    var json = new JsonWriter()
                        .BeginObject()
                            .Property("command", "run")
                            .Property("state", report.CompilationState)
                            .Property("errors", report.Errors)
                            .Property("warnings", report.Warnings)
                            .BeginArray("messages");
                    foreach (var m in report.Messages) WriteMessage(json, m);
                    json.EndArray()
                            .Property("projectFile", files.Item1)
                            .Property("archiveFile", files.Item2)
                        .EndObject();
                    Console.WriteLine(json.ToString());

                    return report.Errors > 0 ? 1 : 0;
                }
                finally
                {
                    // 显式关闭工程,否则 TIA 在 Dispose 时可能不退出、锁住工程文件
                    try { project.Close(); } catch { }
                }
            }
        }

        /// <summary>import:向已有工程导入 SCL 并编译保存。</summary>
        private static int ImportIntoProject(Dictionary<string, string> opts)
        {
            var projectFile = opts.Require("project");
            var scl = opts.Require("scl");

            using (var manager = new TiaPortalManager())
            {
                manager.Connect(TiaPortalMode.WithoutUserInterface);
                var project = ProjectOperations.OpenProject(manager.TiaPortal, projectFile);
                var plcSoftware = FindFirstPlcSoftware(project);

                BlockImporter.ImportAndGenerate(plcSoftware, scl);
                var report = CompileDiagnostics.Compile(plcSoftware);
                project.Save();
                Console.Error.WriteLine("[info] 工程已保存");

                var json = new JsonWriter()
                    .BeginObject()
                        .Property("command", "import")
                        .Property("state", report.CompilationState)
                        .Property("errors", report.Errors)
                        .Property("warnings", report.Warnings)
                        .BeginArray("messages");
                foreach (var m in report.Messages) WriteMessage(json, m);
                json.EndArray()
                        .Property("projectFile", projectFile)
                    .EndObject();
                Console.WriteLine(json.ToString());

                return report.Errors > 0 ? 1 : 0;
            }
        }

        /// <summary>compile:仅编译已有工程并导出诊断。</summary>
        private static int CompileProject(Dictionary<string, string> opts)
        {
            var projectFile = opts.Require("project");

            using (var manager = new TiaPortalManager())
            {
                manager.Connect(TiaPortalMode.WithoutUserInterface);
                var project = ProjectOperations.OpenProject(manager.TiaPortal, projectFile);
                var plcSoftware = FindFirstPlcSoftware(project);

                var report = CompileDiagnostics.Compile(plcSoftware);

                var json = new JsonWriter()
                    .BeginObject()
                        .Property("command", "compile")
                        .Property("state", report.CompilationState)
                        .Property("errors", report.Errors)
                        .Property("warnings", report.Warnings)
                        .BeginArray("messages");
                foreach (var m in report.Messages) WriteMessage(json, m);
                json.EndArray()
                        .Property("projectFile", projectFile)
                    .EndObject();
                Console.WriteLine(json.ToString());

                return report.Errors > 0 ? 1 : 0;
            }
        }

        /// <summary>add-tags:向已有工程批量添加变量表标签(命令行验证用,serve 模式内也有同款能力)。</summary>
        private static int AddTagsCommand(Dictionary<string, string> opts)
        {
            var projectFile = opts.Require("project");
            var tagsFile = opts.Require("tags-file");
            var table = opts.Get("table") ?? "TagTable_1";

            if (!File.Exists(tagsFile)) throw new FileNotFoundException($"标签文件不存在: {tagsFile}");
            var root = JsonParser.Parse(File.ReadAllText(tagsFile));
            if (!(root is List<object> list))
                throw new InvalidOperationException("tags 文件必须是 JSON 数组,如 [{\"name\":\"Motor\",\"dataType\":\"Bool\",\"address\":\"I0.0\"}]");

            var tags = new List<TagSpec>();
            foreach (var item in list)
            {
                if (!(item is Dictionary<string, object> o))
                    throw new InvalidOperationException("tags 元素必须是对象 {name, dataType, address}");
                tags.Add(new TagSpec
                {
                    Name = JsonParser.GetString(o, "name"),
                    DataType = JsonParser.GetString(o, "dataType"),
                    Address = JsonParser.GetString(o, "address"),
                });
            }

            using (var manager = new TiaPortalManager())
            {
                manager.Connect(TiaPortalMode.WithoutUserInterface);
                var project = ProjectOperations.OpenProject(manager.TiaPortal, projectFile);
                var plcSoftware = FindFirstPlcSoftware(project);
                var created = TagOperations.AddTags(plcSoftware, table, tags);
                project.Save();
                Console.Error.WriteLine("[info] 工程已保存");

                var json = new JsonWriter().BeginObject()
                    .Property("command", "add-tags")
                    .Property("table", table)
                    .Property("requested", tags.Count)
                    .Property("created", created.Count)
                    .BeginArray("tags");
                foreach (var t in created)
                {
                    json.BeginObject()
                        .Property("name", t.Name)
                        .Property("dataType", t.DataTypeName)
                        .Property("address", t.LogicalAddress)
                    .EndObject();
                }
                json.EndArray().EndObject();
                Console.WriteLine(json.ToString());
                return 0;
            }
        }

        /// <summary>import-block:向已有工程导入 SimaticML XML 块(LAD 梯形图)。</summary>
        private static int ImportBlockCommand(Dictionary<string, string> opts)
        {
            var projectFile = opts.Require("project");
            var xml = opts.Require("xml");

            using (var manager = new TiaPortalManager())
            {
                manager.Connect(TiaPortalMode.WithoutUserInterface);
                var project = ProjectOperations.OpenProject(manager.TiaPortal, projectFile);
                var plcSoftware = FindFirstPlcSoftware(project);
                var count = LadBlockImporter.ImportXml(plcSoftware, xml);
                project.Save();
                Console.Error.WriteLine("[info] 工程已保存");

                var json = new JsonWriter().BeginObject()
                    .Property("command", "import-block")
                    .Property("xmlFile", xml)
                    .Property("imported", count)
                    .EndObject();
                Console.WriteLine(json.ToString());
                return 0;
            }
        }

        /// <summary>gen-lad:根据 JSON 规格生成 LAD 梯形图 SimaticML XML(不连 TIA,纯文本)。</summary>
        private static int GenLadCommand(Dictionary<string, string> opts)
        {
            var specFile = opts.Require("spec");
            var outDir = opts.Get("out") ?? Path.Combine(Directory.GetCurrentDirectory(), "lad");
            if (!File.Exists(specFile)) throw new FileNotFoundException($"规格文件不存在: {specFile}");
            var root = JsonParser.Parse(File.ReadAllText(specFile));
            if (!(root is Dictionary<string, object> spec))
                throw new InvalidOperationException("spec 文件必须是 JSON 对象,如 {\"type\":\"FB\",\"name\":\"FB_X\",\"networks\":[...]}");
            var ladSpec = LadComposer.ParseSpec(spec);
            var xml = LadComposer.BuildXml(ladSpec);
            Directory.CreateDirectory(Path.GetFullPath(outDir));
            var xmlPath = Path.Combine(Path.GetFullPath(outDir), ladSpec.Name + ".xml");
            File.WriteAllText(xmlPath, xml, new System.Text.UTF8Encoding(true));

            var json = new JsonWriter().BeginObject()
                .Property("command", "gen-lad")
                .Property("blockName", ladSpec.Name)
                .Property("xmlFile", xmlPath)
                .EndObject();
            Console.WriteLine(json.ToString());
            return 0;
        }

        /// <summary>open:打开工程并列出 PLC 设备(连通性验证用)。</summary>
        private static int OpenProjectAndList(Dictionary<string, string> opts)
        {
            var projectFile = opts.Require("project");

            using (var manager = new TiaPortalManager())
            {
                manager.Connect(TiaPortalMode.WithoutUserInterface);
                var project = ProjectOperations.OpenProject(manager.TiaPortal, projectFile);

                var json = new JsonWriter().BeginObject()
                    .Property("command", "open")
                    .Property("projectFile", projectFile)
                    .BeginArray("devices");
                foreach (var device in project.Devices)
                {
                    json.BeginObject()
                        .Property("name", device.Name)
                        .Property("type", (device.GetAttribute("Type") ?? "").ToString())
                        .Property("number", (device.GetAttribute("Number") ?? "").ToString())
                    .EndObject();
                }
                json.EndArray().EndObject();
                Console.WriteLine(json.ToString());
                return 0;
            }
        }

        // ---------- 工具 ----------

        private static void WriteMessage(JsonWriter json, DiagnosticMessage m)
        {
            json.BeginObject()
                    .Property("state", m.State)
                    .Property("block", m.Block)
                    .Property("description", m.Description)
                .EndObject();
        }

        private static PlcSoftware FindFirstPlcSoftware(Project project)
        {
            foreach (var device in project.Devices)
            {
                var plcSoftware = ProjectOperations.GetPlcSoftware(device);
                if (plcSoftware != null) return plcSoftware;
            }
            throw new InvalidOperationException("工程中没有 PLC 设备(PlcSoftware)");
        }

        private static void PrintUsage()
        {
            Console.Error.WriteLine(
                "用法:\n" +
                "  TiaOpennessWorker run     [--scl <file>] [--out <dir>] [--openness-dir <dir>]\n" +
                "  TiaOpennessWorker import  --project <ap21> --scl <file> [--openness-dir <dir>]\n" +
                "  TiaOpennessWorker compile --project <ap21> [--openness-dir <dir>]\n" +
                "  TiaOpennessWorker open    --project <ap21> [--openness-dir <dir>]\n" +
                "\n退出码: 0=成功且无编译错误; 1=编译有错误; 2=失败/参数错误");
        }

        private static Dictionary<string, string> ParseArgs(string[] args)
        {
            var opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--") && i + 1 < args.Length)
                    opts[a.Substring(2)] = args[++i];
                else if (a.StartsWith("--"))
                    opts[a.Substring(2)] = null;
            }
            return opts;
        }
    }

    internal static class OptsExtensions
    {
        public static string Get(this Dictionary<string, string> opts, string key)
            => opts.TryGetValue(key, out var v) ? v : null;

        public static string Require(this Dictionary<string, string> opts, string key)
        {
            var v = opts.Get(key);
            if (string.IsNullOrEmpty(v))
                throw new InvalidOperationException($"缺少必需参数: --{key}");
            return v;
        }
    }
}
