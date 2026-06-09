using System;
using System.Linq;
using System.Reflection;

class Program
{
    static void Main()
    {
        var assertType = typeof(Xunit.Assert);
        Console.WriteLine("=== Xunit.Assert Public Methods (from reflection) ===");
        Console.WriteLine();

        var methods = assertType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(m => m.Name)
            .ThenBy(m => m.GetParameters().Length)
            .ToList();

        // Group by method name
        var groups = methods.GroupBy(m => m.Name).OrderBy(g => g.Key).ToList();

        foreach (var group in groups)
        {
            Console.WriteLine($"--- {group.Key} ({group.Count()} overloads) ---");
            foreach (var method in group)
            {
                var parameters = string.Join(", ", method.GetParameters()
                    .Select(p => $"{p.ParameterType.GetDisplayName()} {p.Name}"));
                var returnType = method.ReturnType.GetDisplayName();
                Console.WriteLine($"  {returnType} {method.Name}({parameters})");
            }
            Console.WriteLine();
        }

        Console.WriteLine($"Total: {methods.Count} methods, {groups.Count} unique method names");
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
