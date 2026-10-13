using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.EnumDeclaration;

/// <summary>
/// switch → if-else 链降级的作用域事实（§4.5.4）。
/// C# 中各 case 段直接声明的局部变量同属 switch 块作用域：后序段可以引用
/// 前序段声明的名字（逐段做明确赋值分析）；不同段也允许声明同名变量。
/// 降级成相互独立的 if 分支块后，只有【被其他段引用】的变量必须提升到
/// 合成包裹块；不能安全提升的站点判 ED020。
/// </summary>
internal static class SwitchSectionScopeFacts
{
    private sealed record ScopeEntry(int Section, bool Hoistable, SyntaxNode Declarator);

    /// <summary>
    /// 盘点各 case 段直接引入的名字，返回：
    /// ① 无法靠变量提升保持语义的跨段引用（const/using/ref 局部、本地函数、
    /// 解构/out-var）；② 必须提升的零修饰符局部变量声明符集合。
    /// </summary>
    public static ((SyntaxNode Node, string Reason)? Unhoistable,
        HashSet<VariableDeclaratorSyntax> HoistedVariables) Analyze(
        SwitchStatementSyntax node, SemanticModel model)
    {
        var sections = node.Sections;
        var declared = new Dictionary<ISymbol, ScopeEntry>(SymbolEqualityComparer.Default);

        for (var i = 0; i < sections.Count; i++)
        {
            foreach (var statement in sections[i].Statements)
            {
                switch (statement)
                {
                    case LocalDeclarationStatementSyntax local:
                    {
                        var hoistable = local.Modifiers.Count == 0;
                        foreach (var variable in local.Declaration.Variables)
                        {
                            if (model.GetDeclaredSymbol(variable) is { } symbol)
                                declared[symbol] = new ScopeEntry(i, hoistable, variable);
                        }
                        break;
                    }
                    case LocalFunctionStatementSyntax localFunction:
                    {
                        if (model.GetDeclaredSymbol(localFunction) is { } fn)
                            declared[fn] = new ScopeEntry(i, false, localFunction);
                        break;
                    }
                    default:
                    {
                        // 直接语句中的解构/out-var 声明作用域只到本段，收集为
                        // 不可提升名；跨段引用即 ED020（基线编译不过则不会出现）。
                        foreach (var single in statement.DescendantNodes()
                                     .OfType<SingleVariableDesignationSyntax>())
                        {
                            if (model.GetDeclaredSymbol(single) is { } symbol)
                                declared[symbol] = new ScopeEntry(i, false, single);
                        }
                        break;
                    }
                }
            }
        }

        var unhoistable = (SyntaxNode?)null;
        string? reason = null;
        var hoisted = new HashSet<VariableDeclaratorSyntax>();

        if (declared.Count > 0)
        {
            for (var i = 0; i < sections.Count && unhoistable == null; i++)
            {
                foreach (var identifier in sections[i].DescendantNodes()
                             .OfType<IdentifierNameSyntax>())
                {
                    var symbol = model.GetSymbolInfo(identifier).Symbol;
                    if (symbol == null || !declared.TryGetValue(symbol, out var origin))
                        continue;
                    if (origin.Section == i) continue;

                    if (!origin.Hoistable)
                    {
                        var kind = symbol.Kind switch
                        {
                            SymbolKind.Local => "a local declared with modifiers "
                                + "(const/using/ref) or introduced by deconstruction/out-var",
                            SymbolKind.Method => "a local function",
                            _ => "a section-scoped declaration",
                        };
                        unhoistable = identifier;
                        reason = "switch on a rewritten enum references " + kind
                            + " declared in another case section; cross-section scope cannot "
                            + "be preserved by local hoisting. Lower this switch in an "
                            + "earlier pass.";
                        break;
                    }

                    if (origin.Declarator is VariableDeclaratorSyntax variable)
                        hoisted.Add(variable);
                }
            }
        }

        return (unhoistable == null ? null : (unhoistable, reason!), hoisted);
    }
}
