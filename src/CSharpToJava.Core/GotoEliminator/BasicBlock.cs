using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GotoEliminator;

internal enum BlockExit { FallThrough, Goto, ConditionalGoto, Return, Break }

internal sealed class BasicBlock
{
    public int Index { get; init; }
    public string? Label { get; set; }
    public List<StatementSyntax> Statements { get; } = new();
    public BlockExit Exit { get; set; } = BlockExit.FallThrough;
    public int? FallThroughTarget { get; set; }
}
