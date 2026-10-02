using CSharpToJava.Core.LexicalNormalize;

namespace CSharpToJava.Tests;

/// <summary>Core matrix for M1: LN-1 #line strip, LN-2 #error, LN-3 residual
/// conditionals, idempotence and malformed input.</summary>
public sealed class LexicalNormalizeTests
{
    private static LexicalNormalizeResult Run(string source, LexicalNormalizeOptions? options = null)
        => new LexicalNormalizePreprocessor().Normalize(source, options);

    [Fact]
    public void LN1_StripsAllLineDirectiveForms_AndKeepsTokenSequence()
    {
        const string source = """
            #line 100 "x.cs"
            using System;

            #line default
            public class C
            {
                #line hidden
                public void M() { }
            }
            """;

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(3, result.Statistics.LineDirectivesRemoved);
        Assert.DoesNotContain(result.OutputCode, "#line");
        Assert.Equal(1, result.Statistics.FilesTransformed);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LN1_WhenDisabled_LeavesFileUnchanged()
    {
        const string source = "#line 5\npublic class C { }\n";

        var result = Run(source, new LexicalNormalizeOptions { StripLineDirectives = false });

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.FilesSkippedClean);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void LN2_ActiveErrorDirective_FailsAndDoesNotRewrite()
    {
        const string source = "#error boom\npublic class C { }\n";

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == LexicalNormalizeCodes.ErrorDirective);
        Assert.Equal(1, result.Statistics.ErrorDirectivesSeen);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void LN3_ResidualDefineOrConditional_FailsWithOrderingDiagnostic()
    {
        const string source = "#define X\npublic class C { }\n";

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == LexicalNormalizeCodes.ConditionalFirst);

        const string ifSource = "#if X\npublic class C { }\n#endif\n";
        var ifResult = Run(ifSource);
        Assert.Contains(ifResult.Diagnostics, d => d.Code == LexicalNormalizeCodes.ConditionalFirst);
    }

    [Fact]
    public void LN3_PragmaRegionNullableWarning_DoNotTrigger()
    {
        const string source = """
            #pragma warning disable CS0105
            #nullable enable
            #region R
            #warning careful
            public class C { }
            #endregion
            """;

        var result = Run(source);

        Assert.DoesNotContain(result.Diagnostics, d => d.Code == LexicalNormalizeCodes.ConditionalFirst);
    }

    [Fact]
    public void LN4_ExternAlias_ErrorsByDefault_GlobalColonExcluded()
    {
        const string source = "extern alias X;\npublic class C { X::N.T Field; global::System.Int32 Other; }\n";

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Equal(1, result.Statistics.ExternAliasDirectives);
        Assert.Equal(1, result.Statistics.ExternAliasQualifiedSites);
        Assert.Contains(result.Diagnostics, d => d.Code == LexicalNormalizeCodes.ExternAlias);
    }

    [Fact]
    public void LN4_SeverityWarning_RemainingRulesStillRewrite()
    {
        const string source = "extern alias X;\n#line 2\npublic class C { X::N.T Field; }\n";

        var result = Run(source, new LexicalNormalizeOptions
        {
            ExternAliasSeverity = LexicalNormalizeSeverity.Warning,
        });

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.OutputCode, "#line");
        Assert.Contains(result.Diagnostics, d => d.Code == LexicalNormalizeCodes.ExternAlias
            && d.Severity == LexicalNormalizeSeverity.Warning);
    }

    [Fact]
    public void Idempotent_SecondRunProducesNoChange()
    {
        const string source = "#line 7\npublic class C { public int X; }\n";

        var first = Run(source);
        Assert.True(first.Changed);

        var second = Run(first.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(1, second.Statistics.FilesSkippedClean);
    }

    [Fact]
    public void CleanFile_IsReturnedByteEqual()
    {
        const string source = "public class C { }\n";

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void MalformedInput_ReturnsOriginalWithParseError()
    {
        const string source = "public class {\n";

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Severity == LexicalNormalizeSeverity.Error);
        Assert.Equal(source, result.OutputCode);
    }
}
