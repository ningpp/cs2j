using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpToJava.TypeMapping;

/// <summary>
/// 类型映射配置
/// </summary>
public class TypeMappingConfig
{
    [JsonPropertyName("typeMappings")]
    public List<TypeMappingEntry> TypeMappings { get; set; } = new();

    [JsonPropertyName("methodMappings")]
    public List<MethodMappingEntry> MethodMappings { get; set; } = new();

    [JsonPropertyName("namespaceMappings")]
    public List<NamespaceMappingEntry> NamespaceMappings { get; set; } = new();
}

/// <summary>
/// 类型映射条目
/// </summary>
public class TypeMappingEntry
{
    [JsonPropertyName("csharp")]
    public string CSharpType { get; set; } = string.Empty;

    [JsonPropertyName("java")]
    public string JavaType { get; set; } = string.Empty;

    [JsonPropertyName("imports")]
    public List<string> Imports { get; set; } = new();

    [JsonPropertyName("converter")]
    public string? Converter { get; set; }
}

/// <summary>
/// 方法映射条目
/// </summary>
public class MethodMappingEntry
{
    [JsonPropertyName("type")]
    public string TypeName { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string MethodName { get; set; } = string.Empty;

    [JsonPropertyName("javaMethod")]
    public string JavaMethodName { get; set; } = string.Empty;

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }
}

/// <summary>
/// 命名空间映射条目
/// </summary>
public class NamespaceMappingEntry
{
    [JsonPropertyName("csharp")]
    public string CSharpNamespace { get; set; } = string.Empty;

    [JsonPropertyName("java")]
    public string JavaPackage { get; set; } = string.Empty;
}

/// <summary>
/// 类型映射注册表
/// </summary>
public class TypeMappingRegistry
{
    private readonly Dictionary<string, TypeMappingEntry> _typeMappings = new();
    private readonly Dictionary<(string TypeName, string MethodName), MethodMappingEntry> _methodMappings = new();
    private readonly Dictionary<string, string> _namespaceMappings = new();

    public TypeMappingRegistry(string? configPath = null)
    {
        LoadDefaultMappings();

        if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
        {
            LoadMappings(configPath);
        }
    }

    public TypeMappingRegistry(TypeMappingConfig config)
    {
        LoadMappings(config);
    }

