using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Signatures;

/// <summary>调用点五分类（设计决策 4）：以 SemanticModel 绑定的符号/接收者静态类型为准，零文本推测。</summary>
internal enum SignatureCallSiteClass
{
    /// <summary>(a)(b)(c) 接收者静态类型为接口/接口约束/强转接口 → 绑接口槽，改写时不动。</summary>
    InterfaceDispatch,

    /// <summary>(d) 绑定到让位/隐藏的类成员符号 → 改名。</summary>
    DisplacedMember,

    /// <summary>(e) <c>base.</c>/类型内 <c>this.</c> 直连 → 按绑定符号归 (d) 或不动。</summary>
    BaseOrThis,

    /// <summary>表达式树内（force 不解除）或 dynamic 接收者 → 不可判定（CS2J5194）。</summary>
    Undecidable,
}

/// <summary>
/// C46 语法/语义判定工具（决策 4/2/15；M1 建能力，M2+ 消费）：调用点五分类、
/// nameof/反射/表达式树/dynamic 判定（复用共享 <see cref="MemberDesugarSyntaxFacts"/>）、
/// 体等价比较骨架（SI-3 同源判定）、强转最小化。
/// </summary>
internal static class SignatureSyntaxFacts
{
    /// <summary>
    /// 绑定符号（<c>Symbol</c> 优先，否则唯一 <c>CandidateSymbols</c> 兜底）。
    /// Roslyn 对 <c>nameof(M)</c> 的方法组实参常把符号放入候选而非 <c>Symbol</c>；
    /// 一律按绑定结果判定，禁止文本匹配。
    /// </summary>
    public static ISymbol? BoundSymbol(SemanticModel model, SyntaxNode node)
    {
        var info = model.GetSymbolInfo(node);
        return info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
    }

    /// <summary>调用点五分类（决策 4）。</summary>
    public static SignatureCallSiteClass ClassifyCallSite(ExpressionSyntax node, SemanticModel model)
    {
        if (MemberDesugarSyntaxFacts.IsInsideExpressionTree(node, model)
            || MemberDesugarSyntaxFacts.IsDynamic(node, model))
            return SignatureCallSiteClass.Undecidable;

        if (node is MemberAccessExpressionSyntax ma)
        {
            if (ma.Expression is BaseExpressionSyntax)
                return SignatureCallSiteClass.BaseOrThis;
            if (model.GetTypeInfo(ma.Expression).Type is { } recvType)
                return ClassifyReceiver(recvType);
            return SignatureCallSiteClass.DisplacedMember;
        }

        // 类型内未限定的 this 成员引用：绑定符号归 DisplacedMember（非接口）。
        if (node is IdentifierNameSyntax)
            return SignatureCallSiteClass.BaseOrThis;

        return SignatureCallSiteClass.DisplacedMember;
    }

    private static SignatureCallSiteClass ClassifyReceiver(ITypeSymbol recvType)
    {
        if (recvType.TypeKind == TypeKind.Interface)
            return SignatureCallSiteClass.InterfaceDispatch;
        if (recvType is ITypeParameterSymbol tp
            && tp.ConstraintTypes.Any(c => c.TypeKind == TypeKind.Interface))
            return SignatureCallSiteClass.InterfaceDispatch;
        return SignatureCallSiteClass.DisplacedMember;
    }

    /// <summary>表达式树包含判定（force 不解除，决策 15）。</summary>
    public static bool IsInsideExpressionTree(ExpressionSyntax node, SemanticModel model)
        => MemberDesugarSyntaxFacts.IsInsideExpressionTree(node, model);

    /// <summary>dynamic 接收者判定（force 不解除，CS2J5194）。</summary>
    public static bool IsDynamic(ExpressionSyntax node, SemanticModel model)
        => MemberDesugarSyntaxFacts.IsDynamic(node, model);

    /// <summary>nameof 站点判定（决策 15：字面量化）。Roslyn 将 <c>nameof(x)</c> 建模为
    /// 以 <c>nameof</c> 标识符为 callee 的 invocation。</summary>
    public static bool IsNameofSite(ExpressionSyntax node)
        => node is InvocationExpressionSyntax
        {
            Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" },
        };

