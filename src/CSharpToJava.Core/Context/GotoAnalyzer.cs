using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Context;

/// <summary>
/// Classification of a goto statement's relationship to its target label.
/// </summary>
public enum GotoScopeClassification
{
    /// <summary>A1: goto is inside the labeled loop (e.g. outer: for(){ if(x) goto outer; })</summary>
    InScopeLoop,

    /// <summary>A2: goto is inside the labeled block/other (e.g. target: { if(x) goto target; })</summary>
    InScopeBlock,

    /// <summary>B1: same method body, backward goto to a loop label (e.g. outer: for(){} if(x) goto outer;)</summary>
    SameBodyLoop,

    /// <summary>B2/B3: same method body, goto to a non-loop label (forward or backward)</summary>
    SameBodyOther,

    /// <summary>C: cross-scope goto (target is in a different nested scope)</summary>
    CrossScope,

    /// <summary>D: goto target label is a sibling of the enclosing loop (immediately after it),
    /// equivalent to break; from that loop. No state machine needed.</summary>
    BreakFromEnclosingLoop
}

/// <summary>
/// Exit type for a basic block.
/// </summary>
public enum BlockExit
{
    /// <summary>Sequential execution falls through to the next block.</summary>
    FallThrough,

    /// <summary>Unconditional goto to another block.</summary>
    Goto,

    /// <summary>Conditional goto (if with goto) + fall-through.</summary>
    ConditionalGoto,

    /// <summary>return/throw statement.</summary>
    Return,

    /// <summary>break/continue (non-goto) statement.</summary>
    Break
}

/// <summary>
/// Represents a basic block in the method body - a sequence of statements
/// with a single entry point and a single exit point.
/// </summary>
public sealed class BasicBlock
{
    public int Index { get; init; }
    public string? Label { get; set; }
    public List<StatementSyntax> Statements { get; } = new();
    public BlockExit Exit { get; set; } = BlockExit.FallThrough;
    public int? FallThroughTarget { get; set; }
}

/// <summary>
/// Information about a variable that needs to be hoisted out of the state machine loop.
/// </summary>
public sealed class HoistedVariable
{
    public string JavaType { get; init; } = "";
    public string Name { get; init; } = "";
    public string DefaultValue { get; init; } = "";
}

/// <summary>
/// Information about a goto statement and its classification.
/// </summary>
public record GotoInfo(
    GotoStatementSyntax GotoStatement,
    string TargetLabel,
    GotoScopeClassification Classification);

/// <summary>
/// Result of analyzing goto statements in a method body.
/// Provides basic block splitting, goto classification, and variable hoisting information.
/// </summary>
public class GotoAnalyzer
{
    private readonly List<GotoInfo> _allGotos = new();
    private readonly List<BasicBlock> _basicBlocks = new();
    private readonly Dictionary<string, int> _labelToBlockIndex = new(StringComparer.Ordinal);
    private readonly List<HoistedVariable> _hoistedVariables = new();

    /// <summary>
    /// All goto statements found in the method body with their classifications.
    /// </summary>
    public IReadOnlyList<GotoInfo> AllGotos => _allGotos;

    /// <summary>
    /// Basic blocks resulting from splitting the method body.
    /// Only populated when <see cref="NeedsStateMachine"/> is true.
    /// </summary>
    public IReadOnlyList<BasicBlock> BasicBlocks => _basicBlocks;

    /// <summary>
    /// Maps label names to their basic block indices.
    /// Only populated when <see cref="NeedsStateMachine"/> is true.
    /// </summary>
    public IReadOnlyDictionary<string, int> LabelToBlockIndex => _labelToBlockIndex;

    /// <summary>
    /// Variables that need to be hoisted out of the state machine loop.
    /// Only populated when <see cref="NeedsStateMachine"/> is true.
    /// </summary>
    public IReadOnlyList<HoistedVariable> HoistedVariables => _hoistedVariables;

    /// <summary>
    /// True if all gotos are in-scope (A class) - no state machine needed.
    /// </summary>
    public bool HasOnlyInScopeGotos =>
        _allGotos.Count == 0 || _allGotos.All(g =>
            g.Classification == GotoScopeClassification.InScopeLoop ||
            g.Classification == GotoScopeClassification.InScopeBlock ||
            g.Classification == GotoScopeClassification.BreakFromEnclosingLoop);

