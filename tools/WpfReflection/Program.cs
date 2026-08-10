using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace WpfReflection;

internal static class Program
{
    private const string RefPack = @"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\6.0.36";

    private static readonly Dictionary<string, string> s_keywords = new()
    {
        { "System.Void", "void" }, { "System.Boolean", "bool" }, { "System.Int32", "int" },
        { "System.Int64", "long" }, { "System.Int16", "short" }, { "System.Byte", "byte" },
        { "System.SByte", "sbyte" }, { "System.Single", "float" }, { "System.Double", "double" },
        { "System.Decimal", "decimal" }, { "System.String", "string" }, { "System.Object", "object" },
        { "System.Char", "char" }, { "System.UInt32", "uint" }, { "System.UInt64", "ulong" },
        { "System.UInt16", "ushort" },
    };

    private static string Show(Type t)
    {
        var full = t.FullName ?? t.Name;
        if (s_keywords.TryGetValue(full, out var kw)) return kw;
        if (t.IsGenericType)
        {
            var name = full.Split('`')[0].Replace('+', '.');
            var args = string.Join(", ", t.GetGenericArguments().Select(Show));
            return $"{name}<{args}>";
        }
        if (t.IsArray) return $"{Show(t.GetElementType()!)}[]";
        if (t.IsByRef) return $"{Show(t.GetElementType()!)}&";
        return full.Replace('+', '.');
    }

    private static string Params(IEnumerable<ParameterInfo> ps)
        => string.Join(", ", ps.Select(p => $"{Show(p.ParameterType)} {p.Name ?? "arg"}"));

    private static readonly HashSet<string> s_ignoreMethods = new()
    {
        "GetType", "MemberwiseClone", "Finalize",
    };

    private static void DumpType(Type t, StringBuilder sb)
    {
        var kinds = new List<string>();
        if (t.IsInterface) kinds.Add("interface");
        else if (t.IsEnum) kinds.Add("enum");
        else if (t.IsValueType) kinds.Add("struct");
        else kinds.Add("class");
        if (t.IsAbstract && !t.IsInterface) kinds.Add("abstract");
        if (t.IsSealed && !t.IsValueType) kinds.Add("sealed");
        if (t.IsPublic) kinds.Add("public");
        else if (t.IsNestedPublic) kinds.Add("public-nested");
        var baseInfo = t.BaseType != null && t.BaseType != typeof(object) && t.BaseType != typeof(ValueType)
            ? $" : {Show(t.BaseType)}"
            : "";
        var interfaces = t.GetInterfaces().Where(i => i.IsPublic || (i.IsNestedPublic))
            .Select(Show).OrderBy(x => x).ToList();
        if (interfaces.Count > 0)
        {
            baseInfo += (baseInfo.Length > 0 ? ", " : " : ") + string.Join(", ", interfaces);
        }
        sb.AppendLine($"## {Show(t)}");
        sb.AppendLine($"Kind: {string.Join(" ", kinds)}{baseInfo}");

        if (t.IsEnum)
        {
            var vals = Enum.GetNames(t).Take(200);
            sb.AppendLine($"EnumValues: {string.Join(", ", vals)}");
            sb.AppendLine();
            return;
        }

        foreach (var ctor in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                     .OrderBy(c => c.GetParameters().Length))
        {
            sb.AppendLine($"ctor({Params(ctor.GetParameters())})");
        }

        var props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(p => p.Name);
        foreach (var p in props)
        {
            var access = new List<string>();
            if (p.CanRead) access.Add("get");
            if (p.CanWrite) access.Add("set");
            var stat = p.GetGetMethod(true)?.IsStatic == true || p.GetSetMethod(true)?.IsStatic == true ? "static " : "";
            sb.AppendLine($"{stat}{Show(p.PropertyType)} {p.Name} {{ {string.Join("; ", access)}; }}");
        }

        var fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(f => f.Name);
        foreach (var f in fields)
        {
            var stat = f.IsStatic ? "static " : "";
            var ro = f.IsInitOnly ? " readonly" : "";
            sb.AppendLine($"{stat}{Show(f.FieldType)}{ro} {f.Name};");
        }

        foreach (var e in t.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                     .OrderBy(e => e.Name))
        {
            sb.AppendLine($"event {Show(e.EventHandlerType!)} {e.Name};");
        }

        var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && !s_ignoreMethods.Contains(m.Name))
            .OrderBy(m => m.Name)
            .ThenBy(m => m.GetParameters().Length);
        foreach (var m in methods)
        {
            var stat = m.IsStatic ? "static " : "";
            if (m.IsGenericMethodDefinition)
            {
                sb.AppendLine($"{stat}{Show(m.ReturnType)} {m.Name}<{string.Join(", ", m.GetGenericArguments().Select(a => a.Name))}>({Params(m.GetParameters())})");
            }
            else
            {
                sb.AppendLine($"{stat}{Show(m.ReturnType)} {m.Name}({Params(m.GetParameters())})");
            }
        }
        sb.AppendLine();
    }

    private static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: WpfReflection <TypeFullName> [MoreTypeNames...]");
            Console.WriteLine("Dumps public-ish API surface from the .NET 6 WPF reference assemblies.");
            return;
        }

        var dlls = new[] { "WindowsBase.dll", "PresentationCore.dll", "PresentationFramework.dll" };
        var asmCache = new Dictionary<string, Assembly>();
        Assembly? Resolve(object? s, ResolveEventArgs rea)
        {
            var name = new AssemblyName(rea.Name).Name + ".dll";
            if (asmCache.TryGetValue(name, out var cached)) return cached;
            var path = Path.Combine(RefPack, name);
            if (!File.Exists(path)) return null;
            var asm = Assembly.LoadFrom(path);
            asmCache[name] = asm;
            return asm;
        }

        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        foreach (var dll in dlls)
        {
            var asm = Assembly.LoadFrom(Path.Combine(RefPack, dll));
            asmCache[dll] = asm;
        }

        var sb = new StringBuilder();
        foreach (var typeName in args)
        {
            Type? t = null;
            foreach (var dll in dlls)
            {
                t = asmCache[dll].GetType(typeName, false);
                if (t != null) break;
            }
            if (t == null)
            {
                sb.AppendLine($"## {typeName}");
                sb.AppendLine("NOT FOUND");
                sb.AppendLine();
                continue;
            }
            DumpType(t, sb);
        }
        Console.Write(sb.ToString());
    }
}
