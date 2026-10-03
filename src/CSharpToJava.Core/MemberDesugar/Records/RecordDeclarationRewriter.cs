using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.MemberDesugar.Properties;

namespace CSharpToJava.Core.MemberDesugar.Records;

/// <summary>
/// RC-1/RC-2 克隆工厂注入（设计 §4.3/决策 3）：按 <see cref="RecordClonePlan"/> 期算定的
/// 工厂形态与统一定名，在目标 record 声明部件的成员表末尾发射一个工厂方法——
/// abstract record 发 <c>public abstract T Cs2jWithClone();</c>；
/// 非密封具体 record 发 <c>public virtual T Cs2jWithClone() =&gt; new T(this);</c>；
/// 派生发 <c>public override D Cs2jWithClone() =&gt; new D(this);</c>；
/// 密封 record 发非虚普通方法。方法体 <c>new T(this)</c> 调复制 ctor（编译器合成
/// protected 或用户自定义均自动接管，零特殊处理）。
/// 本类只做纯语法发射，不做任何符号判定（结论全部来自 plan）；声明自身的 XML doc/
/// trivia/特性原样保留，新成员挂族 RewriteAnnotation，间距交给族 FormatAndValidate。
/// </summary>
internal static class RecordDeclarationRewriter
{
    /// <summary>
    /// 为指定声明部件构造克隆工厂；该部件不是 plan 选定的注入部件 / 工厂已存在 /
    /// 闸门形态不发工厂时返回 null。
    /// </summary>
    public static MethodDeclarationSyntax? TryBuildFactory(
        RecordDeclarationSyntax decl,
        RecordClonePlan plan,
        MemberDesugarOptions options,
        MemberDesugarStatistics stats,
        List<MemberDesugarDiagnostic> diagnostics,
        string filePath)
    {
        // 部件锚定（FactoryInsertSpanStart == 声明标识符跨度）由唯一调用方
        // WithExpressionRewriter.VisitRecordDeclaration 在绿色原节点上完成；此处 decl
        // 可能是 reshape 后的红色脱离节点（跨度为子树相对值），不再比较跨度。
        if (plan.IsOrphan || !plan.FactoryRequired || plan.FactoryAlreadyExists
            || plan.FactoryName is not { Length: > 0 } factoryName
            || plan.FactoryForm is not { } form)
        {
            return null;
        }

        // 防御性幂等：plan 期已按符号识别同签名工厂（首轮产物/用户手写）；此处再按
        // 语法挡一层，任何情况下同部件不重发同名方法。
        if (decl.Members.OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.ValueText == factoryName))
        {
            return null;
        }

        var typeName = BuildSelfTypeName(decl);
        var modifiers = BuildModifiers(form);

