using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.TypeScript.Rewriters;

/// <summary>
/// 模式匹配 TS 化语法层 rewriter（Task 27，M3）。共享前端按 Java 16 形态降低模式匹配
/// （`expr instanceof T p` 模式变量绑定、switch 表达式三元链），TS 无模式变量语法，
/// 在本 pass 改写为前端可直译的形态（spec §1.4.4）：
/// ① `if (x is T p) body` / `while (x is T p) body` → `if (x is T) { T p = (T)x; body }`
///    （类型模式 + 显式强转声明，前端译 `x instanceof T` + `x as T`，均为合法 TS）；
/// ② `x is not T` → `!(x is T)`（前端 unary-not 路径把类型当常量比较，产出 `x == T`
///    非法比较，显式改写为取反的类型模式）；
/// ③ switch 表达式 → 嵌套三元表达式（前端既有 lowering 的 TS 化版本）：声明模式臂
///    `T p [when W] => V` → `(x is T && W[p→(T)x] ? V[p→(T)x] : ...)`，类型模式臂
///    `T => V` → `(x is T ? V : ...)`，常量模式臂 `C => V` → `(_ when x == C ? V : ...)`
///    （绕开前端 `java.util.Objects.equals` 无 TS 映射），递归/属性模式按子条件拼装，
///    模式变量在使用处（when 守卫/臂值）替换为 `((T)x)` 强转（与前端重复求值行为一致）；
/// ④ switch 语句模式标签 `case T p:` → `case T:` + 段首声明（when 子句引用模式变量的
///    标签不改写，登记限制）——接口模式标签经此前端 instanceof + 探针 rewriter 链路
///    修复 Task 25 注记⑦的 `case IFoo f:` 限制。
/// 原始类型模式（`int i` 等）前端 `instanceof int` 无 TS 对应，登记限制不改写。
/// </summary>
public sealed class TsPatternMatchingRewriter : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitIfStatement(IfStatementSyntax node)
    {
        var visited = (IfStatementSyntax?)base.VisitIfStatement(node);
        if (visited == null)
        {
            return visited;
        }
        if (TryBuildPatternPrologue(visited.Condition, out var newCondition, out var prologue))
        {
            var newBody = visited.Statement is BlockSyntax block
                ? block.WithStatements(block.Statements.Insert(0, prologue))
                : SyntaxFactory.Block(prologue, visited.Statement);
            return visited.WithCondition(newCondition).WithStatement(newBody);
        }
        return visited;
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        var visited = (WhileStatementSyntax?)base.VisitWhileStatement(node);
        if (visited == null)
        {
            return visited;
        }
        if (TryBuildPatternPrologue(visited.Condition, out var newCondition, out var prologue))
        {
            var newBody = visited.Statement is BlockSyntax block
                ? block.WithStatements(block.Statements.Insert(0, prologue))
                : SyntaxFactory.Block(prologue, visited.Statement);
            return visited.WithCondition(newCondition).WithStatement(newBody);
        }
        return visited;
    }

    /// <summary>
    /// if/while 条件的声明模式 / not 类型模式改写。声明模式产出类型模式条件 +
    /// 段首声明语句（`T p = (T)x;`）；not 类型模式产出 `!(x is T)`。
    /// </summary>
    private static bool TryBuildPatternPrologue(
        ExpressionSyntax condition,
        out ExpressionSyntax newCondition,
        out StatementSyntax prologue)
    {
        newCondition = condition;
        prologue = SyntaxFactory.EmptyStatement();

        if (condition is not IsPatternExpressionSyntax isPattern)
        {
            return false;
        }
        if (isPattern.Pattern is DeclarationPatternSyntax decl
            && decl.Designation is SingleVariableDesignationSyntax sv)
        {
            var cast = SyntaxFactory.CastExpression(decl.Type.WithoutTrivia(), isPattern.Expression.WithoutTrivia());
            var declarator = SyntaxFactory.VariableDeclarator(sv.Identifier)
                .WithInitializer(SyntaxFactory.EqualsValueClause(cast));
            var declaration = SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(decl.Type.WithoutTrivia(),
                    SyntaxFactory.SingletonSeparatedList(declarator)));
            newCondition = SyntaxFactory.IsPatternExpression(
                isPattern.Expression, SyntaxFactory.TypePattern(decl.Type));
            prologue = declaration;
            return true;
        }
        if (TryGetNotTypePattern(isPattern.Pattern, out var notType))
        {
            var negated = SyntaxFactory.IsPatternExpression(isPattern.Expression, SyntaxFactory.TypePattern(notType!));
            // 操作数需自带括号：前端 unary 发射不补括号（(!s is Circle) 会输出 !s instanceof）
            newCondition = SyntaxFactory.PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                SyntaxFactory.ParenthesizedExpression(negated));
            return true;
        }
        return false;
    }

    /// <summary>
    /// `is not T` 的语法形态：Roslyn 将 `not Circle` 解析为 UnaryPattern(not,
    /// ConstantPattern(Circle))（binder 层才重解释为类型模式）；直接写 `!(s is Circle)`
    /// 时才是 TypePattern。此处把常量模式中的类型名（标识符/成员访问，非字面量）视为
    /// 类型模式返回；`is not null` / `is not 5` 等常量否定直通（前端产物合法）。
    /// </summary>
    private static bool TryGetNotTypePattern(PatternSyntax pattern, out TypeSyntax? type)
    {
        type = null;
        if (pattern is not UnaryPatternSyntax unary
            || !unary.OperatorToken.IsKind(SyntaxKind.NotKeyword))
        {
            return false;
        }
        if (unary.Pattern is TypePatternSyntax tp)
        {
            type = tp.Type;
            return true;
        }
        if (unary.Pattern is ConstantPatternSyntax cp
            && cp.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax)
        {
            type = SyntaxFactory.ParseTypeName(cp.Expression.ToString());
            return true;
        }
        return false;
    }

    public override SyntaxNode? VisitIsPatternExpression(IsPatternExpressionSyntax node)
    {
        var visited = (IsPatternExpressionSyntax?)base.VisitIsPatternExpression(node);
        if (visited == null)
        {
            return visited;
        }
        // 表达式上下文（return/赋值/实参）的 `x is not T`：前端 unary-not 路径将类型
        // 当常量比较（产出 `x == T` 非法），改写为 `!(x is T)`（前端译 instanceof）
        if (TryGetNotTypePattern(visited.Pattern, out var notType))
        {
            var negated = SyntaxFactory.IsPatternExpression(visited.Expression, SyntaxFactory.TypePattern(notType!));
            // 操作数需自带括号：前端 unary 发射不补括号（(!s is Circle) 会输出 !s instanceof）
            return SyntaxFactory.PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                SyntaxFactory.ParenthesizedExpression(negated));
        }
        // 表达式上下文的递归/属性模式 `x is T { P: sub }`：前端 Java 16 lowering 硬编码
        // getter 调用（((T)x).getP()，字段形态泄漏不存在的 getP）且强转为 Java
        // 形态；在语法层重建为类型检查 + 成员访问子条件
        //（字段 → .P，自动属性 → getP() 由 TsPropertyReconstructionRewriter
        // 还原为 .P）。带 designation/无类型的形态保留前端降级（登记限制）。
        if (visited.Pattern is RecursivePatternSyntax recPattern
            && recPattern.Type != null
            && recPattern.Designation == null
            && recPattern.PropertyPatternClause != null
            && recPattern.PropertyPatternClause.Subpatterns.Count > 0)
        {
            return BuildRecursiveCondition(visited.Expression, recPattern);
        }
        return visited;
    }

    public override SyntaxNode? VisitSwitchStatement(SwitchStatementSyntax node)
    {
        var visited = (SwitchStatementSyntax?)base.VisitSwitchStatement(node);
        if (visited == null)
        {
            return visited;
        }

        var anyChange = false;
        var sections = new List<SwitchSectionSyntax>();
        foreach (var section in visited.Sections)
        {
            var newSection = section;
            var newLabels = new List<SwitchLabelSyntax>();
            var declStatements = new List<StatementSyntax>();
            foreach (var label in section.Labels)
            {
                if (label is CasePatternSwitchLabelSyntax patternLabel
                    && patternLabel.Pattern is DeclarationPatternSyntax decl
                    && decl.Designation is SingleVariableDesignationSyntax sv
                    && patternLabel.WhenClause == null)
                {
                    anyChange = true;
                    newLabels.Add(patternLabel.WithPattern(SyntaxFactory.TypePattern(decl.Type)));
                    var cast = SyntaxFactory.CastExpression(decl.Type.WithoutTrivia(),
                        visited.Expression.WithoutTrivia());
                    var declarator = SyntaxFactory.VariableDeclarator(sv.Identifier)
                        .WithInitializer(SyntaxFactory.EqualsValueClause(cast));
                    declStatements.Add(SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(decl.Type.WithoutTrivia(),
                            SyntaxFactory.SingletonSeparatedList(declarator))));
                }
                else
                {
                    newLabels.Add(label);
                }
            }
            if (declStatements.Count > 0)
            {
                newSection = newSection.WithLabels(SyntaxFactory.List(newLabels))
                    .WithStatements(SyntaxFactory.List(declStatements.Concat(section.Statements)));
            }
            sections.Add(newSection);
        }
        return anyChange ? visited.WithSections(SyntaxFactory.List(sections)) : visited;
    }

    public override SyntaxNode? VisitSwitchExpression(SwitchExpressionSyntax node)
    {
        var visited = (SwitchExpressionSyntax?)base.VisitSwitchExpression(node);
        if (visited == null)
        {
            return visited;
        }
        if (visited.Arms.All(a => a.Pattern is DiscardPatternSyntax or TypePatternSyntax))
        {
            // 无模式变量/常量臂：前端 lowering 已产出合法 TS（TypePattern →
            // instanceof），直通不重建，保持既有产物形态
            return visited;
        }

        var governing = visited.GoverningExpression;
        ExpressionSyntax result = SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
        foreach (var arm in visited.Arms.Reverse())
        {
            if (arm.Pattern is DiscardPatternSyntax && arm.WhenClause == null)
            {
                result = arm.Expression;
                continue;
            }
            // Roslyn 语法节点不允许跨父节点共享：每臂克隆 governing（WithoutTrivia 产生新节点）
            var armGoverning = governing.WithoutTrivia();
            var condition = BuildArmCondition(armGoverning, arm.Pattern);
            ExpressionSyntax whenTrue = arm.Expression;
            string? patternVar = GetPatternVariable(arm.Pattern, out var patternType);
            if (patternVar != null)
            {
                var replacement = SyntaxFactory.CastExpression(patternType!.WithoutTrivia(), armGoverning);
                whenTrue = (ExpressionSyntax)new PatternVarReplacer(patternVar, replacement).Visit(whenTrue)!;
            }
            if (arm.WhenClause != null)
            {
                ExpressionSyntax whenCond = arm.WhenClause.Condition;
                if (patternVar != null)
                {
                    var replacement = SyntaxFactory.CastExpression(patternType!.WithoutTrivia(), armGoverning);
                    whenCond = (ExpressionSyntax)new PatternVarReplacer(patternVar, replacement).Visit(whenCond)!;
                }
                condition = SyntaxFactory.BinaryExpression(
                    SyntaxKind.LogicalAndExpression,
                    SyntaxFactory.ParenthesizedExpression(condition),
                    SyntaxFactory.ParenthesizedExpression(whenCond));
            }
            result = SyntaxFactory.ConditionalExpression(
                SyntaxFactory.ParenthesizedExpression(condition),
                whenTrue,
                result);
        }
        return SyntaxFactory.ParenthesizedExpression(result);
    }

    /// <summary>
    /// 递归构建 switch 臂条件（TS 化的类型检查 + 子条件）。
    /// </summary>
    private static ExpressionSyntax BuildArmCondition(ExpressionSyntax expr, PatternSyntax pattern)
    {
        // 每调用克隆一次，保证递归组合（and/or/子模式）中同一表达式不跨父共享
        expr = expr.WithoutTrivia();
        return pattern switch
        {
            ConstantPatternSyntax cp when cp.Expression.IsKind(SyntaxKind.NullLiteralExpression)
                => SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, expr, cp.Expression),
            ConstantPatternSyntax cp
                => SyntaxFactory.BinaryExpression(SyntaxKind.EqualsExpression, expr, cp.Expression),
            DeclarationPatternSyntax dp => SyntaxFactory.IsPatternExpression(expr, SyntaxFactory.TypePattern(dp.Type)),
            TypePatternSyntax tp => SyntaxFactory.IsPatternExpression(expr, tp),
            DiscardPatternSyntax => SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression),
            RelationalPatternSyntax rel
                => SyntaxFactory.BinaryExpression(RelationalKind(rel.OperatorToken), expr, rel.Expression),
            UnaryPatternSyntax np when np.OperatorToken.IsKind(SyntaxKind.NotKeyword)
                => SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression,
                    SyntaxFactory.ParenthesizedExpression(BuildArmCondition(expr, np.Pattern))),
            BinaryPatternSyntax bin when bin.IsKind(SyntaxKind.AndPattern)
                => SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression,
                    SyntaxFactory.ParenthesizedExpression(BuildArmCondition(expr, bin.Left)),
                    SyntaxFactory.ParenthesizedExpression(BuildArmCondition(expr, bin.Right))),
            BinaryPatternSyntax bin when bin.IsKind(SyntaxKind.OrPattern)
                => SyntaxFactory.BinaryExpression(SyntaxKind.LogicalOrExpression,
                    SyntaxFactory.ParenthesizedExpression(BuildArmCondition(expr, bin.Left)),
                    SyntaxFactory.ParenthesizedExpression(BuildArmCondition(expr, bin.Right))),
            VarPatternSyntax varPattern => BuildVarCondition(expr, varPattern),
            RecursivePatternSyntax recPattern => BuildRecursiveCondition(expr, recPattern),
            ParenthesizedPatternSyntax parenPattern => BuildArmCondition(expr, parenPattern.Pattern),
            _ => SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)
        };
    }

    private static SyntaxKind RelationalKind(SyntaxToken token)
    {
        return token.Kind() switch
        {
            SyntaxKind.GreaterThanToken => SyntaxKind.GreaterThanExpression,
            SyntaxKind.GreaterThanEqualsToken => SyntaxKind.GreaterThanOrEqualExpression,
            SyntaxKind.LessThanToken => SyntaxKind.LessThanExpression,
            SyntaxKind.LessThanEqualsToken => SyntaxKind.LessThanOrEqualExpression,
            _ => SyntaxKind.EqualsExpression,
        };
    }

    private static ExpressionSyntax BuildVarCondition(ExpressionSyntax expr, VarPatternSyntax pattern)
    {
        if (pattern.Designation is SingleVariableDesignationSyntax sv)
        {
            var replacement = expr.WithoutTrivia();
            var assigned = SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(sv.Identifier),
                replacement);
            return SyntaxFactory.BinaryExpression(
                SyntaxKind.LogicalOrExpression,
                SyntaxFactory.ParenthesizedExpression(SyntaxFactory.BinaryExpression(
                    SyntaxKind.NotEqualsExpression,
                    SyntaxFactory.ParenthesizedExpression(assigned),
                    SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))),
                SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression));
        }
        return SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression);
    }

    /// <summary>
    /// 递归/属性模式：`expr is T { P: sub } p` → `expr is T && ((T)expr).P sub`
    /// （属性子模式递归构建，getter 链由前端产出）；designation 替换为强转。
    /// </summary>
    private static ExpressionSyntax BuildRecursiveCondition(ExpressionSyntax expr, RecursivePatternSyntax pattern)
    {
        ExpressionSyntax condition = SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression);
        if (pattern.Type != null)
        {
            condition = SyntaxFactory.IsPatternExpression(expr, SyntaxFactory.TypePattern(pattern.Type));
        }
        if (pattern.PropertyPatternClause != null && pattern.Type != null)
        {
            var castExpr = SyntaxFactory.CastExpression(pattern.Type.WithoutTrivia(), expr.WithoutTrivia());
            foreach (var sub in pattern.PropertyPatternClause.Subpatterns)
            {
                string? propName = sub.NameColon?.Name.Identifier.Text
                    ?? (sub.ExpressionColon?.Expression is IdentifierNameSyntax idName ? idName.Identifier.Text : null);
                if (propName == null)
                {
                    continue;
                }
                var memberAccess = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.ParenthesizedExpression(castExpr),
                    SyntaxFactory.IdentifierName(propName));
                var subCondition = BuildArmCondition(memberAccess, sub.Pattern);
                condition = SyntaxFactory.BinaryExpression(
                    SyntaxKind.LogicalAndExpression,
                    SyntaxFactory.ParenthesizedExpression(condition),
                    SyntaxFactory.ParenthesizedExpression(subCondition));
            }
        }
        return condition;
    }

    /// <summary>
    /// 提取声明/递归模式的模式变量名与类型（无变量返回 null）。
    /// </summary>
    private static string? GetPatternVariable(PatternSyntax pattern, out TypeSyntax? patternType)
    {
        patternType = null;
        if (pattern is DeclarationPatternSyntax decl
            && decl.Designation is SingleVariableDesignationSyntax sv)
        {
            patternType = decl.Type;
            return sv.Identifier.Text;
        }
        if (pattern is RecursivePatternSyntax rec
            && rec.Designation is SingleVariableDesignationSyntax svRec)
        {
            patternType = rec.Type;
            return svRec.Identifier.Text;
        }
        return null;
    }

    /// <summary>
    /// 模式变量使用处替换：`c` → `((T)x)`。仅替换表达式位置（成员访问接收者、
    /// 实参、独立标识符），跳过声明/名称位置（成员名、限定名、形参、声明符）。
    /// </summary>
    private sealed class PatternVarReplacer : CSharpSyntaxRewriter
    {
        private readonly string _varName;
        private readonly ExpressionSyntax _replacement;

        public PatternVarReplacer(string varName, ExpressionSyntax replacement)
        {
            _varName = varName;
            _replacement = replacement;
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (node.Identifier.Text == _varName && !IsNamePosition(node))
            {
                return _replacement.WithTriviaFrom(node);
            }
            return base.VisitIdentifierName(node);
        }

        private static bool IsNamePosition(SyntaxNode node)
        {
            return node.Parent switch
            {
                MemberAccessExpressionSyntax ma => ma.Name == node,
                QualifiedNameSyntax qn => qn.Right == node || qn.Left == node,
                NameColonSyntax => true,
                NameEqualsSyntax => true,
                VariableDeclaratorSyntax => true,
                DeclarationExpressionSyntax => true,
                ParameterSyntax => true,
                SingleVariableDesignationSyntax => true,
                _ => false,
            };
        }
    }
}
