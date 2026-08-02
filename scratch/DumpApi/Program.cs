using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

/// <summary>
/// 临时工具:反射加载 Openness V21 程序集,打印 PlcTag 相关类型的真实 API 签名。
/// </summary>
internal static class Program
{
    private const string ApiDir = @"D:\Workspace\Portal V21\PublicAPI\V21\net48";
    private const string BinDir = @"D:\Workspace\Portal V21\Bin";

    private static readonly string[] Targets =
    {
         "NetworkInterface", "INetworkInterface",
    };

    private static int Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var name = new AssemblyName(e.Name).Name + ".dll";
            foreach (var dir in new[] { ApiDir, BinDir })
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return Assembly.LoadFrom(p);
            }
            return null;
        };

        foreach (var dll in Directory.GetFiles(ApiDir, "Siemens.Engineering*.dll"))
        {
            Assembly asm;
            try { asm = Assembly.LoadFrom(dll); }
            catch { continue; }

            foreach (var type in SafeTypes(asm))
            {
                if (!Targets.Any(t => type.Name.Contains(t))) continue;
                Console.WriteLine($"\n=== {type.FullName} ({Path.GetFileName(dll)})");

                foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                    Console.WriteLine($"  C .ctor({string.Join(", ", ctor.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");

                var chain = new List<Type>();
                for (var t = type; t != null && t != typeof(object); t = t.BaseType) chain.Add(t);
                foreach (var bt in chain)
                {
                    foreach (var m in bt.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        if (m.IsSpecialName) continue;
                        Console.WriteLine($"  M {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))}) -> {m.ReturnType.Name}{(bt != type ? "  [from " + bt.Name + "]" : "")}");
                    }
                    foreach (var p in bt.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                        Console.WriteLine($"  P {p.Name}: {p.PropertyType.Name}{(bt != type ? "  [from " + bt.Name + "]" : "")}");
                }
            }
        }
        return 0;
    }

    private static IEnumerable<Type> SafeTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
    }
}
