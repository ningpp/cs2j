using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.PartialType;
using CSharpToJava.Core.Transformers;

namespace CSharpToJava.Core.Context;

/// <summary>
/// 转换选项
/// </summary>
public class ConversionOptions
{
    /// <summary>
    /// 目标 Java 版本
    /// </summary>
    public JavaVersion TargetJavaVersion { get; set; } = JavaVersion.Java25;

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
    /// instead of procedural loops via LinqRewriter. Default: true for Java 25.
    /// When set explicitly, overrides the version-based default.
    /// </summary>
    public bool? PreferStreamApi { get; set; }

    /// <summary>
    /// Resolved value: uses explicit setting if provided, otherwise defaults to Stream API for Java 25.
    /// </summary>
    public bool EffectivePreferStreamApi => PreferStreamApi ?? true;

    /// <summary>
    /// When enabled, extension methods on known types are promoted to instance methods on those types,
    /// and the receiver ('this') parameter is stripped from the Java method signature.
    /// </summary>
    public bool RewriteExtensionMethods { get; set; } = false;

    /// <summary>
    /// When false, project conversion skips emitting generated compatibility helper classes
    /// such as ObjectHolder, StringHelper, and XML/JSON shims into the current output.
    /// This is used by multi-module conversion when those helpers are centralized in a
    /// shared compatibility module.
    /// </summary>
    public bool EmitCompatibilityHelpers { get; set; } = true;

    /// <summary>
    /// Optional shared compatibility package that should be imported into all generated files.
    /// Used together with EmitCompatibilityHelpers=false when helper classes live in a
    /// dedicated shared module.
    /// </summary>
    public string? SharedCompatibilityPackage { get; set; }
}

/// <summary>
/// Java 版本枚举
/// </summary>
public enum JavaVersion
{
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

    internal ConvertedCommentSet GetDeclarationComments(SyntaxNode node, ISymbol? symbol = null)
    {
        return CommentConversion.ExtractDeclarationComments(node, symbol, this);
    }

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
    /// When true, suppresses .clone() on return statements (used inside property getters
    /// where the consumption site handles cloning instead).
    /// </summary>
    public bool SuppressReturnClone { get; set; }

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
    /// Method-level mutable state (pre/post statements, ref holders, stream vars).
    /// </summary>
    public MethodConversionState MethodState { get; } = new();

    // ─── Facade properties delegating to MethodState for backward compatibility ───

    public HashSet<string> StreamLocalVariables => MethodState.StreamLocalVariables;
    public Dictionary<string, string> QueryLetAliases
    {
        get => MethodState.QueryLetAliases;
        set => MethodState.QueryLetAliases = value;
    }

    /// <summary>
    /// Type mapping service (owns MapType, MapTypeFromSyntax, NamespaceToPackage, TypeCache, FlagsEnums).
    /// </summary>
    public TypeMappingService TypeMapper { get; private set; } = null!;

    /// <summary>
    /// 类型符号到 Java 类型的缓存 (delegates to TypeMapper)
    /// </summary>
    public Dictionary<ITypeSymbol, string> TypeCache => TypeMapper.TypeCache;

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
    /// Set of [Flags] enum names (delegates to TypeMapper).
    /// </summary>
    public void RegisterFlagsEnum(string enumName) => TypeMapper.RegisterFlagsEnum(enumName);
    public bool IsFlagsEnum(string enumName) => TypeMapper.IsFlagsEnum(enumName);

    // ─── Facade methods delegating to MethodState for backward compatibility ───

    public void AddPreStatement(string statement) => MethodState.AddPreStatement(statement);
    public IReadOnlyList<string> DrainPreStatements() => MethodState.DrainPreStatements();
    public bool HasPendingPreStatements => MethodState.HasPendingPreStatements;

    public void AddPostStatement(string statement) => MethodState.AddPostStatement(statement);
    public IReadOnlyList<string> DrainPostStatements() => MethodState.DrainPostStatements();
    public bool HasPendingPostStatements => MethodState.HasPendingPostStatements;

    public bool IsReadOnlyRefStructParam(string paramName) => MethodState.IsReadOnlyRefStructParam(paramName);
    public string AllocateOutHolderName(string varName) => MethodState.AllocateOutHolderName(varName);
    public bool TryGetActiveRefHolder(string varName, out string holderName) => MethodState.TryGetActiveRefHolder(varName, out holderName);
    public void SetActiveRefHolder(string varName, string holderName) => MethodState.SetActiveRefHolder(varName, holderName);
    public string AllocateRefHolderName(string varName) => MethodState.AllocateRefHolderName(varName);

