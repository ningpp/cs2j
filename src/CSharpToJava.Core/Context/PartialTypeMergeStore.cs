using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.PartialType;

namespace CSharpToJava.Core.Context;

/// <summary>
/// Tracks merged partial types during project-level conversion.
/// Extracted from ConversionContext.
/// </summary>
public class PartialTypeMergeStore
{
    private readonly Dictionary<string, MergedTypeDeclaration> _mergedTypes = new();
    private readonly Dictionary<string, string> _syntaxNodeMap = new();

    public void Register(MergedTypeDeclaration mergedType)
    {
        _mergedTypes[mergedType.TypeSymbol.Name] = mergedType;
        foreach (var syntaxNode in mergedType.OriginalSyntaxNodes)
        {
            var key = GetSyntaxNodeKey(syntaxNode);
            _syntaxNodeMap[key] = mergedType.TypeSymbol.Name;
        }
    }

    public bool IsMerged(TypeDeclarationSyntax syntaxNode)
    {
        return _syntaxNodeMap.ContainsKey(GetSyntaxNodeKey(syntaxNode));
    }

    public MergedTypeDeclaration? Get(string typeName)
    {
        return _mergedTypes.GetValueOrDefault(typeName);
    }

    public IReadOnlyList<MergedTypeDeclaration> GetAll()
    {
        return _mergedTypes.Values.ToList();
    }

    public void Clear()
    {
        _mergedTypes.Clear();
        _syntaxNodeMap.Clear();
    }

    private static string GetSyntaxNodeKey(TypeDeclarationSyntax syntaxNode)
    {
        var location = syntaxNode.SyntaxTree.FilePath;
        var span = syntaxNode.Span;
        return $"{location}:{span.Start}:{span.Length}";
    }
}
