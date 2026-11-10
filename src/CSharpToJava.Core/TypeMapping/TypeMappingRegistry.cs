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

    /// <summary>
    /// 工程内类型重定向：把源码里声明的某个类型引用改写为另一个源码类型。
    /// 与 typeMappings 不同，它显式允许覆盖项目自身定义的类（平台能力替换，
    /// 例如 Java 无 IL 生成 → Reflection.Emit 版成员访问器改用反射回退版）。
    /// </summary>
    [JsonPropertyName("sourceTypeRedirects")]
    public List<SourceTypeRedirectEntry> SourceTypeRedirects { get; set; } = new();
}

/// <summary>工程内类型重定向条目（csharp/java 都用源码类型的 C# 全名/Java 简名）。</summary>
public class SourceTypeRedirectEntry
{
    [JsonPropertyName("csharp")]
    public string CSharpType { get; set; } = string.Empty;

    [JsonPropertyName("java")]
    public string JavaType { get; set; } = string.Empty;
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
    private readonly Dictionary<string, string> _sourceTypeRedirects = new();
    private Dictionary<string, string>? _sourceRedirectSimpleIndex;

    // Lazy-built index: simpleName`arity → (configKey, javaType)
    // e.g. "IEnumerable`1" → ("System.Collections.Generic.IEnumerable`1", "Iterable")
    private Dictionary<string, (string ConfigKey, string JavaType)>? _simpleNameIndex;

    // Eager-built canonical-name indexes (see BuildCanonicalNameIndexes).
    // generic base name (config key without its `arity suffix) → JavaType;
    // Java simple name → first configured import ending in "." + simpleName.
    // Both use TryAdd so the first inserted mapping wins, exactly like the
    // linear scans they replace.
    private readonly Dictionary<string, string> _genericTypeBaseIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _javaSimpleTypeImportIndex = new(StringComparer.Ordinal);

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

        // 加载工程内类型重定向
        foreach (var redirect in config.SourceTypeRedirects)
        {
            if (!string.IsNullOrEmpty(redirect.CSharpType) && !string.IsNullOrEmpty(redirect.JavaType))
                _sourceTypeRedirects[redirect.CSharpType] = redirect.JavaType;
        }

