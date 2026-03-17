using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.PartialType;
using CSharpToJava.Core.Transformers;
using System.Text;

namespace CSharpToJava.Core.Context;

/// <summary>
/// 转换选项
/// </summary>
public class ConversionOptions
{
    /// <summary>
    /// 目标 Java 版本
    /// </summary>
    public JavaVersion TargetJavaVersion { get; set; } = JavaVersion.Java17;

    /// <summary>
    /// 类型映射配置文件路径
    /// </summary>
    public string? TypeMappingConfigPath { get; set; }

    /// <summary>
    /// 是否生成 JavaDoc 注释
    /// </summary>
    public bool GenerateJavaDoc { get; set; } = true;

    /// <summary>
    /// 是否使用 Java Record（对于 C# record）
    /// </summary>
    public bool UseRecords { get; set; } = true;

    /// <summary>
    /// 是否使用 Optional 替代 nullable
    /// </summary>
    public bool UseOptionalForNullable { get; set; } = false;

    /// <summary>
    /// 命名空间到包的映射规则
    /// </summary>
    public Dictionary<string, string> NamespaceMappings { get; set; } = new();

    /// <summary>
    /// 是否启用 LINQ 预处理（将 LINQ 转换为过程化代码）
    /// </summary>
    public bool EnableLinqRewrite { get; set; } = true;

    /// <summary>
    /// When true, LINQ chains are converted to Java Stream API calls (.stream().filter().map()...)
    /// instead of procedural loops via LinqRewriter. Default: true for Java 9+, false for Java 8.
    /// When set explicitly, overrides the version-based default.
    /// </summary>
    public bool? PreferStreamApi { get; set; }

    /// <summary>
    /// Resolved value: uses explicit setting if provided, otherwise defaults based on Java version.
    /// Java 9+ defaults to Stream API; Java 8 defaults to procedural (LinqRewriter).
    /// </summary>
    public bool EffectivePreferStreamApi => PreferStreamApi ?? (TargetJavaVersion >= JavaVersion.Java11);

    /// <summary>
    /// When enabled, extension methods on known types are promoted to instance methods on those types,
    /// and the receiver ('this') parameter is stripped from the Java method signature.
    /// </summary>
    public bool RewriteExtensionMethods { get; set; } = false;
}

/// <summary>
/// Java 版本枚举
/// </summary>
public enum JavaVersion
{
    Java8 = 8,
    Java11 = 11,
    Java17 = 17,
    Java21 = 21,
    Java25 = 25,
}

/// <summary>
/// 转换上下文 - 存储转换过程中的状态信息
/// </summary>
public class ConversionContext
{
    private readonly Stack<string> _namespaceStack = new();
    private readonly Stack<JavaTypeDeclaration> _typeStack = new();
    private readonly Stack<IMethodSymbol?> _methodStack = new();

    public ConversionOptions Options { get; }
    public SemanticModel? SemanticModel { get; set; }
    public TypeMapping.TypeMappingRegistry TypeMappings { get; }
    public DiagnosticCollector Diagnostics { get; } = new();

    /// <summary>
    /// 当前命名空间
    /// </summary>
    public string CurrentNamespace => _namespaceStack.Count > 0 ? _namespaceStack.Peek() : string.Empty;

    /// <summary>
    /// 当前类型
    /// </summary>
    public JavaTypeDeclaration? CurrentType => _typeStack.Count > 0 ? _typeStack.Peek() : null;

    /// <summary>
    /// 当前方法符号
    /// </summary>
    public IMethodSymbol? CurrentMethod => _methodStack.Count > 0 ? _methodStack.Peek() : null;

    /// <summary>
    /// 是否在 async 上下文中
    /// </summary>
    public bool IsInAsyncContext { get; set; }

    /// <summary>
    /// 是否在 lambda 表达式中
    /// </summary>
    public bool IsInLambdaContext { get; set; }

    /// <summary>
    /// 是否在 yield return 方法中（转换为列表积累模式）
    /// </summary>
    public bool IsInYieldMethod { get; set; }

    /// <summary>
    /// Whether the current type being converted is an interface body.
    /// </summary>
    public bool IsInInterfaceBody => CurrentType is Java.JavaInterfaceDeclaration;

    /// <summary>
    /// Checks whether the current Java type implements the given interface name (e.g. "Map", "List").
    /// Matches both the raw name and generic forms like Map&lt;K,V&gt;.
    /// </summary>
    public bool ImplementsInterface(string interfaceName)
    {
        if (CurrentType is Java.JavaClassDeclaration classDecl)
        {
            return classDecl.ImplementedTypes.Any(t =>
                t == interfaceName || t.StartsWith(interfaceName + "<"));
        }
        return false;
    }

