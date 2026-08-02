using System;
using System.Collections.Generic;
using System.IO;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;

namespace TiaOpennessWorker
{
    /// <summary>
    /// serve 长驻模式:stdin 逐行读 JSON 请求,stdout 逐行回 JSON 响应。
    /// 避免 MCP/AI 每次调用都重启 TIA(单次启动 30-90 秒)。
    ///
    /// 两种场景:
    ///  1) 用户已打开 TIA Portal(带界面/工程)→ Attach 进用户实例,AI 直接写进
    ///     用户正在看的工程,界面实时可见(ready 响应带 "attach":true)。
    ///  2) 无运行实例 → 启动无界面实例,自建/自开工程(headless 自动化)。
    ///
    /// 协议(每行一个 JSON):
    ///   请求: {"id": 1, "cmd": "create-project", "args": {...}}
    ///   响应: {"id": 1, "ok": true, "result": {...}}
    ///     或 {"id": 1, "ok": false, "error": "..."}
    ///
    /// 命令:create-project / open-project / close-project / list-projects / use-project /
    ///       import-scl / import-block / gen-lad / compile / add-tags / list-tag-tables /
    ///       save-archive / shutdown
    /// 进度日志一律走 stderr,stdout 只输出协议(与命令行模式一致)。
    /// </summary>
    public static class ServeMode
    {
        /// <summary>本次会话是否为 Attach 用户实例(attach 时 shutdown 不关用户工程、Dispose 不杀用户 TIA)。</summary>
        private static bool _attached;

        /// <summary>当前会话的 HMI 目标(add-hmi 后设置,gen-hmi 使用)。</summary>
        private static Siemens.Engineering.Hmi.HmiTarget _hmiTarget;

