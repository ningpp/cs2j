using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Analysis;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.PartialType;
using CSharpToJava.Core.Transformers;
using CSharpToJava.Core.Transformers.Expression.Utilities;

namespace CSharpToJava.Core.Context;

/// <summary>
/// 转换上下文 - 存储转换过程中的状态信息
/// </summary>
public class ConversionContext
{
    private readonly Stack<string> _namespaceStack = new();
    private readonly Stack<JavaTypeDeclaration> _typeStack = new();
    private readonly Stack<IMethodSymbol?> _methodStack = new();
    private readonly Stack<Dictionary<string, string>> _runtimeClassFieldsStack = new();
    private readonly Stack<List<FixedPointerInfo>> _fixedScopeStack = new();
    public bool IsInFixedScope => _fixedScopeStack.Count > 0;
    public void PushFixedScope(List<FixedPointerInfo> pointers) => _fixedScopeStack.Push(pointers);
    public void PopFixedScope() => _fixedScopeStack.Pop();
    public FixedPointerInfo? FindPointerInfo(string varName)
    {
        foreach (var scope in _fixedScopeStack)
        {
            var info = scope.FirstOrDefault(p => p.VariableName == varName);
            if (info != null) return info;
        }
        return null;
    }

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
    /// <summary>The Roslyn type symbol for the type currently being emitted.</summary>
    public INamedTypeSymbol? CurrentEnclosingRoslynType { get; set; }
    public bool IsInAsyncContext { get; set; }
    public bool IsInLambdaContext { get; set; }
    public bool IsInYieldMethod { get; set; }
    /// <summary>
    /// True when the current member being transformed is static (static method,
    /// static field initializer, or static constructor). Used by the default-value
    /// factory method logic to avoid emitting instance method calls from static contexts.
    /// </summary>
    public bool IsInStaticMember { get; set; }
    /// <summary>
    /// Set during method/class attribute processing when the current test class
    /// needs @ExtendWith(MSTestExtension.class) for deployment items, TestContext
    /// parameter injection, or lifecycle support.
    /// </summary>
    public bool CurrentClassNeedsMSTestExtension { get; set; }
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
    public Dictionary<string, string> PreferredJavaTypeImports { get; } = new(StringComparer.Ordinal);

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

    private TypeParameterBindingAnalyzer? _bindingAnalyzer;

    public TypeParameterBindingAnalyzer GetBindingAnalyzer()
    {
        if (_bindingAnalyzer == null)
        {
            var compilation = ProjectCompilation ?? SemanticModel?.Compilation;
            _bindingAnalyzer = compilation != null
                ? TypeParameterBindingAnalyzer.Analyze(compilation)
                : new TypeParameterBindingAnalyzer();
        }
        return _bindingAnalyzer;
    }

    /// <summary>
    /// Tracks which generic classes need a protected factory method for creating
    /// default values of unconstrained type parameters (Unknown binding).
    /// Delegates to the shared store on <see cref="Options"/> so that factory methods
    /// registered during one project's conversion are visible to subsequent projects.
    /// </summary>
    private DefaultFactoryMethodStore DefaultFactoryMethods => Options.DefaultFactoryMethods;

    public void RegisterDefaultFactoryMethod(string classFullMetadataName, string typeParamName)
    {
        DefaultFactoryMethods.Register(classFullMetadataName, typeParamName);
    }

    public IReadOnlySet<string>? GetDefaultFactoryMethodsForClass(string classFullMetadataName)
    {
        return DefaultFactoryMethods.GetForClass(classFullMetadataName);
    }

    /// <summary>
    /// Per-method accumulator for Class&lt;T&gt; parameters needed by method-level
    /// type parameters whose bodies use default(T). Drained by ClassTransformer
    /// after each method is processed to add params to the method signature.
    /// </summary>
    private HashSet<string>? _pendingClassTypeParams;

    /// <summary>
    /// Persistent set of method keys (containingType.MetadataName|method.MetadataName)
    /// that have had Class&lt;T&gt; parameters added. Used by InvocationExpressionTransformer
    /// to add type-token arguments at call sites.
    /// </summary>
    private readonly HashSet<string> _methodsWithClassParams = new();

    /// <summary>
    /// Persistent map from method key to ordered list of type parameter names that
    /// need Class&lt;T&gt; tokens. Survives DrainClassTypeParams so call-site
    /// transformers can determine which type tokens to prepend.
    /// </summary>
    private readonly Dictionary<string, List<string>> _methodClassTypeParamNames = new();

    public void RequireClassTypeParam(string containingTypeMetadataName, string methodMetadataName, string typeParamName)
    {
        _pendingClassTypeParams ??= new();
        _pendingClassTypeParams.Add(typeParamName);
        _methodsWithClassParams.Add($"{containingTypeMetadataName}|{methodMetadataName}");

        var key = $"{containingTypeMetadataName}|{methodMetadataName}";
        if (!_methodClassTypeParamNames.TryGetValue(key, out var nameList))
        {
            nameList = new List<string>();
            _methodClassTypeParamNames[key] = nameList;
        }
        if (!nameList.Contains(typeParamName))
            nameList.Add(typeParamName);
    }