    /// <summary>
    /// 收集的导入语句
    /// </summary>
    public HashSet<string> ImportedTypes { get; } = new();

    /// <summary>
    /// Names of local variables whose initializer is a Java Stream expression.
    /// Populated by TransformLocalDeclaration when a stream-typed initializer is detected.
    /// Used by TransformForEachStatement to detect for-each over stream-typed variables.
    /// Cleared on entering each method to prevent cross-method contamination.
    /// </summary>
    public HashSet<string> StreamLocalVariables { get; } = new();

    /// <summary>
    /// Maps LINQ query 'let' variable names to their inlined Java expressions.
    /// Set by TransformQueryBodyRecursive when processing let clauses.
    /// Used by TransformIdentifier to inline let variables, avoiding variable scoping issues
    /// when multiple 'let' clauses are chained (e.g. let left = ...; where ...; let right = ...).
    /// Cleared after the query expression is fully processed.
    /// </summary>
    public Dictionary<string, string> QueryLetAliases { get; set; } = new();

    /// <summary>
    /// 类型符号到 Java 类型的缓存
    /// </summary>
    public Dictionary<ITypeSymbol, string> TypeCache { get; } = new();

    /// <summary>
    /// Synthesized Java record definitions generated from C# anonymous types.
    /// Key is the structural key (ordered "name:type" pairs), value is the record info.
    /// </summary>
    private readonly Dictionary<string, SynthesizedRecordInfo> _synthesizedRecords = new(StringComparer.Ordinal);

    /// <summary>
    /// Names already used by synthesized records, to avoid conflicts.
    /// </summary>
    private readonly HashSet<string> _synthesizedRecordNames = new(StringComparer.Ordinal);

    /// <summary>
    /// All synthesized records registered during conversion.
    /// </summary>
    public IReadOnlyCollection<SynthesizedRecordInfo> SynthesizedRecords => _synthesizedRecords.Values;

    /// <summary>
    /// Try to find an existing synthesized record with the given structural key.
    /// </summary>
    public bool TryGetSynthesizedRecord(string structuralKey, out SynthesizedRecordInfo? record)
    {
        return _synthesizedRecords.TryGetValue(structuralKey, out record);
    }

    /// <summary>
    /// Register a new synthesized record. If the name conflicts with an existing record,
    /// a numeric suffix is appended.
    /// </summary>
    public void RegisterSynthesizedRecord(SynthesizedRecordInfo record)
    {
        // Ensure unique name
        var name = record.RecordName;
        if (_synthesizedRecordNames.Contains(name))
        {
            int suffix = 2;
            while (_synthesizedRecordNames.Contains(name + suffix))
                suffix++;
            name = name + suffix;
            record = new SynthesizedRecordInfo(name, record.Fields, record.StructuralKey);
        }
        _synthesizedRecordNames.Add(name);
        _synthesizedRecords[record.StructuralKey] = record;
    }

    /// <summary>
    /// Clear synthesized records (per-file reset).
    /// </summary>
    public void ClearSynthesizedRecords()
    {
        _synthesizedRecords.Clear();
        _synthesizedRecordNames.Clear();
    }

    /// <summary>
    /// 需要合成的唯一名称计数器
    /// </summary>
    private Dictionary<string, int> _syntheticNameCounters = new();

    /// <summary>
    /// 跟踪已合并的 partial 类型
    /// Key: Type name, Value: MergedTypeDeclaration
    /// </summary>
    private Dictionary<string, MergedTypeDeclaration> _mergedPartialTypes = new();

    /// <summary>
    /// 跟踪哪些语法节点是 partial 类型的一部分
    /// Key: Syntax node span, Value: merged type name
    /// </summary>
    private Dictionary<string, string> _partialSyntaxNodeMap = new();

    /// <summary>
    /// 完整的项目编译，用于跨文件语义分析
    /// </summary>
    public CSharpCompilation? ProjectCompilation { get; set; }

    /// <summary>
    /// Set of [Flags] enum names that should be mapped to int in Java.
    /// Populated by EnumTransformer when a [Flags] enum is encountered.
    /// </summary>
    private static readonly HashSet<string> _flagsEnumNames = new(StringComparer.Ordinal);

    public void RegisterFlagsEnum(string enumName)
    {
        _flagsEnumNames.Add(enumName);
    }

    public bool IsFlagsEnum(string enumName) => _flagsEnumNames.Contains(enumName);

