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

    // ---- M4c：访问器面跨文件站点的「接收者静态类型锚定」回退 ----
    //
    // 背景（设计 §4.3-3/风险 3）：签名分相晚于 C11–C13 方法化。使用点所在文件在本轮被
    // C11–C13 方法化（d.Name → d.get_Name()）后重解析进编译，但编译中其余文件（声明文件）
    // 仍是原始形态（属性 Name 而非 get_Name 方法），故 d.get_Name() 无法绑定符号
    // （GetSymbolInfo 返回 null）。此时以「接收者静态类型 + 访问器原始名 + 参数个数」查
    // plan 派生的回退表即可恢复改名。该回退严格仅在符号绑定失败时启用，符号绑定成功一律优先。

    /// <summary>访问器方法名前缀（C11–C13 方法化产物：property get_/set_、indexer get_/set_、event add_/remove_）。</summary>
    internal static bool IsAccessorMethodName(string name)
        => name.StartsWith("get_", StringComparison.Ordinal)
           || name.StartsWith("set_", StringComparison.Ordinal)
           || name.StartsWith("add_", StringComparison.Ordinal)
           || name.StartsWith("remove_", StringComparison.Ordinal);

    /// <summary>解析稳定键 <c>{类型显示名}::{元数据名}({参数类型,…})</c> 为
    /// (类型显示名, 成员名, 参数个数)（非方法键——无参括号——参数个数为 -1）。
    /// 仅解析 plan 自带的结构化键，不做源码文本推测。</summary>
    internal static bool TryParseSiteKey(
        string key, out string typeDisplay, out string memberName, out int arity)
    {
        typeDisplay = string.Empty;
        memberName = string.Empty;
        arity = -1;
        var sep = key.IndexOf("::", StringComparison.Ordinal);
        if (sep < 0) return false;
        typeDisplay = key.Substring(0, sep);
        var rest = key.Substring(sep + 2);
        var open = rest.IndexOf('(');
        if (open < 0)
        {
            memberName = rest;
            return true;
        }
        var close = rest.LastIndexOf(')');
        if (close < open) return false;
        memberName = rest.Substring(0, open);
        arity = CountTopLevelArguments(rest.Substring(open + 1, close - open - 1));
        return true;
    }

    private static int CountTopLevelArguments(string parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters)) return 0;
        var depth = 0;
        var count = 1;
        foreach (var ch in parameters)
        {
            switch (ch)
            {
                case '<' or '(': depth++; break;
                case '>' or ')': depth--; break;
                case ',' when depth == 0: count++; break;
            }
        }
        return count;
    }

    /// <summary>把一条 (含入类型显示名, 访问器原始名, 参数个数) → 新名 的回退项并入表；
    /// 非访问器名或非方法键跳过。</summary>
    internal static void AddAccessorRename(
        Dictionary<(string Type, string Name, int Arity), string> map, string siteKey, string newName)
    {
        if (!TryParseSiteKey(siteKey, out var type, out var name, out var arity)) return;
        if (arity < 0 || !IsAccessorMethodName(name)) return;
        map[(type, name, arity)] = newName;
    }

    /// <summary>按接收者静态类型沿基链 most-derived-first 查回退表（镜像 C# 成员查找）；
    /// 接收者为接口/类型参数时不命中（接口分派点不动）。</summary>
    internal static bool TryLookupAccessorRename(
        IReadOnlyDictionary<(string Type, string Name, int Arity), string> map,
        ITypeSymbol receiverType, string accessorName, int arity, out string newName)
    {
        newName = string.Empty;
        for (var type = receiverType as INamedTypeSymbol; type != null; type = type.BaseType)
            // 回退表键来自 plan 的声明符号（泛型定义实例），接收者静态类型可能是构造实例
            // （MyDict<string,string>）→ 统一归一到原始定义再比对。
            if (map.TryGetValue(
                    (type.OriginalDefinition.ToDisplayString(), accessorName, arity), out var found))
            {
                newName = found;
                return true;
            }
        return false;
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