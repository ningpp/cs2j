using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.PartialType;
using CSharpToJava.Core.Transformers;

namespace CSharpToJava.Core.Context;

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
    /// Synthesized Java record store (delegates to SynthesizedRecordStore).
    /// </summary>
    private readonly SynthesizedRecordStore _synthesizedRecordStore = new();

    public IReadOnlyCollection<SynthesizedRecordInfo> SynthesizedRecords => _synthesizedRecordStore.Records;

    public bool TryGetSynthesizedRecord(string structuralKey, out SynthesizedRecordInfo? record)
        => _synthesizedRecordStore.TryGet(structuralKey, out record);

    public void RegisterSynthesizedRecord(SynthesizedRecordInfo record)
        => _synthesizedRecordStore.Register(record);

    public void ClearSynthesizedRecords() => _synthesizedRecordStore.Clear();

    /// <summary>
    /// 需要合成的唯一名称计数器
    /// </summary>
    private Dictionary<string, int> _syntheticNameCounters = new();

    /// <summary>
    /// Partial type merge tracking (delegates to PartialTypeMergeStore).
    /// </summary>
    private readonly PartialTypeMergeStore _partialTypeStore = new();

    public void RegisterMergedPartialType(MergedTypeDeclaration mergedType) => _partialTypeStore.Register(mergedType);
    public bool IsMergedPartialType(TypeDeclarationSyntax syntaxNode) => _partialTypeStore.IsMerged(syntaxNode);
    public MergedTypeDeclaration? GetMergedType(string typeName) => _partialTypeStore.Get(typeName);
    public IReadOnlyList<MergedTypeDeclaration> GetAllMergedTypes() => _partialTypeStore.GetAll();
    public void ClearMergedTypes() => _partialTypeStore.Clear();

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
    public static bool HasTypeErasureConflict(IMethodSymbol method) => JavaNaming.HasTypeErasureConflict(method);
    public static string GetErasureRenamedSuffix(int typeParameterCount) => JavaNaming.GetErasureRenamedSuffix(typeParameterCount);
}