    /// <summary>
    /// Pre-statements to emit before the current statement being transformed.
    /// Used when an expression transformation must be split into multiple statements
    /// (e.g., chain property assignment used as method argument: list.Add(obj.Prop = local = expr)
    ///  → local = expr; obj.setProp(local); list.add(local))
    /// </summary>
    private readonly List<string> _pendingPreStatements = new();

    public void AddPreStatement(string statement)
    {
        _pendingPreStatements.Add(statement);
    }

    public IReadOnlyList<string> DrainPreStatements()
    {
        var result = _pendingPreStatements.ToList();
        _pendingPreStatements.Clear();
        return result;
    }

    public bool HasPendingPreStatements => _pendingPreStatements.Count > 0;

    /// <summary>
    /// Post-statements to emit after the current statement being transformed.
    /// Used to read back out-parameter holder values into the original variables after a call.
    /// </summary>
    private readonly List<string> _pendingPostStatements = new();

    public void AddPostStatement(string statement)
    {
        _pendingPostStatements.Add(statement);
    }

    public IReadOnlyList<string> DrainPostStatements()
    {
        var result = _pendingPostStatements.ToList();
        _pendingPostStatements.Clear();
        _activeRefHolders.Clear();  // holders' writebacks emitted; lifecycle complete
        return result;
    }

    public bool HasPendingPostStatements => _pendingPostStatements.Count > 0;

    /// <summary>
    /// Maps local variable names to their current active ref-holder names.
    /// An entry is alive while the holder's writeback is still pending in _pendingPostStatements.
    /// Cleared when post-statements are drained (writebacks emitted) or when entering a new method.
    /// </summary>
    private readonly Dictionary<string, string> _activeRefHolders = new(StringComparer.Ordinal);

    /// <summary>
    /// Tracks how many ref holders have been allocated per variable name in the current method.
    /// Used to generate unique names (_varRef, _varRef2, etc.) when a variable is passed as ref
    /// in multiple independent groups of statements (i.e. after a previous holder's writeback was drained).
    /// </summary>
    private readonly Dictionary<string, int> _refHolderAllocCounts = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns true and sets holderName if an active (not-yet-drained) ref holder exists for the given variable.
    /// </summary>
    public bool TryGetActiveRefHolder(string varName, out string holderName)
    {
        return _activeRefHolders.TryGetValue(varName, out holderName!);
    }

    /// <summary>
    /// Allocates a unique ref-holder name for the given variable and marks it active.
    /// First allocation returns "_varRef"; subsequent (after a lifecycle drain) returns "_varRef2", "_varRef3", etc.
    /// </summary>
    public string AllocateRefHolderName(string varName)
    {
        var count = _refHolderAllocCounts.GetValueOrDefault(varName, 0);
        _refHolderAllocCounts[varName] = count + 1;
        var holderName = count == 0 ? $"_{varName}Ref" : $"_{varName}Ref{count + 1}";
        _activeRefHolders[varName] = holderName;
        return holderName;
    }

    public ConversionContext(ConversionOptions options, TypeMapping.TypeMappingRegistry typeMappings)
    {
        Options = options;
        TypeMappings = typeMappings;
    }

    /// <summary>
    /// 进入命名空间
    /// </summary>
    public void EnterNamespace(string ns)
    {
        _namespaceStack.Push(ns);
    }

    /// <summary>
    /// 离开命名空间
    /// </summary>
    public void LeaveNamespace()
    {
        if (_namespaceStack.Count > 0)
            _namespaceStack.Pop();
    }

    /// <summary>
    /// 进入类型
    /// </summary>
    public void EnterType(JavaTypeDeclaration type)
    {
        _typeStack.Push(type);
    }

    /// <summary>
    /// 离开类型
    /// </summary>
    public void LeaveType()
    {
        if (_typeStack.Count > 0)
            _typeStack.Pop();
    }

    /// <summary>
    /// 进入方法
    /// </summary>
    public void EnterMethod(IMethodSymbol? method)
    {
        _methodStack.Push(method);
        // Clear stream variable tracking to avoid cross-method contamination
        StreamLocalVariables.Clear();
        // Clear LINQ let-alias mappings to avoid cross-method contamination
        QueryLetAliases.Clear();
        // Clear ref holder tracking to avoid cross-method contamination
        _activeRefHolders.Clear();
        _refHolderAllocCounts.Clear();
    }

    /// <summary>
    /// 离开方法
    /// </summary>
    public void LeaveMethod()
    {
        if (_methodStack.Count > 0)
            _methodStack.Pop();
    }

    /// <summary>
    /// 生成唯一的合成名称
    /// </summary>
    public string GenerateSyntheticName(string prefix)
    {
        if (!_syntheticNameCounters.ContainsKey(prefix))
        {
            _syntheticNameCounters[prefix] = 0;
        }

        return $"{prefix}{++_syntheticNameCounters[prefix]}";
    }

