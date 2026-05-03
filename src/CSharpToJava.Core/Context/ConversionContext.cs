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

    /// <summary>Pre-scanned var-declared local types, populated by VarTypeResolver.</summary>
    public Dictionary<string, ITypeSymbol> VarTypeMap { get; } = new(StringComparer.Ordinal);

    internal ConvertedCommentSet GetDeclarationComments(SyntaxNode node, ISymbol? symbol = null)
    {
        return CommentConversion.ExtractDeclarationComments(node, symbol, this);
    }

    public string CurrentNamespace => _namespaceStack.Count > 0 ? _namespaceStack.Peek() : string.Empty;
    public JavaTypeDeclaration? CurrentType => _typeStack.Count > 0 ? _typeStack.Peek() : null;
    public IMethodSymbol? CurrentMethod => _methodStack.Count > 0 ? _methodStack.Peek() : null;
    public bool IsInAsyncContext { get; set; }
    public bool IsInLambdaContext { get; set; }
    public bool IsInYieldMethod { get; set; }
    /// <summary>
    /// When true, suppresses .clone() on return statements (used inside property getters
    /// where the consumption site handles cloning instead).
    /// </summary>
    public bool SuppressReturnClone { get; set; }
    public bool IsInInterfaceBody => CurrentType is Java.JavaInterfaceDeclaration;

    public bool ImplementsInterface(string interfaceName)
    {
        if (CurrentType is Java.JavaClassDeclaration classDecl)
        {
            return classDecl.ImplementedTypes.Any(t =>
                t == interfaceName || t.StartsWith(interfaceName + "<"));
        }
        return false;
    }

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

    public TypeMappingService TypeMapper { get; private set; } = null!;
    public Dictionary<ITypeSymbol, string> TypeCache => TypeMapper.TypeCache;

    private readonly SynthesizedRecordStore _synthesizedRecordStore = new();
    public IReadOnlyCollection<SynthesizedRecordInfo> SynthesizedRecords => _synthesizedRecordStore.Records;
    public bool TryGetSynthesizedRecord(string structuralKey, out SynthesizedRecordInfo? record)
        => _synthesizedRecordStore.TryGet(structuralKey, out record);
    public void RegisterSynthesizedRecord(SynthesizedRecordInfo record)
        => _synthesizedRecordStore.Register(record);
    public void ClearSynthesizedRecords() => _synthesizedRecordStore.Clear();

    private Dictionary<string, int> _syntheticNameCounters = new();

    private readonly PartialTypeMergeStore _partialTypeStore = new();
    public void RegisterMergedPartialType(MergedTypeDeclaration mergedType) => _partialTypeStore.Register(mergedType);
    public bool IsMergedPartialType(TypeDeclarationSyntax syntaxNode) => _partialTypeStore.IsMerged(syntaxNode);
    public MergedTypeDeclaration? GetMergedType(string typeName) => _partialTypeStore.Get(typeName);
    public IReadOnlyList<MergedTypeDeclaration> GetAllMergedTypes() => _partialTypeStore.GetAll();
    public void ClearMergedTypes() => _partialTypeStore.Clear();

    public CSharpCompilation? ProjectCompilation { get; set; }

    /// <summary>
    /// Saved original compilation from before the LinqDesugarPass rebuild.
    /// Used as a fallback when the original syntax tree is not in the current
    /// ProjectCompilation (e.g. after LinqRewriter modified the tree).
    /// </summary>
    public CSharpCompilation? PreDesugarCompilation { get; set; }

    public void RegisterFlagsEnum(string enumName) => TypeMapper.RegisterFlagsEnum(enumName);
    public bool IsFlagsEnum(string enumName) => TypeMapper.IsFlagsEnum(enumName);

    public void RegisterExplicitValueEnum(string enumName) => TypeMapper.RegisterExplicitValueEnum(enumName);
    public bool IsExplicitValueEnum(string enumName) => TypeMapper.IsExplicitValueEnum(enumName);

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
            key => TryGetSynthesizedRecord(key, out _),
            resolveAlias: name => ResolveAlias(name));
        TypeMapper.SetSynthesizedRecordNameResolver(key =>
            TryGetSynthesizedRecord(key, out var rec) && rec != null ? rec.RecordName : "Object");
    }

    public void EnterNamespace(string ns) => _namespaceStack.Push(ns);

    public void LeaveNamespace()
    {
        if (_namespaceStack.Count > 0)
            _namespaceStack.Pop();
    }

    public void EnterType(JavaTypeDeclaration type) => _typeStack.Push(type);

    public void LeaveType()
    {
        if (_typeStack.Count > 0)
            _typeStack.Pop();
    }

    public void EnterMethod(IMethodSymbol? method)
    {
        _methodStack.Push(method);
        var readOnlyParams = method?.Parameters
            .Where(p => StructCloneHelper.IsRefParamEffectivelyReadOnly(p))
            .Select(p => p.Name);
        MethodState.Reset(readOnlyParams);
    }

    public void LeaveMethod()
    {
        if (_methodStack.Count > 0)
            _methodStack.Pop();
    }

    public string GenerateSyntheticName(string prefix)
    {
        if (!_syntheticNameCounters.ContainsKey(prefix))
            _syntheticNameCounters[prefix] = 0;
        return $"{prefix}{++_syntheticNameCounters[prefix]}";
    }

    public void AddImport(string typeName)
    {
        if (typeName.StartsWith("java.lang.")) return;
        // Reject bare package names (e.g. "java.util.stream") — Java requires class-level
        // or wildcard imports ("java.util.stream.Collectors" or "java.util.stream.*").
        // Bare package imports like "import java.util.stream;" are compile errors.
        if (!typeName.EndsWith(".*") && typeName.Count(c => c == '.') >= 2)
        {
            var lastSegment = typeName.Substring(typeName.LastIndexOf('.') + 1);
            if (lastSegment.Length > 0 && char.IsLower(lastSegment[0]))
                return; // Skip — looks like a package path, not a type
        }
        ImportedTypes.Add(typeName);
    }

    public void ClearImports()
    {
        ImportedTypes.Clear();
        // Clearing cache ensures each type group re-triggers import additions.
        TypeMapper.ClearCache();
    }

    public string NamespaceToPackage(string ns) => TypeMapper.NamespaceToPackage(ns);
    public string MapType(ITypeSymbol typeSymbol)
    {
        // Check using alias: if the type name matches a registered alias, resolve it
        if (AliasRegistry.Resolve(typeSymbol.Name) is ITypeSymbol aliasTarget
            && aliasTarget.Name != typeSymbol.Name)
            return TypeMapper.MapType(aliasTarget);
        return TypeMapper.MapType(typeSymbol);
    }

    // ─── Facade methods delegating to TypeMapper for backward compatibility ───
    public void AddImportsForTypePublic(string csharpType) => TypeMapper.AddImportsForTypePublic(csharpType);
    public string MapTypeFromSyntax(TypeSyntax typeSyntax)
    {
        if (typeSyntax == null) return "Object";
        // Prefer semantic resolution when available — this correctly handles
        // generic type arguments, primitive boxing, and namespace-qualified lookups.
        if (SemanticModel != null)
        {
            var typeInfo = SemanticModel.GetTypeInfo(typeSyntax);
            if (typeInfo.Type != null && typeInfo.Type is not IErrorTypeSymbol)
                return TypeMapper.MapType(typeInfo.Type);
        }
        return TypeMapper.MapTypeFromSyntax(typeSyntax);
    }

    public SemanticModel? GetSemanticModelForTree(SyntaxTree syntaxTree)
    {
        if (ProjectCompilation != null)
        {
            // Synthetic trees (e.g. created by WithMembers) are NOT in the compilation.
            if (ProjectCompilation.ContainsSyntaxTree(syntaxTree))
                return ProjectCompilation.GetSemanticModel(syntaxTree);

            // The LinqDesugarPass may have rebuilt the compilation with modified syntax
            // trees. If the original tree is not in the current compilation, try the
            // pre-desugar compilation (saved before LinqRewriter modified anything).
            if (PreDesugarCompilation != null && PreDesugarCompilation.ContainsSyntaxTree(syntaxTree))
                return PreDesugarCompilation.GetSemanticModel(syntaxTree);

            return SemanticModel;
        }
        return SemanticModel;
    }

    public UsingAliasRegistry AliasRegistry { get; } = new();

    // ─── Facade properties/methods delegating to AliasRegistry ───
    public IReadOnlyDictionary<string, UsingAliasRegistry.UsingAliasInfo> UsingAliases => AliasRegistry.Aliases;
    public bool RegisterUsingAlias(string aliasName, ITypeSymbol targetType, Location? location)
        => AliasRegistry.Register(aliasName, targetType, location, Diagnostics, CurrentNamespace, GlobalNamespace, TypeCache);
    public bool IsAlias(string identifier) => AliasRegistry.IsAlias(identifier);
    public ITypeSymbol? ResolveAlias(string aliasName) => AliasRegistry.Resolve(aliasName);
    public void ClearAliases() => AliasRegistry.Clear();

    public string? MapAliasToJavaType(string aliasName)
    {
        var targetType = AliasRegistry.Resolve(aliasName);
        if (targetType == null) return null;
        return MapType(targetType);
    }

    private INamespaceSymbol? GlobalNamespace => SemanticModel?.Compilation?.GlobalNamespace;

    // ─── Static facades delegating to JavaNaming ───
    public static bool IsJavaKeyword(string word) => JavaNaming.IsJavaKeyword(word);
    public static string EscapeJavaKeyword(string word) => JavaNaming.EscapeJavaKeyword(word);
    public static bool HasTypeErasureConflict(IMethodSymbol method) => JavaNaming.HasTypeErasureConflict(method);
    public static string GetErasureRenamedSuffix(int typeParameterCount) => JavaNaming.GetErasureRenamedSuffix(typeParameterCount);
}
