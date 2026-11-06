using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.EqualityConcat;
using CSharpToJava.Core.EnumDeclaration;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.LexicalNormalize;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.StructValueSemantics;
using CSharpToJava.Core.UnsafeFfm;
using CSharpToJava.Core.VarianceAudit;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// W-01 (upgrade plan §6.4-R0): java-ready/audit must make the generic family
/// (generic-deepen / closed-metadata) and the variance-audit constraint gate
/// run STRICT. Historically <c>PassChainFactory</c> constructed
/// <c>GenericDeepenOptions</c>/<c>ClosedMetadataOptions</c> without any
/// strict/profile input, so java-ready silently downgraded the very semantics
/// it promises to Warnings (G-24). The default profile keeps the historical
/// non-strict behavior.
/// </summary>
public sealed class JavaReadyStrictWiringTests
{
    private static PassChainConfiguration CreateConfig(
        bool javaReady,
        bool varianceStrict = false,
        IReadOnlySet<string>? enabledPassIds = null) => new()
    {
        MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
        RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
        JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
        MemberDesugarOptionsFactory = () => new MemberDesugarOptions(),
        StructValueSemanticsOptionsFactory = () => new StructValueSemanticsOptions(),
        EnumDeclarationOptionsFactory = () => new EnumDeclarationOptions(),
        LexicalNormalizeOptionsFactory = () => new LexicalNormalizeOptions(),
        EqualityConcatOptionsFactory = () => new EqualityConcatOptions(),
        UnsafeFfmOptionsFactory = () => new UnsafeFfmOptions(),
        ExpressionDesugarOptionsFactory = () => new ExpressionDesugarOptions(),
        VarianceAuditOptionsFactory = () => new VarianceAuditOptions
        {
            Strict = varianceStrict,
        },
        EnabledPassIds = enabledPassIds,
        JavaReady = javaReady,
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GenericDeepen_StrictFollowsJavaReady(bool javaReady)
    {
        var adapter = (GenericDeepenPassAdapter)PassChainFactory.CreateAdapter(
            PassIds.GenericDeepen, CreateConfig(javaReady));

        Assert.Equal(javaReady, adapter.Options.Strict);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ClosedMetadata_StrictFollowsJavaReady(bool javaReady)
    {
        var adapter = (ClosedMetadataPassAdapter)PassChainFactory.CreateAdapter(
            PassIds.ClosedMetadata, CreateConfig(javaReady));

        Assert.Equal(javaReady, adapter.Options.Strict);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VarianceAudit_StrictFollowsJavaReady(bool javaReady)
    {
        var adapter = (VarianceAuditPassAdapter)PassChainFactory.CreateAdapter(
            PassIds.VarianceAudit, CreateConfig(javaReady));

        Assert.Equal(javaReady, adapter.Options.Strict);
    }

    [Fact]
    public void VarianceAudit_ExplicitStrictStaysStrictWithoutJavaReady()
    {
        var adapter = (VarianceAuditPassAdapter)PassChainFactory.CreateAdapter(
            PassIds.VarianceAudit, CreateConfig(javaReady: false, varianceStrict: true));

        Assert.True(adapter.Options.Strict);
    }

    [Fact]
    public void DefaultProfile_KeepsTheHistoricalNonStrictGenericFamily()
    {
        var config = CreateConfig(javaReady: false);

        Assert.False(((GenericDeepenPassAdapter)PassChainFactory.CreateAdapter(
            PassIds.GenericDeepen, config)).Options.Strict);
        Assert.False(((ClosedMetadataPassAdapter)PassChainFactory.CreateAdapter(
            PassIds.ClosedMetadata, config)).Options.Strict);
        Assert.False(((VarianceAuditPassAdapter)PassChainFactory.CreateAdapter(
            PassIds.VarianceAudit, config)).Options.Strict);
    }

    [Fact]
    public void ClosedMetadata_WiringKeepsTheHardChainDependencyOptions()
    {
        // The W-01 wiring must not drop the pre-existing option plumbing
        // (EnabledPassIds feeds the generic-deepen hard-chain check CS2J1205).
        var adapter = (ClosedMetadataPassAdapter)PassChainFactory.CreateAdapter(
            PassIds.ClosedMetadata,
            CreateConfig(
                javaReady: true,
                enabledPassIds: new HashSet<string>(
                    new[] { PassIds.Generic, PassIds.GenericDeepen, PassIds.ClosedMetadata },
                    StringComparer.Ordinal)));

        Assert.True(adapter.Options.Strict);
        Assert.NotNull(adapter.Options.EnabledPassIds);
        Assert.Contains(PassIds.GenericDeepen, adapter.Options.EnabledPassIds);
    }
}