    /// <summary>
    /// 添加导入
    /// </summary>
    public void AddImport(string typeName)
    {
        // 跳过 java.lang 包下的类型
        if (typeName.StartsWith("java.lang.")) return;
        // 跳过 C# 命名空间风格的导入（首段首字母大写，如 System.*, Microsoft.*）
        // Java 包名首段必须为全小写 (java.*, com.*, org.* 等)
        var firstDot = typeName.IndexOf('.');
        var firstSegment = firstDot >= 0 ? typeName.Substring(0, firstDot) : typeName;
        if (firstSegment.Length > 0 && char.IsUpper(firstSegment[0])) return;
        ImportedTypes.Add(typeName);
    }

    /// <summary>
    /// 重置每个文件转换前的导入集合（避免跨文件污染）
    /// </summary>
    public void ClearImports()
    {
        ImportedTypes.Clear();
        // TypeCache maps ITypeSymbol → Java type name and is used to skip re-running MapTypeInternal.
        // But the import side-effects inside MapTypeInternal (AddImportsForType calls) are NOT replayed
        // on cache hits. Clearing the cache here ensures each type group re-triggers those import additions.
        TypeCache.Clear();
    }

    /// <summary>
    /// 将 C# 命名空间转换为 Java 包名
    /// </summary>
    public string NamespaceToPackage(string ns)
    {
        // Normalize global namespace to empty string
        var normalizedNs = (string.IsNullOrWhiteSpace(ns) || ns == "<global namespace>") ? "" : ns;

        // 优先检查 TypeMappings.json 中的配置 (supports empty string key for global namespace)
        var mapped = TypeMappings.MapNamespace(normalizedNs);
        if (mapped != null)
        {
            return mapped;
        }

        if (string.IsNullOrEmpty(normalizedNs))
            return string.Empty;

        // 其次检查用户自定义映射
        foreach (var (pattern, replacement) in Options.NamespaceMappings)
        {
            if (normalizedNs.StartsWith(pattern))
            {
                return normalizedNs.Replace(pattern, replacement);
            }
        }

        // 默认转换：直接使用原始 namespace（适用于非 System 命名空间）
        return normalizedNs;
    }

    /// <summary>
    /// 映射 C# 类型到 Java 类型
    /// </summary>
    public string MapType(ITypeSymbol typeSymbol)
    {
        if (TypeCache.TryGetValue(typeSymbol, out var cached))
        {
            return cached;
        }

        var result = MapTypeInternal(typeSymbol);
        TypeCache[typeSymbol] = result;
        return result;
    }

