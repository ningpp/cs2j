using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Preprocessing.Jcs;

/// <summary>One enabled pass as observed by the capability scanner.</summary>
public sealed record JcsCapabilityScanPass(
    string Id,
    PassContract? Contract,
    bool Succeeded,
    IReadOnlyList<string> Diagnostics);

/// <summary>
/// R12: derives the JCS-1 capability set from FINAL-TREE evidence only.
/// WORKSPACE/SYMBOL/CONVERSION/EVAL/CALL are syntax-scanned; VALUE/GENERIC/
/// CLOSURE remain unsupported until a pass declares them (R13 owns the
/// semantic construct scan). Guaranteed is created exclusively through
/// <see cref="JcsCapability.GuaranteedForScanner"/>.
/// </summary>
public static class JcsCapabilityScanner
{
    private const string ExpressionDesugarPassId = "expression-desugar";
    private const string ScanOwner = "final-tree-scan";

    public static IReadOnlyList<JcsCapability> Scan(
        string treeRoot,
        IReadOnlyList<JcsCapabilityScanPass> passes,
        string? profile = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(treeRoot);
        ArgumentNullException.ThrowIfNull(passes);

        var scan = ScanTree(treeRoot);
        var variant = ManifestSolutionVariant.FromTree(treeRoot);

        var capabilities = new List<JcsCapability>();
        foreach (var id in Enum.GetValues<JcsCapabilityId>())
        {
            capabilities.Add(Derive(id, variant, passes, scan));
        }

        return capabilities;
    }

    private static JcsCapability Derive(
        JcsCapabilityId id,
        ManifestSolutionVariant variant,
        IReadOnlyList<JcsCapabilityScanPass> passes,
        TreeScan scan)
    {
        var (constructs, finalizers) = Declared(passes, id);
        var capabilityText = JcsCapabilityIds.Format(id);

        switch (id)
        {
            case JcsCapabilityId.Workspace:
                if (scan.CsprojCount > 0
                    && variant.Projects.Any(project => !string.IsNullOrWhiteSpace(project.Tfm))
                    && scan.WorkspaceDirectives.Count == 0
                    && scan.ParseErrors.Count == 0)
                {
                    return Guaranteed(
                        id, constructs, finalizers, scan, $"projects={scan.CsprojCount}");
                }

                return Partial(
                    id, constructs, finalizers,
                    scan.WorkspaceDirectives
                        .Concat(scan.ParseErrors)
                        .DefaultIfEmpty(new ScanResidual(
                            string.Empty, ScanOwner, "final tree has no project with a non-empty raw TFM value"))
                        .ToList());

            case JcsCapabilityId.Symbol:
                var symbolResiduals = scan.SymbolResiduals
                    .Concat(scan.ParseErrors)
                    .Concat(passes
                        .Where(pass => !pass.Succeeded
                            || pass.Diagnostics.Any(IsError))
                        .Select(pass => new ScanResidual(
                            string.Empty, pass.Id, "pass reported an error diagnostic")))
                    .ToList();
                if (symbolResiduals.Count == 0)
                {
                    return Guaranteed(
                        id, constructs, finalizers, scan,
                        $"symbolResiduals={scan.SymbolResiduals.Count}");
                }

                return Partial(id, constructs, finalizers, symbolResiduals);

            case JcsCapabilityId.Conversion:
                if (!passes.Any(pass => pass.Id == ExpressionDesugarPassId && pass.Succeeded))
                {
                    return JcsCapability.Unsupported(
                        id,
                        $"{ExpressionDesugarPassId} is not enabled; collection-shape conversions cannot be verified");
                }

                if (scan.CollectionExpressions.Count == 0 && scan.ParseErrors.Count == 0)
                {
                    return Guaranteed(
                        id, constructs, finalizers, scan,
                        $"collectionExpressions={scan.CollectionExpressions.Count}");
                }

                return Partial(
                    id, constructs, finalizers,
                    scan.CollectionExpressions
                        .Concat(scan.ParseErrors)
                        .ToList());

            case JcsCapabilityId.Eval:
                if (scan.EvalViolations.Count == 0 && scan.ParseErrors.Count == 0)
                {
                    return Guaranteed(
                        id, constructs, finalizers, scan,
                        $"evalViolations={scan.EvalViolations.Count}");
                }

                return Partial(
                    id, constructs, finalizers,
                    scan.EvalViolations.Concat(scan.ParseErrors).ToList());

            case JcsCapabilityId.Call:
                if (scan.CallViolations.Count == 0 && scan.ParseErrors.Count == 0)
                {
                    return Guaranteed(
                        id, constructs, finalizers, scan,
                        $"callViolations={scan.CallViolations.Count}");
                }

                return Partial(
                    id, constructs, finalizers,
                    scan.CallViolations.Concat(scan.ParseErrors).ToList());

            default:
                if (constructs.Count == 0 && finalizers.Count == 0
                    && !passes.Any(pass => Declares(pass, capabilityText)))
                {
                    return JcsCapability.Unsupported(
                        id,
                        $"no enabled pass declares {capabilityText}; final-tree construct scan deferred to R13");
                }

                var residuals = passes
                    .Where(pass => Declares(pass, capabilityText))
                    .Select(pass => new ScanResidual(
                        string.Empty,
                        pass.Id,
                        "final-tree construct scan deferred to R13"))
                    .ToList();
                if (residuals.Count == 0)
                {
                    residuals.Add(new ScanResidual(
                        string.Empty,
                        ScanOwner,
                        "final-tree construct scan deferred to R13"));
                }

                return Partial(id, constructs, finalizers, residuals);
        }
    }

