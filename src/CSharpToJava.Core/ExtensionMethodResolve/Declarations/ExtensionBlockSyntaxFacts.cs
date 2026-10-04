using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve.Declarations;

/// <summary>
/// Syntax facts for C#14 <c>extension</c> blocks. Detection uses BOTH
/// <see cref="SyntaxKind.ExtensionBlockDeclaration"/> and the reflection type
/// name <c>ExtensionBlockDeclarationSyntax</c> (probe-proven dual guard), so a
/// bound Roslyn that reuses the kind value cannot cause a false positive.
/// Typed member access still goes through the real syntax type (pinned on
/// Roslyn 5.0.0 GA).
/// </summary>
internal static class ExtensionBlockSyntaxFacts
{
    public const string BlockSyntaxTypeName = "ExtensionBlockDeclarationSyntax";

    /// <summary>True when <paramref name="node"/> is a C#14 extension block (dual detection).</summary>
    public static bool IsExtensionBlock(SyntaxNode? node)
        => node is not null
           && node.RawKind == (int)SyntaxKind.ExtensionBlockDeclaration
           && node.GetType().Name == BlockSyntaxTypeName;

    /// <summary>Typed block node when the dual detection passes; null otherwise.</summary>
    public static ExtensionBlockDeclarationSyntax? AsBlock(SyntaxNode? node)
        => IsExtensionBlock(node) ? (ExtensionBlockDeclarationSyntax)node! : null;

    /// <summary>Enumerate every extension block in <paramref name="root"/>.</summary>
    public static IEnumerable<ExtensionBlockDeclarationSyntax> EnumerateBlocks(SyntaxNode root)
        => root.DescendantNodes().Where(IsExtensionBlock)
            .Cast<ExtensionBlockDeclarationSyntax>();

    /// <summary>True when <paramref name="node"/> lexically sits inside an extension block.</summary>
    public static bool IsInsideBlock(SyntaxNode node)
        => node.Ancestors().Any(IsExtensionBlock);

    /// <summary>Enclosing extension block of <paramref name="node"/>; null when outside.</summary>
    public static ExtensionBlockDeclarationSyntax? EnclosingBlock(SyntaxNode node)
        => node.Ancestors().Select(AsBlock).FirstOrDefault(b => b is not null);

    /// <summary>The receiver parameter (first parameter of the block ParameterList).</summary>
    public static ParameterSyntax? GetReceiverParameter(ExtensionBlockDeclarationSyntax block)
        => block.ParameterList?.Parameters.FirstOrDefault();

    /// <summary>Block-level type parameter list (<c>extension&lt;T&gt;(T v)</c>); null for non-generic blocks.</summary>
    public static TypeParameterListSyntax? GetBlockTypeParameterList(ExtensionBlockDeclarationSyntax block)
        => block.TypeParameterList;

    /// <summary>Block-level generic constraint clauses.</summary>
    public static SyntaxList<TypeParameterConstraintClauseSyntax> GetBlockConstraints(
        ExtensionBlockDeclarationSyntax block)
        => block.ConstraintClauses;

    /// <summary>Block member declarations.</summary>
    public static SyntaxList<MemberDeclarationSyntax> GetMembers(ExtensionBlockDeclarationSyntax block)
        => block.Members;

    /// <summary>True when the member is an indexer declared inside an extension block.</summary>
    public static bool IsBlockIndexer(MemberDeclarationSyntax member)
        => member is IndexerDeclarationSyntax && EnclosingBlock(member) is not null;
}
