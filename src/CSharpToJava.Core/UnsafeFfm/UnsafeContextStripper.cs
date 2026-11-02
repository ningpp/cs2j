using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// R1：unsafe 上下文剥除（设计 D4）。
/// <list type="bullet">
/// <item><see cref="UnsafeStatementSyntax"/> 退化为其内部 Block（嵌套递归剥除，
/// 语句逐条保留）；</item>
/// <item>一切可带 unsafe 修饰符的声明——class/struct/interface/record、方法、
/// 字段（含 event field）、构造函数、局部函数，以及终结器/运算符/转换运算符/
/// 属性/索引器/事件声明——剥除 unsafe 修饰符。</item>
/// <item>剥除 unsafe 后失去编译许可的 <c>sizeof(非预定义尺寸 struct)</c>
/// （CS0233）改写为安全上下文可用的
/// <c>System.Runtime.CompilerServices.Unsafe.SizeOf&lt;T&gt;()</c>，返回值
/// 与 sizeof 同为 int；基元/枚举等预定义尺寸类型保持 sizeof 原样。</item>
/// </list>
/// unsafe 仅是编译许可、不产生运行时语义，故剥除无语义变化。剥除计数实时
/// 写入调用方提供的 <see cref="UnsafeFfmStatistics"/>。输出不含任何
/// <see cref="UnsafeStatementSyntax"/> 或 unsafe keyword token，天然幂等。
/// </summary>
internal sealed class UnsafeContextStripper : CSharpSyntaxRewriter
{
    private readonly UnsafeFfmStatistics _statistics;
    private readonly SemanticModel _semanticModel;

    public UnsafeContextStripper(UnsafeFfmStatistics statistics, SemanticModel semanticModel)
    {
        _statistics = statistics;
        _semanticModel = semanticModel;
    }

    // ---- unsafe 块 ----

    public override SyntaxNode? VisitUnsafeStatement(UnsafeStatementSyntax node)
    {
        // 先递归：内层嵌套 unsafe 块/unsafe 声明在本层退化之前处理完。
        var visited = (UnsafeStatementSyntax)base.VisitUnsafeStatement(node)!;

        _statistics.UnsafeBlocksStripped++;

        // 把 unsafe keyword 的 trivia（缩进/换行/注释）并入 Block 前导，
        // 保留源码排版；格式化阶段会再统一整理。
        var block = visited.Block;
        var mergedLeading = visited.UnsafeKeyword.LeadingTrivia
            .Concat(visited.UnsafeKeyword.TrailingTrivia)
            .Concat(block.GetLeadingTrivia());
        return block.WithLeadingTrivia(mergedLeading);
    }

    // ---- sizeof(非预定义尺寸 struct) → Unsafe.SizeOf<T>() ----

