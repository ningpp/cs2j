using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// W-11/G-28 (upgrade plan §6.4-R0): the profile×disable semantics must be
/// identical on BOTH CLI entry points (convert-project and preprocess).
/// Historically convert-project silently honored
/// <c>--disable-pass normalization-audit</c> under java-ready/audit while the
/// preprocess verb unconditionally appended the audit and then failed with a
/// resolver conflict. Both verbs now call the shared
/// <see cref="Program.TryRejectRequiredPassDisable"/> helper before any
/// directory is created, so these tests pin the single implementation both
/// entry points delegate to.
/// </summary>
public sealed class ProfileRequiredPassTests
{
    [Theory]
    [InlineData("java-ready")]
    [InlineData("audit")]
    public void Audit_profiles_reject_disabling_normalization_audit(string profile)
    {
        var rejected = Program.TryRejectRequiredPassDisable(
            profile,
            new[] { PassIds.NormalizationAudit },
            out var error);

        Assert.True(rejected);
        Assert.Contains(profile, error, StringComparison.Ordinal);
        Assert.Contains(PassIds.NormalizationAudit, error, StringComparison.Ordinal);
        Assert.StartsWith("Error:", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_profiles_reject_disabling_normalization_audit_among_other_disables()
    {
        var rejected = Program.TryRejectRequiredPassDisable(
            "java-ready",
            new[] { PassIds.Enum, PassIds.NormalizationAudit, PassIds.Tuple },
            out var error);

        Assert.True(rejected);
        Assert.Contains(PassIds.NormalizationAudit, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_profile_allows_disabling_normalization_audit()
    {
        // Explicit disable stays legal on the default profile (the audit is
        // default-off anyway); only java-ready/audit make it required.
        var rejected = Program.TryRejectRequiredPassDisable(
            "default",
            new[] { PassIds.NormalizationAudit },
            out var error);

        Assert.False(rejected);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("java-ready")]
    [InlineData("audit")]
    [InlineData("default")]
    public void Disabling_other_passes_is_never_rejected(string profile)
    {
        var rejected = Program.TryRejectRequiredPassDisable(
            profile,
            new[] { PassIds.Enum, PassIds.Tuple },
            out var error);

        Assert.False(rejected);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("java-ready")]
    [InlineData("audit")]
    [InlineData("default")]
    public void Null_disable_list_is_never_rejected(string profile)
    {
        var rejected = Program.TryRejectRequiredPassDisable(
            profile,
            disablePasses: null,
            out var error);

        Assert.False(rejected);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void Empty_disable_list_is_never_rejected()
    {
        var rejected = Program.TryRejectRequiredPassDisable(
            "java-ready",
            Array.Empty<string>(),
            out var error);

        Assert.False(rejected);
        Assert.Equal(string.Empty, error);
    }

    [Theory]
    [InlineData("java-ready")]
    [InlineData("audit")]
    public void Rejection_is_case_sensitive_on_the_pass_id(string profile)
    {
        // Pass ids are kebab-case constants; a differently-cased variant is an
        // unknown-pass error (CS2J9002), not a required-pass violation.
        var rejected = Program.TryRejectRequiredPassDisable(
            profile,
            new[] { "Normalization-Audit" },
            out var error);

        Assert.False(rejected);
        Assert.Equal(string.Empty, error);
    }

    // ---- profile normalization (shared by both verbs via TryNormalizeProfile) ----

    [Theory]
    [InlineData(null, "default")]
    [InlineData("default", "default")]
    [InlineData("java-ready", "java-ready")]
    [InlineData("JAVA-READY", "java-ready")]
    [InlineData(" audit ", "audit")]
    public void TryNormalizeProfile_accepts_and_lowercases_known_values(
        string? raw, string expected)
    {
        Assert.True(Program.TryNormalizeProfile(raw, out var profile, out var error));
        Assert.Equal(expected, profile);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void TryNormalizeProfile_rejects_empty_value()
    {
        // Historical behavior: only a null/absent --profile defaults to
        // "default"; an explicitly empty string is rejected like any other
        // unknown value.
        Assert.False(Program.TryNormalizeProfile("", out var profile, out var error));
        Assert.Equal(string.Empty, profile);
        Assert.Contains("default|java-ready|audit", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("java_ready")]
    [InlineData("strict")]
    [InlineData("javaready")]
    public void TryNormalizeProfile_rejects_unknown_values(string raw)
    {
        Assert.False(Program.TryNormalizeProfile(raw, out var profile, out var error));
        Assert.Equal(string.Empty, profile);
        Assert.Contains(raw, error, StringComparison.Ordinal);
        Assert.Contains("default|java-ready|audit", error, StringComparison.Ordinal);
    }
}