    /// <summary>
    /// Returns the ordered list of type parameter names that need Class&lt;T&gt;
    /// tokens for a method, or null if the method has none.
    /// </summary>
    public IReadOnlyList<string>? GetMethodClassTypeParamNames(string containingTypeMetadataName, string methodMetadataName)
    {
        var key = $"{containingTypeMetadataName}|{methodMetadataName}";
        return _methodClassTypeParamNames.TryGetValue(key, out var list) ? list : null;
    }

    public IReadOnlySet<string>? DrainClassTypeParams()
    {
        var result = _pendingClassTypeParams;
        _pendingClassTypeParams = null;
        return result;
    }

    public bool MethodHasClassParams(string containingTypeMetadataName, string methodMetadataName)
    {
        return _methodsWithClassParams.Contains($"{containingTypeMetadataName}|{methodMetadataName}");
    }

    /// <summary>
    /// Returns the Class&lt;T&gt; literal for a type symbol at a call site.
    /// E.g., for C# int → "int.class", for ValueType → "ValueType.class".
    /// </summary>
    public static string GetClassLiteral(ITypeSymbol typeSymbol, ConversionContext context)
    {
        if (typeSymbol.SpecialType == SpecialType.System_Int32) return "int.class";
        if (typeSymbol.SpecialType == SpecialType.System_Int64) return "long.class";
        if (typeSymbol.SpecialType == SpecialType.System_Int16) return "short.class";
        if (typeSymbol.SpecialType == SpecialType.System_Byte) return "byte.class";
        if (typeSymbol.SpecialType == SpecialType.System_Single) return "float.class";
        if (typeSymbol.SpecialType == SpecialType.System_Double) return "double.class";
        if (typeSymbol.SpecialType == SpecialType.System_Boolean) return "boolean.class";
        if (typeSymbol.SpecialType == SpecialType.System_Char) return "char.class";
        var mappedType = context.MapType(typeSymbol);
        return $"{mappedType}.class";
    }

    /// <summary>
    /// Saved compilation from immediately before the final procedural LINQ rewrite
    /// replacement. Used as a fallback when a syntax node still points at a tree
    /// that is no longer in the current ProjectCompilation.
    /// </summary>
    public CSharpCompilation? PreDesugarCompilation { get; set; }

    public void RegisterFlagsEnum(string enumName) => TypeMapper.RegisterFlagsEnum(enumName);
    public void RegisterFlagsEnum(string enumName, string valueType) => TypeMapper.RegisterFlagsEnum(enumName, valueType);
    public bool IsFlagsEnum(string enumName) => TypeMapper.IsFlagsEnum(enumName);
    public string GetFlagsEnumValueType(string enumName) => TypeMapper.GetFlagsEnumValueType(enumName);

    public void RegisterExplicitValueEnum(string enumName) => TypeMapper.RegisterExplicitValueEnum(enumName);
    public void RegisterExplicitValueEnum(string enumName, string valueType) => TypeMapper.RegisterExplicitValueEnum(enumName, valueType);
    public bool IsExplicitValueEnum(string enumName) => TypeMapper.IsExplicitValueEnum(enumName);
    public string GetExplicitValueEnumValueType(string enumName) => TypeMapper.GetExplicitValueEnumValueType(enumName);

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
    public void RegisterRuntimeClassParameter(string typeParameterName, string parameterName)
        => MethodState.RegisterRuntimeClassParameter(typeParameterName, parameterName);
    public bool TryGetRuntimeClassParameter(string typeParameterName, out string parameterName)
    {
        if (MethodState.TryGetRuntimeClassParameter(typeParameterName, out parameterName))
            return true;

        foreach (var fields in _runtimeClassFieldsStack)
        {
            if (fields.TryGetValue(typeParameterName, out parameterName!))
                return true;
        }

        parameterName = string.Empty;
        return false;
    }

    public void RegisterRuntimeClassField(string typeParameterName, string fieldName)
    {
        if (_runtimeClassFieldsStack.Count > 0)
            _runtimeClassFieldsStack.Peek()[typeParameterName] = fieldName;
    }

    private readonly Dictionary<IMethodSymbol, IReadOnlyList<ITypeParameterSymbol>> _runtimeClassRequiredTypeParametersCache =
        new(SymbolEqualityComparer.Default);
    private readonly Dictionary<INamedTypeSymbol, IReadOnlyList<ITypeParameterSymbol>> _runtimeClassRequiredTypeParametersByTypeCache =
        new(SymbolEqualityComparer.Default);

    public bool TryGetCachedRuntimeClassRequiredTypeParameters(
        IMethodSymbol methodSymbol,
        out IReadOnlyList<ITypeParameterSymbol> typeParameters)
        => _runtimeClassRequiredTypeParametersCache.TryGetValue(methodSymbol.OriginalDefinition, out typeParameters!);

