using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// R5 配对索引展开的过载碰撞消歧：指针重载按「T[] + int 索引」重建形参表后，
/// 可能与同类型既有数组重载出现完全相同的参数类型序列（真实 SDK：
/// XmlTextReaderImpl.AdjustLineInfo 的 <c>(char*, int, bool, ref LineInfo)</c>
/// 展开为 <c>(char[], int, int, bool, ByRef&lt;LineInfo&gt;)</c>，与既有
/// <c>(char[], int, int, bool, ByRef&lt;LineInfo&gt;)</c> 重载撞名 → CS0111，
/// 调用点随之 CS0121）。在 R5 改写后的树上按「类型 + 方法名 + 规范化形参类型
/// 串」分组检测碰撞，碰撞组内优先保留未经指针改写的成员原名，其余（指针改写
/// 产物）追加 Ptr/Ptr2… 后缀改名；返回「原始方法符号 → 新名」映射，供 R6 在
/// 调用点按符号同步改名。跨文件 partial 类型的碰撞不在单文件扫描范围内。
/// </summary>
internal static class OverloadCollisionDisambiguator
{
    public static (CompilationUnitSyntax Root, IReadOnlyDictionary<IMethodSymbol, string> Renames)
        Disambiguate(
            CompilationUnitSyntax rewrittenRoot,
            CompilationUnitSyntax originalRoot,
            SemanticModel semanticModel)
    {
        var renames = new Dictionary<IMethodSymbol, string>(SymbolEqualityComparer.Default);
        var renamedDeclarations = new Dictionary<MethodDeclarationSyntax, string>();

        // 类型按文档遍历顺序配对（R5 不增删类型声明）；类型节点整体不等价
        // （签名已改写），不能走 OriginalSyntaxResolver 的结构等价映射。
        var originalTypes = originalRoot.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();
        var rewrittenTypes = rewrittenRoot.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();
        if (originalTypes.Count != rewrittenTypes.Count)
        {
            return (rewrittenRoot, renames);
        }

        for (var typeIndex = 0; typeIndex < rewrittenTypes.Count; typeIndex++)
        {
            var rewrittenType = rewrittenTypes[typeIndex];
            var originalType = originalTypes[typeIndex];
            if (rewrittenType.Members.Count != originalType.Members.Count) continue;

            // 成员顺序在 R5 前后保持一致（签名改写只替换节点、不增删成员），
            // 按成员索引对齐改写后与原始方法声明。
            var rewrittenMethods = rewrittenType.Members
                .Select((m, i) => (Method: m as MethodDeclarationSyntax, Index: i))
                .Where(x => x.Method != null)
                .ToList();

            var groups = rewrittenMethods
                .GroupBy(x => (
                    x.Method!.Identifier.ValueText,
                    Signature: NormalizeSignature(x.Method.ParameterList)))
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var group in groups)
            {
                var members = group
                    .Select(x => (
                        Rewritten: x.Method!,
                        Original: originalType.Members[x.Index] as MethodDeclarationSyntax))
                    .Where(x => x.Original != null)
                    .ToList();

                var rewrittenOnes = members
                    .Where(m => HasPointerShape(m.Original!))
                    .ToList();
                if (rewrittenOnes.Count == 0) continue;
                var keepCount = members.Count - rewrittenOnes.Count;
                // 组内既有未改写成员时保留其原名，否则保留改写组中的第一个
                var toRename = rewrittenOnes.Skip(keepCount == 0 ? 1 : 0).ToList();

                foreach (var member in toRename)
                {
                    var newName = PickNewName(rewrittenType, member.Rewritten.Identifier.ValueText);
                    renames[semanticModel.GetDeclaredSymbol(member.Original!)!] = newName;
                    renamedDeclarations[member.Rewritten] = newName;
                }
            }
        }

        if (renamedDeclarations.Count == 0)
        {
            return (rewrittenRoot, renames);
        }

        var newRoot = rewrittenRoot.ReplaceNodes(
            renamedDeclarations.Keys,
            (original, _) => renamedDeclarations[original] is { } name
                ? ((MethodDeclarationSyntax)original).WithIdentifier(
                    Identifier(original.GetLeadingTrivia(), name, original.GetTrailingTrivia()))
                : original);
        return (newRoot, renames);
    }

    /// <summary>在类型内取一个未被占用的方法名：base → basePtr → basePtr2 …</summary>
    private static string PickNewName(TypeDeclarationSyntax type, string baseName)
    {
        var occupied = type.Members
            .OfType<MethodDeclarationSyntax>()
            .Select(m => m.Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);

        var candidate = baseName + "Ptr";
        for (var suffix = 2; occupied.Contains(candidate); suffix++)
        {
            candidate = baseName + "Ptr" + suffix;
        }
        return candidate;
    }

    /// <summary>规范化形参类型串：逐参数取类型文本并剔除全部空白。</summary>
    private static string NormalizeSignature(ParameterListSyntax parameterList)
        => string.Join("|", parameterList.Parameters.Select(p =>
            p.Type?.ToString()?.Replace(" ", string.Empty).Replace("\t", string.Empty) ?? string.Empty));

    /// <summary>原始方法是否带指针形态（指针形参或指针返回）——即 R5 的改写对象。</summary>
    private static bool HasPointerShape(MethodDeclarationSyntax method)
        => method.ParameterList.Parameters.Any(p => p.Type is PointerTypeSyntax)
           || method.ReturnType is PointerTypeSyntax;
}
