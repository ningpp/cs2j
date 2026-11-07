using CSharpToJava.Core.Normalization;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.Preprocessing.Jcs;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// R12: JCS capabilities are derived from final-tree evidence only. Guaranteed
/// claims carry a scanner evidence hash; the syntax-checked families
/// (WORKSPACE/SYMBOL/CONVERSION/EVAL/CALL) flip on real evidence, while
/// VALUE/GENERIC/CLOSURE stay unsupported without a declaring pass.
/// </summary>
public sealed class JcsCapabilityScannerTests
{
    [Fact]
    public void Capabilities_are_ordered_by_the_enum_and_workspace_sees_a_raw_tfm()
    {
        var tree = CreateTree(tfm: "$(NetCoreAppCurrent)");

        try
        {
            var capabilities = Scan(tree, ExpressionDesugar());

            Assert.Equal(
                new[]
                {
                    JcsCapabilityId.Workspace,
                    JcsCapabilityId.Symbol,
                    JcsCapabilityId.Call,
                    JcsCapabilityId.Eval,
                    JcsCapabilityId.Value,
                    JcsCapabilityId.Generic,
                    JcsCapabilityId.Conversion,
                    JcsCapabilityId.Closure,
                },
                capabilities.Select(capability => capability.Id).ToArray());

            var workspace = Find(capabilities, JcsCapabilityId.Workspace);
            Assert.Equal(JcsCapabilityStatus.Guaranteed, workspace.Status);
            Assert.NotNull(workspace.EvidenceHash);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void Workspace_is_partial_when_the_tree_has_no_project()
    {
        var tree = CreateTree(tfm: null);

        try
        {
            var workspace = Find(Scan(tree, ExpressionDesugar()), JcsCapabilityId.Workspace);

            Assert.Equal(JcsCapabilityStatus.Partial, workspace.Status);
            Assert.NotEmpty(workspace.Residuals);
            Assert.False(string.IsNullOrWhiteSpace(workspace.Residuals[0].Reason));
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void Conversion_is_guaranteed_only_after_a_clean_collection_expression_scan()
    {
        var tree = CreateTree();

        try
        {
            var clean = Find(Scan(tree, ExpressionDesugar()), JcsCapabilityId.Conversion);
            Assert.Equal(JcsCapabilityStatus.Guaranteed, clean.Status);
            Assert.NotNull(clean.EvidenceHash);

            File.WriteAllText(
                Path.Combine(tree, "Collection.cs"),
                "class C { void M() { int[] values = [1, 2, 3]; } }");

            var dirty = Find(Scan(tree, ExpressionDesugar()), JcsCapabilityId.Conversion);
            Assert.Equal(JcsCapabilityStatus.Partial, dirty.Status);
            var residual = Assert.Single(dirty.Residuals);
            Assert.Equal("Collection.cs", residual.File);
            Assert.False(string.IsNullOrWhiteSpace(residual.Reason));
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void Eval_is_partial_when_a_condition_carries_an_increment()
    {
        var tree = CreateTree();

        try
        {
            Assert.Equal(
                JcsCapabilityStatus.Guaranteed,
                Find(Scan(tree, ExpressionDesugar()), JcsCapabilityId.Eval).Status);

            File.WriteAllText(
                Path.Combine(tree, "Loop.cs"),
                "class C { void M() { int i = 0; while (i++ < 3) { } } }");

            var eval = Find(Scan(tree, ExpressionDesugar()), JcsCapabilityId.Eval);
            Assert.Equal(JcsCapabilityStatus.Partial, eval.Status);
            Assert.Equal("Loop.cs", Assert.Single(eval.Residuals).File);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void Symbol_ignores_warning_text_that_mentions_errors()
    {
        var tree = CreateTree();

        try
        {
            var noisy = new JcsCapabilityScanPass(
                "member-desugar",
                Contract(capabilityId: "JCS.SYMBOL"),
                Succeeded: true,
                Diagnostics: new[]
                {
                    "[Warning] CS2J5101: whole-tree validation attributed a new error to this plan",
                });

            var symbol = Find(Scan(tree, ExpressionDesugar(), noisy), JcsCapabilityId.Symbol);

            Assert.Equal(JcsCapabilityStatus.Guaranteed, symbol.Status);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void Value_is_unsupported_without_a_declaring_pass()
    {
        var tree = CreateTree();

        try
        {
            var value = Find(Scan(tree, ExpressionDesugar()), JcsCapabilityId.Value);

            Assert.Equal(JcsCapabilityStatus.Unsupported, value.Status);
            Assert.False(string.IsNullOrWhiteSpace(Assert.Single(value.Residuals).Reason));
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    [Fact]
    public void Value_is_partial_with_the_declaring_pass_owner_and_r13_reason()
    {
        var tree = CreateTree();

        try
        {
            var valuePass = new JcsCapabilityScanPass(
                "struct-value-semantics",
                Contract(capabilityId: "JCS.VALUE", consumes: "NORM-VALUE-COPY"),
                Succeeded: true,
                Diagnostics: Array.Empty<string>());

            var value = Find(
                Scan(tree, ExpressionDesugar(), valuePass),
                JcsCapabilityId.Value);

            Assert.Equal(JcsCapabilityStatus.Partial, value.Status);
            Assert.Equal(new[] { "NORM-VALUE-COPY" }, value.Constructs);
            var residual = Assert.Single(value.Residuals);
            Assert.Equal("struct-value-semantics", residual.Owner);
            Assert.Equal("final-tree construct scan deferred to R13", residual.Reason);
        }
        finally
        {
            DeleteTree(tree);
        }
    }

    private static IReadOnlyList<JcsCapability> Scan(
        string treeRoot,
        params JcsCapabilityScanPass[] passes)
        => JcsCapabilityScanner.Scan(treeRoot, passes, profile: "java-ready");

    private static JcsCapability Find(IReadOnlyList<JcsCapability> capabilities, JcsCapabilityId id)
        => capabilities.Single(capability => capability.Id == id);

    private static JcsCapabilityScanPass ExpressionDesugar()
        => new(
            "expression-desugar",
            Contract(capabilityId: "JCS.CONVERSION"),
            Succeeded: true,
            Diagnostics: Array.Empty<string>());

    private static PassContract Contract(string capabilityId, params string[] consumes)
        => new(
            Consumes: consumes,
            Produces: Array.Empty<string>(),
            Preconditions: Array.Empty<string>(),
            Postconditions: Array.Empty<string>(),
            MayLeave: Array.Empty<string>(),
            Owners: Array.Empty<string>(),
            Policy: ResidualPolicy.Fail,
            Idempotent: true,
            RequiresWholeTree: false,
            CapabilityIds: new[] { capabilityId });

    private static string CreateTree(string? tfm = "net10.0")
    {
        var root = Path.Combine(
            Path.GetTempPath(), "cs2j-capability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Program.cs"), "class P { void M() { } }\n");

        if (tfm is not null)
        {
            File.WriteAllText(
                Path.Combine(root, "App.csproj"),
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>{tfm}</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
        }

        return root;
    }

    private static void DeleteTree(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