    /// <summary>
    /// True if a state machine is needed (B1/B2/B3 or C class gotos exist).
    /// B1 (SameBodyLoop) requires a state machine because the goto is outside the loop,
    /// so Java's continue label is not valid there.
    /// </summary>
    public bool NeedsStateMachine =>
        _allGotos.Any(g =>
            g.Classification == GotoScopeClassification.SameBodyLoop ||
            g.Classification == GotoScopeClassification.SameBodyOther ||
            g.Classification == GotoScopeClassification.CrossScope);

    /// <summary>
    /// True if any cross-scope goto exists (C class).
    /// </summary>
    public bool HasCrossScopeGoto =>
        _allGotos.Any(g => g.Classification == GotoScopeClassification.CrossScope);

    /// <summary>
    /// Gets all labels that are targets of state-machine-requiring gotos.
    /// </summary>
    public IReadOnlySet<string> StateMachineLabels =>
        _allGotos
            .Where(g => g.Classification == GotoScopeClassification.SameBodyLoop ||
                        g.Classification == GotoScopeClassification.SameBodyOther ||
                        g.Classification == GotoScopeClassification.CrossScope)
            .Select(g => g.TargetLabel)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Analyzes a method body for goto patterns and produces basic block splitting.
    /// </summary>
    public static GotoAnalyzer Analyze(BlockSyntax methodBody, LabelRegistry labelRegistry)
    {
        var analyzer = new GotoAnalyzer();
        analyzer.ClassifyAllGotos(methodBody, labelRegistry);

        if (analyzer.NeedsStateMachine)
        {
            analyzer.SplitIntoBasicBlocks(FlattenStateMachineStatements(methodBody.Statements));
        }

        return analyzer;
    }

    // Goto classification

    private void ClassifyAllGotos(BlockSyntax methodBody, LabelRegistry labelRegistry)
    {
        ClassifyGotosInStatements(methodBody.Statements, labelRegistry);
    }

    private void ClassifyGotosInStatements(SyntaxList<StatementSyntax> statements, LabelRegistry labelRegistry)
    {
        foreach (var stmt in statements)
        {
            ClassifyGotosInStatement(stmt, labelRegistry);
        }
    }

    private void ClassifyGotosInStatement(StatementSyntax statement, LabelRegistry labelRegistry)
    {
        switch (statement)
        {
            case LabeledStatementSyntax labeled:
                ClassifyGotosInStatement(labeled.Statement, labelRegistry);
                break;

            case GotoStatementSyntax gotoStmt:
                ClassifySingleGoto(gotoStmt, labelRegistry);
                break;

            case BlockSyntax blockStmt:
                ClassifyGotosInStatements(blockStmt.Statements, labelRegistry);
                break;

            case IfStatementSyntax ifStmt:
                ClassifyGotosInStatement(ifStmt.Statement, labelRegistry);
                if (ifStmt.Else != null)
                    ClassifyGotosInStatement(ifStmt.Else.Statement, labelRegistry);
                break;

            case WhileStatementSyntax whileStmt:
                ClassifyGotosInStatement(whileStmt.Statement, labelRegistry);
                break;

            case ForStatementSyntax forStmt:
                ClassifyGotosInStatement(forStmt.Statement, labelRegistry);
                break;

            case ForEachStatementSyntax foreachStmt:
                ClassifyGotosInStatement(foreachStmt.Statement, labelRegistry);
                break;

            case DoStatementSyntax doStmt:
                ClassifyGotosInStatement(doStmt.Statement, labelRegistry);
                break;

            case SwitchStatementSyntax switchStmt:
                foreach (var section in switchStmt.Sections)
                    ClassifyGotosInStatements(section.Statements, labelRegistry);
                break;

            case TryStatementSyntax tryStmt:
                ClassifyGotosInStatement(tryStmt.Block, labelRegistry);
                foreach (var catchClause in tryStmt.Catches)
                    ClassifyGotosInStatement(catchClause.Block, labelRegistry);
                if (tryStmt.Finally != null)
                    ClassifyGotosInStatement(tryStmt.Finally.Block, labelRegistry);
                break;

            case UsingStatementSyntax usingStmt:
                ClassifyGotosInStatement(usingStmt.Statement, labelRegistry);
                break;

            case LockStatementSyntax lockStmt:
                ClassifyGotosInStatement(lockStmt.Statement, labelRegistry);
                break;

            case FixedStatementSyntax fixedStmt:
                ClassifyGotosInStatement(fixedStmt.Statement, labelRegistry);
                break;

            case UnsafeStatementSyntax unsafeStmt:
                if (unsafeStmt.Block != null)
                    ClassifyGotosInStatement(unsafeStmt.Block, labelRegistry);
                break;

            case CheckedStatementSyntax checkedStmt:
                ClassifyGotosInStatement(checkedStmt.Block, labelRegistry);
                break;
        }
    }

