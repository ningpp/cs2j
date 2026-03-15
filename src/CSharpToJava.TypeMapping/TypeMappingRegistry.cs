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
/// 类型映射配置异常
/// </summary>
public class TypeMappingConfigurationException : Exception
{
    public string? ConfigPath { get; }

    public TypeMappingConfigurationException(string message, string? configPath = null)
        : base(message)
    {
        ConfigPath = configPath;
    }

    public TypeMappingConfigurationException(string message, string configPath, Exception innerException)
        : base(message, innerException)
    {
        ConfigPath = configPath;
    }
}

/// <summary>
/// 类型映射注册表
/// </summary>
public class TypeMappingRegistry
{
    private const string DefaultConfigFileName = "TypeMappings.json";
    private const string DefaultConfigSubDirectory = "config";

    private readonly Dictionary<string, TypeMappingEntry> _typeMappings = new();
    private readonly Dictionary<(string TypeName, string MethodName), MethodMappingEntry> _methodMappings = new();
    private readonly Dictionary<string, string> _namespaceMappings = new();

    /// <summary>
    /// 解析配置文件路径，处理相对路径和默认路径
    /// </summary>
    private static string ResolveConfigPath(string? configPath)
    {
        if (!string.IsNullOrEmpty(configPath))
        {
            if (Path.IsPathRooted(configPath))
                return configPath;
            return Path.Combine(Directory.GetCurrentDirectory(), configPath);
        }

        // 默认路径: ./config/TypeMappings.json
        return Path.Combine(Directory.GetCurrentDirectory(), DefaultConfigSubDirectory, DefaultConfigFileName);
    }

    public TypeMappingRegistry(string? configPath = null)
    {
        var resolvedPath = ResolveConfigPath(configPath);

        if (!File.Exists(resolvedPath))
        {
            throw new TypeMappingConfigurationException(
                $"Type mapping configuration file not found: {resolvedPath}",
                resolvedPath);
        }

        LoadMappings(resolvedPath);
    }

    public TypeMappingRegistry(TypeMappingConfig config)
    {
        LoadMappings(config);
    }

    private void LoadMappings(string configPath)
    {
        try
        {
            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<TypeMappingConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (config == null)
            {
                throw new TypeMappingConfigurationException(
                    $"Failed to deserialize configuration file: {configPath}",
                    configPath);
            }

            LoadMappings(config);
        }
        catch (JsonException ex)
        {
            throw new TypeMappingConfigurationException(
                $"Invalid JSON in configuration file: {configPath}",
                configPath,
                ex);
        }
        catch (IOException ex)
        {
            throw new TypeMappingConfigurationException(
                $"Error reading configuration file: {configPath}",
                configPath,
                ex);
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
        // Handle generic type name format mismatch:
        //   Roslyn uses angle-bracket format: System.Collections.Generic.HashSet<T>
        //   JSON config uses backtick format:  System.Collections.Generic.HashSet`1
        foreach (var (key, value) in _methodMappings)
        {
            if (key.MethodName != methodName) continue;

            if (typeName.StartsWith(key.TypeName))
                return value.JavaMethodName;

            // Normalize backtick suffix: "HashSet`1" → "HashSet", then match "HashSet<..."
            var backtickIdx = key.TypeName.LastIndexOf('`');
            if (backtickIdx >= 0)
            {
                var baseKeyName = key.TypeName.Substring(0, backtickIdx);
                bool baseNameMatches = typeName.StartsWith(baseKeyName + "<") || typeName == baseKeyName;
                if (baseNameMatches)
                {
                    // Verify arity: the backtick number must match the number of type arguments in typeName.
                    // This prevents "System.Tuple`2: Item1 → getKey" matching System.Tuple<A, B, C, D> (arity 4).
                    if (int.TryParse(key.TypeName.Substring(backtickIdx + 1), out int expectedArity))
                    {
                        if (typeName == baseKeyName)
                        {
                            // Non-generic usage — only match if expectedArity == 0 (no type params)
                            if (expectedArity == 0) return value.JavaMethodName;
                        }
                        else
                        {
                            // Count top-level commas inside <...> to determine actual arity.
                            // openAnglePos must point at the '<' character itself (not one past it).
                            int actualArity = CountTopLevelTypeArgs(typeName, baseKeyName.Length);
                            if (actualArity == expectedArity) return value.JavaMethodName;
                        }
                    }
                    else
                    {
                        return value.JavaMethodName; // No arity in config key, use startsWith (legacy)
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 映射命名空间到包
    /// </summary>
    public string? MapNamespace(string ns)
    {
        // Handle global namespace (empty string) with exact match only
        if (string.IsNullOrEmpty(ns))
        {
            foreach (var (pattern, replacement) in _namespaceMappings)
            {
                if (pattern == "")
                    return replacement;
            }
            return null;
        }

        foreach (var (pattern, replacement) in _namespaceMappings)
        {
            if (!string.IsNullOrEmpty(pattern) && ns.StartsWith(pattern))
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
    /// Count the number of top-level type arguments inside the &lt;&gt; at position `openAnglePos`.
    /// e.g., "System.Tuple&lt;int, int, double, double&gt;" with openAnglePos pointing at '&lt;' returns 4.
    /// </summary>
    private static int CountTopLevelTypeArgs(string typeName, int openAnglePos)
    {
        int depth = 0;
        int count = 1;
        for (int i = openAnglePos; i < typeName.Length; i++)
        {
            char c = typeName[i];
            if (c == '<') depth++;
            else if (c == '>') { depth--; if (depth < 0) break; }
            else if (c == ',' && depth == 1) count++;
        }
        return count;
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
