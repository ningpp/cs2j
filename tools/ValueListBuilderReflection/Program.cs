using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

class Program
{
    static void Main()
    {
        // ValueListBuilder<T> is internal in System.Private.CoreLib
        // Search all loaded assemblies for it
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        Type? vlbType = null;

        foreach (var asm in assemblies)
        {
            try
            {
                vlbType = asm.GetTypes().FirstOrDefault(t =>
                    t.IsGenericTypeDefinition &&
                    t.FullName != null &&
                    t.FullName.Contains("ValueListBuilder"));
                if (vlbType != null) break;
            }
            catch (ReflectionTypeLoadException) { }
        }

        if (vlbType == null)
        {
            Console.WriteLine("ValueListBuilder<T> not found in loaded assemblies.");
            Console.WriteLine("Trying to load from System.Private.CoreLib...");
            var coreLib = typeof(object).Assembly;
            try
            {
                vlbType = coreLib.GetTypes().FirstOrDefault(t =>
                    t.IsGenericTypeDefinition &&
                    t.FullName != null &&
                    t.FullName.Contains("ValueListBuilder"));
            }
            catch (ReflectionTypeLoadException ex)
            {
                vlbType = ex.Types?.FirstOrDefault(t =>
                    t != null && t.IsGenericTypeDefinition &&
                    t.FullName != null &&
                    t.FullName.Contains("ValueListBuilder"));
            }
        }

        if (vlbType == null)
        {
            Console.WriteLine("Could not find ValueListBuilder<T>.");
            return;
        }

        Console.WriteLine($"Found: {vlbType.FullName}");
        Console.WriteLine($"IsPublic: {vlbType.IsPublic}");
        Console.WriteLine($"IsValueType: {vlbType.IsValueType}");
        Console.WriteLine($"IsRefLikeType: {vlbType.IsByRefLike}");
        Console.WriteLine();

        // Make a concrete instantiation with int for display
        var concreteType = vlbType.MakeGenericType(typeof(int));
        Console.WriteLine($"=== {concreteType.FullName} (instantiated with int) ===");
        Console.WriteLine();

        // Static fields (constants)
        var staticFields = concreteType.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(f => f.Name).ToList();
        if (staticFields.Count > 0)
        {
            Console.WriteLine("--- Static Fields / Constants ---");
            foreach (var f in staticFields)
            {
                var val = f.IsLiteral ? f.GetRawConstantValue() : "?";
                Console.WriteLine($"  {GetDisplayName(f.FieldType)} {f.Name} = {val}");
            }
            Console.WriteLine();
        }

        // Instance fields
        var instanceFields = concreteType.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(f => f.Name).ToList();
        if (instanceFields.Count > 0)
        {
            Console.WriteLine("--- Instance Fields ---");
            foreach (var f in instanceFields)
            {
                Console.WriteLine($"  {GetDisplayName(f.FieldType)} {f.Name}");
            }
            Console.WriteLine();
        }

        // Static properties
        var staticProps = concreteType.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(p => p.Name).ToList();
        if (staticProps.Count > 0)
        {
            Console.WriteLine("--- Static Properties ---");
            foreach (var p in staticProps)
            {
                Console.WriteLine($"  {GetDisplayName(p.PropertyType)} {p.Name} {{ {GetAccessors(p)} }}");
            }
            Console.WriteLine();
        }

        // Instance properties
        var instanceProps = concreteType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(p => p.Name).ToList();
        if (instanceProps.Count > 0)
        {
            Console.WriteLine("--- Instance Properties ---");
            foreach (var p in instanceProps)
            {
                Console.WriteLine($"  {GetDisplayName(p.PropertyType)} {p.Name} {{ {GetAccessors(p)} }}");
            }
            Console.WriteLine();
        }

        // Constructors
        var ctors = concreteType.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (ctors.Length > 0)
        {
            Console.WriteLine("--- Constructors ---");
            foreach (var c in ctors)
            {
                var parameters = string.Join(", ", c.GetParameters()
                    .Select(p => $"{GetDisplayName(p.ParameterType)} {p.Name}"));
                Console.WriteLine($"  {concreteType.Name}({parameters})");
            }
            Console.WriteLine();
        }

        // Static methods
        var staticMethods = concreteType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(m => m.Name).ThenBy(m => m.GetParameters().Length).ToList();
        if (staticMethods.Count > 0)
        {
            Console.WriteLine("--- Static Methods ---");
            foreach (var m in staticMethods)
            {
                var parameters = string.Join(", ", m.GetParameters()
                    .Select(p => $"{GetDisplayName(p.ParameterType)} {p.Name}"));
                Console.WriteLine($"  {GetDisplayName(m.ReturnType)} {m.Name}({parameters})");
            }
            Console.WriteLine();
        }

        // Instance methods
        var instanceMethods = concreteType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(m => m.Name).ThenBy(m => m.GetParameters().Length).ToList();
        if (instanceMethods.Count > 0)
        {
            Console.WriteLine("--- Instance Methods ---");
            foreach (var m in instanceMethods)
            {
                var parameters = string.Join(", ", m.GetParameters()
                    .Select(p => $"{GetDisplayName(p.ParameterType)} {p.Name}"));
                Console.WriteLine($"  {GetDisplayName(m.ReturnType)} {m.Name}({parameters})");
            }
            Console.WriteLine();
        }

        // Also check non-public members for completeness
        Console.WriteLine("=== ALL members (including non-public) ===");
        Console.WriteLine();

        var allMethods = concreteType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .OrderBy(m => m.Name).ToList();
        Console.WriteLine("--- All Methods ---");
        foreach (var m in allMethods)
        {
            var visibility = m.IsPublic ? "public" : m.IsPrivate ? "private" : m.IsAssembly ? "internal" : m.IsFamilyOrAssembly ? "protected internal" : "other";
            var staticMod = m.IsStatic ? "static " : "";
            var parameters = string.Join(", ", m.GetParameters()
                .Select(p => $"{GetDisplayName(p.ParameterType)} {p.Name}"));
            Console.WriteLine($"  [{visibility}] {staticMod}{GetDisplayName(m.ReturnType)} {m.Name}({parameters})");
        }
        Console.WriteLine();

        // Check specifically for Pop method
        Console.WriteLine("=== Checking for Pop method ===");
        var popMethod = concreteType.GetMethod("Pop", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        if (popMethod != null)
        {
            Console.WriteLine($"Pop found! Public: {popMethod.IsPublic}, Return: {GetDisplayName(popMethod.ReturnType)}");
        }
        else
        {
            Console.WriteLine("Pop NOT found on concrete type.");
        }

        // Also check on the generic type definition
        var popGeneric = vlbType.GetMethod("Pop", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        if (popGeneric != null)
        {
            Console.WriteLine($"Pop found on generic definition! Public: {popGeneric.IsPublic}, Return: {GetDisplayName(popGeneric.ReturnType)}");
        }
        else
        {
            Console.WriteLine("Pop NOT found on generic type definition either.");
        }
    }

    static string GetAccessors(PropertyInfo p)
    {
        var get = p.CanRead ? "get" : "";
        var set = p.CanWrite ? "set" : "";
        return string.Join("; ", new[] { get, set }.Where(s => s != ""));
    }

    static string GetDisplayName(Type type)
    {
        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();
            var genericArgs = type.GetGenericArguments();
            var name = genericDef.Name;
            var idx = name.IndexOf('`');
            if (idx >= 0) name = name.Substring(0, idx);
            return $"{name}<{string.Join(", ", genericArgs.Select(GetDisplayName))}>";
        }
        if (type.IsArray)
        {
            return $"{GetDisplayName(type.GetElementType()!)}[]";
        }
        if (type.IsNested)
        {
            return $"{type.DeclaringType!.Name}.{type.Name}";
        }
        if (type.IsByRef)
        {
            return $"ref {GetDisplayName(type.GetElementType()!)}";
        }

        var map = new Dictionary<Type, string>
        {
            { typeof(void), "void" },
            { typeof(bool), "bool" },
            { typeof(int), "int" },
            { typeof(long), "long" },
            { typeof(float), "float" },
            { typeof(double), "double" },
            { typeof(decimal), "decimal" },
            { typeof(string), "string" },
            { typeof(object), "object" },
            { typeof(char), "char" },
            { typeof(byte), "byte" },
        };
        return map.TryGetValue(type, out var alias) ? alias : type.Name;
    }
}