    private void ClassifySingleGoto(GotoStatementSyntax gotoStmt, LabelRegistry labelRegistry)
    {
        // Skip goto case / goto default - handled by switch transformer.
        if (gotoStmt.Kind() == Microsoft.CodeAnalysis.CSharp.SyntaxKind.GotoCaseStatement ||
            gotoStmt.Kind() == Microsoft.CodeAnalysis.CSharp.SyntaxKind.GotoDefaultStatement)
        {
            return;
        }

        if (gotoStmt.Expression is not IdentifierNameSyntax identifier)
            return;

        var targetLabel = identifier.Identifier.Text;
        if (!labelRegistry.Contains(targetLabel))
            return;

        var classification = ClassifyGoto(gotoStmt, targetLabel, labelRegistry);
        _allGotos.Add(new GotoInfo(gotoStmt, targetLabel, classification));
    }

    /// <summary>
    /// Classifies a goto statement's relationship to its target label.
    /// </summary>
    internal static GotoScopeClassification ClassifyGoto(
        GotoStatementSyntax gotoStmt,
        string targetLabel,
        LabelRegistry labelRegistry)
    {
        // Step 1: Check if the label is an ancestor of the goto (A class)
        var current = gotoStmt.Parent;
        while (current != null)
        {
            if (current is LabeledStatementSyntax labeled
                && labeled.Identifier.Text == targetLabel)
            {
                // A class: label is an ancestor of goto
                return labelRegistry.TryGetLabel(targetLabel, out var info) && info!.Kind == LabelKind.Loop
                    ? GotoScopeClassification.InScopeLoop
                    : GotoScopeClassification.InScopeBlock;
            }

            // Stop at method body boundary
            if (current is BlockSyntax block && block.Parent is MethodDeclarationSyntax
                or ConstructorDeclarationSyntax
                or OperatorDeclarationSyntax
                or ConversionOperatorDeclarationSyntax
                or ArrowExpressionClauseSyntax)
            {
                break;
            }

            current = current.Parent;
        }

        // Step 1.5: Check if the target label is a sibling of the enclosing loop
        // (i.e., the label is immediately after the loop in the same parent block).
        // This means the goto is equivalent to break; from that loop.
        if (IsTargetLabelSiblingOfEnclosingLoop(gotoStmt, targetLabel))
        {
            return GotoScopeClassification.BreakFromEnclosingLoop;
        }

        // Step 2: Check if the label is in the same method body (B class)
        // Find the method body block
        var methodBodyBlock = FindMethodBodyBlock(gotoStmt);
        if (methodBodyBlock != null && ContainsLabelInMethodBody(methodBodyBlock, targetLabel))
        {
            // B class: same method body but label is not an ancestor
            return labelRegistry.TryGetLabel(targetLabel, out var info2) && info2!.Kind == LabelKind.Loop
                ? GotoScopeClassification.SameBodyLoop
                : GotoScopeClassification.SameBodyOther;
        }

        // C class: cross-scope
        return GotoScopeClassification.CrossScope;
    }

