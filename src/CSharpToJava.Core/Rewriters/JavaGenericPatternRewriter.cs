using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Rewriters;

/// <summary>
/// Java 路径语法层 rewriter：把类型携带泛型实参的声明模式（declaration pattern）
/// 提升为类型模式 + 段首强转声明，恢复模式变量的元素类型信息。
///
/// Java instanceof 只接受可具体化（reifiable）类型，前端因此剥掉泛型实参并以原始
/// 类型绑定模式变量（如 `o instanceof CSharpList l`）。原始 CSharpList 的 get(k)
/// 返回 Object，后续 `l.get(k).Member` 会因找不到成员而编译失败。改写为
/// `if (o instanceof CSharpList) { CSharpList<String> l = (CSharpList<String>)o; ... }`
/// 后，声明变量保持完整泛型类型，调用点语义恢复正常。非泛型声明模式保持 Java 16
/// 模式变量形态（instanceof T var），行为不变。
/// </summary>
public sealed class JavaGenericPatternRewriter : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        var visited = (IfStatementSyntax?)base.VisitIfStatement(node);
        return visited != null
            && TryHoistCondition(visited.Condition, out var condition, out var prologue)
            ? visited.WithCondition(condition).WithStatement(InsertPrologue(visited.Statement, prologue))
            : visited;
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        var visited = (WhileStatementSyntax?)base.VisitWhileStatement(node);
        return visited != null
            && TryHoistCondition(visited.Condition, out var condition, out var prologue)
            ? visited.WithCondition(condition).WithStatement(InsertPrologue(visited.Statement, prologue))
            : visited;
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
        var visited = (DoStatementSyntax?)base.VisitDoStatement(node);
        return visited != null
            && TryHoistCondition(visited.Condition, out var condition, out var prologue)
            ? visited.WithCondition(condition).WithStatement(InsertPrologue(visited.Statement, prologue))
            : visited;
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        var visited = (ForStatementSyntax?)base.VisitForStatement(node);
        if (visited == null
            || !TryHoistCondition(visited.Condition, out var condition, out var prologue))
        {
            return visited;
        }
        return visited.WithCondition(condition).WithStatement(InsertPrologue(visited.Statement, prologue));
    }

    public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
    {
        var visited = (SwitchStatementSyntax?)base.VisitSwitchStatement(node);
        if (visited == null)
        {
            return null;
        }
        var anyChange = false;
        var sections = new List<SwitchSectionSyntax>();
        foreach (var section in visited.Sections)
        {
            var newLabels = new List<SwitchLabelSyntax>();
            var declarations = new List<StatementSyntax>();
            foreach (var label in section.Labels)
            {
                if (label is CasePatternSwitchLabelSyntax { WhenClause: null } patternLabel
                    && patternLabel.Pattern is DeclarationPatternSyntax decl
                    && decl.Designation is SingleVariableDesignationSyntax sv
                    && IsGenericTypeSyntax(decl.Type))
                {
                    anyChange = true;
                    newLabels.Add(patternLabel.WithPattern(SyntaxFactory.TypePattern(decl.Type.WithoutTrivia())));
                    declarations.Add(BuildDeclaration(decl.Type.WithoutTrivia(), sv.Identifier, visited.Expression.WithoutTrivia()));
                }
                else
                {
                    newLabels.Add(label);
                }
            }
            if (declarations.Count > 0)
            {
                sections.Add(section
                    .WithLabels(SyntaxFactory.List(newLabels))
                    .WithStatements(SyntaxFactory.List(declarations.Concat(section.Statements))));
            }
            else
            {
                sections.Add(section);
            }
        }
        return anyChange ? visited.WithSections(SyntaxFactory.List(sections)) : visited;
    }

    private static StatementSyntax InsertPrologue(StatementSyntax body, StatementSyntax prologue)
        => body is BlockSyntax block
            ? block.WithStatements(block.Statements.Insert(0, prologue))
            : SyntaxFactory.Block(prologue, body);

    /// <summary>
    /// 仅当条件为携带泛型实参的声明模式时改写：条件 → 类型模式（instanceof 检查），
    /// prologue → `T v = (T)expr;`（T 保留完整泛型实参）。
    /// </summary>
    private static bool TryHoistCondition(
        ExpressionSyntax? condition,
        out ExpressionSyntax newCondition,
        out StatementSyntax prologue)
    {
        newCondition = condition!;
        prologue = SyntaxFactory.EmptyStatement();
        if (condition is not IsPatternExpressionSyntax isPattern
            || isPattern.Pattern is not DeclarationPatternSyntax decl
            || decl.Designation is not SingleVariableDesignationSyntax sv
            || !IsGenericTypeSyntax(decl.Type))
        {
            return false;
        }
        var type = decl.Type.WithoutTrivia();
        var expr = isPattern.Expression.WithoutTrivia();
        newCondition = SyntaxFactory.IsPatternExpression(expr, SyntaxFactory.TypePattern(type));
        prologue = BuildDeclaration(type, sv.Identifier, expr);
        return true;
    }

    private static LocalDeclarationStatementSyntax BuildDeclaration(TypeSyntax type, SyntaxToken identifier, ExpressionSyntax expr)
    {
        var declarator = SyntaxFactory.VariableDeclarator(identifier)
            .WithInitializer(SyntaxFactory.EqualsValueClause(SyntaxFactory.CastExpression(type, expr)));
        return SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(type, SyntaxFactory.SingletonSeparatedList(declarator)));
    }

    /// <summary>
    /// 判断类型语法是否携带泛型实参（List&lt;T&gt;、System...List&lt;T&gt;、
    /// List&lt;T&gt;[]、Nullable&lt;T&gt; 等）；裸标识符/简单类型不算。
    /// </summary>
    private static bool IsGenericTypeSyntax(TypeSyntax type) => type switch
    {
        GenericNameSyntax => true,
        QualifiedNameSyntax q => IsGenericTypeSyntax(q.Right),
        AliasQualifiedNameSyntax aq => IsGenericTypeSyntax(aq.Name),
        ArrayTypeSyntax arr => IsGenericTypeSyntax(arr.ElementType),
        NullableTypeSyntax nt => IsGenericTypeSyntax(nt.ElementType),
        _ => false
    };
}