    private void LoadDefaultMappings()
    {
        var config = new TypeMappingConfig
        {
            TypeMappings = new List<TypeMappingEntry>
            {
                // 基础类型
                new() { CSharpType = "System.String", JavaType = "String", Imports = new() },
                new() { CSharpType = "System.Int32", JavaType = "int", Imports = new() },
                new() { CSharpType = "System.Int64", JavaType = "long", Imports = new() },
                new() { CSharpType = "System.Int16", JavaType = "short", Imports = new() },
                new() { CSharpType = "System.Byte", JavaType = "byte", Imports = new() },
                new() { CSharpType = "System.SByte", JavaType = "byte", Imports = new() },
                new() { CSharpType = "System.UInt32", JavaType = "int", Imports = new() },
                new() { CSharpType = "System.UInt64", JavaType = "long", Imports = new() },
                new() { CSharpType = "System.UInt16", JavaType = "short", Imports = new() },
                new() { CSharpType = "System.Single", JavaType = "float", Imports = new() },
                new() { CSharpType = "System.Double", JavaType = "double", Imports = new() },
                new() { CSharpType = "System.Boolean", JavaType = "boolean", Imports = new() },
                new() { CSharpType = "System.Char", JavaType = "char", Imports = new() },
                new() { CSharpType = "System.Object", JavaType = "Object", Imports = new() },
                new() { CSharpType = "System.Void", JavaType = "void", Imports = new() },

                // 集合类型
                new() { CSharpType = "System.Collections.Generic.List`1", JavaType = "ArrayList", Imports = new() { "java.util.ArrayList" } },
                new() { CSharpType = "System.Collections.Generic.IList`1", JavaType = "List", Imports = new() { "java.util.List" } },
                new() { CSharpType = "System.Collections.Generic.Dictionary`2", JavaType = "HashMap", Imports = new() { "java.util.HashMap" } },
                new() { CSharpType = "System.Collections.Generic.IDictionary`2", JavaType = "Map", Imports = new() { "java.util.Map" } },
                new() { CSharpType = "System.Collections.Generic.HashSet`1", JavaType = "HashSet", Imports = new() { "java.util.HashSet" } },
                new() { CSharpType = "System.Collections.Generic.IEnumerable`1", JavaType = "Iterable", Imports = new() { "java.lang.Iterable" } },
                new() { CSharpType = "System.Collections.Generic.IEnumerator`1", JavaType = "Iterator", Imports = new() { "java.util.Iterator" } },

                // 异步类型
                new() { CSharpType = "System.Threading.Tasks.Task`1", JavaType = "CompletableFuture", Imports = new() { "java.util.concurrent.CompletableFuture" } },
                new() { CSharpType = "System.Threading.Tasks.Task", JavaType = "CompletableFuture", Imports = new() { "java.util.concurrent.CompletableFuture" } },
                new() { CSharpType = "System.Threading.Tasks.ValueTask`1", JavaType = "CompletableFuture", Imports = new() { "java.util.concurrent.CompletableFuture" } },

                // 日期时间类型
                new() { CSharpType = "System.DateTime", JavaType = "LocalDateTime", Imports = new() { "java.time.LocalDateTime" } },
                new() { CSharpType = "System.DateTimeOffset", JavaType = "OffsetDateTime", Imports = new() { "java.time.OffsetDateTime" } },
                new() { CSharpType = "System.TimeSpan", JavaType = "Duration", Imports = new() { "java.time.Duration" } },
                new() { CSharpType = "System.DateOnly", JavaType = "LocalDate", Imports = new() { "java.time.LocalDate" } },
                new() { CSharpType = "System.TimeOnly", JavaType = "LocalTime", Imports = new() { "java.time.LocalTime" } },

                // 其他常用类型
                new() { CSharpType = "System.Guid", JavaType = "UUID", Imports = new() { "java.util.UUID" } },
                new() { CSharpType = "System.Uri", JavaType = "URI", Imports = new() { "java.net.URI" } },
                new() { CSharpType = "System.Text.StringBuilder", JavaType = "StringBuilder", Imports = new() { "java.lang.StringBuilder" } },
                new() { CSharpType = "System.Exception", JavaType = "Exception", Imports = new() { "java.lang.Exception" } },
                new() { CSharpType = "System.ArgumentException", JavaType = "IllegalArgumentException", Imports = new() { "java.lang.IllegalArgumentException" } },
                new() { CSharpType = "System.ArgumentNullException", JavaType = "NullPointerException", Imports = new() { "java.lang.NullPointerException" } },
                new() { CSharpType = "System.InvalidOperationException", JavaType = "IllegalStateException", Imports = new() { "java.lang.IllegalStateException" } },
                new() { CSharpType = "System.NotImplementedException", JavaType = "UnsupportedOperationException", Imports = new() { "java.lang.UnsupportedOperationException" } },
                new() { CSharpType = "System IDisposable", JavaType = "AutoCloseable", Imports = new() { "java.lang.AutoCloseable" } },

                // LINQ 相关
                new() { CSharpType = "System.Linq.ILookup`2", JavaType = "Map", Imports = new() { "java.util.Map" } },
                new() { CSharpType = "System.Linq.IGrouping`2", JavaType = "Map.Entry", Imports = new() { "java.util.Map" } },
            },
            MethodMappings = new List<MethodMappingEntry>
            {
                // 集合方法映射
                new() { TypeName = "System.Collections.Generic.Dictionary", MethodName = "Add", JavaMethodName = "put" },
                new() { TypeName = "System.Collections.Generic.IDictionary", MethodName = "Add", JavaMethodName = "put" },
                new() { TypeName = "System.Collections.Generic.List", MethodName = "Add", JavaMethodName = "add" },
                new() { TypeName = "System.Collections.Generic.IList", MethodName = "Add", JavaMethodName = "add" },
                new() { TypeName = "System.Collections.Generic.List", MethodName = "Remove", JavaMethodName = "remove" },
                new() { TypeName = "System.Collections.Generic.IList", MethodName = "Remove", JavaMethodName = "remove" },
                new() { TypeName = "System.Collections.Generic.List", MethodName = "Contains", JavaMethodName = "contains" },
                new() { TypeName = "System.Collections.Generic.IList", MethodName = "Contains", JavaMethodName = "contains" },
                new() { TypeName = "System.Collections.Generic.Dictionary", MethodName = "ContainsKey", JavaMethodName = "containsKey" },
                new() { TypeName = "System.Collections.Generic.IDictionary", MethodName = "ContainsKey", JavaMethodName = "containsKey" },
                new() { TypeName = "System.Collections.Generic.Dictionary", MethodName = "TryGetValue", JavaMethodName = "get" },
                new() { TypeName = "System.Collections.Generic.IDictionary", MethodName = "TryGetValue", JavaMethodName = "get" },

                // 字符串方法映射
                new() { TypeName = "System.String", MethodName = "Equals", JavaMethodName = "equals" },
                new() { TypeName = "System.String", MethodName = "Substring", JavaMethodName = "substring" },
                new() { TypeName = "System.String", MethodName = "IndexOf", JavaMethodName = "indexOf" },
                new() { TypeName = "System.String", MethodName = "LastIndexOf", JavaMethodName = "lastIndexOf" },
                new() { TypeName = "System.String", MethodName = "Replace", JavaMethodName = "replace" },
                new() { TypeName = "System.String", MethodName = "Split", JavaMethodName = "split" },
                new() { TypeName = "System.String", MethodName = "Trim", JavaMethodName = "trim" },
                new() { TypeName = "System.String", MethodName = "ToLower", JavaMethodName = "toLowerCase" },
                new() { TypeName = "System.String", MethodName = "ToUpper", JavaMethodName = "toUpperCase" },
                new() { TypeName = "System.String", MethodName = "StartsWith", JavaMethodName = "startsWith" },
                new() { TypeName = "System.String", MethodName = "EndsWith", JavaMethodName = "endsWith" },
                new() { TypeName = "System.String", MethodName = "Length", JavaMethodName = "length()" },
                new() { TypeName = "System.String", MethodName = "IsEmpty", JavaMethodName = "isEmpty" },
            }
        };

        LoadMappings(config);
    }

