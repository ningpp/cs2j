using CSharpToJava.Core.MemberDesugar;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExtensionMethodResolve.Declarations;

/// <summary>
/// Call-site rewriting for materialized extension blocks:
/// <list type="bullet">
/// <item><c>recv.M(args)</c> → <c>global::Host.M(recv, args)</c> (explicit
/// type arguments on the source GenericName are retained);</item>
/// <item><c>recv.P</c> → <c>global::Host.get_P(recv)</c>;</item>
/// <item><c>recv.P = v</c> → <c>global::Host.set_P(recv, v)</c>;</item>
/// <item>compound / ++-- at statement positions use the read-modify-write
/// recipe; complex receivers are single-evaluation hoisted into a temp with
/// an annotated splice block.</item>
/// </list>
/// Property member-binding seam nodes for synthetic null-conditional plans
/// are converted to <c>get_X()</c> invocations so the existing
/// <see cref="ExtensionConditionalLowerer"/> can take them.
/// </summary>
internal sealed class ExtensionBlockCallSiteRewriter : CSharpSyntaxRewriter
{
    private static readonly SyntaxAnnotation HoistedBlockMarker = new("C49", "HoistedBlock");

    private readonly Dictionary<SyntaxAnnotation, MethodSitePlan> _methodSites;
    private readonly Dictionary<SyntaxAnnotation, PropertyReadSitePlan> _readSites;
    private readonly Dictionary<SyntaxAnnotation, PropertyWriteSitePlan> _writeSites;
    private readonly Dictionary<SyntaxAnnotation, PropertyCompoundSitePlan> _compoundSites;
    private readonly Dictionary<SyntaxAnnotation, ConditionalSeamNode> _seams;

    public ExtensionBlockCallSiteRewriter(BlockAnalysisResult analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        _methodSites = analysis.MethodSites.ToDictionary(p => p.Annotation);
        _readSites = analysis.ReadSites.ToDictionary(p => p.Annotation);
        _writeSites = analysis.WriteSites.ToDictionary(p => p.Annotation);
        _compoundSites = analysis.CompoundSites.ToDictionary(p => p.StatementAnnotation);
        _seams = analysis.ConditionalSeams.ToDictionary(s => s.Annotation);
    }

    // ---- Block splice: flatten hoisted blocks into their enclosing container ----

    public override SyntaxNode? VisitBlock(BlockSyntax node)
        => node.WithStatements(SyntaxFactory.List(SpliceStatements(node.Statements)));

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        => node.WithStatements(SyntaxFactory.List(SpliceStatements(node.Statements)));

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
        => node.WithMembers(SyntaxFactory.List(SpliceMembers(node.Members)));

    private IEnumerable<StatementSyntax> SpliceStatements(SyntaxList<StatementSyntax> statements)
    {
        foreach (var statement in statements)
        {
            var visited = (StatementSyntax?)Visit(statement);
            if (visited is null)
            {
                continue;
            }

            if (visited is BlockSyntax spliced && spliced.HasAnnotations(HoistedBlockMarker.Kind!))
            {
                foreach (var inner in Splice(spliced))
                {
                    yield return inner;
                }

                continue;
            }

            yield return visited;
        }
    }

    private IEnumerable<MemberDeclarationSyntax> SpliceMembers(SyntaxList<MemberDeclarationSyntax> members)
    {
        foreach (var member in members)
        {
            if (member is GlobalStatementSyntax global
                && Visit(global.Statement) is StatementSyntax visitedStatement)
            {
                if (visitedStatement is BlockSyntax spliced && spliced.HasAnnotations(HoistedBlockMarker.Kind!))
                {
                    foreach (var inner in Splice(spliced))
                    {
                        yield return SyntaxFactory.GlobalStatement(inner);
                    }

                    continue;
                }

                yield return SyntaxFactory.GlobalStatement(visitedStatement);
                continue;
            }

            yield return (MemberDeclarationSyntax?)Visit(member) ?? member;
        }
    }

    private static IEnumerable<StatementSyntax> Splice(BlockSyntax block)
    {
        var list = block.Statements.ToList();
        if (list.Count == 0)
        {
            yield break;
        }

        list[0] = list[0].WithLeadingTrivia(block.GetLeadingTrivia());
        list[^1] = list[^1].WithTrailingTrivia(block.GetTrailingTrivia());
        foreach (var statement in list)
        {
            yield return statement;
        }
    }

    // ---- Statements: compound / inc-dec ----

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        var compound = Find(node, _compoundSites);
        if (compound is null)
        {
            return base.VisitExpressionStatement(node);
        }