    public void CacheRuntimeClassRequiredTypeParameters(
        IMethodSymbol methodSymbol,
        IReadOnlyList<ITypeParameterSymbol> typeParameters)
    {
        _runtimeClassRequiredTypeParametersCache[methodSymbol.OriginalDefinition] = typeParameters;
    }

    public bool TryGetCachedRuntimeClassRequiredTypeParameters(
        INamedTypeSymbol typeSymbol,
        out IReadOnlyList<ITypeParameterSymbol> typeParameters)
        => _runtimeClassRequiredTypeParametersByTypeCache.TryGetValue(typeSymbol.OriginalDefinition, out typeParameters!);

    public void CacheRuntimeClassRequiredTypeParameters(
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<ITypeParameterSymbol> typeParameters)
    {
        _runtimeClassRequiredTypeParametersByTypeCache[typeSymbol.OriginalDefinition] = typeParameters;
    }

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

    public void EnterType(JavaTypeDeclaration type)
    {
        _typeStack.Push(type);
        _runtimeClassFieldsStack.Push(new Dictionary<string, string>(StringComparer.Ordinal));
        CurrentClassNeedsMSTestExtension = false;
    }

    public void LeaveType()
    {
        if (_typeStack.Count > 0)
        {
            _typeStack.Pop();
            _runtimeClassFieldsStack.Pop();
        }
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
        if (IsImplicitJavaLangType(typeName)) return;
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

    private static bool IsImplicitJavaLangType(string typeName)
    {
        if (!typeName.StartsWith("java.lang.", StringComparison.Ordinal))
            return false;

        var remainder = typeName["java.lang.".Length..];
        return !remainder.Contains('.', StringComparison.Ordinal);
    }

    public void ClearImports()
    {
        ImportedTypes.Clear();
        PreferredJavaTypeImports.Clear();
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
        var result = TypeMapper.MapType(typeSymbol);
        // When the mapped type's simple name collides with the current class name,
        // use the fully-qualified Java name (package prefixed) to avoid ambiguity.
        if (CurrentType != null && typeSymbol is INamedTypeSymbol)
        {
            var simpleName = typeSymbol.Name;
            if (simpleName == CurrentType.Name)
            {
                var ns = typeSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                if (!string.IsNullOrEmpty(ns) && ns != "<global namespace>")
                {
                    var javaPkg = TypeMapper.NamespaceToPackage(ns);
                    if (!string.IsNullOrEmpty(javaPkg))
                    {
                        var idx = result.IndexOf('<');
                        var baseName = idx >= 0 ? result.Substring(0, idx) : result;
                        var genArgs = idx >= 0 ? result.Substring(idx) : "";
                        // Only prefix with package if not already qualified
                        if (!baseName.Contains('.'))
                            result = $"{javaPkg}.{baseName}{genArgs}";
                    }
                }
            }
        }
        return result;
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
                return MapType(typeInfo.Type);
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
            // trees. If the tree is not in the current compilation, try the saved
            // compilation from immediately before final procedural LINQ rewrite.
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
    {
        var registered = AliasRegistry.Register(aliasName, targetType, location, Diagnostics, CurrentNamespace, GlobalNamespace, TypeCache);
        if (registered)
        {
            var mapped = MapType(targetType);
            var importName = BuildImportName(targetType, mapped);
            if (!string.IsNullOrWhiteSpace(importName))
                PreferredJavaTypeImports[aliasName] = importName!;
        }
        return registered;
    }

    private string? BuildImportName(ITypeSymbol targetType, string mappedType)
    {
        var bareMappedType = StripTypeArguments(mappedType);
        if (bareMappedType.Contains('.'))
            return bareMappedType;

        var ns = targetType.ContainingNamespace?.ToDisplayString();
        if (string.IsNullOrWhiteSpace(ns) || ns == "<global namespace>")
            return null;

        var fullName = $"{ns}.{targetType.Name}";
        var configuredImports = TypeMappings.GetRequiredImports(fullName);
        if (configuredImports.Count > 0)
        {
            var exactImport = configuredImports.FirstOrDefault(i =>
                string.Equals(i[(i.LastIndexOf('.') + 1)..], bareMappedType, StringComparison.Ordinal));
            return exactImport ?? configuredImports[0];
        }

        var mappedNs = NamespaceToPackage(ns);
        return string.IsNullOrWhiteSpace(mappedNs) ? null : $"{mappedNs}.{bareMappedType}";
    }

    private static string StripTypeArguments(string typeName)
    {
        var idx = typeName.IndexOf('<');
        return idx > 0 ? typeName[..idx] : typeName;
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

    private INamespaceSymbol? GlobalNamespace => SemanticModel?.Compilation?.GlobalNamespace;

    // ─── Static facades delegating to JavaNaming ───
    public static bool IsJavaKeyword(string word) => JavaNaming.IsJavaKeyword(word);
    public static string EscapeJavaKeyword(string word) => JavaNaming.EscapeJavaKeyword(word);
    public static string GetErasureConflictSuffix(IMethodSymbol method) => JavaNaming.GetErasureConflictSuffix(method);
}
