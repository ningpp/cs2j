using System.Text;
using Microsoft.CodeAnalysis;
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
        // The edits are pure text splices, so the text is wrapped directly instead
        // of being parsed: parsing a whole file only to reach its SourceText would
        // re-do work the candidate compilation's parse already covers.
        var text = SourceText.From(source, Encoding.UTF8);

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

        return text.WithChanges(changes).ToString();
    }
}
