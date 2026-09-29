using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Events;

/// <summary>
/// EV-1..EV-9 声明展开：EventFieldDeclaration 变量 → private 同名字段 + add_X/remove_X 两方法；
/// EventDeclaration → 两方法（accessor 体原样搬入，形参保留 value）；abstract/interface → 抽象签名；
/// 显式接口 → 显式实现。语义绑定（特性路由）一律走原始声明节点（展开产物脱离语法树无法再绑定）。
/// </summary>
internal sealed class EventDeclarationExpander
{
    private readonly SemanticModel _model;
    private readonly MemberDesugarOptions _options;
    private readonly MemberDesugarStatistics _stats;
    private readonly List<MemberDesugarDiagnostic> _diagnostics;
    private readonly string _filePath;

    public EventDeclarationExpander(
        SemanticModel model, MemberDesugarOptions options,
        MemberDesugarStatistics stats, List<MemberDesugarDiagnostic> diagnostics,
        string filePath)
    {
        _model = model;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _filePath = filePath;
    }

    /// <summary>field-like：一个变量一组产物（同名字段 + 两方法）。visitedVar 的初始化器已过站点改写。
    /// leadingTrivia 事件声明的文档/注释 trivia，只挂到首个产物。</summary>
    public IEnumerable<MemberDeclarationSyntax> ExpandFieldVariable(
        EventFieldDeclarationSyntax original, VariableDeclaratorSyntax visitedVar, EventPlan plan,
        SyntaxTriviaList leadingTrivia)
    {
        var type = original.Declaration.Type;
        var declName = visitedVar.Identifier.ValueText;

        var (fieldAttrs, methodAttrs) = RouteAttributeLists(original.AttributeLists, fieldGenerated: true);

        var declarator = SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(declName));
        if (visitedVar.Initializer != null)
            declarator = declarator.WithInitializer(visitedVar.Initializer);
        var field = SyntaxFactory.FieldDeclaration(
            SyntaxFactory.List(fieldAttrs),
            EventFacts.BuildFieldModifiers(original),
            SyntaxFactory.VariableDeclaration(type, SyntaxFactory.SeparatedList(new[] { declarator })))
            .WithLeadingTrivia(leadingTrivia)
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        yield return field;
        _stats.EventBackingFieldsGenerated++;