    private string MapTypeInternal(ITypeSymbol typeSymbol)
    {
        // 处理可空值类型
        if (typeSymbol.OriginalDefinition?.SpecialType == SpecialType.System_Nullable_T || typeSymbol.OriginalDefinition?.ToDisplayString() == "System.Nullable")
        {
            var underlyingType = ((INamedTypeSymbol)typeSymbol).TypeArguments[0];
            var javaType = MapType(underlyingType);

            if (Options.UseOptionalForNullable)
            {
                AddImport("java.util.Optional");
                return $"Optional<{javaType}>";
            }

            // 默认：使用装箱类型
            return javaType;
        }

        // Anonymous types (e.g. new { x = 1, y = 2 }) have no Java equivalent — use Object
        if (typeSymbol is INamedTypeSymbol anonymousCheck && anonymousCheck.IsAnonymousType)
        {
            return "Object";
        }

        // 处理数组类型
        if (typeSymbol is IArrayTypeSymbol arrayType)
        {
            var elementType = MapType(arrayType.ElementType);
            // C# int[,] (rank 2) → Java int[][] (two levels of brackets)
            var brackets = string.Concat(Enumerable.Repeat("[]", arrayType.Rank));
            return elementType + brackets;
        }

        // 处理泛型类型
        if (typeSymbol is INamedTypeSymbol namedType && namedType.TypeArguments.Length > 0)
        {
            var baseType = namedType.Name;

            // 获取未绑定的泛型类型定义，用于查找映射
            var originalDefinition = namedType.OriginalDefinition ?? namedType.ConstructedFrom;

            // 构建完全限定名（不带类型参数，使用 ` 数字后缀）
            string fullQualifiedName;
            if (originalDefinition != null)
            {
                // 使用命名空间和类型名 + 泛型参数数量
                var namespaceStr = originalDefinition.ContainingNamespace?.ToDisplayString() ?? "";
                if (!string.IsNullOrEmpty(namespaceStr))
                {
                    fullQualifiedName = namespaceStr + "." + baseType + "`" + namedType.TypeArguments.Length;
                }
                else
                {
                    fullQualifiedName = baseType + "`" + namedType.TypeArguments.Length;
                }
            }
            else
            {
                fullQualifiedName = baseType + "`" + namedType.TypeArguments.Length;
            }

            // 移除 global:: 前缀（如果有）
            if (fullQualifiedName.StartsWith("global::"))
            {
                fullQualifiedName = fullQualifiedName.Substring(8);
            }
            var mappedBase = TypeMappings.MapType(fullQualifiedName);

            // 如果完全限定名没有匹配，尝试简单名称 + 泛型数量
            var configKey = fullQualifiedName;
            if (mappedBase == fullQualifiedName)
            {
                configKey = baseType + "`" + namedType.TypeArguments.Length;
                mappedBase = TypeMappings.MapType(configKey);
            }

            if (mappedBase != fullQualifiedName)
            {
                // 使用映射后的基础类型
                baseType = MapSimpleTypeName(mappedBase);
                // 添加导入
                AddImportsForType(configKey);
            }
            else
            {
                // 对于未映射的类型，baseType 已经在开头处理过 ` 后缀了
                // 这里不需要额外处理
            }

            // 递归映射类型参数（对于泛型类型参数，需要使用装箱类型）
            var typeArgs = string.Join(", ", namedType.TypeArguments.Select(t => MapTypeForGeneric(t)));

            // 确保基础类型名不包含 ` 后缀
            var tickIndex = baseType.IndexOf('`');
            if (tickIndex > 0)
            {
                baseType = baseType.Substring(0, tickIndex);
            }

            // Object doesn't take type parameters in Java - strip them
            if (baseType == "Object")
                return "Object";

            return $"{baseType}<{typeArgs}>";
        }

        // 处理动态类型
        if (typeSymbol is IDynamicTypeSymbol)
        {
            Diagnostics.Warning("Dynamic type converted to Object");
            return "Object";
        }

        // [Flags] enum types → int (registered by EnumTransformer during project conversion)
        if (typeSymbol is INamedTypeSymbol namedEnumCheck && namedEnumCheck.TypeKind == TypeKind.Enum)
        {
            // Check static registry (populated when EnumTransformer processes [Flags] enums)
            if (IsFlagsEnum(namedEnumCheck.Name))
                return "int";
            // Also check the Roslyn attribute directly for same-compilation flags enums
            bool hasFlagsAttr = namedEnumCheck.GetAttributes().Any(a =>
                a.AttributeClass?.Name is "FlagsAttribute" or "Flags");
            if (hasFlagsAttr)
            {
                RegisterFlagsEnum(namedEnumCheck.Name);
                return "int";
            }
        }

        // 对于没有类型参数的命名类型，检查配置映射
        var name = typeSymbol.Name;

        // 首先尝试完全限定名
        var fullQualifiedNameSimple = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fullQualifiedNameSimple.StartsWith("global::"))
        {
            fullQualifiedNameSimple = fullQualifiedNameSimple.Substring(8);
        }
        var mapped = TypeMappings.MapType(fullQualifiedNameSimple);

        // 如果完全限定名没有匹配，尝试简单名称
        var configKeySimple = fullQualifiedNameSimple;
        if (mapped == fullQualifiedNameSimple)
        {
            configKeySimple = name;
            mapped = TypeMappings.MapType(name);
        }

        if (mapped != name && mapped != fullQualifiedNameSimple)
        {
            AddImportsForType(configKeySimple);
            return MapSimpleTypeName(mapped);
        }

        // For unmapped types from the project being converted (e.g. Microsoft.Msagl.*), add an explicit
        // import when the simple name conflicts with a java.util.* or other wildcard-imported type.
        // This prevents ambiguous reference errors like "reference to Timer is ambiguous".
        var ns = typeSymbol.ContainingNamespace?.ToDisplayString();
        if (!string.IsNullOrEmpty(ns) && ns.StartsWith("Microsoft."))
        {
            // Names that are also in java.util or other wildcard imports
            if (name is "Timer" or "Set" or "Date" or "Random" or "Scanner" or "Arrays" or "Collections"
                or "Optional" or "Stack" or "Queue" or "Deque" or "Iterator")
            {
                // Add explicit import to resolve ambiguity
                var javaPackage = NamespaceToPackage(ns);
                AddImport($"{javaPackage}.{name}");
            }
        }

