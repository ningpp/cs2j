using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Analysis;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// R8：extern 互操作边界（设计 D8，§4 组件表）。
/// <list type="bullet">
/// <item>extern-like 方法（<see cref="InteropBoundaryFacts.IsExternLikeMethod"/>：
/// extern 修饰符或 [DllImport]/[LibraryImport]）：未加载白名单或未命中 →
/// UF060 Error（消息含「平台隔离通道（C42）」指引）；命中白名单但封送
/// 布局不可证明 → UF061；命中且可证明 → 归一为受控 interop facade：
/// 删除 interop 特性与 extern/partial 修饰符，加
/// [global::Cs2j.Generation.InteropFacade(library, entryPoint)] 标记，
/// 方法体为 throw PlatformNotSupportedException 的响亮 C# stub（Java 半区
/// FFM 发射通道消费标记并替换方法体），并在同一编译单元注入一次支撑
/// 标记类型；</item>
/// <item>方法体内/站点对 Marshal.* / NativeLibrary.* 的调用与成员访问、
/// SafeHandle（含派生）/HandleRef 类型的使用（字段/局部/参数/构造站点）
/// → UF070 Error，指引与 CS2J3202 相同隔离去向。</item>
/// </list>
/// 判定全部基于 SemanticModel/ISymbol；非 facade 路径只诊断不改写
/// （拒绝即整文回滚）。
/// </summary>
internal sealed class ExternInteropRewriter : CSharpSyntaxRewriter
{
    /// <summary>facade 支撑标记类型的元数据名（与 InteropFacadeMarker 契约一致）。</summary>
    public const string SupportAttributeMetadataName = "Cs2j.Generation.InteropFacadeAttribute";

    private const string SupportSource = """
        namespace Cs2j.Generation
        {
            [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = false)]
            internal sealed class InteropFacadeAttribute : global::System.Attribute
            {
                public InteropFacadeAttribute(string library, string entryPoint)
                {
                    Library = library;
                    EntryPoint = entryPoint;
                }

                // 必须以「字段」而非属性暴露：该支撑类型随中间树参与后续 C# 预处理，
                // 属性会被 member-desugar 纳入计划，并把特性命名参数
                // (StringEncoding = "utf16") 左侧标识符误改写为 get_ 调用而崩树；
                // 特性命名实参本就允许绑定可写公共字段。
                public readonly string Library;
                public readonly string EntryPoint;
                public string StringEncoding = "utf8";
            }
        }
        """;

    private readonly SemanticModel _model;
    private readonly UnsafeFfmStatistics _statistics;
    private readonly OriginalSyntaxResolver _resolver;
    private readonly ExternWhitelist? _whitelist;

    /// <summary>
    /// 跨文件共享：已注入支撑标记类型的程序集集合（preprocessor 持有，
    /// 同一程序集只注入一次）。
    /// </summary>
    private readonly HashSet<string> _supportEmittedAssemblies;

    private readonly HashSet<string> _reported = new();

    /// <summary>当前编译单元待替换的 extern 方法（当前树节点 → 白名单条目/符号）。</summary>
    private readonly Dictionary<MethodDeclarationSyntax, (ExternWhitelistEntry Entry, IMethodSymbol Symbol)>
        _facadePlans = new();

    private bool _needSupportInThisUnit;
    private bool _addedSupportKeyInThisUnit;
    private string? _supportAssemblyKey;

    public ExternInteropRewriter(
        SemanticModel model,
        UnsafeFfmStatistics statistics,
        OriginalSyntaxResolver resolver,
        ExternWhitelist? whitelist,
        HashSet<string> supportEmittedAssemblies)
    {
        _model = model;
        _statistics = statistics;
        _resolver = resolver;
        _whitelist = whitelist;
        _supportEmittedAssemblies = supportEmittedAssemblies;
    }