        // Current-tree nodes (plan-stored nodes predate the annotation
        // rebuild and carry no annotations).
        ExpressionSyntax currentRight;
        MemberAccessExpressionSyntax currentTarget;
        if (node.Expression is AssignmentExpressionSyntax currentAssignment)
        {
            currentTarget = (MemberAccessExpressionSyntax)currentAssignment.Left;
            currentRight = currentAssignment.Right;
        }
        else
        {
            // Prefix/postfix unary inc-dec: operand is the member access.
            currentTarget = (MemberAccessExpressionSyntax)(
                node.Expression is PrefixUnaryExpressionSyntax prefix
                    ? prefix.Operand
                    : ((PostfixUnaryExpressionSyntax)node.Expression).Operand);
            currentRight = null!;
        }

        if (compound.HoistReceiver)
        {
            var receiverToken = SyntaxFactory.Identifier(compound.ReceiverTemp!);
            var receiverTemp = SyntaxFactory.IdentifierName(receiverToken);
            var hoistedReceiver = (ExpressionSyntax)Visit(currentTarget.Expression)!;
            var visitedRight = compound.CompoundKind == SyntaxKind.None
                ? null
                : (ExpressionSyntax)Visit(currentRight)!;
            var write = BuildWriteback(compound, receiverTemp, receiverTemp, visitedRight);
            var block = SyntaxFactory.Block(
                SyntaxFactory.List(new StatementSyntax[]
                {
                    SyntaxFactory.LocalDeclarationStatement(
                        SyntaxFactory.VariableDeclaration(
                            SyntaxFactory.IdentifierName("var"),
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(receiverToken)
                                    .WithInitializer(SyntaxFactory.EqualsValueClause(hoistedReceiver))))),
                    SyntaxFactory.ExpressionStatement(write)
                        .WithAdditionalAnnotations(compound.StatementAnnotation),
                }));
            return block.WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(HoistedBlockMarker);
        }

        var receiver = (ExpressionSyntax)Visit(currentTarget.Expression)!;
        var inlineRight = compound.CompoundKind == SyntaxKind.None
            ? null
            : (ExpressionSyntax)Visit(currentRight)!;
        var inline = BuildWriteback(compound, receiver, receiver, inlineRight);
        return SyntaxFactory.ExpressionStatement(inline)
            .WithLeadingTrivia(node.GetLeadingTrivia())
            .WithTrailingTrivia(node.GetTrailingTrivia())
            .WithAdditionalAnnotations(compound.StatementAnnotation);
    }

    private InvocationExpressionSyntax BuildWriteback(
        PropertyCompoundSitePlan plan,
        ExpressionSyntax readReceiver,
        ExpressionSyntax writeReceiver,
        ExpressionSyntax? visitedRight)
    {
        var getterName = AccessorsToMethods.GetterName(plan.Member.Name);
        var setterName = AccessorsToMethods.SetterName(plan.Member.Name);
        var read = BuildQualifiedCall(plan.Block, getterName, readReceiver, default);

        ExpressionSyntax rightOperand;
        SyntaxKind binaryKind;
        if (plan.CompoundKind != SyntaxKind.None)
        {
            rightOperand = visitedRight!;
            binaryKind = CompoundBinaryMapping.BinaryKind(plan.CompoundKind);
        }
        else
        {
            rightOperand = SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(1));
            binaryKind = plan.UnaryKind is SyntaxKind.PostIncrementExpression
                or SyntaxKind.PreIncrementExpression
                ? SyntaxKind.AddExpression
                : SyntaxKind.SubtractExpression;
        }

        var binary = SyntaxFactory.BinaryExpression(binaryKind, read, rightOperand);
        return BuildQualifiedCall(
            plan.Block,
            setterName,
            writeReceiver,
            SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(binary)),
            extraAnnotations: null);
    }

    // ---- Assignments: simple writes ----

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var write = Find(node, _writeSites);
        if (write is null)
        {
            return base.VisitAssignmentExpression(node);
        }

        var right = (ExpressionSyntax)Visit(node.Right)!;
        return BuildQualifiedCall(
            write.Block,
            AccessorsToMethods.SetterName(write.Member.Name),
            (ExpressionSyntax)Visit(((MemberAccessExpressionSyntax)node.Left).Expression)!,
            SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(right)),
            new[] { write.Annotation });
    }

    // ---- Member access: property reads ----

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var read = Find(node, _readSites);
        if (read is null)
        {
            return base.VisitMemberAccessExpression(node);
        }

        var receiver = (ExpressionSyntax)Visit(node.Expression)!;
        return BuildQualifiedCall(
            read.Block,
            AccessorsToMethods.GetterName(read.Member.Name),
            receiver,
            default,
            new[] { read.Annotation });
    }

    // ---- Invocations: block methods ----

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var method = Find(node, _methodSites);
        if (method is null)
        {
            return base.VisitInvocationExpression(node);
        }

        ExpressionSyntax receiver;
        switch (node.Expression)
        {
            case MemberAccessExpressionSyntax access:
                receiver = (ExpressionSyntax)Visit(access.Expression)!;
                break;
            case IdentifierNameSyntax:
                // Bare invocation from inside the receiver's own scope:
                // use the synthesized receiver parameter name.
                receiver = SyntaxFactory.IdentifierName(method.Block.Receiver.Name);
                break;
            default:
                receiver = (ExpressionSyntax)Visit(method.Receiver)!;
                break;
        }

        var hostName = ExtensionHostSyntaxFactory.BuildHostName(method.Block.Host);

        var visitedArguments = VisitArguments(node.ArgumentList.Arguments);

        return ExtensionStaticCallFactory.Build(
            hostName,
            method.SourceName,
            receiver,
            method.Block.Receiver.RefKind,
            method.Block.Receiver.ScopedKind,
            visitedArguments,
            new[] { ExtensionCallRewriter.RewrittenAnnotation, method.Annotation });
    }

    // ---- Property seam: member-binding → invocation member-binding ----

    public override SyntaxNode? VisitMemberBindingExpression(MemberBindingExpressionSyntax node)
    {
        var seam = Find(node, _seams);
        if (seam is not null)
        {
            return seam.Replacement;
        }

        return base.VisitMemberBindingExpression(node);
    }

    // ---- Helpers ----

    private SeparatedSyntaxList<ArgumentSyntax> VisitArguments(SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        var visited = new List<ArgumentSyntax>(arguments.Count);
        foreach (var argument in arguments)
        {
            visited.Add((ArgumentSyntax)Visit(argument)!);
        }

        return SyntaxFactory.SeparatedList(visited);
    }

    private static InvocationExpressionSyntax BuildQualifiedCall(
        BlockPlan block,
        string methodName,
        ExpressionSyntax receiver,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        IEnumerable<SyntaxAnnotation>? extraAnnotations = null)
    {
        var hostName = ExtensionHostSyntaxFactory.BuildHostName(block.Host);
        return ExtensionStaticCallFactory.Build(
            hostName,
            SyntaxFactory.IdentifierName(methodName),
            receiver,
            block.Receiver.RefKind,
            block.Receiver.ScopedKind,
            arguments,
            CombineAnnotations(extraAnnotations));
    }

    private static IEnumerable<SyntaxAnnotation> CombineAnnotations(IEnumerable<SyntaxAnnotation>? extra)
    {
        yield return ExtensionCallRewriter.RewrittenAnnotation;
        if (extra is not null)
        {
            foreach (var annotation in extra)
            {
                yield return annotation;
            }
        }
    }

    private static TPlan? Find<TPlan>(SyntaxNode node, Dictionary<SyntaxAnnotation, TPlan> plans)
        where TPlan : class
    {
        foreach (var annotation in node.GetAnnotations("C49"))
        {
            if (plans.TryGetValue(annotation, out var plan))
            {
                return plan;
            }
        }

        // Property/statements may carry a C19 annotation too; only C49 keys are used.
        return null;
    }
}