    /// <summary>反射/字符串契约风险站点判定（决策 15 / CS2J5193）：Type.GetMethod/GetMember/
    /// GetProperty/GetField/GetInterfaceMap 与 Delegate.CreateDelegate。</summary>
    public static bool IsReflectionContractSite(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method) return false;
        var container = method.ContainingType;
        if (container == null) return false;
        var ns = container.ContainingNamespace?.ToDisplayString();
        if (ns != "System") return false;
        return container.Name switch
        {
            "Type" => method.Name is "GetMethod" or "GetMember" or "GetProperty"
                or "GetField" or "GetEvent" or "GetInterfaceMap" or "GetMethods",
            "Delegate" => method.Name == "CreateDelegate",
            _ => false,
        };
    }

    /// <summary>
    /// 体等价比较骨架（决策 2 SI-3 同源判定）。保守口径：同一符号，或体文本（去空白）相同，
    /// 或二者皆纯转发同一 base 成员 → 等价；否则视为分歧（CS2J5191）。
    /// </summary>
    public static bool AreBodiesEquivalent(ISymbol a, ISymbol b)
    {
        if (SymbolEqualityComparer.Default.Equals(a, b)) return true;

        var fa = ForwardTargetName(a);
        var fb = ForwardTargetName(b);
        if (fa != null && fb != null) return string.Equals(fa, fb, StringComparison.Ordinal);

        var ta = BodyText(a);
        var tb = BodyText(b);
        return ta != null && tb != null && Normalize(ta) == Normalize(tb);
    }

    /// <summary>强转最小化（决策 4c）：内表达式已为目标类型时去掉冗余强转。</summary>
    public static ExpressionSyntax MinimizeCast(CastExpressionSyntax cast, SemanticModel model)
    {
        var innerType = model.GetTypeInfo(cast.Expression).Type;
        var castType = model.GetTypeInfo(cast.Type).Type;
        return castType != null && SymbolEqualityComparer.Default.Equals(innerType, castType)
            ? cast.Expression
            : cast;
    }

    // ---- 内部 ----

    private static string? BodyText(ISymbol symbol)
    {
        var syntax = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        return syntax switch
        {
            MethodDeclarationSyntax m => m.Body?.ToFullString() ?? m.ExpressionBody?.ToFullString(),
            // C11–C13 方法化前，属性/索引器/事件的显式实现是访问器符号：体在 AccessorDeclarationSyntax。
            AccessorDeclarationSyntax a => a.Body?.ToFullString() ?? a.ExpressionBody?.ToFullString(),
            PropertyDeclarationSyntax p => p.AccessorList?.ToFullString()
                ?? p.ExpressionBody?.ToFullString(),
            IndexerDeclarationSyntax i => i.AccessorList?.ToFullString()
                ?? i.ExpressionBody?.ToFullString(),
            EventDeclarationSyntax e => e.AccessorList?.ToFullString(),
            _ => null,
        };
    }

    /// <summary>纯转发目标名（单语句 <c>base.X(...)</c>/<c>return base.X</c>/<c>=> base.X</c>），否则 null。</summary>
    private static string? ForwardTargetName(ISymbol symbol)
    {
        var syntax = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        ExpressionSyntax? body = syntax switch
        {
            MethodDeclarationSyntax { Body.Statements.Count: 1 } m
                => (m.Body!.Statements[0] as ReturnStatementSyntax)?.Expression
                   ?? (m.Body.Statements[0] as ExpressionStatementSyntax)?.Expression,
            MethodDeclarationSyntax m => m.ExpressionBody?.Expression,
            AccessorDeclarationSyntax { Body.Statements.Count: 1 } a
                => (a.Body!.Statements[0] as ReturnStatementSyntax)?.Expression
                   ?? (a.Body.Statements[0] as ExpressionStatementSyntax)?.Expression,
            AccessorDeclarationSyntax a => a.ExpressionBody?.Expression,
            PropertyDeclarationSyntax { ExpressionBody: { } eb } => eb.Expression,
            _ => null,
        };
        return GetBaseMemberAccessName(body);
    }

    private static string? GetBaseMemberAccessName(ExpressionSyntax? expr)
    {
        var target = expr switch
        {
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax inner } => inner,
            MemberAccessExpressionSyntax ma => ma,
            _ => null,
        };
        return target is { Expression: BaseExpressionSyntax } ? target.Name.Identifier.ValueText : null;
    }

    private static string Normalize(string text)
        => new(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
}