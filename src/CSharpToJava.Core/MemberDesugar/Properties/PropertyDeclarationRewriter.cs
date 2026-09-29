using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Properties;

/// <summary>
/// PD-1/PD-2/PD-3/PD-4/PD-5/PD-10：把一个属性声明展开为
/// 「[后备字段] + get_X/set_X 方法」的成员序列。修饰符/可访问性/体/显式接口/初始化器/
/// 特性/文档 trivia 平移由本类负责；使用点改写在 <see cref="PropertyUseSiteRewriter"/>。
/// </summary>
internal sealed class PropertyDeclarationExpander
{
    private readonly SemanticModel _model;
    private readonly MemberDesugarOptions _options;
    private readonly MemberDesugarStatistics _stats;
    private readonly List<MemberDesugarDiagnostic> _diagnostics;
    private readonly string _filePath;

    public PropertyDeclarationExpander(
        SemanticModel model, MemberDesugarOptions options, MemberDesugarStatistics stats,
        List<MemberDesugarDiagnostic> diagnostics, string filePath)
    {
        _model = model;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _filePath = filePath;
    }

    public IReadOnlyList<MemberDeclarationSyntax> Expand(PropertyDeclarationSyntax decl, PropertyPlan plan)
    {
        var prop = plan.Property;
        var inInterface = prop.ContainingType is { TypeKind: TypeKind.Interface };
        var isExplicit = decl.ExplicitInterfaceSpecifier != null;
        var fieldGenerated = plan.BackingFieldName != null && !prop.IsAbstract;
        var isAuto = PropertyFacts.IsAutoProperty(decl, prop);

        var members = new List<MemberDeclarationSyntax>();
        if (fieldGenerated)
            members.Add(BuildField(decl, plan));

        var accessors = decl.AccessorList != null
            ? decl.AccessorList.Accessors
            : default(SyntaxList<AccessorDeclarationSyntax>);
        var getterIndex = -1;
        foreach (var acc in accessors)
        {
            if (acc.IsKind(SyntaxKind.GetAccessorDeclaration) && prop.GetMethod != null)
            {
                if (getterIndex < 0) getterIndex = members.Count;
                members.Add(BuildMethod(decl, plan, acc, plan.GetterMethodName,
                    isGetter: true, inInterface, isExplicit, isAuto));
            }
            else if ((acc.IsKind(SyntaxKind.SetAccessorDeclaration)
                    || acc.IsKind(SyntaxKind.InitAccessorDeclaration)) && plan.HasSetter)
            {
                members.Add(BuildMethod(decl, plan, acc, plan.SetterMethodName!,
                    isGetter: false, inInterface, isExplicit, isAuto));
            }
        }

        // 文档/前导 trivia 归 getter（现转换器惯例）；尾随 trivia 归最后一个成员。
        if (members.Count > 0)
        {
            if (getterIndex >= 0)
                members[getterIndex] = members[getterIndex].WithLeadingTrivia(decl.GetLeadingTrivia());
            else
                members[0] = members[0].WithLeadingTrivia(decl.GetLeadingTrivia());
            members[^1] = members[^1].WithTrailingTrivia(decl.GetTrailingTrivia());
        }

        if (_options.ReportInfo)
        {
            _diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.PlanCreated,
                $"property plan executed ({(fieldGenerated ? "field + " : "")}" +
                $"{(prop.GetMethod != null ? "get + " : "")}{(plan.HasSetter ? "set" : "")})",
                _filePath, LineOf(prop), 0, plan.SymbolName));
            if (plan.HasInitAccessor)
                _diagnostics.Add(new MemberDesugarDiagnostic(
                    MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.InitWidened,
                    "init accessor widened to an ordinary set method (compile-time constraint relaxed)",
                    _filePath, LineOf(prop), 0, plan.SymbolName));
            if (plan.IsRequired)
                _diagnostics.Add(new MemberDesugarDiagnostic(
                    MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.RequiredDropped,
                    "required modifier not transferred to the desugared members " +
                    "(compile-time constraint relaxed)",
                    _filePath, LineOf(prop), 0, plan.SymbolName));
        }
        if (plan.HasInitAccessor) _stats.InitAccessorsWidened++;
        if (plan.IsRequired) _stats.RequiredConstraintsDropped++;

        return members;
    }

    private static int LineOf(IPropertySymbol prop)
        => (prop.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan().StartLinePosition.Line ?? 0) + 1;

    private FieldDeclarationSyntax BuildField(PropertyDeclarationSyntax decl, PropertyPlan plan)
    {
        var prop = plan.Property;
        var mods = new List<SyntaxToken> { SyntaxFactory.Token(SyntaxKind.PrivateKeyword) };
        if (prop.IsStatic)
            mods.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        // get-only（无 set/init）→ readonly；init 放宽为可写 set 后字段必须可变（决策 4）。
        if (prop.SetMethod == null)
            mods.Add(SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword));

        var declarator = SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(plan.BackingFieldName!));
        if (decl.Initializer != null)
            declarator = declarator.WithInitializer(decl.Initializer);
        var declaration = SyntaxFactory.VariableDeclaration(decl.Type)
            .WithVariables(SyntaxFactory.SeparatedList(new[] { declarator }));

        // PD-10：[field:] 显式目标与「Field 可施用」的属性级特性 → 字段。
        var attrLists = new List<AttributeListSyntax>();
        foreach (var list in decl.AttributeLists)
        {
            if (AttributeRouting.RouteList(list, _model, fieldGenerated: true) == AttributeRouting.Destination.Field)
                attrLists.Add(list.WithTarget(null)); // 落到字段上即默认目标
        }

        var field = SyntaxFactory.FieldDeclaration(
            SyntaxFactory.List(attrLists),
            SyntaxFactory.TokenList(mods),
            declaration,
            SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        _stats.BackingFieldsGenerated++;
        if (_options.ReportInfo)
            _diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.BackingFieldGenerated,
                $"backing field '{plan.BackingFieldName}' generated for property '{prop.Name}'",
                _filePath, LineOf(prop), 0, plan.SymbolName));

        return (FieldDeclarationSyntax)field
            .WithLeadingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.ElasticSpace))
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    private MethodDeclarationSyntax BuildMethod(
        PropertyDeclarationSyntax decl, PropertyPlan plan, AccessorDeclarationSyntax acc,
        string name, bool isGetter, bool inInterface, bool isExplicit, bool isAuto)
    {
        var modifiers = AccessorsToMethods.BuildMethodModifiers(decl, acc, inInterface, isExplicit);
        var returnType = isGetter
            ? decl.Type
            : SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword));

        var parameterList = isGetter
            ? SyntaxFactory.ParameterList()
            : SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(new[]
            {
                SyntaxFactory.Parameter(
                    default, default,
                    decl.Type.WithoutTrivia(),
                    SyntaxFactory.Identifier("value"),
                    null)
            }));

        var attrLists = new List<AttributeListSyntax>(acc.AttributeLists);
        if (isGetter)
        {
            var fieldGenerated = plan.BackingFieldName != null && !plan.Property.IsAbstract;
            foreach (var list in decl.AttributeLists)
            {
                var route = AttributeRouting.RouteList(list, _model, fieldGenerated);
                if (route is AttributeRouting.Destination.Getter or AttributeRouting.Destination.Methods)
                    attrLists.Add(list.WithTarget(null));
            }
        }
        if (!isGetter)
        {
            var fieldGenerated = plan.BackingFieldName != null && !plan.Property.IsAbstract;
            foreach (var list in decl.AttributeLists)
            {
                if (AttributeRouting.RouteList(list, _model, fieldGenerated) == AttributeRouting.Destination.Methods)
                    attrLists.Add(list.WithTarget(null));
            }
        }

        BlockSyntax? body = acc.Body;
        ArrowExpressionClauseSyntax? expressionBody = acc.ExpressionBody;
        SyntaxToken semicolon = acc.SemicolonToken;

        // 自动访问器：合成方法体（PD-1）。显式接口实现/抽象不可能为 auto。
        if (isAuto && body == null && expressionBody == null && !plan.Property.IsAbstract && !inInterface)
        {
            if (isGetter)
            {
                body = SyntaxFactory.Block(
                    SyntaxFactory.ReturnStatement(
                        SyntaxFactory.IdentifierName(plan.BackingFieldName!)));
            }
            else
            {
                body = SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(plan.BackingFieldName!),
                            SyntaxFactory.IdentifierName("value"))));
            }
            semicolon = default;
        }

        var method = SyntaxFactory.MethodDeclaration(
            SyntaxFactory.List(attrLists),
            modifiers,
            returnType,
            isExplicit
                ? SyntaxFactory.ExplicitInterfaceSpecifier(
                    decl.ExplicitInterfaceSpecifier!.Name,
                    SyntaxFactory.Token(SyntaxKind.DotToken))
                : null,
            SyntaxFactory.Identifier(name),
            null,
            parameterList,
            SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
            body,
            expressionBody)
            .WithSemicolonToken(semicolon);

        return (MethodDeclarationSyntax)method.WithAdditionalAnnotations(
            MemberDesugarSyntaxFacts.RewriteAnnotation);
    }
}