/// <summary>Maps compound assignment kinds to binary expression kinds.</summary>
internal static class CompoundBinaryMapping
{
    public static SyntaxKind BinaryKind(SyntaxKind compoundKind)
        => compoundKind switch
        {
            SyntaxKind.AddAssignmentExpression => SyntaxKind.AddExpression,
            SyntaxKind.AndAssignmentExpression => SyntaxKind.BitwiseAndExpression,
            SyntaxKind.DivideAssignmentExpression => SyntaxKind.DivideExpression,
            SyntaxKind.ExclusiveOrAssignmentExpression => SyntaxKind.ExclusiveOrExpression,
            SyntaxKind.LeftShiftAssignmentExpression => SyntaxKind.LeftShiftExpression,
            SyntaxKind.ModuloAssignmentExpression => SyntaxKind.ModuloExpression,
            SyntaxKind.MultiplyAssignmentExpression => SyntaxKind.MultiplyExpression,
            SyntaxKind.OrAssignmentExpression => SyntaxKind.BitwiseOrExpression,
            SyntaxKind.RightShiftAssignmentExpression => SyntaxKind.RightShiftExpression,
            SyntaxKind.SubtractAssignmentExpression => SyntaxKind.SubtractExpression,
            SyntaxKind.UnsignedRightShiftAssignmentExpression => SyntaxKind.UnsignedRightShiftExpression,
            SyntaxKind.CoalesceAssignmentExpression => SyntaxKind.CoalesceExpression,
            _ => throw new InvalidOperationException($"Unsupported compound assignment kind '{compoundKind}'."),
        };
}
