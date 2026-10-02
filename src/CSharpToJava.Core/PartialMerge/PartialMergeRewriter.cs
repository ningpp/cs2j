using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.PartialMerge;

/// <summary>
/// Applies a file's edits as deterministic TextChanges (design §4.2 point 9):
/// edits are ordered by descending span so earlier offsets stay valid; removal
/// leaves exactly one LF-terminated provenance comment. No timestamps/GUIDs are
/// ever emitted.
/// </summary>
internal sealed class PartialMergeRewriter
{
    public string Apply(string source, IReadOnlyList<FileEdit> edits)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest));

        var changes = edits
            .OrderByDescending(edit => edit.Span.Start)
            .Select(edit => edit switch
            {
                ReplaceTypeEdit replace => new TextChange(
                    new TextSpan(replace.Span.Start, replace.Span.Length), replace.Replacement),
                RemoveTypeEdit remove => new TextChange(
                    new TextSpan(remove.Span.Start, remove.Span.Length), remove.Comment + "\n"),
                InsertUsingsEdit insert => new TextChange(
                    new TextSpan(insert.InsertionPosition, 0), insert.UsingsText),
                _ => throw new InvalidOperationException(
                    $"Unknown edit kind: {edit.GetType().Name}"),
            })
            .ToList();

        return syntaxTree.GetText().WithChanges(changes).ToString();
    }
}