    public List<UnsafeFfmDiagnostic> Diagnostics { get; } = new();

    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == UnsafeFfmSeverity.Error);

    /// <summary>本单元计划归一的 facade 数（回滚时据此扣减统计）。</summary>
    public int FacadePlansCount => _facadePlans.Count;

    /// <summary>
    /// 文件最终回滚时调用：若支撑标记类型的程序集占位由本单元登记，
    /// 释放占位，保证后续成功文件仍会注入支撑类型。
    /// </summary>
    public void RollbackSupportEmission()
    {
        if (!_addedSupportKeyInThisUnit || _supportAssemblyKey == null)
        {
            return;
        }

        _supportEmittedAssemblies.Remove(_supportAssemblyKey);
        _addedSupportKeyInThisUnit = false;
    }

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        var visited = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;
        if (!_needSupportInThisUnit)
        {
            return visited;
        }

        // 幂等：支撑类型已存在于原始编译（二次跑/早前文件已注入）则不重复注入。
        if (_model.Compilation.GetTypeByMetadataName(SupportAttributeMetadataName) != null)
        {
            return visited;
        }

        var assemblyName = _model.Compilation.AssemblyName ?? string.Empty;
        if (!_supportEmittedAssemblies.Add(assemblyName))
        {
            return visited;
        }

        _addedSupportKeyInThisUnit = true;
        _supportAssemblyKey = assemblyName;
        var support = (NamespaceDeclarationSyntax)ParseCompilationUnit(SupportSource).Members[0];

        // 同一源文件不允许混用文件级与块级 namespace（CS8955）：宿主单元为
        // 文件级 namespace 时，先机械翻转为块级（成员/using/trivia 原样保留），
        // 再在根挂载支撑类型。
        var fileScoped = visited.Members
            .OfType<FileScopedNamespaceDeclarationSyntax>()
            .FirstOrDefault();
        if (fileScoped is null)
        {
            return visited.AddMembers(support);
        }

        var blockNamespace = NamespaceDeclaration(fileScoped.Name)
            .WithAttributeLists(List(fileScoped.AttributeLists))
            .WithModifiers(TokenList(fileScoped.Modifiers))
            .WithExterns(List(fileScoped.Externs))
            .WithUsings(List(fileScoped.Usings))
            .WithMembers(List(fileScoped.Members))
            .WithLeadingTrivia(fileScoped.GetLeadingTrivia())
            .WithTrailingTrivia(fileScoped.GetTrailingTrivia());

        var withoutFileScoped = visited.RemoveNode(fileScoped, SyntaxRemoveOptions.KeepNoTrivia)!;
        return withoutFileScoped.AddMembers(blockNamespace, support);
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        ScanType(node);
        return base.VisitClassDeclaration(node);
    }

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        ScanType(node);
        return base.VisitStructDeclaration(node);
    }

    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        ScanType(node);
        return base.VisitInterfaceDeclaration(node);
    }

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        ScanType(node);
        return base.VisitRecordDeclaration(node);
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (_facadePlans.TryGetValue(node, out var plan))
        {
            return BuildFacadeMethod(node, plan.Symbol, plan.Entry);
        }

        return base.VisitMethodDeclaration(node);
    }

    /// <summary>
    /// 扫描类型<b>直接成员</b>（嵌套类型自身被 Visit 单独扫描，避免重复）：
    /// extern-like 方法 → 白名单归一 / UF060 / UF061；成员内互操作
    /// API/类型站点 → UF070。
    /// </summary>
    private void ScanType(TypeDeclarationSyntax node)
    {
        foreach (var member in node.Members)
        {
            if (member is TypeDeclarationSyntax) continue;

            var originalMember = _resolver.Resolve(member);
            if (originalMember == null)
            {
                // 无法映射回原始模型树时（极端插入形态）只做纯语法 extern
                // 判定，绝不把重建节点传给 SemanticModel（否则抛异常）。
                // 白名单匹配依赖符号，此类节点无法归一，维持 UF060。
                if (member is MethodDeclarationSyntax unresolvedMethod
                    && unresolvedMethod.Modifiers.Any(SyntaxKind.ExternKeyword))
                {
                    AddError(
                        UnsafeFfmDiagnosticCode.UF060,
                        $"extern-like method '{unresolvedMethod.Identifier.ValueText}' (extern modifier) " +
                        "is not on an FFM whitelist; all extern interop is rejected by default. Port it " +
                        "through the 平台隔离通道（C42） or the opt-in FFM whitelist " +
                        "facade; no silent stubs are emitted.",
                        unresolvedMethod.Identifier);
                }

                continue;
            }

            if (member is MethodDeclarationSyntax method
                && originalMember is MethodDeclarationSyntax originalMethod
                && InteropBoundaryFacts.IsExternLikeMethod(_model, originalMethod))
            {
                ClassifyExternMethod(method, originalMethod);
            }

            ScanMemberSites(originalMember);
        }
    }

    /// <summary>
    /// extern-like 方法三分类：白名单命中且布局可证明 → 登记 facade 替换
    /// 计划；命中但不可证明 → UF061；未命中（或未加载白名单）→ UF060。
    /// </summary>
    private void ClassifyExternMethod(
        MethodDeclarationSyntax currentMethod,
        MethodDeclarationSyntax originalMethod)
    {
        var symbol = _model.GetDeclaredSymbol(originalMethod);
        if (symbol == null)
        {
            AddUf060(currentMethod.Identifier.ValueText, originalMethod.Identifier);
            return;
        }

        var entry = _whitelist?.Find(symbol.ContainingType.ToDisplayString(), symbol.Name);
        if (entry == null)
        {
            AddUf060(symbol.Name, originalMethod.Identifier);
            return;
        }

        var reason = ValidateFacadeShape(symbol);
        if (reason != null)
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF061,
                $"extern-like method '{symbol.Name}' matches an FFM whitelist entry " +
                $"('{entry.Library}!{entry.ResolveEntryPoint(symbol.Name)}') but its marshalling " +
                $"layout is not provable: {reason}. Change the signature to the v1 ABI subset " +
                "(primitive/string parameters, primitive/void return) or isolate it behind " +
                "the platform isolation channel (C42).",
                originalMethod.Identifier);
            return;
        }

        _facadePlans.Add(currentMethod, (entry, symbol));
        _needSupportInThisUnit = true;
        _statistics.FfmFacadesEmitted++;
    }

    private void AddUf060(string methodName, SyntaxToken anchor)
    {
        AddError(
            UnsafeFfmDiagnosticCode.UF060,
            $"extern-like method '{methodName}' (extern modifier or " +
            "[DllImport]/[LibraryImport] attribute) is not on an FFM whitelist; all extern " +
            "interop is rejected by default. Port it through the 平台隔离通道（C42） " +
            "or the opt-in FFM whitelist facade; no silent stubs are emitted.",
            anchor);
    }

    /// <summary>
    /// v1 ABI 子集可证明性检查；返回 null=可归一，否则给出拒绝原因。
    /// </summary>
    private static string? ValidateFacadeShape(IMethodSymbol method)
    {
        if (!method.IsStatic)
            return "interop entry points must be static";
        if (method.IsGenericMethod)
            return "generic interop methods are not supported";
        if (method.IsVararg)
            return "__arglist (varargs) interop methods are not supported in v1";

        var returnType = method.ReturnType;
        if (returnType.SpecialType != SpecialType.System_Void
            && !IsAbiPrimitive(returnType))
        {
            return $"return type '{returnType.ToDisplayString()}' is not a provable ABI primitive " +
                   "(string write-back is not provable; struct/enum returns are not supported in v1)";
        }

        foreach (var parameter in method.Parameters)
        {
            if (parameter.RefKind != RefKind.None)
                return $"parameter '{parameter.Name}' is ref/out/in; managed references do not cross the ABI in v1";
            if (parameter.IsParams)
                return $"parameter '{parameter.Name}' is a params array; arrays do not cross the ABI in v1";

            var type = parameter.Type;
            if (type is IPointerTypeSymbol or IFunctionPointerTypeSymbol)
                return $"parameter '{parameter.Name}' is a pointer; pointer ABI is not supported in v1";
            if (type.TypeKind == TypeKind.Pointer)
                return $"parameter '{parameter.Name}' is a function pointer";
            if (type.SpecialType == SpecialType.System_Boolean)
                return $"parameter '{parameter.Name}' is bool: native boolean width is ambiguous " +
                       "(Win32 BOOL is 4 bytes; ValueLayout.JAVA_BOOLEAN is 1 byte); declare it as int or byte explicitly";
            if (!IsAbiPrimitive(type) && type.SpecialType != SpecialType.System_String)
                return $"parameter '{parameter.Name}' of type '{type.ToDisplayString()}' is not a provable " +
                       "ABI type in v1 (only primitives and read-only strings are allowed; enums/structs/classes are rejected)";
        }

        return null;
    }

    private static bool IsAbiPrimitive(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_Byte or SpecialType.System_SByte
            or SpecialType.System_Int16 or SpecialType.System_UInt16
            or SpecialType.System_Char
            or SpecialType.System_Int32 or SpecialType.System_UInt32
            or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_Single or SpecialType.System_Double => true,
        _ => false,
    };

    /// <summary>
    /// 构造受控 facade 方法：剥除 interop 特性与 extern/partial 修饰符，
    /// 加 InteropFacade 标记与响亮 stub 表达式体。格式交由 pass 末端
    /// Formatter 统一规整。
    /// </summary>
    private MethodDeclarationSyntax BuildFacadeMethod(
        MethodDeclarationSyntax current,
        IMethodSymbol symbol,
        ExternWhitelistEntry entry)
    {
        var retainedAttributes = current.AttributeLists
            .Select(list => AttributeList(
                list.Target,
                SeparatedList(list.Attributes.Where(a => !IsInteropAttribute(a)))))
            .Where(list => list.Attributes.Count > 0)
            .ToArray();

        var markerArguments = new List<AttributeArgumentSyntax>
        {
            AttributeArgument(LiteralExpression(
                SyntaxKind.StringLiteralExpression, Literal(entry.Library))),
            AttributeArgument(LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                Literal(entry.ResolveEntryPoint(symbol.Name)))),
        };
        if (entry.StringEncoding == ExternStringEncoding.Utf16)
        {
            markerArguments.Add(AttributeArgument(
                NameEquals(IdentifierName("StringEncoding")),
                nameColon: null,
                expression: LiteralExpression(
                    SyntaxKind.StringLiteralExpression, Literal("utf16"))));
        }

        var markerList = AttributeList(SingletonSeparatedList(
            Attribute(
                ParseName("global::Cs2j.Generation.InteropFacade"),
                AttributeArgumentList(SeparatedList(markerArguments)))));

        var allAttributeLists = List(new[] { markerList }.Concat(retainedAttributes));

        var modifiers = TokenList(current.Modifiers
            .Where(token => !token.IsKind(SyntaxKind.ExternKeyword)
                            && !token.IsKind(SyntaxKind.PartialKeyword))
            .Select(token => token.WithoutTrivia()));

        var message =
            $"cs2j FFM facade '{symbol.Name}': the native downcall " +
            $"('{entry.Library}!{entry.ResolveEntryPoint(symbol.Name)}') is emitted only on the " +
            "Java target; this C# intermediate stub is never meant to execute.";
        var stub = ThrowExpression(
            ObjectCreationExpression(ParseName("global::System.PlatformNotSupportedException"))
                .WithArgumentList(
                    ArgumentList(SingletonSeparatedList(
                        Argument(LiteralExpression(
                            SyntaxKind.StringLiteralExpression, Literal(message)))))));

        return current
            .WithAttributeLists(allAttributeLists)
            .WithModifiers(modifiers)
            .WithBody(null)
            .WithExpressionBody(ArrowExpressionClause(stub))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
    }

    /// <summary>
    /// 当前树上的特性是否为 DllImport/LibraryImport（经 resolver 映射回
    /// 原始符号判定；无法映射的保留不误删）。
    /// </summary>
    private bool IsInteropAttribute(AttributeSyntax attribute)
    {
        var original = _resolver.Resolve(attribute);
        if (original == null) return false;
        var name = (_model.GetSymbolInfo(original).Symbol as IMethodSymbol)?
            .ContainingType.ToDisplayString();
        return name is "System.Runtime.InteropServices.DllImportAttribute"
            or "System.Runtime.InteropServices.LibraryImportAttribute";
    }

    /// <summary>扫描单个成员范围内的 Marshal/NativeLibrary/SafeHandle/HandleRef 站点。</summary>
    private void ScanMemberSites(SyntaxNode member)
    {
        foreach (var invocation in member.DescendantNodesAndSelf()
                     .OfType<InvocationExpressionSyntax>())
        {
            var symbol = _model.GetSymbolInfo(invocation).Symbol;
            if (TryGetInteropContainer(symbol, out var container))
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF070,
                    $"use of '{container}.{symbol!.Name}' is rejected: System.Runtime.InteropServices " +
                    "Marshal/NativeLibrary APIs have no Java equivalent. Isolate the call behind the " +
                    "platform isolation channel (C42) (same destination as CS2J3202).",
                    invocation);
            }
        }

        // 方法组/属性形态（非调用）：父节点是 invocation 时由调用分支覆盖，不重复报。
        foreach (var memberAccess in member.DescendantNodesAndSelf()
                     .OfType<MemberAccessExpressionSyntax>()
                     .Where(m => m.Parent is not InvocationExpressionSyntax))
        {
            var symbol = _model.GetSymbolInfo(memberAccess).Symbol;
            if (TryGetInteropContainer(symbol, out var container))
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF070,
                    $"use of '{container}.{symbol!.Name}' is rejected; isolate it behind the platform " +
                    "isolation channel (C42) (same destination as CS2J3202).",
                    memberAccess);
            }
        }

        foreach (var creation in member.DescendantNodesAndSelf()
                     .OfType<ObjectCreationExpressionSyntax>())
        {
            var type = _model.GetTypeInfo(creation).Type;
            if (IsSafeHandleOrHandleRef(type))
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF070,
                    $"construction of '{type!.ToDisplayString()}' is rejected: SafeHandle/HandleRef are " +
                    "runtime interop ownership types with no Java equivalent. Isolate the site behind " +
                    "the platform isolation channel (C42).",
                    creation);
            }
        }

        // 类型站点（字段/局部/参数/返回）：对象创建表达式内的 Type 由创建分支覆盖。
        // 限定名/可空类型的内部名也是 TypeSyntax，只扫最外层节点；表达式
        // 上下文（参数/成员访问右侧/二元运算等）中的 NameSyntax 不属类型站点，
        // 避免同一符号在使用表达式处被重复计数。
        foreach (var typeSyntax in member.DescendantNodesAndSelf().OfType<TypeSyntax>())
        {
            if (typeSyntax.Parent is TypeSyntax) continue;
            if (IsExpressionContext(typeSyntax.Parent)) continue;
            if (IsInsideObjectCreationType(typeSyntax)) continue;
            var type = _model.GetTypeInfo(typeSyntax).Type;
            if (IsSafeHandleOrHandleRef(type))
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF070,
                    $"use of interop ownership type '{type!.ToDisplayString()}' is rejected: " +
                    "SafeHandle/HandleRef encode runtime interop lifetime. Isolate the site behind " +
                    "the platform isolation channel (C42) (same destination as CS2J3202).",
                    typeSyntax);
            }
        }
    }

    private static bool TryGetInteropContainer(ISymbol? symbol, out string container)
    {
        container = string.Empty;
        var name = symbol?.ContainingType?.ToDisplayString();
        if (name == "System.Runtime.InteropServices.Marshal")
        {
            container = "Marshal";
            return true;
        }

        if (name == "System.Runtime.InteropServices.NativeLibrary")
        {
            container = "NativeLibrary";
            return true;
        }

        return false;
    }

    /// <summary>SafeHandle（含派生类型链）或 HandleRef（精确）。</summary>
    private static bool IsSafeHandleOrHandleRef(ITypeSymbol? type)
    {
        if (type == null) return false;
        if (type.OriginalDefinition.ToDisplayString() == "System.Runtime.InteropServices.HandleRef")
        {
            return true;
        }

        var current = type;
        while (current != null)
        {
            if (current.OriginalDefinition.ToDisplayString() == "System.Runtime.InteropServices.SafeHandle")
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }

    /// <summary>
    /// 父节点是否把该 TypeSyntax 当作表达式使用（而非类型站点）。
    /// 默认未知上下文按类型站点处理（拒绝优先，宁多报不漏报）。
    /// </summary>
    private static bool IsExpressionContext(SyntaxNode? parent) => parent switch
    {
        ArgumentSyntax => true,
        AttributeArgumentSyntax => true,
        MemberAccessExpressionSyntax => true,
        MemberBindingExpressionSyntax => true,
        InvocationExpressionSyntax => true,
        BinaryExpressionSyntax => true,
        AssignmentExpressionSyntax => true,
        AwaitExpressionSyntax => true,
        ReturnStatementSyntax => true,
        ThrowExpressionSyntax => true,
        ThrowStatementSyntax => true,
        ArrowExpressionClauseSyntax => true,
        ExpressionStatementSyntax => true,
        ConditionalExpressionSyntax => true,
        ConditionalAccessExpressionSyntax => true,
        PostfixUnaryExpressionSyntax => true,
        PrefixUnaryExpressionSyntax => true,
        InterpolationSyntax => true,
        InitializerExpressionSyntax => true,
        LockStatementSyntax => true,
        YieldStatementSyntax => true,
        ElementAccessExpressionSyntax => true,
        ParenthesizedExpressionSyntax => true,
        EqualsValueClauseSyntax => true,
        SwitchExpressionSyntax => true,
        SwitchExpressionArmSyntax => true,
        WhileStatementSyntax => true,
        DoStatementSyntax => true,
        IfStatementSyntax => true,
        ForStatementSyntax => true,
        RefExpressionSyntax => true,
        TupleExpressionSyntax => true,
        ImplicitElementAccessSyntax => true,
        _ => false,
    };

    private static bool IsInsideObjectCreationType(TypeSyntax typeSyntax)
    {
        var creation = typeSyntax.Ancestors().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault();
        return creation != null && creation.Type.DescendantNodesAndSelf().Contains(typeSyntax);
    }

    private void AddError(UnsafeFfmDiagnosticCode code, string message, SyntaxNode node)
        => AddError(code, message, node.GetLocation());

    private void AddError(
        UnsafeFfmDiagnosticCode code, string message, SyntaxToken token)
        => AddError(code, message, token.GetLocation());

    private void AddError(
        UnsafeFfmDiagnosticCode code, string message, Location location)
    {
        var line = 1;
        var character = 1;
        try
        {
            var span = location.GetLineSpan();
            line = span.StartLinePosition.Line + 1;
            character = span.StartLinePosition.Character + 1;
        }
        catch
        {
            // 行号不可用时退回 (1,1)。
        }

        // 同位置同码去重（不同成员范围可能扫到嵌套节点）。
        if (!_reported.Add($"{code}:{line}:{character}")) return;

        Diagnostics.Add(new UnsafeFfmDiagnostic(
            UnsafeFfmSeverity.Error, code, message, null, line, character));
        _statistics.Rejections++;
        switch (code)
        {
            case UnsafeFfmDiagnosticCode.UF060:
                _statistics.Uf060Rejections++;
                break;
            case UnsafeFfmDiagnosticCode.UF061:
                _statistics.Uf061Rejections++;
                break;
            case UnsafeFfmDiagnosticCode.UF070:
                _statistics.Uf070Rejections++;
                break;
        }
    }
}