        MethodDeclarationSyntax method;
        if (form == RecordFactoryForm.Abstract)
        {
            method = SyntaxFactory.MethodDeclaration(
                    default,
                    modifiers,
                    typeName,
                    null,
                    SyntaxFactory.Identifier(factoryName),
                    null,
                    SyntaxFactory.ParameterList(),
                    SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                    null,
                    null)
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }
        else
        {
            // new T(this) / new T<...>(this)：复制 ctor 由 record 语言保证存在且可从
            // 类型内部访问（protected 合成 ctor 同样可见），派生 ctor 强制链基类复制 ctor。
            var copyCall = SyntaxFactory.ObjectCreationExpression(
                typeName.WithoutTrivia(),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(SyntaxFactory.ThisExpression()))),
                null);
            method = SyntaxFactory.MethodDeclaration(
                    default,
                    modifiers,
                    typeName,
                    null,
                    SyntaxFactory.Identifier(factoryName),
                    null,
                    SyntaxFactory.ParameterList(),
                    SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                    null,
                    SyntaxFactory.ArrowExpressionClause(copyCall))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        }

        method = method
            .WithLeadingTrivia(
                SyntaxFactory.TriviaList(SyntaxFactory.ElasticCarriageReturnLineFeed))
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);

        stats.CloneFactoriesSynthesized++;
        if (options.ReportInfo)
        {
            diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Info,
                MemberDesugarDiagnosticCode.CloneFactorySynthesized,
                RecordDiagnostics.CloneFactorySynthesized(
                    plan.RecordType?.ToDisplayString() ?? factoryName, form, factoryName),
                filePath,
                decl.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                0,
                plan.RecordType?.Name));
        }

        return method;
    }

    // ---- RC-3：位置 record class reshape（决策 8a/8c/6）----

    /// <summary>
    /// 将带 ParameterList 的位置 record class 部件降形为终形：去参数表（基部件搬入主 ctor
    /// 的 <c>: base(...)</c>），每位置成员发字段/get_X/set_X，补发主 ctor、protected 复制
    /// ctor、可选 public 无参 ctor、Deconstruct。全部物料来自 plan 期 shape，运行期零语义
    /// 查询。非形状部件 / struct（RC-4）/ 已无参数表（幂等）返回 null。
    /// </summary>
    public static RecordDeclarationSyntax? TryApplyReshape(
        RecordDeclarationSyntax anchor,
        RecordDeclarationSyntax decl,
        RecordClonePlan plan,
        MemberDesugarOptions options,
        MemberDesugarStatistics stats,
        List<MemberDesugarDiagnostic> diagnostics,
        string filePath)
    {
        var shape = plan.Reshape;
        if (shape is null or { IsStruct: true }) return null;
        // 部件身份用绿色锚节点的文件绝对跨度（decl 是红色脱离节点，其跨度为子树相对值）。
        if (anchor.Identifier.SpanStart != shape.Part.Identifier.SpanStart) return null;
        if (decl.ParameterList is not { Parameters.Count: > 0 }) return null; // 幂等：已降形

        var self = BuildSelfTypeName(decl);
        var ctorName = SyntaxFactory.Identifier(decl.Identifier.ValueText);
        var newMembers = new List<MemberDeclarationSyntax>();

        // 字段/get/set 仅为本类型「自有」位置成员发射；基类转发参数只走 base(...) 转发。
        var ownMembers = shape.Members.Where(m => !m.IsInherited).ToList();
        foreach (var m in ownMembers)
        {
            var type = m.Parameter.Type!;
            var propName = m.Property.Name;
            var fieldName = m.FieldName;

            // private T x;
            newMembers.Add(SyntaxFactory.FieldDeclaration(
                    SyntaxFactory.VariableDeclaration(type,
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.VariableDeclarator(
                                SyntaxFactory.Identifier(fieldName)))))
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PrivateKeyword))));

            // public T get_X() { return this.x; }
            newMembers.Add(SyntaxFactory.MethodDeclaration(
                    type, SyntaxFactory.Identifier(AccessorsToMethods.GetterName(propName)))
                .WithModifiers(PublicModifiers())
                .WithBody(SyntaxFactory.Block(
                    SyntaxFactory.ReturnStatement(ThisField(fieldName)))));

            // public void set_X(T value) { this.x = value; }
            newMembers.Add(SyntaxFactory.MethodDeclaration(
                    PredefinedVoid(), SyntaxFactory.Identifier(AccessorsToMethods.SetterName(propName)))
                .WithModifiers(PublicModifiers())
                .WithParameterList(SyntaxFactory.ParameterList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Parameter(SyntaxFactory.Identifier("value"))
                            .WithType(type))))
                .WithBody(SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            ThisField(fieldName),
                            SyntaxFactory.IdentifierName("value"))))));
        }

        // 主 ctor：public R(T x, ...) : base(原 PrimaryConstructorBaseTypeSyntax 实参) { this.x = x; }
        // 参数绿节点整体复用（默认值/特性/params 原样），标识符引用取原始 Text（保 @ 转义）。
        var primaryParameters = SyntaxFactory.SeparatedList(
            shape.Members.Select(m => (ParameterSyntax)m.Parameter));
        var primaryBody = SyntaxFactory.Block(ownMembers.Select(m =>
            SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                ThisField(m.FieldName),
                SyntaxFactory.IdentifierName(m.Parameter.Identifier.Text)))));
        ConstructorInitializerSyntax? primaryInitializer = null;
        if (shape.PrimaryBase != null)
        {
            primaryInitializer = SyntaxFactory.ConstructorInitializer(
                SyntaxKind.BaseConstructorInitializer,
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SeparatedList(
                        shape.PrimaryBase.ArgumentList.Arguments
                            .Select(a => SyntaxFactory.Argument(a.Expression)))));
        }
        newMembers.Add(SyntaxFactory.ConstructorDeclaration(ctorName)
            .WithModifiers(PublicModifiers())
            .WithParameterList(SyntaxFactory.ParameterList(primaryParameters))
            .WithInitializer(primaryInitializer)
            .WithBody(primaryBody));

        // protected 复制 ctor：基类是 record（合成/显式复制 ctor 保证存在且可从派生访问）
        // 时强制 : base(other)；object 基类不发 initializer。
        var baseType = shape.Type.BaseType;
        var hasRecordBase = baseType is { IsRecord: true }
            && baseType.SpecialType != SpecialType.System_Object;
        // 复制 ctor 体只拷贝本类型自有字段；基类成员由 : base(other) 链式拷贝。
        var copyBody = SyntaxFactory.Block(ownMembers.Select(m =>
            SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                ThisField(m.FieldName),
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("other"),
                    SyntaxFactory.Token(SyntaxKind.DotToken),
                    SyntaxFactory.IdentifierName(m.FieldName))))));
        var copyCtor = SyntaxFactory.ConstructorDeclaration(ctorName)
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.ProtectedKeyword)))
            .WithParameterList(SyntaxFactory.ParameterList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier("other")).WithType(self))))
            .WithBody(copyBody);
        if (hasRecordBase)
        {
            copyCtor = copyCtor.WithInitializer(SyntaxFactory.ConstructorInitializer(
                SyntaxKind.BaseConstructorInitializer,
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName("other"))))));
        }
        newMembers.Add(copyCtor);

        // public R() {}（基类无可访问无参 ctor 时物料已判不发）。
        if (shape.EmitParameterlessCtor)
        {
            newMembers.Add(SyntaxFactory.ConstructorDeclaration(ctorName)
                .WithModifiers(PublicModifiers())
                .WithParameterList(SyntaxFactory.ParameterList())
                .WithBody(SyntaxFactory.Block()));
        }

        // public void Deconstruct(out T X, ...) { X = get_X(); ... }
        // out 参数名保持源位置属性名（隐式合成 Deconstruct 的命名约定，兼容命名实参）。
        // 含基类全部位置参数（与隐式合成派生 Deconstruct 同形）：自有成员读本类型
        // get_X，转发成员读基类 reshape 后继承来的 get_X；派生层加 new 隐藏基类同形方法。
        var deconstructModifiers = PublicModifiers();
        if (hasRecordBase)
            deconstructModifiers = deconstructModifiers.Add(SyntaxFactory.Token(SyntaxKind.NewKeyword));
        newMembers.Add(SyntaxFactory.MethodDeclaration(
                PredefinedVoid(), SyntaxFactory.Identifier("Deconstruct"))
            .WithModifiers(deconstructModifiers)
            .WithParameterList(SyntaxFactory.ParameterList(
                SyntaxFactory.SeparatedList(shape.Members.Select(m =>
                    (ParameterSyntax)SyntaxFactory.Parameter(
                            SyntaxFactory.Identifier(m.Property.Name))
                        .WithModifiers(SyntaxFactory.TokenList(
                            SyntaxFactory.Token(SyntaxKind.OutKeyword)))
                        .WithType(m.Parameter.Type!)))))
            .WithBody(SyntaxFactory.Block(shape.Members.Select(m =>
                SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(m.Property.Name),
                    SyntaxFactory.InvocationExpression(
                        SyntaxFactory.IdentifierName(AccessorsToMethods.GetterName(m.Property.Name)))))))));

        // 去 ParameterList；PrimaryConstructorBaseTypeSyntax 搬入 ctor initializer 后从
        // BaseList 移除（其余接口/基类保留；BaseList 空了则整删）。
        var reshaped = decl.WithParameterList(null);
        if (shape.PrimaryBase != null)
        {
            // 参数实参已搬入主 ctor 的 : base(...)；继承关系必须保留——把主 ctor 基部件
            // （base.Visit 后可能是红节点副本）就地降级为无参普通基类 : Point。
            var remaining = reshaped.BaseList!.Types
                .Select(t => t is PrimaryConstructorBaseTypeSyntax primary
                    ? (BaseTypeSyntax)SyntaxFactory.SimpleBaseType(primary.Type)
                    : t)
                .ToList();
            reshaped = reshaped.WithBaseList(SyntaxFactory.BaseList(
                SyntaxFactory.SeparatedList<BaseTypeSyntax>(remaining)));
        }
        // 无体 record（record R(...);）补花括号后才能 AddMembers。
        if (!reshaped.OpenBraceToken.IsKind(SyntaxKind.OpenBraceToken))
        {
            reshaped = reshaped
                .WithSemicolonToken(default)
                .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken))
                .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken));
        }
        reshaped = reshaped.AddMembers(newMembers
            .Select(m => m.NormalizeWhitespace()
                .WithLeadingTrivia(SyntaxFactory.TriviaList(
                    SyntaxFactory.ElasticCarriageReturnLineFeed))
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation))
            .ToArray());

        stats.PositionalRecordsReshaped++;
        if (options.ReportInfo)
        {
            var line = anchor.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Info,
                MemberDesugarDiagnosticCode.PositionalReshaped,
                RecordDiagnostics.PositionalReshaped(
                    shape.Type.ToDisplayString(), shape.Members.Count, shape.IsStruct),
                filePath, line, 0, shape.Type.Name));
        }
        return reshaped;
    }

    private static SyntaxTokenList PublicModifiers()
        => SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword));

    private static PredefinedTypeSyntax PredefinedVoid()
        => SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword));

    /// <summary><c>this.field</c> 成员访问（field 名已是契约转义名）。</summary>
    private static MemberAccessExpressionSyntax ThisField(string fieldName)
        => SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.ThisExpression(),
            SyntaxFactory.Token(SyntaxKind.DotToken),
            SyntaxFactory.IdentifierName(fieldName));

    /// <summary>工厂修饰符（决策 3）：abstract/virtual/override/非虚（密封）。</summary>
    private static SyntaxTokenList BuildModifiers(RecordFactoryForm form)
    {
        var kind = form switch
        {
            RecordFactoryForm.Abstract => SyntaxKind.AbstractKeyword,
            RecordFactoryForm.Virtual => SyntaxKind.VirtualKeyword,
            RecordFactoryForm.Override => SyntaxKind.OverrideKeyword,
            _ => (SyntaxKind?)null,
        };
        var tokens = new List<SyntaxToken> { SyntaxFactory.Token(SyntaxKind.PublicKeyword) };
        if (kind is { } k) tokens.Add(SyntaxFactory.Token(k));
        return SyntaxFactory.TokenList(tokens);
    }

    /// <summary>本类型在自身体内的自命名类型：带类型形参（Box&lt;T&gt;），无需任何限定。</summary>
    private static TypeSyntax BuildSelfTypeName(RecordDeclarationSyntax decl)
    {
        var identifier = SyntaxFactory.Identifier(decl.Identifier.ValueText);
        if (decl.TypeParameterList is not { Parameters.Count: > 0 } typeParameters)
            return SyntaxFactory.IdentifierName(identifier);

        var args = typeParameters.Parameters
            .Select(p => (TypeSyntax)SyntaxFactory.IdentifierName(
                SyntaxFactory.Identifier(p.Identifier.ValueText)));
        return SyntaxFactory.GenericName(
            identifier,
            SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList(args)));
    }
}