    /// <summary>
    /// Checks if the target label is a sibling of the enclosing loop statement.
    /// This means the goto is inside a loop, and the target label comes immediately
    /// after (or near) that loop in the same parent block. In this case, the goto
    /// is equivalent to break; from the enclosing loop.
    /// </summary>
    private static bool IsTargetLabelSiblingOfEnclosingLoop(GotoStatementSyntax gotoStmt, string targetLabel)
    {
        // Walk up from the goto to find the enclosing loop statement
        var node = gotoStmt.Parent;
        StatementSyntax? enclosingLoop = null;
        BlockSyntax? loopParentBlock = null;

        while (node != null)
        {
            // Check if we've found an enclosing loop
            if (node is WhileStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or DoStatementSyntax)
            {
                enclosingLoop = (StatementSyntax)node;
                // The loop's parent should be a block that also contains the target label
                if (node.Parent is BlockSyntax parentBlock)
                {
                    loopParentBlock = parentBlock;
                }
                break;
            }

            // Stop at method body boundary
            if (node is BlockSyntax block && block.Parent is MethodDeclarationSyntax
                or ConstructorDeclarationSyntax
                or OperatorDeclarationSyntax
                or ConversionOperatorDeclarationSyntax
                or ArrowExpressionClauseSyntax)
            {
                break;
            }

            node = node.Parent;
        }

        if (enclosingLoop == null || loopParentBlock == null)
            return false;

        // Check if the target label exists in the same parent block,
        // and comes after the enclosing loop
        var loopIndex = -1;
        var labelIndex = -1;

        for (var i = 0; i < loopParentBlock.Statements.Count; i++)
        {
            var stmt = loopParentBlock.Statements[i];
            if (stmt == enclosingLoop)
                loopIndex = i;

            // Check direct label match
            if (stmt is LabeledStatementSyntax labeled && labeled.Identifier.Text == targetLabel)
                labelIndex = i;

            // Also check inside fixed/using/unsafe/etc. blocks that wrap the loop
            if (labelIndex == -1 && ContainsTargetLabel(stmt, targetLabel))
                labelIndex = i;
        }

        // The label must come after the loop in the same parent block
        return labelIndex > loopIndex;
    }

    /// <summary>
    /// Checks if a statement (or its immediate wrapper like fixed/using) contains the target label.
    /// </summary>
    private static bool ContainsTargetLabel(StatementSyntax stmt, string targetLabel)
    {
        return stmt.DescendantNodes().OfType<LabeledStatementSyntax>()
            .Any(l => l.Identifier.Text == targetLabel);
    }

    private static BlockSyntax? FindMethodBodyBlock(SyntaxNode node)
    {
        var current = node;
        while (current != null)
        {
            if (current is BlockSyntax block && block.Parent is MethodDeclarationSyntax
                or ConstructorDeclarationSyntax
                or OperatorDeclarationSyntax
                or ConversionOperatorDeclarationSyntax
                or ArrowExpressionClauseSyntax)
            {
                return block;
            }
            current = current.Parent;
        }
        return null;
    }

    private static bool ContainsLabelInMethodBody(BlockSyntax methodBody, string targetLabel)
    {
        return methodBody.DescendantNodes()
            .OfType<LabeledStatementSyntax>()
            .Any(l => l.Identifier.Text == targetLabel);
    }

    // Basic block splitting

    private static List<StatementSyntax> FlattenStateMachineStatements(SyntaxList<StatementSyntax> statements)
    {
        var flattened = new List<StatementSyntax>();
        foreach (var statement in statements)
        {
            FlattenStateMachineStatement(statement, flattened);
        }

        return flattened;
    }

    private static void FlattenStateMachineStatement(StatementSyntax statement, List<StatementSyntax> flattened)
    {
        if (statement is BlockSyntax block && ContainsLabelOrGoto(block))
        {
            foreach (var nestedStatement in block.Statements)
            {
                FlattenStateMachineStatement(nestedStatement, flattened);
            }

            return;
        }

        flattened.Add(statement);
    }

    private static bool ContainsLabelOrGoto(BlockSyntax block)
    {
        return block.DescendantNodes().Any(node =>
            node is LabeledStatementSyntax or GotoStatementSyntax);
    }