    private void LoadMappings(string configPath)
    {
        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<TypeMappingConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (config != null)
        {
            LoadMappings(config);
        }
    }

    private void LoadMappings(TypeMappingConfig config)
    {
        // 加载类型映射
        foreach (var mapping in config.TypeMappings)
        {
            _typeMappings[mapping.CSharpType] = mapping;
        }

        // 加载方法映射
        foreach (var mapping in config.MethodMappings)
        {
            _methodMappings[(mapping.TypeName, mapping.MethodName)] = mapping;
        }

        // 加载命名空间映射
        foreach (var mapping in config.NamespaceMappings)
        {
            _namespaceMappings[mapping.CSharpNamespace] = mapping.JavaPackage;
        }
    }

    /// <summary>
    /// 映射 C# 类型到 Java 类型
    /// </summary>
    public string MapType(string csharpType)
    {
        // 尝试精确匹配
        if (_typeMappings.TryGetValue(csharpType, out var mapping))
        {
            return mapping.JavaType;
        }

        // 处理泛型类型（如 System.Collections.Generic.List`1）
        if (csharpType.Contains('`'))
        {
            var baseTypeName = csharpType.Split('`')[0];
            if (_typeMappings.TryGetValue(baseTypeName + "`1", out var genericMapping))
            {
                return genericMapping.JavaType;
            }
        }

        // 处理可空类型
        if (csharpType.StartsWith("System.Nullable<") || csharpType.StartsWith("System.Nullable`1<"))
        {
            var innerType = ExtractGenericArgument(csharpType);
            return MapType(innerType);
        }

        // 移除命名空间前缀，返回简单类型名
        var lastDot = csharpType.LastIndexOf('.');
        return lastDot >= 0 ? csharpType.Substring(lastDot + 1) : csharpType;
    }

    /// <summary>
    /// 获取类型所需的导入
    /// </summary>
    public List<string> GetRequiredImports(string csharpType)
    {
        if (_typeMappings.TryGetValue(csharpType, out var mapping))
        {
            return mapping.Imports;
        }

        // 处理泛型类型
        if (csharpType.Contains('`'))
        {
            var baseTypeName = csharpType.Split('`')[0];
            if (_typeMappings.TryGetValue(baseTypeName + "`1", out var genericMapping))
            {
                return genericMapping.Imports;
            }
        }

        return new List<string>();
    }

    /// <summary>
    /// 映射方法名
    /// </summary>
    public string? MapMethod(string typeName, string methodName)
    {
        if (_methodMappings.TryGetValue((typeName, methodName), out var mapping))
        {
            return mapping.JavaMethodName;
        }

        // 尝试匹配类型的任何基类型
        foreach (var (key, value) in _methodMappings)
        {
            if (key.MethodName == methodName && typeName.StartsWith(key.TypeName))
            {
                return value.JavaMethodName;
            }
        }

        return null;
    }

    /// <summary>
    /// 映射命名空间到包
    /// </summary>
    public string? MapNamespace(string ns)
    {
        foreach (var (pattern, replacement) in _namespaceMappings)
        {
            if (ns.StartsWith(pattern))
            {
                return ns.Replace(pattern, replacement);
            }
        }

        return null;
    }

    private string ExtractGenericArgument(string type)
    {
        var start = type.IndexOf('<');
        var end = type.LastIndexOf('>');
        if (start >= 0 && end > start)
        {
            return type.Substring(start + 1, end - start - 1);
        }
        return type;
    }

    /// <summary>
    /// 保存当前映射到配置文件
    /// </summary>
    public void SaveMappings(string configPath)
    {
        var config = new TypeMappingConfig
        {
            TypeMappings = _typeMappings.Values.ToList(),
            MethodMappings = _methodMappings.Values.ToList(),
            NamespaceMappings = _namespaceMappings.Select(kvp =>
                new NamespaceMappingEntry
                {
                    CSharpNamespace = kvp.Key,
                    JavaPackage = kvp.Value
                }).ToList()
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        File.WriteAllText(configPath, json);
    }
}
