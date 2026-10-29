using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Analysis;
using CSharpToJava.Core.AsyncNormalize;
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
    private readonly Stack<List<FixedPointerInfo>> _fixedScopeStack = new();

    // 映射 IMethodSymbol.OriginalDefinition → JavaMethodDeclaration，
    // 用于 override 处理时向 base/abstract 方法签名注入 leading _cs2j_T 参数。
    private readonly Dictionary<IMethodSymbol, Java.JavaMethodDeclaration> _methodDeclMap = new(SymbolEqualityComparer.Default);

    /// <summary>
    /// 注册方法符号到 Java 方法声明的映射，用于 override 处理时查找 base/abstract 方法。
    /// </summary>
    public void RegisterMethodDeclaration(IMethodSymbol methodSymbol, Java.JavaMethodDeclaration javaMethod)
    {
        if (methodSymbol != null)
            _methodDeclMap[methodSymbol.OriginalDefinition] = javaMethod;
    }

    /// <summary>
    /// 根据 override/abstract 方法符号查找已注册的 Java 方法声明。
    /// </summary>
    public Java.JavaMethodDeclaration? FindMethodDeclaration(IMethodSymbol methodSymbol)
    {
        if (methodSymbol == null)
            return null;
        _methodDeclMap.TryGetValue(methodSymbol.OriginalDefinition, out var decl);
        return decl;
    }
    private readonly Dictionary<string, string> _addressOfScratchSegments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FixedPointerInfo> _addressOfScratchPointerInfos = new(StringComparer.Ordinal);
    // Maps "ptrName-stepCount" to the pre-increment variable name for pointer backtracking
    // e.g., after "curPos++" generates "_ptrPost3 = curPos; curPos = curPos.asSlice(2);",
    // this maps "curPos-1" → "_ptrPost3" so that *(curPos - 1) can use _ptrPost3
    private readonly Dictionary<string, string> _pointerBacktrackVars = new(StringComparer.Ordinal);
    // Maps pointer variable name to its base MemorySegment variable name
    // e.g., after "fixed (char* p = arr)" generates "MemorySegment __base_p = MemorySegment.ofArray(arr); MemorySegment p = __base_p;",
    // this maps "p" → "__base_p" so that p.asSlice(-N) can use __base_p.asSlice(p.address() - __base_p.address() - N)
    private readonly Dictionary<string, string> _pointerBaseSegments = new(StringComparer.Ordinal);
    public bool IsInFixedScope => _fixedScopeStack.Count > 0;
    // 【C06 退役评估 M3b】fixed scope 注册零调用：PushFixedScope/PopFixedScope
    // 已无任何调用方（fixed 语句在预处理阶段归一，不再经转换器维护 scope）；
    // 暂保留 API 作为直连安全网，不删除、不改行为。
    public void PushFixedScope(List<FixedPointerInfo> pointers) => _fixedScopeStack.Push(pointers);
    public void PopFixedScope() => _fixedScopeStack.Pop();
    public FixedPointerInfo? FindPointerInfo(string varName)
    {
        foreach (var scope in _fixedScopeStack)
        {
            var info = scope.FirstOrDefault(p => p.VariableName == varName);
            if (info != null) return info;
        }
        return _addressOfScratchPointerInfos.TryGetValue(varName, out var scratchInfo) ? scratchInfo : null;
    }

    public bool TryGetAddressOfScratchSegment(string sourceName, out string segmentName)
        => _addressOfScratchSegments.TryGetValue(sourceName, out segmentName!);

    public void RegisterAddressOfScratchSegment(
        string sourceName,
        string segmentName,
        FixedPointerInfo pointerInfo)
    {
        _addressOfScratchSegments[sourceName] = segmentName;
        _addressOfScratchPointerInfos[segmentName] = new FixedPointerInfo
        {
            VariableName = segmentName,
            CSharpElementTypeName = pointerInfo.CSharpElementTypeName,
            ValueLayoutName = pointerInfo.ValueLayoutName,
            ElementSize = pointerInfo.ElementSize,
            NeedsUnsignedMask = pointerInfo.NeedsUnsignedMask,
            MaskSuffix = pointerInfo.MaskSuffix,
            WriteCast = pointerInfo.WriteCast
        };
    }

    public void RegisterPointerBacktrackVar(string pointerName, int stepCount, string preIncrementVar)
    {
        _pointerBacktrackVars[$"{pointerName}-{stepCount}"] = preIncrementVar;
    }

    public bool TryGetPointerBacktrackVar(string pointerName, int stepCount, out string preIncrementVar)
    {
        return _pointerBacktrackVars.TryGetValue($"{pointerName}-{stepCount}", out preIncrementVar!);
    }

    public void InvalidatePointerBacktrackVars(string pointerName)
    {
        var keysToRemove = _pointerBacktrackVars.Keys.Where(k => k.StartsWith(pointerName + "-", StringComparison.Ordinal)).ToList();
        foreach (var key in keysToRemove)
            _pointerBacktrackVars.Remove(key);
    }

    public void RegisterPointerBase(string pointerName, string baseSegmentName)
    {
        _pointerBaseSegments[pointerName] = baseSegmentName;
    }

    public bool TryGetPointerBase(string pointerName, out string baseSegmentName)
    {
        return _pointerBaseSegments.TryGetValue(pointerName, out baseSegmentName!);
    }

    public ConversionOptions Options { get; }
    public SemanticModel? SemanticModel { get; set; }

    public TypeMapping.TypeMappingRegistry TypeMappings { get; }
    public DiagnosticCollector Diagnostics { get; } = new();

    /// <summary>
    /// Name of the project/assembly currently being converted. Some C# internal
    /// helper types are assembly-local even when their namespace and simple name
    /// match helpers in another project.
    /// </summary>
    public string? CurrentProjectName { get; set; }

    /// <summary>Pre-scanned var-declared local types, populated by VarTypeResolver.</summary>
    public Dictionary<string, ITypeSymbol> VarTypeMap { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Types learned while transforming statements in order. These are scoped to
    /// the current method conversion state and take precedence over the coarse
    /// name-only pre-scan cache.
    /// </summary>
    public Dictionary<string, ITypeSymbol> LocalTypeOverrides { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Renames for nested types whose simple name collides with a sibling nested type
    /// of a different generic arity (Java has no per-arity class names). Keyed by the
    /// type symbol's ToDisplayString(); consulted by TypeMappingService.TryMapNestedType
    /// so declarations and reference sites agree.
    /// </summary>
    public Dictionary<string, string> NestedTypeNameOverrides { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Memoizes MethodLikelyNeedsClassTokens results so dense generic call graphs
    /// do not re-scan the same method bodies exponentially.
    /// </summary>
    public Dictionary<IMethodSymbol, bool> MethodClassTokenNeedCache { get; } =
        new(SymbolEqualityComparer.Default);

    // ─── Hoisted Local Function Names ────────────────────────────────

    /// <summary>
    /// Java names assigned to hoisted local functions, keyed by the local function's
    /// method symbol. Assigned during the pre-scan so the hoisted declaration and
    /// every call site (which may be converted before the declaration is hoisted)
    /// emit the same name. When a local function's camelCase name would collide
    /// with a class member or another hoisted local function, a numeric suffix is
    /// appended to keep Java signatures unique.
    /// </summary>
    private readonly Dictionary<ISymbol, string> _hoistedLocalFunctionJavaNames = new(SymbolEqualityComparer.Default);

    private readonly Dictionary<string, HashSet<string>> _hoistedLocalFunctionNameSets = new(StringComparer.Ordinal);

    private readonly Dictionary<string, HashSet<string>> _classMethodJavaNameCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers the Java name of a hoisted local function. The first registration
    /// for a symbol wins so repeated pre-scans stay deterministic.
    /// </summary>
    public void RegisterHoistedLocalFunctionName(ISymbol symbol, string javaName)
    {
        if (!_hoistedLocalFunctionJavaNames.ContainsKey(symbol))
            _hoistedLocalFunctionJavaNames[symbol] = javaName;
    }

    /// <summary>
    /// Looks up the registered Java name for a hoisted local function symbol.
    /// </summary>
    public bool TryGetHoistedLocalFunctionName(ISymbol? symbol, out string javaName)
    {
        if (symbol != null && _hoistedLocalFunctionJavaNames.TryGetValue(symbol, out javaName!))
            return true;
        javaName = string.Empty;
        return false;
    }

    /// <summary>
    /// Gets (creating if needed) the set of Java names already assigned to hoisted
    /// local functions of the class identified by <paramref name="classKey"/>, so
    /// same-named local functions in different methods stay unique.
    /// </summary>
    internal HashSet<string> GetOrCreateHoistedLocalFunctionNameSet(string classKey)
    {
        if (!_hoistedLocalFunctionNameSets.TryGetValue(classKey, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _hoistedLocalFunctionNameSets[classKey] = set;
        }
        return set;
    }

    /// <summary>
    /// Gets (creating if needed) the cached set of Java method names the class
    /// identified by <paramref name="classKey"/> will emit for its declared
    /// methods, so hoisted local functions avoid colliding with real members.
    /// </summary>
    internal HashSet<string> GetOrCreateClassMethodJavaNames(string classKey, Func<HashSet<string>> factory)
    {
        if (!_classMethodJavaNameCache.TryGetValue(classKey, out var set))
        {
            set = factory();
            _classMethodJavaNameCache[classKey] = set;
        }
        return set;
    }

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
    public bool ReturnsCSharpGenericIterable { get; set; }
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
    private readonly Stack<bool> _mstestExtensionStack = new();

    /// <summary>
    /// Stack-scoped per type: nested type transforms push their own entry so they
    /// cannot clear the outer class's requirement before its annotations are emitted.
    /// </summary>
    public bool CurrentClassNeedsMSTestExtension
    {
        get => _mstestExtensionStack.Count > 0 && _mstestExtensionStack.Peek();
        set
        {
            if (_mstestExtensionStack.Count == 0)
            {
                _mstestExtensionStack.Push(value);
            }
            else
            {
                var current = _mstestExtensionStack.Pop();
                _mstestExtensionStack.Push(current || value);
            }
        }
    }
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
    /// <summary>源内定义类型的跨命名空间导入（TypeMappingService 登记）：发射期
    /// 据此豁免「大写 System.* 恒等包导入」的丢弃（真实链 SegmentStringBuilder，
    /// package System.Text）。随 <see cref="ClearImports"/> 按文件清空。</summary>
    public HashSet<string> SourceDefinedImports { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> PreferredJavaTypeImports { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Method-level mutable state (pre/post statements, ref holders, stream vars).
    /// </summary>
    public MethodConversionState MethodState { get; } = new();

    /// <summary>
    /// Stack of switch-with-goto-case state mappings.
    /// Each entry is a tuple of (switchStatement, stateName, loopName, stateByCaseValue, defaultState).
    /// Used to transform nested goto case/default statements inside switch sections.
    /// </summary>
    private readonly Stack<(Microsoft.CodeAnalysis.CSharp.Syntax.SwitchStatementSyntax SwitchStatement, string StateName, string LoopName, IReadOnlyDictionary<string, int> StateByCaseValue, int? DefaultState)> _switchGotoCaseStack = new();

    public bool IsInSwitchGotoCase => _switchGotoCaseStack.Count > 0;

    public void PushSwitchGotoCase(Microsoft.CodeAnalysis.CSharp.Syntax.SwitchStatementSyntax switchStatement, string stateName, string loopName, IReadOnlyDictionary<string, int> stateByCaseValue, int? defaultState)
        => _switchGotoCaseStack.Push((switchStatement, stateName, loopName, stateByCaseValue, defaultState));

    public void PopSwitchGotoCase() => _switchGotoCaseStack.Pop();

    public bool TryGetSwitchGotoCaseInfo(out string stateName, out string loopName, out IReadOnlyDictionary<string, int> stateByCaseValue, out int? defaultState)
        => TryGetSwitchGotoCaseInfo(out _, out stateName, out loopName, out stateByCaseValue, out defaultState);

    public bool TryGetSwitchGotoCaseInfo(out Microsoft.CodeAnalysis.CSharp.Syntax.SwitchStatementSyntax switchStatement, out string stateName, out string loopName, out IReadOnlyDictionary<string, int> stateByCaseValue, out int? defaultState)
    {
        if (_switchGotoCaseStack.Count > 0)
        {
            var info = _switchGotoCaseStack.Peek();
            switchStatement = info.SwitchStatement;
            stateName = info.StateName;
            loopName = info.LoopName;
            stateByCaseValue = info.StateByCaseValue;
            defaultState = info.DefaultState;
            return true;
        }
        switchStatement = null!;
        stateName = "";
        loopName = "";
        stateByCaseValue = null!;
        defaultState = null;
        return false;
    }

    // ─── Facade properties delegating to MethodState for backward compatibility ───

    public HashSet<string> StreamLocalVariables => MethodState.StreamLocalVariables;
    public Dictionary<string, string> QueryLetAliases
    {
        get => MethodState.QueryLetAliases;
        set => MethodState.QueryLetAliases = value;
    }

    public TypeMappingService TypeMapper { get; private set; } = null!;
    public Dictionary<ITypeSymbol, TypeMappingService.TypeMappingCacheEntry> TypeCache => TypeMapper.TypeCache;

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

    public bool IsOverrideMethod(IMethodSymbol method)
        => ConversionContext.IsOverrideOrImplementation(method);

    private readonly Dictionary<ISymbol, string> _erasureRenamedMethods =
        new(SymbolEqualityComparer.Default);

    /// <summary>
    /// Records that a C# method overload was renamed because its Java erasure
    /// signature collides with an earlier overload. Call sites use this mapping to
    /// bind to the renamed declaration instead of the shadowing sibling.
    /// </summary>
    public void RegisterErasureConflictMethodName(IMethodSymbol method, string javaName)
        => _erasureRenamedMethods[method] = javaName;

    /// <summary>
    /// 按方法符号取擦除冲突改名。Java 覆写方法必须与基类改名后的方法同名，
    /// 沿 OverriddenMethod 链向上查（真实链 XPathDocumentNavigator.Clone 覆写
    /// XPathNavigator.Clone，基类改 clone_2 后子类与调用点必须同步 clone_2）。
    /// </summary>
    public bool TryGetErasureConflictMethodName(IMethodSymbol method, out string javaName)
    {
        for (var m = method; m != null; m = m.OverriddenMethod)
        {
            if (_erasureRenamedMethods.TryGetValue(m, out javaName!))
                return true;
            if (!SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, m)
                && _erasureRenamedMethods.TryGetValue(m.OriginalDefinition, out javaName!))
                return true;
        }
        javaName = null!;
        return false;
    }

    /// <summary>
    /// Returns the Java class literal for a concrete type symbol at a call site.
    /// E.g., for C# int → "int.class", for ValueType → "ValueType.class".
    /// Type parameters cannot use T.class in Java. The main C#→Java pipeline must
    /// reject <c>typeof(T)</c>/<c>is T</c> on a type parameter before reaching here
    /// (fail fast, requiring preprocessor normalization); the plain "_cs2j_{T}"
    /// fallback is retained only for the experimental HIR pipeline which manages
    /// its own token declarations.
    /// </summary>
    public static string GetClassLiteral(ITypeSymbol typeSymbol, ConversionContext context)
    {
        if (typeSymbol is ITypeParameterSymbol)
        {
            // Java has no runtime token for an erased type parameter. Call sites that
            // carry a generic-deepen anchor render it explicitly; every other path
            // degrades to Object.class (e.g. `new T[n]` on an erased element type).
            return "Object.class";
        }
        if (typeSymbol.SpecialType == SpecialType.System_Int32) return "int.class";
        if (typeSymbol.SpecialType == SpecialType.System_Int64) return "long.class";
        if (typeSymbol.SpecialType == SpecialType.System_Int16) return "short.class";
        if (typeSymbol.SpecialType == SpecialType.System_Single) return "float.class";
        if (typeSymbol.SpecialType == SpecialType.System_Double) return "double.class";
        if (typeSymbol.SpecialType == SpecialType.System_Boolean) return "boolean.class";
        if (typeSymbol.SpecialType == SpecialType.System_Char) return "char.class";
        // C# multidimensional arrays (int[,], int[,,]) map to Java jagged arrays
        // (int[][]) losing the rank. Route through TypeHelper so it records the
        // original rank; Type.GetArrayRank() (→ TypeHelper.getArrayRank) then still
        // detects multidim types and e.g. System.Text.Json rejects them as in .NET.
        // The helper returns Class<?>; cast back to the exact array Class so calls
        // like serialize(Class<TValue>, TValue) keep generic type inference.
        if (typeSymbol is IArrayTypeSymbol { Rank: > 1 } multiDimArray)
        {
            context.AddImport("io.github.ningpp.compat.TypeHelper");
            var helperCall = $"TypeHelper.getMultiDimArrayClass({GetClassLiteral(multiDimArray.ElementType, context)}, {multiDimArray.Rank})";
            return $"((Class<{context.MapType(typeSymbol)}>) (Class<?>) {helperCall})";
        }
        var mappedType = context.MapType(typeSymbol);
        // Class literals cannot carry generic type arguments (Foo<T>.class is
        // illegal Java) — strip them, including nested ones.
        return $"{StripAllTypeArguments(mappedType)}.class";
    }

    private static string StripAllTypeArguments(string typeName)
    {
        var builder = new System.Text.StringBuilder(typeName.Length);
        int depth = 0;
        foreach (var ch in typeName)
        {
            if (ch == '<')
            {
                depth++;
                continue;
            }
            if (ch == '>' && depth > 0)
            {
                depth--;
                continue;
            }
            if (depth == 0)
                builder.Append(ch);
        }
        return builder.ToString();
    }

    /// <summary>
    /// True when the method overrides a base-class slot or implements an interface
    /// member. Such methods must keep the base slot's Java erasure signature —
    /// synthetic leading Class&lt;T&gt; parameters would break the override check.
    /// </summary>
    public static bool IsOverrideOrImplementation(IMethodSymbol method)
    {
        if (method.OverriddenMethod != null)
            return true;
        if (method.ExplicitInterfaceImplementations.Length > 0)
            return true;

        var containingType = method.ContainingType;
        if (containingType == null)
            return false;

        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var ifaceMember in iface.GetMembers(method.Name).OfType<IMethodSymbol>())
            {
                var implementation = containingType.FindImplementationForInterfaceMember(ifaceMember);
                if (SymbolEqualityComparer.Default.Equals(implementation, method))
                    return true;
            }
        }

        return false;
    }

    // ─── Facade methods delegating to MethodState for backward compatibility ───

    public void AddPreStatement(string statement) => MethodState.AddPreStatement(statement);
    public void AddPreStatementAllowDuplicate(string statement) => MethodState.AddPreStatementAllowDuplicate(statement);
    public IReadOnlyList<string> DrainPreStatements() => MethodState.DrainPreStatements();
    public bool HasPendingPreStatements => MethodState.HasPendingPreStatements;

    public void AddBranchScopedPreStatement(string statement) => MethodState.AddBranchScopedPreStatement(statement);
    public IReadOnlyList<string> DrainBranchScopedPreStatements() => MethodState.DrainBranchScopedPreStatements();
    public bool HasBranchScopedPreStatements => MethodState.HasBranchScopedPreStatements;

    public void AddPostStatement(string statement) => MethodState.AddPostStatement(statement);
    public IReadOnlyList<string> DrainPostStatements() => MethodState.DrainPostStatements();
    public bool HasPendingPostStatements => MethodState.HasPendingPostStatements;

    public LabelRegistry Labels => MethodState.Labels;

    public bool IsReadOnlyRefStructParam(string paramName) => MethodState.IsReadOnlyRefStructParam(paramName);
    public string AllocateOutHolderName(string varName) => MethodState.AllocateOutHolderName(varName);
    public bool TryGetActiveRefHolder(string varName, out string holderName) => MethodState.TryGetActiveRefHolder(varName, out holderName);
    public void SetActiveRefHolder(string varName, string holderName) => MethodState.SetActiveRefHolder(varName, holderName);
    public void RemoveActiveRefHolder(string varName) => MethodState.RemoveActiveRefHolder(varName);
    public string AllocateRefHolderName(string varName) => MethodState.AllocateRefHolderName(varName);
    public bool IsInShortCircuitOperand => MethodState.IsInShortCircuitOperand;
    public void EnterShortCircuitOperand() => MethodState.EnterShortCircuitOperand();
    public void ExitShortCircuitOperand() => MethodState.ExitShortCircuitOperand();
    public void RegisterShortCircuitDeferredSideEffect(string tempVar, string sideEffect) => MethodState.RegisterShortCircuitDeferredSideEffect(tempVar, sideEffect);
    public bool TryConsumeShortCircuitDeferredSideEffect(string tempVar, out string sideEffect) => MethodState.TryConsumeShortCircuitDeferredSideEffect(tempVar, out sideEffect!);

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
            typeSymbol => TryGetCustomJavaTopLevelTypeName(typeSymbol, out var javaName) ? javaName : null,
            resolveAlias: name => ResolveAlias(name),
            getCurrentEnclosingType: () => CurrentEnclosingRoslynType,
            isUsingAlias: name => IsAlias(name),
            getNestedTypeNameOverride: key => NestedTypeNameOverrides.TryGetValue(key, out var nestedName) ? nestedName : null,
            sourceDefinedImports: SourceDefinedImports);
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
        CurrentClassNeedsMSTestExtension = false;
    }

    public void LeaveType()
    {
        if (_typeStack.Count > 0)
        {
            _typeStack.Pop();
        }
    }

    public void EnterMethod(IMethodSymbol? method)
    {
        _methodStack.Push(method);
        // async Task/Task<T>/ValueTask(T) 方法的 Java 形态是 CompletableFuture 返回：
        // return 包 completedFuture（见 StatementTransformer.TransformReturnStatement）。
        // async void 不置位（async-normalize 已由 CS2J1803 拒绝）。注意：这是阻塞式
        // 轻量桥（await=AsyncAwait.join），不是状态机调度——异常契约与返回类型诚实，
        // 挂起语义不做承诺（c18 设计 §4.8 重量形态裁决不做）。
        IsInAsyncContext = method is { IsAsync: true }
            && AsyncShapeClassifier.IsTaskLike(method.ReturnType);
        ReturnsCSharpGenericIterable = false;
        var readOnlyParams = method?.Parameters
            .Where(p => StructCloneHelper.IsRefParamEffectivelyReadOnlyInHierarchy(p, SemanticModel?.Compilation))
            .Select(p => p.Name);
        MethodState.Reset(readOnlyParams);
        _addressOfScratchSegments.Clear();
        _addressOfScratchPointerInfos.Clear();
        _pointerBacktrackVars.Clear();
        _pointerBaseSegments.Clear();
        LocalTypeOverrides.Clear();
    }

    public void LeaveMethod()
    {
        if (_methodStack.Count > 0)
            _methodStack.Pop();
    }

    /// <summary>
    /// Pushes a method symbol onto the current-method stack WITHOUT resetting the
    /// method conversion state. Used when converting hoisted local function bodies
    /// (which belong to their own method scope but must not wipe the enclosing
    /// method's bookkeeping).
    /// </summary>
    public void EnterNestedMethodContext(IMethodSymbol? method)
    {
        _methodStack.Push(method);
    }

    /// <summary>
    /// Pops a method symbol pushed by <see cref="EnterNestedMethodContext"/>.
    /// </summary>
    public void LeaveNestedMethodContext()
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

    /// <summary>
    /// 登记一个指向<b>源内定义类型</b>的导入：与普通导入同入 <see cref="ImportedTypes"/>，
    /// 并同步标记 <see cref="SourceDefinedImports"/>（发射期据此豁免大写 System.*
    /// 恒等包导入的丢弃——如静态接收者 <c>SegmentStringBuilder.create()</c> 的
    /// import System.Text.SegmentStringBuilder）。
    /// </summary>
    public void AddSourceDefinedImport(string typeName)
    {
        AddImport(typeName);
        if (ImportedTypes.Contains(typeName))
            SourceDefinedImports.Add(typeName);
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
        SourceDefinedImports.Clear();
        PreferredJavaTypeImports.Clear();
        // Clearing cache ensures each type group re-triggers import additions.
        TypeMapper.ClearCache();
    }

    public string NamespaceToPackage(string ns) => TypeMapper.NamespaceToPackage(ns);

    /// <summary>
    /// 收集工作区源内在给定 Java 包下定义的所有顶级类型简单名（跨文件）。
    /// 发射期的 java.util 单类型导入回填据此避让：单类型导入会遮蔽同包同名
    /// 类型（JLS 7.5.3/6.4.1——单类型导入优先于同包隐式声明），真实链
    /// dotnet-xml dotnet.xml.schema.BitSet 被 import java.util.BitSet 劫持，
    /// SyntaxTreeNode.constructPos 抽象签名与子类覆盖签名错位（javac: 不是
    /// 抽象的, 并且未覆盖抽象方法 constructPos）。按包缓存，一次编译一遍。
    /// </summary>
    public HashSet<string> CollectSourceTopLevelTypeSimpleNamesInPackage(string javaPackage)
    {
        var cacheKey = javaPackage ?? string.Empty;
        if (_packageSourceTopLevelTypeNamesCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var names = new HashSet<string>(StringComparer.Ordinal);
        var globalNs = SemanticModel?.Compilation?.GlobalNamespace;
        if (globalNs != null)
            CollectSourceNamespaceTypeNames(globalNs, cacheKey, names);
        _packageSourceTopLevelTypeNamesCache[cacheKey] = names;
        return names;
    }

    private readonly Dictionary<string, HashSet<string>> _packageSourceTopLevelTypeNamesCache =
        new(StringComparer.Ordinal);

    private void CollectSourceNamespaceTypeNames(INamespaceSymbol ns, string javaPackage, HashSet<string> names)
    {
        if (string.Equals(NamespaceToPackage(ns.ToDisplayString()), javaPackage, StringComparison.Ordinal))
        {
            foreach (var typeMember in ns.GetTypeMembers())
            {
                if (typeMember.Locations.Any(loc => loc.IsInSource))
                    names.Add(typeMember.Name);
            }
        }

        foreach (var childNs in ns.GetNamespaceMembers())
            CollectSourceNamespaceTypeNames(childNs, javaPackage, names);
    }
    public string GetJavaTopLevelTypeName(INamedTypeSymbol typeSymbol)
    {
        if (TryGetCustomJavaTopLevelTypeName(typeSymbol, out var customName))
            return customName;

        return typeSymbol.Name;
    }

    private bool TryGetCustomJavaTopLevelTypeName(INamedTypeSymbol typeSymbol, out string javaName)
    {
        if (TryGetAssemblyScopedJavaTypeName(typeSymbol, out javaName))
            return true;

        return TryGetAritySafeJavaTypeName(typeSymbol, out javaName);
    }

    private static bool TryGetAritySafeJavaTypeName(INamedTypeSymbol typeSymbol, out string javaName)
    {
        javaName = string.Empty;
        var definition = typeSymbol.OriginalDefinition ?? typeSymbol;
        if (definition.ContainingType != null || definition.Arity == 0)
            return false;

        var namespaceSymbol = definition.ContainingNamespace;
        if (namespaceSymbol == null || namespaceSymbol.IsGlobalNamespace)
            return false;

        var sameNameTypes = new List<INamedTypeSymbol>();
        foreach (var sameNameType in namespaceSymbol.GetTypeMembers(definition.Name)
            .Where(t => t.ContainingType == null && t.Locations.Any(l => l.IsInSource)))
        {
            var originalDefinition = sameNameType.OriginalDefinition;
            if (sameNameTypes.Any(existing => SymbolEqualityComparer.Default.Equals(existing, originalDefinition)))
                continue;

            sameNameTypes.Add(originalDefinition);
        }
        if (sameNameTypes.Select(t => t.Arity).Distinct().Count() <= 1)
            return false;

        var candidate = definition.Name + definition.Arity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!ConflictsWithDifferentSourceType(namespaceSymbol, candidate, definition))
        {
            javaName = EscapeJavaKeyword(candidate);
            return true;
        }

        candidate = definition.Name + "_" + definition.Arity.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var suffix = 1;
        while (ConflictsWithDifferentSourceType(namespaceSymbol, candidate, definition))
        {
            candidate = definition.Name + "_" + definition.Arity.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "_" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            suffix++;
        }

        javaName = EscapeJavaKeyword(candidate);
        return true;
    }

    private static bool ConflictsWithDifferentSourceType(
        INamespaceSymbol namespaceSymbol,
        string candidateName,
        INamedTypeSymbol originalDefinition)
    {
        return namespaceSymbol.GetTypeMembers(candidateName)
            .Where(t => t.ContainingType == null && t.Locations.Any(l => l.IsInSource))
            .Select(t => t.OriginalDefinition ?? t)
            .Any(t => !SymbolEqualityComparer.Default.Equals(t, originalDefinition));
    }

    public bool TryGetAssemblyScopedJavaTypeName(INamedTypeSymbol typeSymbol, out string javaName)
    {
        javaName = string.Empty;
        if (!IsAssemblyScopedSystemResourceType(typeSymbol)
            || string.IsNullOrWhiteSpace(CurrentProjectName))
        {
            return false;
        }

        javaName = BuildAssemblyScopedJavaTypeName(CurrentProjectName, typeSymbol.Name);
        return true;
    }

    private static bool IsAssemblyScopedSystemResourceType(INamedTypeSymbol typeSymbol)
    {
        var namespaceName = typeSymbol.ContainingNamespace?.ToDisplayString();
        return typeSymbol.Name == "SR"
            && typeSymbol.ContainingType == null
            && namespaceName is "System" or "dotnet.system";
    }

    private static string BuildAssemblyScopedJavaTypeName(string projectName, string typeName)
    {
        var prefix = ToPascalIdentifier(projectName);
        return string.IsNullOrWhiteSpace(prefix) ? typeName : prefix + typeName;
    }

    private static string ToPascalIdentifier(string value)
    {
        var builder = new System.Text.StringBuilder();
        var capitalizeNext = true;

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                if (builder.Length == 0 && char.IsDigit(ch))
                    builder.Append('_');

                builder.Append(capitalizeNext ? char.ToUpperInvariant(ch) : ch);
                capitalizeNext = false;
            }
            else
            {
                capitalizeNext = true;
            }
        }

        return EscapeJavaKeyword(builder.ToString());
    }

    public string MapType(ITypeSymbol typeSymbol)
    {
        // Check using alias: if the type name matches a registered alias, resolve it
        if (AliasRegistry.Resolve(typeSymbol.Name) is ITypeSymbol aliasTarget
            && aliasTarget.Name != typeSymbol.Name)
            return TypeMapper.MapType(aliasTarget);
        var result = TypeMapper.MapType(typeSymbol);
        result = QualifyMappedTypeIfImportedTypeNameCollides(typeSymbol, result);
        return QualifyMappedTypeIfCurrentTypeNameCollides(typeSymbol, result);
    }

    private readonly Dictionary<string, bool> _mapsToSourceTypeCache = new(StringComparer.Ordinal);
    private readonly Dictionary<(string TypeName, string PropertyName), bool> _cSharpStylePropertyCache = new();

    /// <summary>
    /// True when <paramref name="containingType"/> (a BCL type such as
    /// <c>System.Xml.XmlReader</c>) maps onto a type declared in this compilation's
    /// source AND that ported class carries <paramref name="propertyName"/> in the
    /// member-desugar accessor form (<c>get_LocalName</c>), which the Java emission
    /// renames to <c>LocalName()</c>. Auto-properties keep their bean-getter shape
    /// (<c>getLocalName()</c>) and must not take this branch.
    /// </summary>
    public bool ExposesCSharpStyleProperty(ITypeSymbol? containingType, string propertyName)
    {
        if (containingType == null || string.IsNullOrEmpty(propertyName)) return false;
        var mapped = MapType(containingType);
        if (string.IsNullOrWhiteSpace(mapped) || mapped.Contains('<') || mapped.Contains('['))
            return false;

        // The mapped rendering is a simple name; resolve it in the ported package of the
        // C# namespace (System.Xml → dotnet.xml), so `System.Xml.XmlReader` is looked up
        // as `dotnet.xml.XmlReader`. When MapType already returns a fully-qualified name
        // (collision qualification), use it directly to avoid double-prefixing.
        string qualified;
        if (mapped.Contains('.'))
        {
            qualified = mapped;
        }
        else
        {
            var ns = containingType.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            var package = TypeMappings.MapNamespace(ns) ?? ns;
            qualified = string.IsNullOrEmpty(package) ? mapped : $"{package}.{mapped}";
        }

        if (_cSharpStylePropertyCache.TryGetValue((qualified, propertyName), out var cached))
            return cached;

        var compilation = SemanticModel?.Compilation;
        var resolved = compilation?.GetTypeByMetadataName(qualified);
        var isSource = resolved != null && resolved.Locations.Any(l => l.IsInSource);
        _mapsToSourceTypeCache[qualified] = isSource;
        // 注意：普通自动属性的访问器方法名也是 get_X，故不能用 GetMembers("get_X") 判定。
        // 区分点是该类型是否仍有同名属性符号：属性 → Java bean getter（getX()）；
        // 只剩 get_X 方法（member-desugar 展开）→ C11 访问器（X()）。
        var accessorForm = isSource
            && !resolved!.GetMembers(propertyName).OfType<IPropertySymbol>().Any()
            && resolved.GetMembers($"get_{propertyName}").OfType<IMethodSymbol>()
                .Any(m => !m.IsStatic && m.Parameters.Length == 0);
        _cSharpStylePropertyCache[(qualified, propertyName)] = accessorForm;
        return accessorForm;
    }

    /// <summary>
    /// True when <paramref name="containingType"/> (a BCL type such as
    /// <c>System.Xml.XmlNodeList</c>) maps onto a type declared in this compilation's
    /// source AND that ported class carries an <c>Item(int)</c> method in the
    /// member-desugar accessor form, which the Java emission renames to
    /// <c>item(int)</c>. This is used by the element-access transformer to emit
    /// <c>item(int)</c> instead of the default Java <c>get(int)</c>.
    /// </summary>
    public bool ExposesCSharpStyleIndexer(ITypeSymbol? containingType)
    {
        if (containingType == null) return false;
        var mapped = MapType(containingType);
        if (string.IsNullOrWhiteSpace(mapped) || mapped.Contains('<') || mapped.Contains('['))
            return false;

        string qualified;
        if (mapped.Contains('.'))
        {
            qualified = mapped;
        }
        else
        {
            var ns = containingType.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            var package = TypeMappings.MapNamespace(ns) ?? ns;
            qualified = string.IsNullOrEmpty(package) ? mapped : $"{package}.{mapped}";
        }

        if (_cSharpStyleIndexerCache.TryGetValue(qualified, out var cached))
            return cached;

        var compilation = SemanticModel?.Compilation;
        var resolved = compilation?.GetTypeByMetadataName(qualified);
        var isSource = resolved != null && resolved.Locations.Any(l => l.IsInSource);
        // The ported class has Item(int) as a member-desugar-expanded method
        // (not an indexer property), and no get_Item method — only Item(int).
        var accessorForm = isSource
            && !resolved!.GetMembers().OfType<IPropertySymbol>().Any(p => p.IsIndexer)
            && resolved.GetMembers("Item").OfType<IMethodSymbol>()
                .Any(m => !m.IsStatic && m.Parameters.Length == 1);
        _cSharpStyleIndexerCache[qualified] = accessorForm;
        return accessorForm;
    }

    private readonly Dictionary<string, bool> _cSharpStyleIndexerCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Maps a type for use as a generic type argument: primitive Java types are
    /// boxed (int → Integer, etc.) since Java generics cannot hold primitives.
    /// </summary>
    public string MapTypeBoxed(ITypeSymbol typeSymbol)
    {
        var mapped = MapType(typeSymbol);
        return mapped switch
        {
            "int" => "Integer",
            "long" => "Long",
            "short" => "Short",
            "byte" => "Byte",
            "float" => "Float",
            "double" => "Double",
            "boolean" => "Boolean",
            "char" => "Character",
            _ => mapped
        };
    }

    public string MapTypeForDeclarationHeader(ITypeSymbol typeSymbol)
    {
        // Class declaration headers have a narrower scope than class bodies:
        // a nested member type is not visible by simple name in "extends"/"implements".
        var result = TypeMapper.MapTypeForDeclarationHeader(typeSymbol);
        return QualifyMappedTypeIfCurrentTypeNameCollides(typeSymbol, result);
    }

    private string QualifyMappedTypeIfCurrentTypeNameCollides(ITypeSymbol typeSymbol, string result)
    {
        // When the mapped type's simple name collides with the current class name,
        // use the fully-qualified Java name (package prefixed) to avoid ambiguity.
        // However, skip qualification when the type IS the current class (self-reference)
        // or is a nested type of the current class — no ambiguity exists in these cases.
        // Also skip when the type has an explicit configured mapping (e.g. System.Decimal → Decimal),
        // since the configured import already resolves any ambiguity.
        if (CurrentType != null && typeSymbol is INamedTypeSymbol namedType)
        {
            var simpleName = typeSymbol.Name;
            if (simpleName == CurrentType.Name)
            {
                // Skip FQN for self-references or references to nested types within
                // the same enclosing type (handled by TryMapNestedType's isSameEnclosingType)
                bool isSelfOrNestedReference = CurrentEnclosingRoslynType != null
                    && (SymbolEqualityComparer.Default.Equals(CurrentEnclosingRoslynType, namedType)
                        || SymbolEqualityComparer.Default.Equals(CurrentEnclosingRoslynType.OriginalDefinition, namedType.OriginalDefinition)
                        || SymbolEqualityComparer.Default.Equals(CurrentEnclosingRoslynType, namedType.ContainingType));
                if (isSelfOrNestedReference)
                    return result;

                // Skip FQN when the type has an explicit configured mapping —
                // the import from the mapping resolves the ambiguity.
                if (HasConfiguredTypeMapping(typeSymbol))
                    return result;

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

    private string QualifyMappedTypeIfImportedTypeNameCollides(ITypeSymbol typeSymbol, string result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return result;

        // Nested types must not be qualified with their namespace: the simple name
        // is correct inside the enclosing type (Java member types shadow single-type
        // imports), and other scopes already carry the "Outer.Inner" prefix from
        // TryMapNestedType. Namespace qualification would drop the outer segment and
        // produce a non-existent type such as Microsoft.Msagl.Layout.Incremental.Complex.
        if (typeSymbol.ContainingType != null)
            return result;

        var genericStart = result.IndexOf('<');
        var baseName = genericStart >= 0 ? result[..genericStart] : result;
        if (baseName.Contains('.', StringComparison.Ordinal))
            return result;

        var ns = typeSymbol.ContainingNamespace?.ToDisplayString();
        if (string.IsNullOrWhiteSpace(ns) || ns == "<global namespace>")
            return result;

        if (HasConfiguredTypeMapping(typeSymbol)
            || MappedGenericTypeArgumentsHaveConfiguredMappings(typeSymbol))
            return result;

        var ownJavaName = $"{NamespaceToPackage(ns)}.{baseName}";
        var hasConflictingImport = ImportedTypes.Any(import =>
        {
            if (import.EndsWith(".*", StringComparison.Ordinal))
                return false;

            var lastDot = import.LastIndexOf('.');
            if (lastDot < 0)
                return false;

            return import[(lastDot + 1)..] == baseName
                && !string.Equals(import, ownJavaName, StringComparison.Ordinal);
        });

        if (!hasConflictingImport)
            return result;

        var typeArgs = genericStart >= 0 ? result[genericStart..] : string.Empty;
        return $"{ownJavaName}{typeArgs}";
    }

    private bool HasConfiguredTypeMapping(ITypeSymbol typeSymbol)
    {
        var fullName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (fullName.StartsWith("global::", StringComparison.Ordinal))
            fullName = fullName["global::".Length..];

        if (TypeMappings.HasTypeMapping(fullName))
            return true;

        if (IsFrameworkTypeSymbol(typeSymbol)
            && TypeMappings.FindConfigKeyBySimpleName(typeSymbol.Name) is not null)
            return true;

        // Built-in nested-type special cases (List<T>.Enumerator → CSharpGenericEnumerator<T>)
        if (TypeMappingService.HasBuiltInNestedTypeMapping(typeSymbol))
            return true;

        if (typeSymbol is INamedTypeSymbol namedType && namedType.TypeArguments.Length > 0)
        {
            var ns = namedType.ContainingNamespace?.ToDisplayString();
            var qualifiedGenericName = !string.IsNullOrWhiteSpace(ns) && ns != "<global namespace>"
                ? $"{ns}.{namedType.Name}`{namedType.TypeArguments.Length}"
                : $"{namedType.Name}`{namedType.TypeArguments.Length}";

            if (TypeMappings.HasTypeMapping(qualifiedGenericName))
                return true;
        }

        return false;
    }

    private bool MappedGenericTypeArgumentsHaveConfiguredMappings(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is not INamedTypeSymbol { TypeArguments.Length: > 0 } namedType)
            return false;

        foreach (var typeArgument in namedType.TypeArguments)
        {
            if (HasConfiguredTypeMapping(typeArgument)
                || MappedGenericTypeArgumentsHaveConfiguredMappings(typeArgument))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFrameworkTypeSymbol(ITypeSymbol typeSymbol)
    {
        if (typeSymbol.SpecialType != SpecialType.None)
            return true;

        var ns = typeSymbol.ContainingNamespace?.ToDisplayString();
        return ns == "System" || ns?.StartsWith("System.", StringComparison.Ordinal) == true;
    }

    // ─── Facade methods delegating to TypeMapper for backward compatibility ───
    public void AddImportsForTypePublic(string csharpType) => TypeMapper.AddImportsForTypePublic(csharpType);
    public string MapTypeFromSyntax(TypeSyntax typeSyntax)
    {
        if (typeSyntax == null) return "Object";
        // Strip C# ref modifier (ref T -> T) so semantic resolution targets
        // the inner type; Roslyn returns null for RefTypeSyntax.
        if (typeSyntax is RefTypeSyntax refType)
            typeSyntax = refType.Type;
        // Prefer semantic resolution when available — this correctly handles
        // generic type arguments, primitive boxing, and namespace-qualified lookups.
        if (SemanticModel != null)
        {
            var typeInfo = GetTypeInfo(typeSyntax);
            if (typeInfo.Type != null && typeInfo.Type is not IErrorTypeSymbol)
                return MapType(typeInfo.Type);
        }
        return TypeMapper.MapTypeFromSyntax(typeSyntax);
    }

    public SemanticModel? GetSemanticModelForTree(SyntaxTree syntaxTree)
    {
        if (ProjectCompilation != null)
        {
            if (ProjectCompilation.ContainsSyntaxTree(syntaxTree))
                return ProjectCompilation.GetSemanticModel(syntaxTree);

            // 项目级 tree-local pass（如 JavaPattern 整体替换）会为同一文件生成新的
            // 语法树对象：节点持有的 SyntaxTree 引用可能指向旧对象，而编译已换为新
            // 对象。按文件路径在当前 ProjectCompilation 中找回匹配树。
            // （C26 M4-c：项目路径嵌入式 LINQ 改写 pass 已摘除，不再存在 pre-desugar
            //   旧编译，原 PreDesugarCompilation 回退随之删除。）
            var filePath = syntaxTree.FilePath;
            if (!string.IsNullOrEmpty(filePath))
            {
                foreach (var tree in ProjectCompilation.SyntaxTrees)
                {
                    if (tree.FilePath == filePath)
                        return ProjectCompilation.GetSemanticModel(tree);
                }
            }

            return SemanticModel;
        }
        return SemanticModel;
    }

    public SemanticModel? GetSemanticModelForNode(SyntaxNode node)
    {
        var nodeTree = node.SyntaxTree;
        var model = GetSemanticModelForTree(nodeTree);
        if (model != null && model.SyntaxTree == nodeTree)
            return model;

        if (ProjectCompilation != null)
        {
            foreach (var tree in ProjectCompilation.SyntaxTrees)
            {
                if (tree.FilePath == nodeTree.FilePath)
                {
                    var treeModel = ProjectCompilation.GetSemanticModel(tree);
                    if (treeModel != null)
                        return treeModel;
                }
            }
        }

        return model ?? SemanticModel;
    }

    public SymbolInfo GetSymbolInfo(SyntaxNode node)
    {
        if (SemanticModel == null) return default;

        try
        {
            return SemanticModel.GetSymbolInfo(node);
        }
        catch (ArgumentException)
        {
            return CorrectModelAndGetSymbolInfo(node);
        }
        catch (InvalidOperationException)
        {
            return CorrectModelAndGetSymbolInfo(node);
        }
    }

    private SymbolInfo CorrectModelAndGetSymbolInfo(SyntaxNode node)
    {
        var correctedModel = GetSemanticModelForNode(node);
        if (correctedModel != null)
        {
            try { return correctedModel.GetSymbolInfo(node); }
            catch { return default; }
        }
        return default;
    }

    public TypeInfo GetTypeInfo(SyntaxNode node)
    {
        if (SemanticModel == null) return default;

        try
        {
            return SemanticModel.GetTypeInfo(node);
        }
        catch (ArgumentException)
        {
            return CorrectModelAndGetTypeInfo(node);
        }
        catch (InvalidOperationException)
        {
            return CorrectModelAndGetTypeInfo(node);
        }
    }

    /// <summary>
    /// Evaluates a compile-time constant expression (literal, cast like
    /// (byte)0xFF, unchecked arithmetic, ...) to its folded value. Returns
    /// <see cref="Optional&lt;object?&gt;.None" /> when the expression has no
    /// constant value or no semantic model is available.
    /// </summary>
    public Optional<object?> GetConstantValue(SyntaxNode node)
    {
        if (SemanticModel == null) return default;

        try
        {
            return SemanticModel.GetConstantValue(node);
        }
        catch (ArgumentException)
        {
            return CorrectModelAndGetConstantValue(node);
        }
        catch (InvalidOperationException)
        {
            return CorrectModelAndGetConstantValue(node);
        }
    }

    private Optional<object?> CorrectModelAndGetConstantValue(SyntaxNode node)
    {
        var correctedModel = GetSemanticModelForNode(node);
        if (correctedModel != null)
        {
            try { return correctedModel.GetConstantValue(node); }
            catch { return default; }
        }
        return default;
    }

    private TypeInfo CorrectModelAndGetTypeInfo(SyntaxNode node)
    {
        var correctedModel = GetSemanticModelForNode(node);
        if (correctedModel != null)
        {
            try { return correctedModel.GetTypeInfo(node); }
            catch { return default; }
        }
        return default;
    }

    public ISymbol? GetDeclaredSymbol(SyntaxNode node)
    {
        if (SemanticModel == null) return null;

        try
        {
            return SemanticModel.GetDeclaredSymbol(node);
        }
        catch (ArgumentException)
        {
            return CorrectModelAndGetDeclaredSymbol(node);
        }
        catch (InvalidOperationException)
        {
            return CorrectModelAndGetDeclaredSymbol(node);
        }
    }

    private ISymbol? CorrectModelAndGetDeclaredSymbol(SyntaxNode node)
    {
        var correctedModel = GetSemanticModelForNode(node);
        if (correctedModel != null)
        {
            try { return correctedModel.GetDeclaredSymbol(node); }
            catch { return null; }
        }
        return null;
    }

    public bool TryGetSymbolInfo(ExpressionSyntax node, out ISymbol? symbol)
    {
        symbol = null;
        if (SemanticModel == null) return false;

        try
        {
            symbol = SemanticModel.GetSymbolInfo(node).Symbol;
            return true;
        }
        catch (ArgumentException)
        {
            return TryCorrectModelAndGetSymbolInfo(node, ref symbol);
        }
        catch (InvalidOperationException)
        {
            return TryCorrectModelAndGetSymbolInfo(node, ref symbol);
        }
    }

    public bool TryGetSymbolInfo(SyntaxNode node, out ISymbol? symbol)
    {
        symbol = null;
        if (SemanticModel == null) return false;

        try
        {
            symbol = SemanticModel.GetSymbolInfo(node).Symbol;
            return true;
        }
        catch (ArgumentException)
        {
            return TryCorrectModelAndGetSymbolInfo(node, ref symbol);
        }
        catch (InvalidOperationException)
        {
            return TryCorrectModelAndGetSymbolInfo(node, ref symbol);
        }
    }

    private bool TryCorrectModelAndGetSymbolInfo(SyntaxNode node, ref ISymbol? symbol)
    {
        var correctedModel = GetSemanticModelForNode(node);
        if (correctedModel != null && correctedModel != SemanticModel)
        {
            try
            {
                symbol = correctedModel.GetSymbolInfo(node).Symbol;
                return true;
            }
            catch { return false; }
        }
        return false;
    }

    public UsingAliasRegistry AliasRegistry { get; } = new();

    // ─── Using directive tracking ───

    private HashSet<string>? _cachedUsingNamespaces;

    /// <summary>
    /// Checks whether the source file contains a "using {namespace};" directive.
    /// Used to disambiguate types that cannot be resolved by Roslyn (e.g. Xunit.Assert
    /// when the xunit assembly is not referenced by the conversion pipeline).
    /// </summary>
    public bool HasUsingDirective(string namespaceName)
    {
        _cachedUsingNamespaces ??= BuildUsingNamespaceSet();
        return _cachedUsingNamespaces.Contains(namespaceName);
    }

    private HashSet<string> BuildUsingNamespaceSet()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (SemanticModel?.SyntaxTree?.GetRoot() is { } root)
        {
            foreach (var usingDir in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                if (usingDir.Name != null && usingDir.Alias == null)
                {
                    result.Add(usingDir.Name.ToString());
                }
            }
        }
        return result;
    }

    // ─── Facade properties/methods delegating to AliasRegistry ───
    public IReadOnlyDictionary<string, UsingAliasRegistry.UsingAliasInfo> UsingAliases => AliasRegistry.Aliases;

    /// <summary>
    /// Records a file-scoped using alias whose target type could not be bound
    /// (e.g. missing framework reference). Syntax fallback uses this to honor
    /// alias precedence over same-named project types.
    /// </summary>
    public void RegisterUnresolvedUsingAlias(string aliasName) => AliasRegistry.RegisterUnresolved(aliasName);

    public bool RegisterUsingAlias(string aliasName, ITypeSymbol targetType, Location? location)
    {
        var typeCacheView = new Dictionary<ITypeSymbol, string>(SymbolEqualityComparer.Default);
        foreach (var kvp in TypeCache)
            typeCacheView[kvp.Key] = kvp.Value.JavaType;

        var registered = AliasRegistry.Register(
            aliasName,
            targetType,
            location,
            Diagnostics,
            CurrentNamespace,
            GlobalNamespace,
            typeCacheView);
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
    public static string GetJsonTypeOverloadSuffix(IMethodSymbol? method, string methodName) => JavaNaming.GetJsonTypeOverloadSuffix(method, methodName);
}