    private void SplitIntoBasicBlocks(IReadOnlyList<StatementSyntax> statements)
    {
        var currentBlock = new BasicBlock { Index = 0 };

        void FinalizeCurrentBlock(BlockExit exit = BlockExit.FallThrough, int? fallThrough = null)
        {
            if (currentBlock.Statements.Count > 0 || currentBlock.Label != null)
            {
                currentBlock.Exit = exit;
                currentBlock.FallThroughTarget = fallThrough;
                _basicBlocks.Add(currentBlock);
            }
        }

        for (var i = 0; i < statements.Count; i++)
        {
            var stmt = statements[i];

            switch (stmt)
            {
                case LabeledStatementSyntax labeled:
                {
                    // Label starts a new basic block
                    var labelName = labeled.Identifier.Text;

                    if (currentBlock.Statements.Count > 0)
                    {
                        var nextIndex = _basicBlocks.Count + 1;
                        FinalizeCurrentBlock(BlockExit.FallThrough, nextIndex);
                        currentBlock = new BasicBlock { Index = nextIndex, Label = labelName };
                    }
                    else
                    {
                        // Empty current block - just set the label.
                        if (currentBlock.Label == null)
                            currentBlock.Label = labelName;
                        else
                        {
                            // Current block already has a label, start new one
                            var nextIndex = _basicBlocks.Count + 1;
                            FinalizeCurrentBlock(BlockExit.FallThrough, nextIndex);
                            currentBlock = new BasicBlock { Index = nextIndex, Label = labelName };
                        }
                    }

                    _labelToBlockIndex[labelName] = currentBlock.Index;

                    // Add the labeled statement's inner statement
                    if (labeled.Statement is not null)
                    {
                        currentBlock.Statements.Add(labeled.Statement);

                        // Check if the inner statement is a block-terminating statement
                        // and finalize the current block accordingly
                        switch (labeled.Statement)
                        {
                            case GotoStatementSyntax:
                                FinalizeCurrentBlock(BlockExit.Goto);
                                currentBlock = new BasicBlock { Index = _basicBlocks.Count };
                                break;
                            case ReturnStatementSyntax or ThrowStatementSyntax:
                                FinalizeCurrentBlock(BlockExit.Return);
                                currentBlock = new BasicBlock { Index = _basicBlocks.Count };
                                break;
                            case BreakStatementSyntax or ContinueStatementSyntax:
                                FinalizeCurrentBlock(BlockExit.Break);
                                currentBlock = new BasicBlock { Index = _basicBlocks.Count };
                                break;
                            default:
                                if (ContainsConditionalGoto(labeled.Statement))
                                {
                                    FinalizeCurrentBlock(BlockExit.ConditionalGoto);
                                    currentBlock = new BasicBlock { Index = _basicBlocks.Count };
                                }
                                break;
                        }
                    }

                    break;
                }

                case GotoStatementSyntax gotoStmt:
                {
                    currentBlock.Statements.Add(stmt);
                    FinalizeCurrentBlock(BlockExit.Goto);
                    currentBlock = new BasicBlock { Index = _basicBlocks.Count };
                    break;
                }

                case ReturnStatementSyntax or ThrowStatementSyntax:
                {
                    currentBlock.Statements.Add(stmt);
                    FinalizeCurrentBlock(BlockExit.Return);
                    currentBlock = new BasicBlock { Index = _basicBlocks.Count };
                    break;
                }

                case BreakStatementSyntax or ContinueStatementSyntax:
                {
                    currentBlock.Statements.Add(stmt);
                    FinalizeCurrentBlock(BlockExit.Break);
                    currentBlock = new BasicBlock { Index = _basicBlocks.Count };
                    break;
                }

                default:
                {
                    // Check if this statement contains a conditional goto (if with goto)
                    if (ContainsConditionalGoto(stmt))
                    {
                        currentBlock.Statements.Add(stmt);
                        FinalizeCurrentBlock(BlockExit.ConditionalGoto);
                        currentBlock = new BasicBlock { Index = _basicBlocks.Count };
                    }
                    else
                    {
                        currentBlock.Statements.Add(stmt);
                    }

                    break;
                }
            }
        }

        // Finalize the last block
        if (currentBlock.Statements.Count > 0 || currentBlock.Label != null)
        {
            currentBlock.Exit = BlockExit.FallThrough;
            _basicBlocks.Add(currentBlock);
        }

        // Set fall-through targets for blocks that don't have them yet
        for (var i = 0; i < _basicBlocks.Count; i++)
        {
            var block = _basicBlocks[i];
            if ((block.Exit == BlockExit.FallThrough || block.Exit == BlockExit.ConditionalGoto)
                && block.FallThroughTarget == null)
            {
                if (i + 1 < _basicBlocks.Count)
                    block.FallThroughTarget = _basicBlocks[i + 1].Index;
            }
        }
    }

    private static bool ContainsConditionalGoto(StatementSyntax stmt)
    {
        if (stmt is IfStatementSyntax ifStmt)
        {
            // Check if the if body contains a goto statement
            return ContainsGoto(ifStmt.Statement) ||
                   (ifStmt.Else != null && ContainsGoto(ifStmt.Else.Statement));
        }

        return false;
    }

    private static bool ContainsGoto(StatementSyntax stmt)
    {
        return stmt.DescendantNodesAndSelf().OfType<GotoStatementSyntax>().Any();
    }
}