        // For nested types (e.g. C# Variable.NeighborAndWeight), use OuterClass.InnerClass in Java.
        // Exclude type parameters (ITypeParameterSymbol) — they are referenced by simple name T, not OuterClass.T.
        if (typeSymbol is not ITypeParameterSymbol
            && typeSymbol.ContainingType is INamedTypeSymbol outerType
            && outerType.TypeKind != TypeKind.Error)
        {
            var nestedStr = $"{outerType.Name}.{MapSimpleTypeName(name)}";
            if (typeSymbol.TypeKind == TypeKind.Delegate) {
                 var curOuter = outerType;
                 var allTypeArgs = new List<string>();
                 while (curOuter != null) {
                     allTypeArgs.InsertRange(0, curOuter.TypeArguments.Select(t => MapTypeForGeneric(t)));
                     curOuter = curOuter.ContainingType;
                 }
                 if (allTypeArgs.Count > 0) {
                     nestedStr += "<" + string.Join(", ", allTypeArgs) + ">";
                 }
            }
            return nestedStr;
        }

        return MapSimpleTypeName(name);
    }

    private string MapSimpleTypeName(string typeName)
    {
        // 基础类型映射
        return typeName switch
        {
            "String" => "String",
            "Int32" => "int",
            "Int64" => "long",
            "Int16" => "short",
            "Byte" => "byte",
            "SByte" => "byte",
            "UInt32" => "int",  // Java 没有 unsigned
            "UInt64" => "long",
            "UInt16" => "short",
            "Single" => "float",
            "Double" => "double",
            "Boolean" => "boolean",
            "Char" => "char",
            "Object" => "Object",
            "Void" => "void",
            "var" => "var",  // Java 10+ 支持 var
            _ => typeName
        };
    }

    /// <summary>
    /// 映射类型用于泛型参数（需要使用装箱类型）
    /// </summary>
    private string MapTypeForGeneric(ITypeSymbol typeSymbol)
    {
        var result = MapType(typeSymbol);

        // 对于原始类型，在泛型参数中需要使用装箱类型
        return result switch
        {
            "int" => "Integer",
            "long" => "Long",
            "short" => "Short",
            "byte" => "Byte",
            "float" => "Float",
            "double" => "Double",
            "boolean" => "Boolean",
            "char" => "Character",
            _ => result
        };
    }

    /// <summary>
    /// 为类型映射添加必要的导入
    /// </summary>
    private void AddImportsForType(string csharpType)
    {
        var imports = TypeMappings.GetRequiredImports(csharpType);
        foreach (var import in imports)
        {
            AddImport(import);
        }
    }

    /// <summary>
    /// Public wrapper for <see cref="AddImportsForType"/> used by transformers.
    /// </summary>
    public void AddImportsForTypePublic(string csharpType) => AddImportsForType(csharpType);

    /// <summary>
    /// Maps a C# type from its syntax representation (string) when the Roslyn semantic model
    /// cannot resolve the type (e.g. unresolved assembly references). Used as fallback from
    /// FieldTransformer / PropertyTransformer / MethodTransformer instead of returning "Object".
    /// </summary>
    public string MapTypeFromSyntax(TypeSyntax typeSyntax)
    {
        if (typeSyntax == null) return "Object";
        return MapTypeFromSyntaxString(typeSyntax.ToString().Trim());
    }

    private string MapTypeFromSyntaxString(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return "Object";

        // C# keyword types → Java equivalents (must happen before TypeMappings lookup,
        // which only knows fully-qualified names like System.Boolean, not the keywords).
        var keywordMapped = typeName switch
        {
            "bool"    => "boolean",
            "int"     => "int",
            "long"    => "long",
            "short"   => "short",
            "byte"    => "byte",
            "sbyte"   => "byte",
            "uint"    => "int",
            "ulong"   => "long",
            "ushort"  => "short",
            "float"   => "float",
            "double"  => "double",
            "decimal" => "double",
            "char"    => "char",
            "void"    => "void",
            "object"  => "Object",
            "string"  => "String",
            _         => (string?)null
        };
        if (keywordMapped != null) return keywordMapped;

        // Nullable T? → strip the ?
        if (typeName.EndsWith("?") && typeName.Length > 1)
            return MapTypeFromSyntaxString(typeName.Substring(0, typeName.Length - 1));

        // Array T[] → map element type + []
        if (typeName.EndsWith("[]"))
        {
            var elemType = MapTypeFromSyntaxString(typeName.Substring(0, typeName.Length - 2));
            return elemType + "[]";
        }

        // Generic type e.g. List<XmlReader>
        var openAngle = typeName.IndexOf('<');
        if (openAngle > 0 && typeName.EndsWith(">"))
        {
            var baseTypeName = typeName.Substring(0, openAngle).Trim();
            var innerArgs = typeName.Substring(openAngle + 1, typeName.Length - openAngle - 2);
            var mappedBase = TypeMappings.MapType(baseTypeName);
            if (mappedBase != baseTypeName)
            {
                AddImportsForType(baseTypeName);
                mappedBase = MapSimpleTypeName(mappedBase);
            }
            if (mappedBase == "Object") return "Object";
            return $"{mappedBase}<{innerArgs}>";
        }

        // Try exact match in type registry
        var mapped = TypeMappings.MapType(typeName);
        if (mapped != typeName)
        {
            AddImportsForType(typeName);
            return MapSimpleTypeName(mapped);
        }

        // No mapping found: use the syntax name directly (handles classes whose name is unchanged)
        return MapSimpleTypeName(typeName);
    }

    /// <summary>
    /// 注册一个已合并的 partial 类型
    /// </summary>
    public void RegisterMergedPartialType(MergedTypeDeclaration mergedType)
    {
        _mergedPartialTypes[mergedType.TypeSymbol.Name] = mergedType;

        // 标记所有原始语法节点为已合并
        foreach (var syntaxNode in mergedType.OriginalSyntaxNodes)
        {
            var key = GetSyntaxNodeKey(syntaxNode);
            _partialSyntaxNodeMap[key] = mergedType.TypeSymbol.Name;
        }
    }

    /// <summary>
    /// 检查一个语法节点是否是已合并的 partial 类型的一部分
    /// </summary>
    public bool IsMergedPartialType(TypeDeclarationSyntax syntaxNode)
    {
        var key = GetSyntaxNodeKey(syntaxNode);
        return _partialSyntaxNodeMap.ContainsKey(key);
    }

    /// <summary>
    /// 获取已合并的类型声明
    /// </summary>
    public MergedTypeDeclaration? GetMergedType(string typeName)
    {
        return _mergedPartialTypes.GetValueOrDefault(typeName);
    }

    /// <summary>
    /// 获取所有已合并的类型
    /// </summary>
    public IReadOnlyList<MergedTypeDeclaration> GetAllMergedTypes()
    {
        return _mergedPartialTypes.Values.ToList();
    }

    /// <summary>
    /// 清除所有已合并的 partial 类型记录
    /// </summary>
    public void ClearMergedTypes()
    {
        _mergedPartialTypes.Clear();
        _partialSyntaxNodeMap.Clear();
    }

    /// <summary>
    /// 为语法节点生成唯一键
    /// </summary>
    private static string GetSyntaxNodeKey(TypeDeclarationSyntax syntaxNode)
    {
        var location = syntaxNode.SyntaxTree.FilePath;
        var span = syntaxNode.Span;
        return $"{location}:{span.Start}:{span.Length}";
    }

    /// <summary>
    /// 获取用于跨文件分析的语义模型
    /// 如果有项目编译，使用它；否则使用当前语法树的语义模型
    /// </summary>
    public SemanticModel? GetSemanticModelForTree(SyntaxTree syntaxTree)
    {
        if (ProjectCompilation != null)
        {
            // Synthetic trees (e.g. created by WithMembers) are NOT in the compilation.
            // Fall back to the current SemanticModel which covers the original tree.
            if (ProjectCompilation.ContainsSyntaxTree(syntaxTree))
                return ProjectCompilation.GetSemanticModel(syntaxTree);
            return SemanticModel;
        }
        return SemanticModel;
    }

    /// <summary>
    /// C# using 别名信息
    /// </summary>
    public class UsingAliasInfo
    {
        /// <summary>
        /// 别名名称（如 P2）
        /// </summary>
        public string AliasName { get; set; } = string.Empty;

        /// <summary>
        /// 目标类型符号
        /// </summary>
        public ITypeSymbol? TargetType { get; set; }

        /// <summary>
        /// 别名在源代码中的位置（用于诊断）
        /// </summary>
        public Location? Location { get; set; }

        /// <summary>
        /// 是否是泛型别名
        /// </summary>
        public bool IsGeneric => TargetType is INamedTypeSymbol named &&
                                  named.TypeArguments.Length > 0;
    }

    /// <summary>
    /// 文件级别名映射表
    /// Key: 别名名称, Value: 别名信息
    /// </summary>
    private Dictionary<string, UsingAliasInfo> _usingAliases = new();

    /// <summary>
    /// 当前文件的别名集合（公开访问）
    /// </summary>
    public IReadOnlyDictionary<string, UsingAliasInfo> UsingAliases => _usingAliases;

    /// <summary>
    /// 注册 using 别名
    /// </summary>
    public bool RegisterUsingAlias(string aliasName, ITypeSymbol targetType, Location? location)
    {
        // 检测重复别名定义
        if (_usingAliases.ContainsKey(aliasName))
        {
            Diagnostics.Error($"Duplicate alias '{aliasName}' in this file", location);
            return false;
        }

        // 检查别名是否是 Java 关键字
        if (IsJavaKeyword(aliasName))
        {
            Diagnostics.Error($"Alias '{aliasName}' is a Java keyword and cannot be used", location);
            return false;
        }

        // 检查别名是否与类型系统中的类型冲突
        foreach (var cachedType in TypeCache.Values)
        {
            if (cachedType == aliasName)
            {
                Diagnostics.Warning($"Alias '{aliasName}' conflicts with existing type", location);
                break;
            }
        }

        // 检查别名指向的简单名称与别名是否相同（无意义别名）
        if (targetType.Name == aliasName)
        {
            Diagnostics.Warning($"Alias '{aliasName}' has the same name as the target type '{targetType.Name}'", location);
        }

        // 检查别名与当前命名空间类型的冲突
        if (!string.IsNullOrEmpty(CurrentNamespace))
        {
            var currentNs = GlobalNamespace?.GetMembers(CurrentNamespace);
            if (currentNs != null)
            {
                foreach (var member in currentNs)
                {
                    if (member is ITypeSymbol type && type.Name == aliasName)
                    {
                        Diagnostics.Warning($"Alias '{aliasName}' conflicts with type '{type.Name}' in current namespace", location);
                        break;
                    }
                }
            }
        }

        _usingAliases[aliasName] = new UsingAliasInfo
        {
            AliasName = aliasName,
            TargetType = targetType,
            Location = location
        };

        return true;
    }

    /// <summary>
    /// 检查标识符是否是别名
    /// </summary>
    public bool IsAlias(string identifier)
    {
        return _usingAliases.ContainsKey(identifier);
    }

    /// <summary>
    /// 解析别名到实际类型
    /// </summary>
    public ITypeSymbol? ResolveAlias(string aliasName)
    {
        return _usingAliases.GetValueOrDefault(aliasName)?.TargetType;
    }

    /// <summary>
    /// 清除当前文件的别名（处理新文件时调用）
    /// </summary>
    public void ClearAliases()
    {
        _usingAliases.Clear();
    }

    /// <summary>
    /// 获取别名的 Java 类型表示
    /// </summary>
    public string? MapAliasToJavaType(string aliasName)
    {
        var alias = _usingAliases.GetValueOrDefault(aliasName);
        if (alias?.TargetType == null) return null;

        return MapType(alias.TargetType);
    }

    /// <summary>
    /// 获取全局命名空间（用于冲突检测）
    /// </summary>
    private INamespaceSymbol? GlobalNamespace => SemanticModel?.Compilation?.GlobalNamespace;

    /// <summary>
    /// 检查是否是 Java 关键字
    /// </summary>
    public static bool IsJavaKeyword(string word)
    {
        return word switch
        {
            "abstract" or "assert" or "boolean" or "break" or "byte" or "case" or "catch" or
            "char" or "class" or "const" or "continue" or "default" or "do" or "double" or
            "else" or "enum" or "extends" or "final" or "finally" or "float" or "for" or
            "goto" or "if" or "implements" or "import" or "instanceof" or "int" or
            "interface" or "long" or "native" or "new" or "package" or "private" or
            "protected" or "public" or "return" or "short" or "static" or "strictfp" or
            "super" or "switch" or "synchronized" or "this" or "throw" or "throws" or
            "transient" or "try" or "void" or "volatile" or "while" => true,
            _ => false
        };
    }

    public static string EscapeJavaKeyword(string word)
    {
        return IsJavaKeyword(word) ? word + "Value" : word;
    }
}

/// <summary>
/// 诊断收集器
/// </summary>
public class DiagnosticCollector
{
    private readonly List<DiagnosticMessage> _messages = new();

    public IReadOnlyList<DiagnosticMessage> Messages => _messages;

    public void Error(string message, Location? location = null)
    {
        _messages.Add(new DiagnosticMessage(DiagnosticSeverity.Error, message, location));
    }

    public void Warning(string message, Location? location = null)
    {
        _messages.Add(new DiagnosticMessage(DiagnosticSeverity.Warning, message, location));
    }

    public void Info(string message, Location? location = null)
    {
        _messages.Add(new DiagnosticMessage(DiagnosticSeverity.Info, message, location));
    }
}

/// <summary>
/// 诊断消息
/// </summary>
public record DiagnosticMessage(
    DiagnosticSeverity Severity,
    string Message,
    Location? Location
);

/// <summary>
/// 诊断严重程度
/// </summary>
public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}