        BuildCanonicalNameIndexes();
    }

    /// <summary>
    /// Replaces the two linear scans that used to run on every canonical-name lookup:
    /// the backtick-aware fallback in <see cref="ResolveJavaCanonicalName"/> and the
    /// import search in <c>MapToCanonical</c>. Both scans iterated <c>_typeMappings</c>
    /// in insertion order, so the index keeps the first match for each key.
    /// </summary>
    private void BuildCanonicalNameIndexes()
    {
        foreach (var (configKey, mapping) in _typeMappings)
        {
            var backtickIdx = configKey.LastIndexOf('`');
            if (backtickIdx >= 0)
            {
                var keyBase = configKey.Substring(0, backtickIdx);
                _genericTypeBaseIndex.TryAdd(keyBase, mapping.JavaType);
            }

            // MapToCanonical only reaches the import scan for non-canonical Java
            // names, so canonical entries cannot contribute a match.
            if (mapping.JavaType.Contains('.')) continue;
            var suffix = "." + mapping.JavaType;
            foreach (var import in mapping.Imports)
            {
                if (import.EndsWith(suffix, StringComparison.Ordinal))
                {
                    _javaSimpleTypeImportIndex.TryAdd(mapping.JavaType, import);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 工程内类型重定向：源码类型全名 → 目标 Java 简名；无重定向时返回 null。
    /// </summary>
    public string? MapSourceTypeRedirect(string csharpType)
        => _sourceTypeRedirects.TryGetValue(csharpType, out var javaType) ? javaType : null;

    /// <summary>
    /// 按简单名查工程内类型重定向。语法回退路径只有简单名可用（合并后的 partial 树
    /// 无法做语义解析），所以声明侧与引用侧都必须能按简单名命中。
    /// </summary>
    public string? MapSourceTypeRedirectBySimpleName(string simpleName)
    {
        _sourceRedirectSimpleIndex ??= BuildSourceRedirectSimpleIndex();
        return _sourceRedirectSimpleIndex.TryGetValue(simpleName, out var javaType) ? javaType : null;
    }

    private Dictionary<string, string> BuildSourceRedirectSimpleIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (csharpType, javaType) in _sourceTypeRedirects)
        {
            var dot = csharpType.LastIndexOf('.');
            var simpleName = dot >= 0 ? csharpType[(dot + 1)..] : csharpType;
            index[simpleName] = javaType;
        }
        return index;
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

        // J8 (CS2J3320): the ref/out/in preprocessor only emits ByRef`1 and
        // RefStructByRef`1 from the Cs2jByRef namespace; any other form must
        // fail fast instead of emitting a bare unresolved class name.
        if (csharpType.StartsWith("Cs2jByRef.", StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                "CS2J3320: unmapped Cs2jByRef form '" + csharpType + "' — the ref/out/in " +
                "preprocessor only emits ByRef<T>/RefStructByRef<T>; hand-written " +
                "support types are not converted (catalog §4 risk 12).");
        }

        // 处理可空类型
        if (csharpType.StartsWith("System.Nullable<", StringComparison.Ordinal)
            || csharpType.StartsWith("System.Nullable`1<", StringComparison.Ordinal))
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
            // Generic type lookups with angle brackets fall through to the backtick normalization below
            // and, if still unresolved, to the final generic-definition fallback.
            if (typeName.StartsWith(key.TypeName, StringComparison.Ordinal)
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
                bool baseNameMatches = typeName.StartsWith(baseKeyName + "<", StringComparison.Ordinal)
                    || typeName == baseKeyName;
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

        // Final fallback for constructed generic types whose definition is stored without
        // a backtick suffix (e.g. "System.IEquatable<T>" → "System.IEquatable" for
        // Equals → equalsTo). This runs after the arity-aware backtick loop so that
        // mappings like "System.Action`1" take precedence over the non-generic "System.Action".
        var genericBaseName = GetGenericTypeDefinitionName(typeName);
        if (genericBaseName != null)
        {
            if (_methodMappings.TryGetValue((genericBaseName, methodName), out entries))
            {
                var resolved = ResolveMethodEntry(entries, paramCount);
                if (resolved != null) return resolved.JavaMethodName;
            }
        }

        return null;
    }

    /// <summary>
    /// 接收者类型无法绑定时的兜底映射：按「方法映射条目的类型简名 == 接收者标识符」
    /// 检索映射。转换编译只引用框架程序集（测试框架 xunit 等不在其中），
    /// <c>Assert.Throws&lt;T&gt;(...)</c> 这类调用没有方法符号，默认只做 Pascal→camel
    /// 改名；<c>Throws</c> 撞上 Java 关键字 <c>throws</c> 又被转义成 <c>throwsValue</c>，
    /// 与 compat 的 <c>throws_</c> 不符。仅当候选映射唯一时返回，避免同简名的
    /// MSTest/NUnit Assert 混淆。
    /// </summary>
    public string? MapMethodByReceiverSimpleName(string receiverSimpleName, string methodName)
    {
        string? result = null;
        foreach (var (key, entryList) in _methodMappings)
        {
            if (key.MethodName != methodName) continue;
            var lastDot = key.TypeName.LastIndexOf('.');
            var simpleName = lastDot >= 0 ? key.TypeName[(lastDot + 1)..] : key.TypeName;
            if (!string.Equals(simpleName, receiverSimpleName, StringComparison.Ordinal)) continue;

            var resolved = ResolveMethodEntry(entryList, paramCount: null);
            if (resolved == null) continue;
            if (result != null && !string.Equals(result, resolved.JavaMethodName, StringComparison.Ordinal))
                return null; // ambiguous: leave the default camelCase rendering.
            result = resolved.JavaMethodName;
        }

        return result;
    }

    /// <summary>
    /// 解析最佳匹配的方法映射条目
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
    /// Resolves a C# fully-qualified type name (as used in TypeMappings.json) to its
    /// canonical Java type name.
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

        // Backtick-notation fallback, now an O(1) index lookup. Config keys never
        // contain '<', so "csharpTypeName.StartsWith(keyBase + "<")" is exactly
        // "baseTypeName == keyBase"; the index preserves the original first-match
        // ordering of the linear scan (see BuildCanonicalNameIndexes).
        return _genericTypeBaseIndex.TryGetValue(baseTypeName, out var javaType)
            ? MapToCanonical(javaType)
            : null;
    }

    /// <summary>
    /// Maps a Java simple type name (from config, e.g. "ArrayList") to its canonical form
    /// (e.g. "java.util.ArrayList") by looking up the configured imports.
    /// </summary>
    private string? MapToCanonical(string javaType)
    {
        // Already canonical (contains dot)
        if (javaType.Contains('.'))
            return javaType;

        // Search through type imports configured for this mapping. Prebuilt in
        // BuildCanonicalNameIndexes with ordinal suffix matching.
        return _javaSimpleTypeImportIndex.TryGetValue(javaType, out var import) ? import : null;
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

    /// <summary>
    /// Checks whether the namespace has an explicit (exact) mapping in the
    /// namespaceMappings configuration, as opposed to a derived mapping obtained
    /// via prefix replacement.  For example, "System.Xml" has an explicit mapping
    /// to "dotnet.xml", while "System.IO" only has a derived mapping from the
    /// "System" → "dotnet.system" catch-all.
    /// </summary>
    /// <summary>
    /// All namespace prefixes that have an explicit namespaceMappings entry.
    /// Used by the unsupported-domain check to allow namespaces backed by a
    /// compatibility package (e.g. System.Windows.Forms → dotnet.winforms.swing).
    /// </summary>
    public IReadOnlyList<string> GetExplicitNamespaceMappings()
    {
        return _namespaceMappings.Keys.ToList();
    }
    public bool HasExplicitNamespaceMapping(string ns)
    {
        return _namespaceMappings.ContainsKey(ns);
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
    /// Returns the generic definition name for a constructed generic type name
    /// (e.g. "System.IEquatable<T>" → "System.IEquatable"). Returns null when
    /// the type name is not constructed generic.
    /// </summary>
    private static string? GetGenericTypeDefinitionName(string typeName)
    {
        var openAngle = typeName.IndexOf('<');
        if (openAngle > 0)
            return typeName.Substring(0, openAngle);
        return null;
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