    private static bool Declares(JcsCapabilityScanPass pass, string capability)
        => pass.Contract is not null
        && pass.Contract.EffectiveCapabilityIds
            .Any(id => string.Equals(id, capability, StringComparison.OrdinalIgnoreCase));

    private static (IReadOnlyList<string> Constructs, IReadOnlyList<string> Finalizers)
        Declared(IReadOnlyList<JcsCapabilityScanPass> passes, JcsCapabilityId id)
    {
        var capabilityText = JcsCapabilityIds.Format(id);
        var constructs = new HashSet<string>(StringComparer.Ordinal);
        var finalizers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pass in passes)
        {
            if (pass.Contract is null)
            {
                continue;
            }

            if (Declares(pass, capabilityText))
            {
                foreach (var construct in pass.Contract.Consumes)
                {
                    constructs.Add(construct);
                }
            }

            if (pass.Contract.Phase == PassPhase.Finalize
                && string.Equals(pass.Contract.FinalizerFor, capabilityText, StringComparison.Ordinal))
            {
                finalizers.Add(pass.Id);
            }
        }

        return (
            constructs.OrderBy(text => text, StringComparer.Ordinal).ToList(),
            finalizers.OrderBy(text => text, StringComparer.Ordinal).ToList());
    }

    private static JcsCapability Guaranteed(
        JcsCapabilityId id,
        IReadOnlyList<string> constructs,
        IReadOnlyList<string> finalizers,
        TreeScan scan,
        string scanCounts)
        => JcsCapability.GuaranteedForScanner(
            id,
            constructs,
            finalizers,
            EvidenceHash(id, JcsCapabilityStatus.Guaranteed, constructs, finalizers,
                Array.Empty<ScanResidual>(), scanCounts));

    private static JcsCapability Partial(
        JcsCapabilityId id,
        IReadOnlyList<string> constructs,
        IReadOnlyList<string> finalizers,
        IReadOnlyList<ScanResidual> residuals)
        => JcsCapability.Partial(
            id,
            residuals
                .Select(residual => new JcsCapabilityResidual(
                    NormalizeFile(residual.File), residual.Owner, residual.Reason))
                .ToList(),
            constructs,
            finalizers);

    private static string NormalizeFile(string file)
        => file.Replace('\\', '/');

    private static string EvidenceHash(
        JcsCapabilityId id,
        JcsCapabilityStatus status,
        IReadOnlyList<string> constructs,
        IReadOnlyList<string> finalizers,
        IReadOnlyList<ScanResidual> residuals,
        string scanCounts)
    {
        var canonical = string.Join(
            '|',
            JcsCapabilityIds.Format(id),
            status.ToString(),
            "constructs=" + string.Join(',', constructs),
            "finalizers=" + string.Join(',', finalizers),
            "residuals=" + string.Join(
                ';',
                residuals.Select(residual =>
                    $"{NormalizeFile(residual.File)}:{residual.Owner}:{residual.Reason}")),
            "scanCounts=" + scanCounts);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "sha256:" + Convert.ToHexStringLower(bytes);
    }

    private static bool IsError(string diagnostic)
        => diagnostic.Contains("[Error]", StringComparison.OrdinalIgnoreCase);

    private static TreeScan ScanTree(string treeRoot)
    {
        var scan = new TreeScan
        {
            CsprojCount = ManifestInputHash.EnumerateProjectFiles(treeRoot).Count(),
        };

        foreach (var file in ManifestInputHash.EnumerateSourceFiles(treeRoot))
        {
            var relative = ManifestInputHash.ToRelativePath(treeRoot, file);
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex)
            {
                scan.ParseErrors.Add(new ScanResidual(relative, ScanOwner, "read failed: " + ex.Message));
                continue;
            }

            ScanText(text, relative, scan);

            try
            {
                var root = CSharpSyntaxTree.ParseText(text).GetRoot();
                ScanDirectives(root, relative, scan);
                ScanSyntax(root, relative, scan);
            }
            catch (Exception ex)
            {
                scan.ParseErrors.Add(new ScanResidual(relative, ScanOwner, "parse failed: " + ex.Message));
            }
        }

        return scan;
    }

    private static void ScanText(string text, string relative, TreeScan scan)
    {
        var symbolFound = false;
        var invokeFound = false;

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimStart();
            if (!symbolFound
                && (line.StartsWith("extern alias ", StringComparison.Ordinal)
                    || line.StartsWith("global using ", StringComparison.Ordinal)
                    || line.StartsWith("#r ", StringComparison.Ordinal)))
            {
                symbolFound = true;
                scan.SymbolResiduals.Add(new ScanResidual(
                    relative, ScanOwner, "extern/global-using/#r residual remains in the final tree"));
            }

            if (!invokeFound && line.Contains(".Invoke(", StringComparison.Ordinal))
            {
                invokeFound = true;
                scan.CallViolations.Add(new ScanResidual(
                    relative, ScanOwner, "explicit .Invoke( call site remains in the final tree"));
            }
        }
    }

    private static void ScanDirectives(SyntaxNode root, string relative, TreeScan scan)
    {
        foreach (var trivia in root.DescendantTrivia())
        {
            if (trivia.GetStructure() is not DirectiveTriviaSyntax directive)
            {
                continue;
            }

            switch (directive.Kind())
            {
                case SyntaxKind.IfDirectiveTrivia:
                case SyntaxKind.ElifDirectiveTrivia:
                case SyntaxKind.ElseDirectiveTrivia:
                case SyntaxKind.EndIfDirectiveTrivia:
                case SyntaxKind.ErrorDirectiveTrivia:
                case SyntaxKind.LineDirectiveTrivia:
                case SyntaxKind.LineSpanDirectiveTrivia:
                    scan.WorkspaceDirectives.Add(new ScanResidual(
                        relative, ScanOwner, "preprocessor directive remains in the final tree"));
                    break;
            }
        }
    }

    private static void ScanSyntax(SyntaxNode root, string relative, TreeScan scan)
    {
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case CollectionExpressionSyntax:
                    scan.CollectionExpressions.Add(new ScanResidual(
                        relative, ScanOwner, "collection expression remains in the final tree"));
                    break;

                case ConditionalAccessExpressionSyntax conditional
                    when conditional.WhenNotNull is IdentifierNameSyntax or MemberAccessExpressionSyntax:
                    scan.CallViolations.Add(new ScanResidual(
                        relative, ScanOwner, "conditional-access method-group residual"));
                    break;
            }

            var condition = node switch
            {
                IfStatementSyntax statement => statement.Condition,
                WhileStatementSyntax statement => statement.Condition,
                DoStatementSyntax statement => statement.Condition,
                ForStatementSyntax statement => statement.Condition,
                ConditionalExpressionSyntax expression => expression.Condition,
                _ => null,
            };

            if (condition is not null)
            {
                ScanCondition(condition, relative, scan);
            }
        }
    }

    private static void ScanCondition(
        ExpressionSyntax condition,
        string relative,
        TreeScan scan)
    {
        if (JcsConditionFacts.TryFindViolation(condition, out _, out var violation))
        {
            scan.EvalViolations.Add(new ScanResidual(relative, ScanOwner, violation!));
        }
    }

    private sealed class TreeScan
    {
        public int CsprojCount { get; init; }
        public List<ScanResidual> WorkspaceDirectives { get; } = new();
        public List<ScanResidual> SymbolResiduals { get; } = new();
        public List<ScanResidual> CollectionExpressions { get; } = new();
        public List<ScanResidual> EvalViolations { get; } = new();
        public List<ScanResidual> CallViolations { get; } = new();
        public List<ScanResidual> ParseErrors { get; } = new();
    }

    private sealed record ScanResidual(string File, string Owner, string Reason);
}