    public override SyntaxNode? VisitSizeOfExpression(SizeOfExpressionSyntax node)
    {
        var visited = (SizeOfExpressionSyntax)base.VisitSizeOfExpression(node)!;

        // 可编译输入中的 sizeof(非基元类型) 必然位于 unsafe 上下文；剥除
        // unsafe 后该表达式不再有编译许可（CS0233）。struct 改写成安全
        // 上下文可用的 Unsafe.SizeOf<T>()（同为 int 结果）。
        if (!NeedsSafeSizeOf(node.Type)) return visited;

        _statistics.UnsafeSizeOfExpressionsRewritten++;
        var invocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.ParseExpression("global::System.Runtime.CompilerServices.Unsafe"),
                SyntaxFactory.GenericName(
                    SyntaxFactory.Identifier("SizeOf"),
                    SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SingletonSeparatedList(visited.Type.WithoutTrivia())))));
        return invocation.WithTriviaFrom(visited);
    }

    /// <summary>
    /// 仅非预定义尺寸的 struct 需要改写：基元/枚举（含 decimal）的 sizeof
    /// 在安全上下文本来就合法；指针/函数指针/类型参数不是 INamedTypeSymbol。
    /// IntPtr/nint 等虽为 SpecialType 但无预定义尺寸，同样需要改写。
    /// </summary>
    private bool NeedsSafeSizeOf(TypeSyntax typeSyntax)
    {
        var type = _semanticModel.GetTypeInfo(typeSyntax).Type;
        if (type is not INamedTypeSymbol named) return false;
        if (named.TypeKind is TypeKind.Enum or not TypeKind.Struct) return false;
        return !HasPredefinedSize(named.SpecialType);
    }

    private static bool HasPredefinedSize(SpecialType specialType) =>
        specialType is SpecialType.System_Boolean
            or SpecialType.System_SByte
            or SpecialType.System_Byte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64
            or SpecialType.System_Char
            or SpecialType.System_Single
            or SpecialType.System_Double
            or SpecialType.System_Decimal;

    // ---- 类型声明（class/struct/interface/record）----

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
        => StripType(base.VisitClassDeclaration(node));

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
        => StripType(base.VisitStructDeclaration(node));

    public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        => StripType(base.VisitInterfaceDeclaration(node));

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
        => StripType(base.VisitRecordDeclaration(node));

    // WithModifiers 在具体类型上以虚方法返回同运行时类型的 TypeDeclarationSyntax。
    private SyntaxNode? StripType(SyntaxNode? visited)
    {
        if (visited is not TypeDeclarationSyntax node) return visited;
        if (!HasUnsafeModifier(node.Modifiers)) return node;
        _statistics.UnsafeTypeModifiersStripped++;
        return node.WithModifiers(RemoveUnsafe(node.Modifiers));
    }

    // ---- 方法 / 构造 / 局部函数 / 终结器 / 运算符 ----

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        if (!HasUnsafeModifier(visited.Modifiers)) return visited;
        _statistics.UnsafeMethodModifiersStripped++;
        return visited.WithModifiers(RemoveUnsafe(visited.Modifiers));
    }

    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var visited = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;
        if (!HasUnsafeModifier(visited.Modifiers)) return visited;
        _statistics.UnsafeConstructorModifiersStripped++;
        return visited.WithModifiers(RemoveUnsafe(visited.Modifiers));
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
        if (!HasUnsafeModifier(visited.Modifiers)) return visited;
        _statistics.UnsafeLocalFunctionModifiersStripped++;
        return visited.WithModifiers(RemoveUnsafe(visited.Modifiers));
    }

    public override SyntaxNode? VisitDestructorDeclaration(DestructorDeclarationSyntax node)
    {
        var visited = (DestructorDeclarationSyntax)base.VisitDestructorDeclaration(node)!;
        if (!HasUnsafeModifier(visited.Modifiers)) return visited;
        _statistics.UnsafeOtherModifiersStripped++;
        return visited.WithModifiers(RemoveUnsafe(visited.Modifiers));
    }

    public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node)
    {
        var visited = (OperatorDeclarationSyntax)base.VisitOperatorDeclaration(node)!;
        if (!HasUnsafeModifier(visited.Modifiers)) return visited;
        _statistics.UnsafeOtherModifiersStripped++;
        return visited.WithModifiers(RemoveUnsafe(visited.Modifiers));
    }

    public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
    {
        var visited = (ConversionOperatorDeclarationSyntax)base.VisitConversionOperatorDeclaration(node)!;
        if (!HasUnsafeModifier(visited.Modifiers)) return visited;
        _statistics.UnsafeOtherModifiersStripped++;
        return visited.WithModifiers(RemoveUnsafe(visited.Modifiers));
    }

    // ---- 字段（含 event field）----

    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        var visited = (FieldDeclarationSyntax)base.VisitFieldDeclaration(node)!;
        if (!HasUnsafeModifier(visited.Modifiers)) return visited;
        _statistics.UnsafeFieldModifiersStripped++;
        return visited.WithModifiers(RemoveUnsafe(visited.Modifiers));
    }

    public override SyntaxNode? VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
    {
        var visited = (EventFieldDeclarationSyntax)base.VisitEventFieldDeclaration(node)!;
        if (!HasUnsafeModifier(visited.Modifiers)) return visited;
        _statistics.UnsafeFieldModifiersStripped++;
        return visited.WithModifiers(RemoveUnsafe(visited.Modifiers));
    }

    // ---- 属性 / 索引器 / 事件声明 ----

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var visited = (PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!;
        if (!HasUnsafeModifier(visited.Modifiers)) return visited;
        _statistics.UnsafeOtherModifiersStripped++;
        return visited.WithModifiers(RemoveUnsafe(visited.Modifiers));
    }

    public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node)
    {
        var visited = (IndexerDeclarationSyntax)base.VisitIndexerDeclaration(node)!;
        if (!HasUnsafeModifier(visited.Modifiers)) return visited;
        _statistics.UnsafeOtherModifiersStripped++;
        return visited.WithModifiers(RemoveUnsafe(visited.Modifiers));
    }

    public override SyntaxNode? VisitEventDeclaration(EventDeclarationSyntax node)
    {
        var visited = (EventDeclarationSyntax)base.VisitEventDeclaration(node)!;
        if (!HasUnsafeModifier(visited.Modifiers)) return visited;
        _statistics.UnsafeOtherModifiersStripped++;
        return visited.WithModifiers(RemoveUnsafe(visited.Modifiers));
    }

    // ---- 共用辅助 ----

    private static bool HasUnsafeModifier(SyntaxTokenList modifiers)
    {
        foreach (var token in modifiers)
        {
            if (token.IsKind(SyntaxKind.UnsafeKeyword)) return true;
        }
        return false;
    }

    /// <summary>
    /// 从修饰符表移除 unsafe keyword；其 trivia 顺延并入下一个保留修饰符的前导
    /// （unsafe 为末位时并入前一修饰符尾部），避免丢失注释。
    /// </summary>
    private static SyntaxTokenList RemoveUnsafe(SyntaxTokenList modifiers)
    {
        var kept = new List<SyntaxToken>();
        var pendingTrivia = new List<SyntaxTrivia>();

        for (var i = 0; i < modifiers.Count; i++)
        {
            var token = modifiers[i];
            if (token.IsKind(SyntaxKind.UnsafeKeyword))
            {
                pendingTrivia.AddRange(token.LeadingTrivia);
                pendingTrivia.AddRange(token.TrailingTrivia);
                continue;
            }

            if (pendingTrivia.Count > 0)
            {
                token = token.WithLeadingTrivia(pendingTrivia.Concat(token.LeadingTrivia));
                pendingTrivia.Clear();
            }

            kept.Add(token);
        }

        if (pendingTrivia.Count > 0 && kept.Count > 0)
        {
            kept[^1] = kept[^1].WithTrailingTrivia(
                kept[^1].TrailingTrivia.Concat(pendingTrivia));
        }

        return SyntaxFactory.TokenList(kept);
    }
}