        EmitPlanDiagnostics(plan);
        foreach (var name in new[] { plan.AdderName, plan.RemoverName })
        {
            yield return BuildGeneratedAccessor(
                original, plan, type, name, name == plan.AdderName, methodAttrs,
                leadingTrivia: default, emitBody: true);
        }
    }

    /// <summary>field-like 语法但 abstract/接口成员（无字段）：仅两方法签名（EV-4/EV-5）。</summary>
    public IEnumerable<MemberDeclarationSyntax> ExpandAbstractSignatures(
        EventFieldDeclarationSyntax original, EventPlan plan, SyntaxTriviaList leadingTrivia)
    {
        var type = original.Declaration.Type;
        var (_, methodAttrs) = RouteAttributeLists(original.AttributeLists, fieldGenerated: false);

        EmitPlanDiagnostics(plan);
        var first = true;
        foreach (var name in new[] { plan.AdderName, plan.RemoverName })
        {
            yield return BuildGeneratedAccessor(
                original, plan, type, name, name == plan.AdderName, methodAttrs,
                first ? leadingTrivia : default, emitBody: false);
            first = false;
        }
    }

    /// <summary>custom / abstract（accessor 语法）/ 接口（含 DIM）/ 显式接口实现：仅两方法，体原样搬入。</summary>
    public IEnumerable<MemberDeclarationSyntax> ExpandEventDeclaration(
        EventDeclarationSyntax visited, EventPlan plan, EventDeclarationSyntax original)
    {
        var (_, methodAttrs) = RouteAttributeLists(original.AttributeLists, fieldGenerated: false);
        var leading = original.GetLeadingTrivia();
        var first = true;
        foreach (var accessor in visited.AccessorList!.Accessors)
        {
            yield return BuildMethodFromAccessor(
                original, accessor, plan, methodAttrs, first ? leading : default);
            first = false;
        }
        EmitPlanDiagnostics(plan);
    }

    // ---- 诊断（CS2J5150/5151/5152，Info 仅 verbose）----

    private void EmitPlanDiagnostics(EventPlan plan)
    {
        if (plan.UseCas)
        {
            _diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.EventCasPolicyApplied,
                $"'{plan.DeclName}': accessors generated in InterlockedCompareExchange mode " +
                "(stronger than the C# compiler's lock-free field-like generation).",
                _filePath, 0, 0, plan.DeclName));
        }
        else if (plan.HasField
                 && _options.EventSubscriptionPolicy == EventSubscriptionPolicy.InterlockedCompareExchange)
        {
            // 仅 struct 命中：HasField + 策略 CAS + UseCas=false ⇒ struct（D6）。
            _diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Warning, MemberDesugarDiagnosticCode.EventStructCasDegraded,
                EventDiagnostics.StructCasDegraded(plan.DeclName),
                _filePath, 0, 0, plan.DeclName));
        }

        _diagnostics.Add(new MemberDesugarDiagnostic(
            MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.EventPlanCreated,
            $"'{plan.DeclName}': desugared to " +
            (plan.HasField ? $"backing field '{plan.DeclName}' + " : string.Empty) +
            $"{plan.AdderName}/{plan.RemoverName} ({plan.PlanKind}).",
            _filePath, 0, 0, plan.DeclName));
    }

    // ---- 特性路由 ----

    private (List<AttributeListSyntax> Field, List<AttributeListSyntax> Methods) RouteAttributeLists(
        SyntaxList<AttributeListSyntax> lists, bool fieldGenerated)
    {
        var field = new List<AttributeListSyntax>();
        var methods = new List<AttributeListSyntax>();
        foreach (var list in lists)
        {
            switch (EventFacts.RouteAttributeList(list, _model, fieldGenerated))
            {
                case EventFacts.EventRoute.Field: field.Add(list); break;
                case EventFacts.EventRoute.Methods: methods.Add(list); break;
            }
        }
        return (field, methods);
    }

    // ---- 生成访问器（field-like：Simple/CAS 体）----

    private MemberDeclarationSyntax BuildGeneratedAccessor(
        MemberDeclarationSyntax original, EventPlan plan, TypeSyntax type,
        string name, bool isAdd, List<AttributeListSyntax> methodAttrs,
        SyntaxTriviaList leadingTrivia, bool emitBody)
    {
        var inInterface = original.Parent is InterfaceDeclarationSyntax;
        // field-like 语法（EventFieldDeclarationSyntax）不存在显式接口形态，仅 EventDeclarationSyntax 有。
        var explicitSpec = (original as EventDeclarationSyntax)?.ExplicitInterfaceSpecifier;
        var isExplicit = explicitSpec != null;
        var modifiers = EventFacts.BuildMethodModifiers(original, null, inInterface, isExplicit);

        BlockSyntax? body = null;
        ArrowExpressionClauseSyntax? arrow = null;
        if (emitBody)
        {
            if (plan.UseCas)
                body = CasBody(plan, isAdd);
            else
                arrow = SyntaxFactory.ArrowExpressionClause(SimpleBody(plan, isAdd));
        }

        var method = SyntaxFactory.MethodDeclaration(
            SyntaxFactory.List(methodAttrs),
            modifiers,
            SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
            explicitSpec,
            SyntaxFactory.Identifier(name),
            null,
            ParameterList(type),
            SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
            body,
            arrow)
            .WithLeadingTrivia(leadingTrivia)
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        if (body == null)
            method = method.WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        return method;
    }

    private ExpressionSyntax SimpleBody(EventPlan plan, bool isAdd)
    {
        var op = isAdd ? "Combine" : "Remove";
        return SyntaxFactory.ParseExpression(
            $"{plan.DeclName} = Cs2jPrelude.CsPrelude.{op}({plan.DeclName}, value)");
    }

    private static BlockSyntax CasBody(EventPlan plan, bool isAdd)
    {
        var op = isAdd ? "Combine" : "Remove";
        var text = $$"""

            {
                var current = {{plan.DeclName}};
                while (true)
                {
                    var combined = Cs2jPrelude.CsPrelude.{{op}}(current, value);
                    var original = System.Threading.Interlocked.CompareExchange(ref {{plan.DeclName}}, combined, current);
                    if (ReferenceEquals(original, current)) break;
                    current = original;
                }
            }
            """;
        return (BlockSyntax)SyntaxFactory.ParseStatement(text);
    }

    // ---- custom/abstract 访问器方法（体原样搬入）----

    private MemberDeclarationSyntax BuildMethodFromAccessor(
        EventDeclarationSyntax original, AccessorDeclarationSyntax accessor,
        EventPlan plan, List<AttributeListSyntax> methodAttrs, SyntaxTriviaList leadingTrivia)
    {
        var isAdd = accessor.IsKind(SyntaxKind.AddAccessorDeclaration);
        var name = isAdd ? plan.AdderName : plan.RemoverName;
        var inInterface = original.Parent is InterfaceDeclarationSyntax;
        var isExplicit = original.ExplicitInterfaceSpecifier != null;
        var modifiers = EventFacts.BuildMethodModifiers(original, accessor, inInterface, isExplicit);
        var attrs = accessor.AttributeLists.Concat(methodAttrs);

        var method = SyntaxFactory.MethodDeclaration(
            SyntaxFactory.List(attrs),
            modifiers,
            SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
            original.ExplicitInterfaceSpecifier,
            SyntaxFactory.Identifier(name),
            null,
            ParameterList(original.Type),
            SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
            accessor.Body,
            accessor.ExpressionBody)
            .WithLeadingTrivia(leadingTrivia)
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        if (accessor.Body == null)
            method = method.WithSemicolonToken(accessor.SemicolonToken);
        return method;
    }

    private static ParameterListSyntax ParameterList(TypeSyntax type)
        => SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(new[]
        {
            SyntaxFactory.Parameter(SyntaxFactory.Identifier("value")).WithType(type),
        }));
}