    public ConversionContext(ConversionOptions options, TypeMapping.TypeMappingRegistry typeMappings)
    {
        Options = options;
        TypeMappings = typeMappings;
        TypeMapper = new TypeMappingService(
            options,
            typeMappings,
            Diagnostics,
            ImportedTypes,
            () => CurrentNamespace,
            () => SemanticModel?.Compilation?.GlobalNamespace,
            key => TryGetSynthesizedRecord(key, out _));
        TypeMapper.SetSynthesizedRecordNameResolver(key =>
            TryGetSynthesizedRecord(key, out var rec) && rec != null ? rec.RecordName : "Object");
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

        var readOnlyParams = method?.Parameters
            .Where(p => StructCloneHelper.IsRefParamEffectivelyReadOnly(p))
            .Select(p => p.Name);
        MethodState.Reset(readOnlyParams);
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
        TypeMapper.ClearCache();
    }

    /// <summary>
    /// 将 C# 命名空间转换为 Java 包名 (delegates to TypeMapper)
    /// </summary>
    public string NamespaceToPackage(string ns) => TypeMapper.NamespaceToPackage(ns);

    /// <summary>
    /// 映射 C# 类型到 Java 类型 (delegates to TypeMapper)
    /// </summary>
    public string MapType(ITypeSymbol typeSymbol) => TypeMapper.MapType(typeSymbol);

    // ─── Facade methods delegating to TypeMapper for backward compatibility ───

    public void AddImportsForTypePublic(string csharpType) => TypeMapper.AddImportsForTypePublic(csharpType);

    public string MapTypeFromSyntax(TypeSyntax typeSyntax) => TypeMapper.MapTypeFromSyntax(typeSyntax);

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
    /// File-scoped using alias registry.
    /// </summary>
    public UsingAliasRegistry AliasRegistry { get; } = new();

    // ─── Facade properties/methods delegating to AliasRegistry ───

    public IReadOnlyDictionary<string, UsingAliasRegistry.UsingAliasInfo> UsingAliases => AliasRegistry.Aliases;

    public bool RegisterUsingAlias(string aliasName, ITypeSymbol targetType, Location? location)
    {
        return AliasRegistry.Register(aliasName, targetType, location, Diagnostics, CurrentNamespace, GlobalNamespace, TypeCache);
    }

    public bool IsAlias(string identifier) => AliasRegistry.IsAlias(identifier);
    public ITypeSymbol? ResolveAlias(string aliasName) => AliasRegistry.Resolve(aliasName);
    public void ClearAliases() => AliasRegistry.Clear();

    public string? MapAliasToJavaType(string aliasName)
    {
        var targetType = AliasRegistry.Resolve(aliasName);
        if (targetType == null) return null;
        return MapType(targetType);
    }

    /// <summary>
    /// 获取全局命名空间（用于冲突检测）
    /// </summary>
    private INamespaceSymbol? GlobalNamespace => SemanticModel?.Compilation?.GlobalNamespace;

    // ─── Static facades delegating to JavaNaming ───

    public static bool IsJavaKeyword(string word) => JavaNaming.IsJavaKeyword(word);
    public static string EscapeJavaKeyword(string word) => JavaNaming.EscapeJavaKeyword(word);

    /// <summary>
    /// Checks whether a C# method has a type-erasure conflict with another overload in the
    /// same type: same name, same erased parameter types, but different generic type parameter
    /// counts.  Returns true for the overload with FEWER type parameters (the one that must
    /// be renamed in Java so that both overloads survive).
    /// </summary>
    public static bool HasTypeErasureConflict(IMethodSymbol method)
    {
        if (method.ContainingType == null) return false;
        // Only rename methods whose containing type is defined in source (user code).
        // BCL / library types (e.g. System.String) are not converted, so their methods
        // must keep their mapped names without an erasure suffix.
        if (method.ContainingType.DeclaringSyntaxReferences.Length == 0) return false;
        foreach (var sibling in method.ContainingType.GetMembers().OfType<IMethodSymbol>())
        {
            if (SymbolEqualityComparer.Default.Equals(sibling, method)) continue;
            if (sibling.Name != method.Name) continue;
            if (sibling.Parameters.Length != method.Parameters.Length) continue;
            if (sibling.TypeParameters.Length == method.TypeParameters.Length) continue;
            if (HaveSameErasedParameters(method, sibling))
            {
                // The overload with fewer type parameters is the one that gets renamed.
                return method.TypeParameters.Length < sibling.TypeParameters.Length;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns the renamed Java method name suffix for a type-erasure–conflicting overload.
    /// E.g. a method with 2 type parameters becomes "methodName_2tp".
    /// </summary>
    public static string GetErasureRenamedSuffix(int typeParameterCount)
        => $"_{typeParameterCount}tp";

    private static bool HaveSameErasedParameters(IMethodSymbol a, IMethodSymbol b)
    {
        for (int i = 0; i < a.Parameters.Length; i++)
        {
            if (GetErasedTypeName(a.Parameters[i].Type) != GetErasedTypeName(b.Parameters[i].Type))
                return false;
        }
        return true;
    }

    private static string GetErasedTypeName(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => "System.Object",
        IArrayTypeSymbol arr => GetErasedTypeName(arr.ElementType) + "[]",
        INamedTypeSymbol named => named.OriginalDefinition.ContainingNamespace + "." + named.OriginalDefinition.Name,
        _ => type.ToDisplayString()
    };
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