        /// <summary>工程根目录(exe 在 src/TiaOpennessWorker/bin/&lt;cfg&gt;/net48/,向上定位仓库根)。</summary>
        private static string ProjectRoot =>
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));

        /// <summary>samples 目录(模板/示例)。</summary>
        private static string SamplesDir => Path.Combine(ProjectRoot, "samples");

        public static int Run()
        {
            using (var manager = new TiaPortalManager())
            {
                // 只支持用户场景:必须先手动打开博途窗口,worker 附着进用户实例干活,
                // 用户能亲眼看到每一步执行。不再自动启动无界面(headless)实例。
                var attached = false;
                try { attached = TiaPortal.GetProcesses().Count > 0; }
                catch { }
                _attached = attached;

                if (!attached)
                {
                    Console.Error.WriteLine("[error] 未检测到运行中的 TIA Portal:请先打开博途窗口(并载入工程),再连接");
                    return 2;
                }

                Console.Error.WriteLine("[info] 检测到已运行的 TIA Portal,执行 Attach(用户场景)");
                // Attach 一般几秒完成;兜底看门狗只退出进程,不杀用户 TIA
                var attachWatchdog = new System.Threading.Timer(_ =>
                {
                    Console.Error.WriteLine("[warn] Attach 超时(120s),退出进程(不动用户 TIA)");
                    Environment.Exit(3);
                }, null, TimeSpan.FromSeconds(120), System.Threading.Timeout.InfiniteTimeSpan);
                try { manager.Connect(TiaPortalMode.WithoutUserInterface); }
                finally { attachWatchdog.Dispose(); }

                Project project = null;
                PlcSoftware plcSoftware = null;
                var projectName = "";
                var outDir = "";

                // Attach 用户实例时:自动绑定用户当前打开的工程(有多个时用 use-project 切换)。
                // 工程可能还没有 PLC 设备,plcSoftware 留空,由 add-cpu 命令补齐。
                if (attached)
                {
                    foreach (var p in manager.TiaPortal.Projects)
                    {
                        if (!(p is Project proj)) continue;
                        project = proj;
                        projectName = proj.Name;
                        try { outDir = Path.GetDirectoryName(proj.Path.FullName); } catch { }
                        try { plcSoftware = FindFirstPlcSoftware(proj); }
                        catch { plcSoftware = null; Console.Error.WriteLine("[warn] 工程当前没有 PLC 设备,可用 add-cpu 添加"); }
                        Console.Error.WriteLine($"[info] 已绑定用户打开的工程: {projectName}");
                        break;
                    }
                }

                var ready = attached
                    ? "{\"ready\":true,\"attach\":true" + (project != null ? ",\"project\":" + JsonEscape(projectName) : "") + "}"
                    : "{\"ready\":true}";
                Console.Out.WriteLine(ready);
                Console.Out.Flush();

                string line;
                while ((line = Console.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var shutdown = false;
                    try
                    {
                        var response = Handle(line, manager, ref project, ref plcSoftware,
                            ref projectName, ref outDir, ref shutdown);
                        Console.Out.WriteLine(response);
                        Console.Out.Flush();
                    }
                    catch (Exception ex)
                    {
                        Console.Out.WriteLine("{\"ok\":false,\"error\":" + JsonEscape(ProjectOperations.Unwrap(ex).Message) + "}");
                        Console.Out.Flush();
                    }
                    if (shutdown) break;
                }
            }
            Console.Error.WriteLine("[info] serve 结束");
            return 0;
        }

        private static string Handle(string line, TiaPortalManager manager,
            ref Project project, ref PlcSoftware plcSoftware,
            ref string projectName, ref string outDir, ref bool shutdown)
        {
            var req = JsonParser.Parse(line) as Dictionary<string, object>
                      ?? throw new FormatException("请求必须是 JSON 对象");
            var id = req.TryGetValue("id", out var idRaw) && idRaw != null ? idRaw.ToString() : "0";
            var cmd = JsonParser.GetString(req, "cmd");
            var args = req.TryGetValue("args", out var a) && a is Dictionary<string, object> d ? d
                       : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            try
            {
                switch (cmd)
                {
                    case "create-project":
                    {
                        projectName = JsonParser.GetString(args, "projectName") ?? "TiaOpennessProj_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        outDir = JsonParser.GetString(args, "outDir") ?? Path.Combine(Directory.GetCurrentDirectory(), "output");
                        var projectDir = new DirectoryInfo(Path.Combine(Path.GetFullPath(outDir), "projects"));
                        project = ProjectOperations.CreateTestProject(manager.TiaPortal, projectName, projectDir);
                        plcSoftware = ProjectOperations.GetPlcSoftware((Device)project.Devices[0]);
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("projectName", projectName)
                            .Property("device", ((Device)project.Devices[0]).Name)
                            .EndObject());
                    }

                    case "connect":
                    {
                        // 用户场景:博途窗口已打开 → 绑定窗口中打开的工程(不重复打开文件)。
                        // 有多个工程时取第一个,可用 use-project 切换。
                        foreach (var p in manager.TiaPortal.Projects)
                        {
                            if (!(p is Project proj)) continue;
                            project = proj;
                            projectName = proj.Name;
                            try { outDir = Path.GetDirectoryName(proj.Path.FullName); } catch { }
                            try { plcSoftware = FindFirstPlcSoftware(proj); }
                            catch { plcSoftware = null; }
                            return Ok(id, new JsonWriter().BeginObject()
                                .Property("attached", true)
                                .Property("projectName", projectName)
                                .EndObject());
                        }
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("attached", true)
                            .Property("projectName", "")
                            .Property("note", "博途窗口里没有打开的工程:请先手动打开工程,再点连接")
                            .EndObject());
                    }

                    case "status":
                    {
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("ready", true)
                            .Property("attach", _attached)
                            .Property("project", projectName)
                            .EndObject());
                    }

                    case "open-project":
                    {
                        var file = JsonParser.GetString(args, "projectFile");
                        if (string.IsNullOrEmpty(file)) throw new InvalidOperationException("缺少参数: projectFile");
                        // Attach 用户实例时:同名工程已在窗口中打开 → 直接切换,
                        // 避免重复打开同一工程报"指定的路径无效"
                        var wantName = Path.GetFileNameWithoutExtension(file);
                        if (_attached && project != null && string.Equals(project.Name, wantName, StringComparison.OrdinalIgnoreCase))
                        {
                            return Ok(id, new JsonWriter().BeginObject()
                                .Property("projectFile", file)
                                .Property("projectName", project.Name)
                                .Property("alreadyOpen", true)
                                .Property("note", "工程已在博途窗口中打开,直接使用")
                                .EndObject());
                        }
                        project = ProjectOperations.OpenProject(manager.TiaPortal, file);
                        plcSoftware = FindFirstPlcSoftware(project);
                        projectName = Path.GetFileNameWithoutExtension(file);
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("projectFile", file)
                            .Property("projectName", projectName)
                            .EndObject());
                    }

                    case "close-project":
                    {
                        // 断开 = 解除 worker 与工程的绑定,绝不关闭用户窗口里打开的工程
                        // (attach 模式下 project.Close() 会直接关掉用户界面里的工程,不能调用)
                        if (project == null)
                            return Ok(id, new JsonWriter().BeginObject()
                                .Property("closed", false)
                                .Property("note", "当前没有打开的工程(已断开)")
                                .EndObject());
                        project = null;
                        plcSoftware = null;
                        projectName = "";
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("closed", true)
                            .EndObject());
                    }

                    case "import-scl":
                    {
                        RequireSoftware(plcSoftware);
                        var scl = JsonParser.GetString(args, "sclFile");
                        if (string.IsNullOrEmpty(scl)) throw new InvalidOperationException("缺少参数: sclFile");
                        BlockImporter.ImportAndGenerate(plcSoftware, scl);
                        TrySave(project);
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("sclFile", scl)
                            .Property("generated", true)
                            .EndObject());
                    }

                    case "compile":
                    {
                        RequireSoftware(plcSoftware);
                        var report = CompileDiagnostics.Compile(plcSoftware);
                        return Ok(id, CompileReportJson(report));
                    }

                    case "add-tags":
                    {
                        RequireSoftware(plcSoftware);
                        var table = JsonParser.GetString(args, "table") ?? "TagTable_1";
                        var tags = ParseTags(args);
                        var created = TagOperations.AddTags(plcSoftware, table, tags);
                        TrySave(project);
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("table", table)
                            .Property("created", created.Count)
                            .EndObject());
                    }

                    case "import-block":
                    {
                        RequireSoftware(plcSoftware);
                        var xml = JsonParser.GetString(args, "xmlFile");
                        if (string.IsNullOrEmpty(xml)) throw new InvalidOperationException("缺少参数: xmlFile");
                        var count = LadBlockImporter.ImportXml(plcSoftware, xml);
                        TrySave(project);
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("xmlFile", xml)
                            .Property("imported", count)
                            .EndObject());
                    }

                    case "gen-lad":
                    {
                        RequireSoftware(plcSoftware);
                        if (!(args.TryGetValue("spec", out var specRaw) && specRaw is Dictionary<string, object> spec))
                            throw new InvalidOperationException("缺少参数: spec(块规格对象)");
                        var ladOutDir = JsonParser.GetString(args, "outDir")
                                        ?? Path.Combine(Directory.GetCurrentDirectory(), "lad");
                        var ladSpec = LadComposer.ParseSpec(spec);
                        var xml = LadComposer.BuildXml(ladSpec);
                        Directory.CreateDirectory(Path.GetFullPath(ladOutDir));
                        var xmlPath = Path.Combine(Path.GetFullPath(ladOutDir), ladSpec.Name + ".xml");
                        File.WriteAllText(xmlPath, xml, new System.Text.UTF8Encoding(true));
                        var count = LadBlockImporter.ImportXml(plcSoftware, xmlPath);
                        TrySave(project);
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("blockName", ladSpec.Name)
                            .Property("xmlFile", xmlPath)
                            .Property("imported", count)
                            .EndObject());
                    }

                    case "list-blocks":
                    {
                        RequireSoftware(plcSoftware);
                        var json = new JsonWriter().BeginObject().BeginArray("blocks");
                        foreach (var b in ProjectReader.ListBlocks(plcSoftware))
                        {
                            json.BeginObject()
                                .Property("name", b.Name)
                                .Property("type", b.Type)
                                .Property("number", b.Number)
                                .Property("language", b.Language)
                            .EndObject();
                        }
                        json.EndArray().EndObject();
                        return Ok(id, json);
                    }

                    case "read-project":
                    {
                        RequireSoftware(plcSoftware);
                        var exportDir = JsonParser.GetString(args, "outDir");
                        var json = new JsonWriter().BeginObject();
                        ProjectReader.WriteProjectJson(plcSoftware, json, exportDir, out var exported);
                        json.BeginArray("exported");
                        foreach (var f in exported)
                            json.BeginObject().Property("path", f).EndObject();
                        json.EndArray();
                        json.EndObject();
                        return Ok(id, json);
                    }

                    case "add-hmi":
                    {
                        RequireProject(project);
                        var device = HmiOperations.AddHmiDevice(project);
                        var hmiTarget = HmiOperations.GetHmiTarget(device);
                        _hmiTarget = hmiTarget;
                        TrySave(project);
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("device", device.Name)
                            .Property("hmiTarget", hmiTarget != null)
                            .EndObject());
                    }

                    case "add-hmi-conn":
                    {
                        RequireProject(project);
                        if (_hmiTarget == null)
                        {
                            foreach (var dev in project.Devices)
                            {
                                try
                                {
                                    var t = HmiOperations.GetHmiTarget((Device)dev);
                                    if (t != null) { _hmiTarget = t; break; }
                                }
                                catch { }
                            }
                        }
                        if (_hmiTarget == null) throw new InvalidOperationException("工程中没有 HMI 设备(先 add-hmi)");
                        var connOutDir = JsonParser.GetString(args, "outDir") ?? Path.Combine(Directory.GetCurrentDirectory(), "hmi");
                        Directory.CreateDirectory(Path.GetFullPath(connOutDir));
                        var connPath = Path.Combine(Path.GetFullPath(connOutDir), "HMI_Connection_1.xml");
                        File.WriteAllText(connPath, HmiComposer.BuildConnectionXml("HMI_Connection_1"), new System.Text.UTF8Encoding(true));
                        var connName = HmiOperations.AddHmiConnection(project, _hmiTarget, connPath);
                        TrySave(project);
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("connection", connName)
                            .EndObject());
                    }

                    case "gen-hmi-tags":
                    {
                        RequireProject(project);
                        if (_hmiTarget == null)
                        {
                            foreach (var dev in project.Devices)
                            {
                                try
                                {
                                    var t = HmiOperations.GetHmiTarget((Device)dev);
                                    if (t != null) { _hmiTarget = t; break; }
                                }
                                catch { }
                            }
                        }
                        if (_hmiTarget == null) throw new InvalidOperationException("工程中没有 HMI 设备(先 add-hmi)");
                        var tableName = JsonParser.GetString(args, "table") ?? "HMI_Tags";
                        if (!(args.TryGetValue("tags", out var tagsRaw) && tagsRaw is List<object> tagList))
                            throw new InvalidOperationException("缺少参数: tags(变量数组)");
                        var defs = new List<HmiTagDef>();
                        foreach (var item in tagList)
                        {
                            if (!(item is Dictionary<string, object> o)) continue;
                            defs.Add(new HmiTagDef
                            {
                                Name = JsonParser.GetString(o, "name"),
                                DataType = JsonParser.GetString(o, "dataType") ?? "Bool",
                                Connection = JsonParser.GetString(o, "connection") ?? "",
                                PlcTag = JsonParser.GetString(o, "plcTag") ?? "",
                            });
                        }
                        if (defs.Count == 0) throw new InvalidOperationException("tags 数组为空");
                        var tagsOutDir = JsonParser.GetString(args, "outDir") ?? Path.Combine(Directory.GetCurrentDirectory(), "hmi");
                        Directory.CreateDirectory(Path.GetFullPath(tagsOutDir));
                        var xmlPath = Path.Combine(Path.GetFullPath(tagsOutDir), tableName + ".xml");
                        File.WriteAllText(xmlPath, HmiComposer.BuildTagTableXml(tableName, defs), new System.Text.UTF8Encoding(true));
                        var count = HmiOperations.ImportHmiTagTable(_hmiTarget, xmlPath);
                        TrySave(project);
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("table", tableName)
                            .Property("tags", count)
                            .Property("xmlFile", xmlPath)
                            .EndObject());
                    }

                    case "list-hmi":
                    {
                        RequireProject(project);
                        var json = new JsonWriter().BeginObject().BeginArray("hmis");
                        foreach (var dev in project.Devices)
                        {
                            try
                            {
                                var t = HmiOperations.GetHmiTarget((Device)dev);
                                if (t == null) continue;
                                json.BeginObject().Property("device", dev.Name);
                                json.BeginArray("connections");
                                try
                                {
                                    foreach (var c in t.Connections)
                                        json.BeginObject().Property("name", c.Name).EndObject();
                                }
                                catch { }
                                json.EndArray();
                                json.BeginArray("screens");
                                foreach (var s in t.ScreenFolder.Screens)
                                    json.BeginObject().Property("name", s.Name).EndObject();
                                json.EndArray();
                                json.EndObject();
                            }
                            catch { }
                        }
                        json.EndArray().EndObject();
                        return Ok(id, json);
                    }

                    case "gen-hmi":
                    {
                        RequireProject(project);
                        // 自动在工程已有设备中找 HMI 目标(serve 每次新进程,_hmiTarget 不跨会话)
                        if (_hmiTarget == null)
                        {
                            foreach (var dev in project.Devices)
                            {
                                try
                                {
                                    var t = HmiOperations.GetHmiTarget((Device)dev);
                                    if (t != null) { _hmiTarget = t; break; }
                                }
                                catch { }
                            }
                            if (_hmiTarget == null)
                                throw new InvalidOperationException("工程中没有 HMI 设备(先 add-hmi)");
                        }
                        if (!(args.TryGetValue("spec", out var specRaw) && specRaw is Dictionary<string, object> spec))
                            throw new InvalidOperationException("缺少参数: spec(画面规格对象)");
                        var ladOutDir = JsonParser.GetString(args, "outDir")
                                        ?? Path.Combine(Directory.GetCurrentDirectory(), "hmi");
                        var hmiSpec = HmiComposer.ParseSpec(spec);
                        var xml = HmiComposer.BuildXml(hmiSpec);
                        Directory.CreateDirectory(Path.GetFullPath(ladOutDir));
                        var xmlPath = Path.Combine(Path.GetFullPath(ladOutDir), hmiSpec.Name + ".xml");
                        File.WriteAllText(xmlPath, xml, new System.Text.UTF8Encoding(true));
                        var count = HmiOperations.ImportScreen(_hmiTarget, xmlPath);
                        TrySave(project);
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("screenName", hmiSpec.Name)
                            .Property("xmlFile", xmlPath)
                            .Property("imported", count)
                            .EndObject());
                    }

                    case "list-tag-tables":
                    {
                        RequireSoftware(plcSoftware);
                        var json = new JsonWriter().BeginObject()
                            .BeginArray("tables");
                        foreach (var t in plcSoftware.TagTableGroup.TagTables)
                        {
                            json.BeginObject()
                                .Property("name", t.Name)
                                .Property("tags", t.Tags.Count)
                            .EndObject();
                        }
                        json.EndArray().EndObject();
                        return Ok(id, json);
                    }

                    case "save-archive":
                    {
                        RequireProject(project);
                        var targetDir = JsonParser.GetString(args, "outDir") ?? outDir;
                        if (string.IsNullOrEmpty(targetDir))
                            throw new InvalidOperationException("缺少参数: outDir(或先 create-project)");
                        var files = ProjectOperations.SaveAndArchive(project, targetDir, projectName);
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("projectFile", files.Item1)
                            .Property("archiveFile", files.Item2)
                            .EndObject());
                    }

                    case "add-cpu":
                    {
                        RequireProject(project);
                        if (plcSoftware != null)
                            throw new InvalidOperationException("当前工程已有 PLC 设备");
                        var device = ProjectOperations.AddCpuDevice(project);
                        plcSoftware = ProjectOperations.GetPlcSoftware(device);
                        try { project.Save(); Console.Error.WriteLine("[info] 工程已保存"); } catch (Exception saveEx) { Console.Error.WriteLine($"[warn] 保存失败(不影响内存内操作): {ProjectOperations.Unwrap(saveEx).Message}"); }
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("device", device.Name)
                            .Property("plcSoftware", plcSoftware != null)
                            .EndObject());
                    }

                    case "download":
                    {
                        RequireProject(project);
                        RequireSoftware(plcSoftware);
                        TrySave(project);
                        DownloadService.EnsureIp(project);
                        DownloadService.StartPlcsim();
                        var report = CompileDiagnostics.Compile(plcSoftware);
                        if (report.Errors > 0)
                            throw new InvalidOperationException($"编译有 {report.Errors} 个错误,先修复再下载");
                        var state = DownloadService.DownloadToPlc(plcSoftware);
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("state", state)
                            .EndObject());
                    }

                    case "report":
                    {
                        RequireSoftware(plcSoftware);
                        var reportOutDir = JsonParser.GetString(args, "outDir")
                                           ?? Path.Combine(Directory.GetCurrentDirectory(), "output", "reports");
                        var path = ReportGenerator.Generate(plcSoftware, reportOutDir,
                            string.IsNullOrEmpty(projectName) ? "Project" : projectName);
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("reportFile", path)
                            .EndObject());
                    }

                    case "gen-template":
                    {
                        RequireSoftware(plcSoftware);
                        var templateName = JsonParser.GetString(args, "template");
                        if (string.IsNullOrEmpty(templateName)) throw new InvalidOperationException("缺少参数: template(traffic-light / motor-rev / counter)");
                        // 模板在仓库 samples/templates 下
                        var candidates = new[]
                        {
                            Path.Combine(SamplesDir, "templates", templateName.Replace("-", "_") + ".scl"),
                        };
                        var found = Array.Find(candidates, File.Exists);
                        if (found == null) throw new FileNotFoundException($"模板不存在: {templateName}(可用: traffic-light / motor-rev / counter)");

                        var text = File.ReadAllText(found, System.Text.Encoding.UTF8);
                        // 参数替换 {{KEY}} → value
                        if (args.TryGetValue("params", out var paramsRaw) && paramsRaw is Dictionary<string, object> paramDict)
                        {
                            foreach (var kv in paramDict)
                                if (kv.Value is string sv)
                                    text = text.Replace("{{" + kv.Key + "}}", sv);
                        }
                        // 未替换的占位符用默认值
                        var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "GREEN_TIME", "3s" }, { "YELLOW_TIME", "1s" }, { "RED_TIME", "5s" }, { "TARGET", "10" },
                        };
                        foreach (var kv in defaults)
                            text = text.Replace("{{" + kv.Key + "}}", kv.Value);

                        var genDir = Path.Combine(SamplesDir, "generated");
                        Directory.CreateDirectory(genDir);
                        var outFile = Path.Combine(genDir, templateName + ".scl");
                        File.WriteAllText(outFile, text, new System.Text.UTF8Encoding(true));
                        Console.Error.WriteLine($"[info] 模板已展开: {outFile}");

                        BlockImporter.ImportAndGenerate(plcSoftware, outFile);
                        TrySave(project);
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("template", templateName)
                            .Property("sclFile", outFile)
                            .Property("generated", true)
                            .EndObject());
                    }

                    case "list-instances":
                    {
                        var json = new JsonWriter().BeginObject().BeginArray("instances");
                        foreach (var p in TiaPortal.GetProcesses())
                        {
                            json.BeginObject()
                                .Property("pid", p.Id)
                            .EndObject();
                        }
                        json.EndArray().EndObject();
                        return Ok(id, json);
                    }

                    case "attach-instance":
                    {
                        var pidStr = JsonParser.GetString(args, "pid");
                        if (!int.TryParse(pidStr, out var pid))
                            throw new InvalidOperationException("缺少参数: pid(数字,用 list-instances 查看)");
                        manager.AttachTo(pid);
                        // 重新绑定该实例中打开的工程
                        project = null;
                        plcSoftware = null;
                        projectName = "";
                        outDir = "";
                        foreach (var p in manager.TiaPortal.Projects)
                        {
                            if (!(p is Project proj)) continue;
                            project = proj;
                            projectName = proj.Name;
                            try { plcSoftware = FindFirstPlcSoftware(proj); }
                            catch { plcSoftware = null; }
                            break;
                        }
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("attached", true)
                            .Property("project", projectName)
                            .EndObject());
                    }

                    case "delete-block":
                    {
                        RequireSoftware(plcSoftware);
                        var blockName = JsonParser.GetString(args, "name");
                        if (string.IsNullOrEmpty(blockName)) throw new InvalidOperationException("缺少参数: name");
                        var deleted = false;
                        foreach (var b in plcSoftware.BlockGroup.Blocks)
                        {
                            if (b.Name == blockName)
                            {
                                b.Delete();
                                deleted = true;
                                break;
                            }
                        }
                        if (!deleted) throw new InvalidOperationException($"未找到块: {blockName}");
                        TrySave(project);
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("deleted", blockName)
                            .EndObject());
                    }

                    case "create-inst-db":
                    {
                        RequireSoftware(plcSoftware);
                        var dbName = JsonParser.GetString(args, "name");
                        var instanceOf = JsonParser.GetString(args, "instanceOf");
                        if (string.IsNullOrEmpty(dbName) || string.IsNullOrEmpty(instanceOf))
                            throw new InvalidOperationException("缺少参数: name / instanceOf");
                        // 先删同名旧 DB(重试幂等)
                        try
                        {
                            foreach (var b in plcSoftware.BlockGroup.Blocks)
                                if (b.Name == dbName) { b.Delete(); break; }
                        }
                        catch { }
                        var db = plcSoftware.BlockGroup.Blocks.CreateInstanceDB(dbName, false, 200, instanceOf);
                        TrySave(project);
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("dbName", db.Name)
                            .Property("instanceOf", instanceOf)
                            .EndObject());
                    }

                    case "search-hw":
                    {
                        var pattern = JsonParser.GetString(args, "pattern");
                        if (string.IsNullOrEmpty(pattern)) throw new InvalidOperationException("缺少参数: pattern");
                        var json = new JsonWriter().BeginObject().BeginArray("entries");
                        var entries = manager.TiaPortal.HardwareCatalog.Find(pattern);
                        foreach (var entry in entries)
                        {
                            var ti = (entry.GetAttribute("TypeIdentifier") ?? "").ToString();
                            string title = null;
                            try { title = (entry.GetAttribute("Title") ?? "").ToString(); } catch { }
                            if (string.IsNullOrEmpty(title))
                                try { title = (entry.GetAttribute("ObjectTitle") ?? "").ToString(); } catch { }
                            json.BeginObject()
                                .Property("title", title ?? ti)
                                .Property("typeIdentifier", ti)
                            .EndObject();
                        }
                        json.EndArray().EndObject();
                        return Ok(id, json);
                    }

                    case "list-projects":
                    {
                        var json = new JsonWriter().BeginObject().BeginArray("projects");
                        foreach (var p in manager.TiaPortal.Projects)
                        {
                            var proj = p as Project;
                            json.BeginObject()
                                .Property("name", proj?.Name ?? p.ToString())
                                .Property("isCurrent", proj != null && ReferenceEquals(proj, project))
                                .EndObject();
                        }
                        json.EndArray().EndObject();
                        return Ok(id, json);
                    }

                    case "use-project":
                    {
                        var name = JsonParser.GetString(args, "name");
                        if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("缺少参数: name");
                        foreach (var p in manager.TiaPortal.Projects)
                        {
                            if (!(p is Project proj) || !string.Equals(proj.Name, name, StringComparison.OrdinalIgnoreCase))
                                continue;
                            project = proj;
                            projectName = proj.Name;
                            try { outDir = Path.GetDirectoryName(proj.Path.FullName); } catch { }
                            plcSoftware = FindFirstPlcSoftware(proj);
                            return Ok(id, new JsonWriter().BeginObject()
                                .Property("projectName", projectName)
                                .EndObject());
                        }
                        throw new InvalidOperationException($"TIA 中没有名为 {name} 的工程(用 list-projects 查看)");
                    }

                    case "save-project":
                    {
                        RequireProject(project);
                        TrySave(project);
                        return Ok(id, new JsonWriter().BeginObject()
                            .Property("saved", true)
                            .EndObject());
                    }

                    case "shutdown":
                    {
                        // Attach 模式:不关闭用户界面里的工程,只退出本 worker;
                        // 自建模式(headless):关闭工程让 TIA 正常退出
                        if (!_attached) { try { project?.Close(); } catch { } }
                        shutdown = true;
                        return Ok(id, new JsonWriter().BeginObject().EndObject());
                    }

                    default:
                        throw new InvalidOperationException($"未知命令: {cmd}");
                }
            }
            catch (Exception ex)
            {
                return Err(id, ProjectOperations.Unwrap(ex).Message);
            }
        }

        private static List<TagSpec> ParseTags(Dictionary<string, object> args)
        {
            var tags = new List<TagSpec>();
            if (!args.TryGetValue("tags", out var raw) || !(raw is List<object> list))
                throw new InvalidOperationException("缺少参数: tags(标签数组)");
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
            return tags;
        }

        /// <summary>写操作后保存工程(失败只警告,不阻断)。</summary>
        private static void TrySave(Project project)
        {
            if (project == null) return;
            try { project.Save(); Console.Error.WriteLine("[info] 工程已保存"); }
            catch (Exception ex) { Console.Error.WriteLine($"[warn] 保存失败(不影响内存内操作): {ProjectOperations.Unwrap(ex).Message}"); }
        }

        private static void RequireProject(Project project)
        {
            if (project == null) throw new InvalidOperationException("当前没有打开的工程(先 create-project 或 open-project)");
        }

        private static void RequireSoftware(PlcSoftware plcSoftware)
        {
            if (plcSoftware == null) throw new InvalidOperationException("当前没有 PLC 软件(先 create-project 或 open-project)");
        }

        private static PlcSoftware FindFirstPlcSoftware(Project project)
        {
            foreach (var device in project.Devices)
            {
                var plc = ProjectOperations.GetPlcSoftware(device);
                if (plc != null) return plc;
            }
            throw new InvalidOperationException("工程中没有 PLC 设备(PlcSoftware)");
        }

        private static string Ok(string id, JsonWriter result) => Ok(id, result.ToString());

        private static string Ok(string id, string resultJson)
        {
            return "{\"id\":" + JsonEscape(id) + ",\"ok\":true,\"result\":" + resultJson + "}";
        }

        private static string Err(string id, string error)
        {
            return "{\"id\":" + JsonEscape(id) + ",\"ok\":false,\"error\":" + JsonEscape(error) + "}";
        }

        private static string CompileReportJson(CompileReport report)
        {
            var json = new JsonWriter().BeginObject()
                .Property("state", report.CompilationState)
                .Property("errors", report.Errors)
                .Property("warnings", report.Warnings)
                .BeginArray("messages");
            foreach (var m in report.Messages)
            {
                json.BeginObject()
                    .Property("state", m.State)
                    .Property("block", m.Block)
                    .Property("description", m.Description)
                .EndObject();
            }
            json.EndArray().EndObject();
            return json.ToString();
        }

        private static string JsonEscape(string s) => "\"" + JsonWriter.Escape(s ?? "") + "\"";
    }
}
