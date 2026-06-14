using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpToJava.TypeMapping.JavaModel;

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
    private readonly Dictionary<(string TypeName, string MethodName), List<MethodMappingEntry>> _methodMappings = new();
    private readonly Dictionary<string, string> _namespaceMappings = new();

    // Lazy-built index: simpleName`arity → (configKey, javaType)
    // e.g. "IEnumerable`1" → ("System.Collections.Generic.IEnumerable`1", "Iterable")
    private Dictionary<string, (string ConfigKey, string JavaType)>? _simpleNameIndex;

    /// <summary>
    /// Optional Java standard-library metadata index.  When set, enables auto-deduction of
    /// method name mappings (PascalCase → camelCase) and validation of mapped Java types.
    /// </summary>
    public JavaLibraryIndex? JavaLibrary { get; private set; }

    /// <summary>
    /// Injects the Java standard-library metadata index.  Should be called once after
    /// construction when the <c>config/java/</c> directory is available.
    /// </summary>
    public void SetJavaLibraryIndex(JavaLibraryIndex index) => JavaLibrary = index;

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
            var key = (mapping.TypeName, mapping.MethodName);
            if (!_methodMappings.TryGetValue(key, out var list))
            {
                list = new List<MethodMappingEntry>();
                _methodMappings[key] = list;
            }
            list.Add(mapping);
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
    /// Returns true when <paramref name="csharpType"/> has an explicit type mapping entry.
    /// This is useful for distinguishing framework/BCL mappings from project types that
    /// merely fall back to their simple name.
    /// </summary>
    public bool HasTypeMapping(string csharpType) => _typeMappings.ContainsKey(csharpType);

    /// <summary>
    /// Fuzzy type lookup by simple name with optional generic arity.
    /// Uses an O(1) lazy-built index; falls back to linear scan only if the index somehow misses.
    /// </summary>
    public string MapTypeBySimpleName(string simpleName, int? genericArity = null)
    {
        // Try exact match first
        var result = MapType(simpleName);
        if (result != simpleName) return result;

        EnsureSimpleNameIndex();
        var suffix = BuildSuffix(simpleName, genericArity);
        if (_simpleNameIndex!.TryGetValue(suffix, out var entry))
            return entry.JavaType;

        return simpleName;
    }

    /// <summary>
    /// Returns the matching config key for a simple-name lookup (used for import resolution).
    /// Returns null when no config entry matches.
    /// </summary>
    public string? FindConfigKeyBySimpleName(string simpleName, int? genericArity = null)
    {
        EnsureSimpleNameIndex();
        var suffix = BuildSuffix(simpleName, genericArity);
        if (_simpleNameIndex!.TryGetValue(suffix, out var entry))
            return entry.ConfigKey;

        return null;
    }

    private static string BuildSuffix(string simpleName, int? genericArity) =>
        genericArity.HasValue ? $"{simpleName}`{genericArity.Value}" : simpleName;

    private void EnsureSimpleNameIndex()
    {
        if (_simpleNameIndex != null) return;

        _simpleNameIndex = new(StringComparer.Ordinal);
        foreach (var (configKey, mapping) in _typeMappings)
        {
            // Extract simple name: last dot-segment of the config key
            // e.g. "System.Collections.Generic.IEnumerable`1" → "IEnumerable`1"
            var lastDot = configKey.LastIndexOf('.');
            var simple = lastDot >= 0 ? configKey.Substring(lastDot + 1) : configKey;

            if (!_simpleNameIndex.ContainsKey(simple))
                _simpleNameIndex[simple] = (configKey, mapping.JavaType);
        }
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
        => MapMethod(typeName, methodName, paramCount: null);

    /// <summary>
    /// 映射方法名（参数数量感知重载）。
    /// When multiple entries exist for the same (type, method) pair, uses <paramref name="paramCount"/>
    /// and the <see cref="MethodMappingEntry.Signature"/> field to disambiguate.
    /// </summary>
    public string? MapMethod(string typeName, string methodName, int? paramCount)
    {
        if (_methodMappings.TryGetValue((typeName, methodName), out var entries))
        {
            var resolved = ResolveMethodEntry(entries, paramCount);
            if (resolved != null) return resolved.JavaMethodName;
        }

        // 尝试匹配类型的任何基类型
        // Handle generic type name format mismatch:
        //   Roslyn uses angle-bracket format: System.Collections.Generic.HashSet<T>
        //   JSON config uses backtick format:  System.Collections.Generic.HashSet`1
        foreach (var (key, entryList) in _methodMappings)
        {
            if (key.MethodName != methodName) continue;

            // Guard: ensure the startsWith match is exact or a namespace prefix (dot),
            // not a false positive where "System.Action<string>" matches "System.Action" (non-generic).
            // Generic type lookups with angle brackets should fall through to the backtick normalization below.
            if (typeName.StartsWith(key.TypeName)
                && (typeName.Length == key.TypeName.Length || typeName[key.TypeName.Length] == '.'))
            {
                var resolved = ResolveMethodEntry(entryList, paramCount);
                if (resolved != null) return resolved.JavaMethodName;
            }

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
                            if (expectedArity == 0)
                            {
                                var resolved = ResolveMethodEntry(entryList, paramCount);
                                if (resolved != null) return resolved.JavaMethodName;
                            }
                        }
                        else
                        {
                            // Count top-level commas inside <...> to determine actual arity.
                            // openAnglePos must point at the '<' character itself (not one past it).
                            int actualArity = CountTopLevelTypeArgs(typeName, baseKeyName.Length);
                            if (actualArity == expectedArity)
                            {
                                var resolved = ResolveMethodEntry(entryList, paramCount);
                                if (resolved != null) return resolved.JavaMethodName;
                            }
                        }
                    }
                    else
                    {
                        var resolved = ResolveMethodEntry(entryList, paramCount);
                        if (resolved != null) return resolved.JavaMethodName;
                    }
                }
            }
        }

        // Auto-deduction: when no explicit config mapping exists and JavaLibraryIndex is
        // available, try PascalCase → camelCase conversion and check if the Java type has
        // a matching method.
        if (JavaLibrary is not null)
        {
            var javaTypeName = ResolveJavaCanonicalName(typeName);
            if (javaTypeName is not null)
            {
                var camelCase = PascalToCamelCase(methodName);
                if (camelCase != methodName) // only try if conversion actually changed something
                {
                    var methods = JavaLibrary.FindMethods(javaTypeName, camelCase);
                    if (methods.Count > 0)
                        return camelCase;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the best matching method entry from a list, using <paramref name="paramCount"/>
    /// to disambiguate when the <see cref="MethodMappingEntry.Signature"/> field is present.
    /// Falls back to the first entry without a signature constraint.
    /// </summary>
    private static MethodMappingEntry? ResolveMethodEntry(List<MethodMappingEntry> entries, int? paramCount)
    {
        if (entries.Count == 1)
        {
            var entry = entries[0];
            if (paramCount.HasValue
                && entry.Signature != null
                && int.TryParse(entry.Signature, out var sigParamCount)
                && sigParamCount != paramCount.Value)
            {
                return null;
            }

            return entry;
        }

        // If paramCount is provided, try to match entries with a signature that specifies param count
        if (paramCount.HasValue)
        {
            foreach (var entry in entries)
            {
                if (entry.Signature != null
                    && int.TryParse(entry.Signature, out var sigParamCount)
                    && sigParamCount == paramCount.Value)
                {
                    return entry;
                }
            }
        }

        // Fall back to the entry without a signature (generic fallback)
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Signature))
                return entry;
        }

        // If the caller supplied an arity and every candidate is signature-constrained,
        // a mismatch means this mapping does not apply to the overload being converted.
        if (paramCount.HasValue)
            return null;

        // Without arity information, preserve the historical fallback.
        return entries[0];
    }

    /// <summary>
    /// Checks whether the given Java method exists on the specified Java type according to
    /// the loaded Java standard-library metadata.  Returns <see langword="true"/> when the
    /// metadata is not available (optimistic).
    /// </summary>
    public bool ValidateMethodExists(string javaTypeName, string javaMethodName)
    {
        if (JavaLibrary is null) return true; // No metadata → optimistic
        return JavaLibrary.FindMethods(javaTypeName, javaMethodName).Count > 0;
    }

    /// <summary>
    /// Checks whether the given Java type exists in the loaded Java standard-library metadata.
    /// Returns <see langword="true"/> when the metadata is not available (optimistic).
    /// </summary>
    public bool ValidateTypeExists(string javaTypeName)
    {
        if (JavaLibrary is null) return true; // No metadata → optimistic
        return JavaLibrary.FindType(javaTypeName) is not null;
    }

    /// <summary>
    /// Resolves a C# fully-qualified type name (as used in TypeMappings.json) to the
    /// canonical Java type name suitable for <see cref="JavaLibraryIndex"/> lookups.
    /// Returns <see langword="null"/> if no mapping is found.
    /// </summary>
    public string? ResolveJavaCanonicalName(string csharpTypeName)
    {
        // Try direct config-based mapping first (strip generics for lookup).
        var baseTypeName = csharpTypeName;
        var angleIdx = baseTypeName.IndexOf('<');
        if (angleIdx > 0)
            baseTypeName = baseTypeName.Substring(0, angleIdx);

        // Try exact match
        if (_typeMappings.TryGetValue(csharpTypeName, out var entry))
            return MapToCanonical(entry.JavaType);

        if (_typeMappings.TryGetValue(baseTypeName, out entry))
            return MapToCanonical(entry.JavaType);

        // Try with backtick notation
        foreach (var kvp in _typeMappings)
        {
            var backtickIdx = kvp.Key.LastIndexOf('`');
            if (backtickIdx >= 0)
            {
                var keyBase = kvp.Key.Substring(0, backtickIdx);
                if (baseTypeName == keyBase || csharpTypeName.StartsWith(keyBase + "<"))
                    return MapToCanonical(kvp.Value.JavaType);
            }
        }

        return null;
    }

    /// <summary>
    /// Maps a Java simple type name (from config, e.g. "ArrayList") to its canonical form
    /// (e.g. "java.util.ArrayList") by looking up imports or using the JavaLibraryIndex.
    /// </summary>
    private string? MapToCanonical(string javaType)
    {
        // Already canonical (contains dot)
        if (javaType.Contains('.'))
            return javaType;

        // Try well-known java.lang types
        if (JavaLibrary?.FindType("java.lang." + javaType) is not null)
            return "java.lang." + javaType;

        // Try java.util types (very common in C# → Java mapping)
        if (JavaLibrary?.FindType("java.util." + javaType) is not null)
            return "java.util." + javaType;

        // Search through type imports configured for this mapping
        foreach (var mapping in _typeMappings.Values)
        {
            if (mapping.JavaType != javaType) continue;
            foreach (var import in mapping.Imports)
            {
                if (import.EndsWith("." + javaType))
                    return import;
            }
        }

        return null;
    }

    /// <summary>
    /// Converts a PascalCase identifier to Java camelCase (first letter lowered).
    /// </summary>
    public static string PascalToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
            return name;
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
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

        var bestMatch = _namespaceMappings
            .Where(kvp => !string.IsNullOrEmpty(kvp.Key) && ns.StartsWith(kvp.Key, StringComparison.Ordinal))
            .OrderByDescending(kvp => kvp.Key.Length)
            .FirstOrDefault();

        return string.IsNullOrEmpty(bestMatch.Key)
            ? null
            : ns.Replace(bestMatch.Key, bestMatch.Value);
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
        int tupleDepth = 0;
        int count = 1;
        for (int i = openAnglePos; i < typeName.Length; i++)
        {
            char c = typeName[i];
            if (c == '<') depth++;
            else if (c == '>') { depth--; if (depth < 0) break; }
            else if (c == '(' && depth >= 1) tupleDepth++;
            else if (c == ')' && tupleDepth > 0) tupleDepth--;
            else if (c == ',' && depth == 1 && tupleDepth == 0) count++;
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
            MethodMappings = _methodMappings.Values.SelectMany(list => list).ToList(),
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
