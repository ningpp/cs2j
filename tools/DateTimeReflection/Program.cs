using System;
using System.Linq;
using System.Reflection;

class Program
{
    static void Main()
    {
        var types = new[]
        {
            typeof(DateTime),
            typeof(DateTimeOffset),
            typeof(TimeSpan),
            typeof(DateOnly),
            typeof(TimeOnly),
        };

        foreach (var type in types)
        {
            Console.WriteLine($"=== {type.FullName} ===");
            Console.WriteLine();

            // Static fields (constants)
            var staticFields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .OrderBy(f => f.Name).ToList();
            if (staticFields.Count > 0)
            {
                Console.WriteLine("--- Static Fields / Constants ---");
                foreach (var f in staticFields)
                {
                    var val = f.IsLiteral ? f.GetRawConstantValue() : "?";
                    Console.WriteLine($"  {f.FieldType.GetDisplayName()} {f.Name} = {val}");
                }
                Console.WriteLine();
            }

            // Static properties
            var staticProps = type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .OrderBy(p => p.Name).ToList();
            if (staticProps.Count > 0)
            {
                Console.WriteLine("--- Static Properties ---");
                foreach (var p in staticProps)
                {
                    Console.WriteLine($"  {p.PropertyType.GetDisplayName()} {p.Name} {{ {GetAccessors(p)} }}");
                }
                Console.WriteLine();
            }

            // Instance properties
            var instanceProps = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .OrderBy(p => p.Name).ToList();
            if (instanceProps.Count > 0)
            {
                Console.WriteLine("--- Instance Properties ---");
                foreach (var p in instanceProps)
                {
                    Console.WriteLine($"  {p.PropertyType.GetDisplayName()} {p.Name} {{ {GetAccessors(p)} }}");
                }
                Console.WriteLine();
            }

            // Static methods
            var staticMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .OrderBy(m => m.Name).ThenBy(m => m.GetParameters().Length).ToList();
            if (staticMethods.Count > 0)
            {
                Console.WriteLine("--- Static Methods ---");
                foreach (var m in staticMethods)
                {
                    var parameters = string.Join(", ", m.GetParameters()
                        .Select(p => $"{p.ParameterType.GetDisplayName()} {p.Name}"));
                    Console.WriteLine($"  {m.ReturnType.GetDisplayName()} {m.Name}({parameters})");
                }
                Console.WriteLine();
            }

            // Instance methods
            var instanceMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .OrderBy(m => m.Name).ThenBy(m => m.GetParameters().Length).ToList();
            if (instanceMethods.Count > 0)
            {
                Console.WriteLine("--- Instance Methods ---");
                foreach (var m in instanceMethods)
                {
                    var parameters = string.Join(", ", m.GetParameters()
                        .Select(p => $"{p.ParameterType.GetDisplayName()} {p.Name}"));
                    Console.WriteLine($"  {m.ReturnType.GetDisplayName()} {m.Name}({parameters})");
                }
                Console.WriteLine();
            }

            Console.WriteLine();
        }
    }

    static string GetAccessors(PropertyInfo p)
    {
        var get = p.CanRead ? "get" : "";
        var set = p.CanWrite ? "set" : "";
        return string.Join("; ", new[] { get, set }.Where(s => s != ""));
    }
}

static class TypeExtensions
{
    public static string GetDisplayName(this Type type)
    {
        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();
            var genericArgs = type.GetGenericArguments();
            var name = genericDef.Name.Substring(0, genericDef.Name.IndexOf('`'));
            return $"{name}<{string.Join(", ", genericArgs.Select(GetDisplayName))}>";
        }
        if (type.IsArray)
        {
            return $"{GetDisplayName(type.GetElementType())}[]";
        }
        if (type.IsNested)
        {
            return $"{type.DeclaringType.Name}.{type.Name}";
        }
        if (type.IsByRef)
        {
            return $"ref {GetDisplayName(type.GetElementType())}";
        }

        var map = new System.Collections.Generic.Dictionary<Type, string>
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
