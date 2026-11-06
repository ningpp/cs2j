using System.Reflection;
using CSharpToJava.Core.AttributeNormalize;
using CSharpToJava.Core.ClosedMetadata;
using CSharpToJava.Core.ExtensionMethodResolve;
using CSharpToJava.Core.ExtensionMethodResolve.Declarations;
using CSharpToJava.Core.LinqChannel;
using CSharpToJava.Core.ThreadStaticRewrite;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// W-03 (upgrade plan §6.3-3): diagnostic codes must be globally unique
/// across pass families. Historically CS2J1203 was ONE code with FOUR
/// meanings (closed-metadata JavaTargetUndecidable / extension-methods
/// ExternalHostNoMapping / extension-block ExternalMetadata / thread-static
/// StructMutationNotLiftable), and the whole CS2J12xx band was shared by
/// closed-metadata, extension-methods(-block) and thread-static while
/// linq-channel's CS2J13xx collided with attribute-normalize. The migration:
/// closed-metadata KEEPS 12xx; extension-methods/block moved to 19xx;
/// thread-static to 16xx; linq-channel to 17xx; attribute-normalize keeps
/// 13xx. Report consumers get a one-version transition window via each
/// family's <c>LegacyCodeOf</c>.
/// </summary>
public sealed class DiagnosticCodeUniquenessTests
{
    private static readonly Type[] CodeTables =
    {
        typeof(ClosedMetadataCodes),
        typeof(ExtensionMethodCodes),
        typeof(ExtensionBlockCodes),
        typeof(ThreadStaticCodes),
        typeof(LinqChannelDiagnosticCodes),
        typeof(AttributeNormalizeDiagnosticCode),
    };

    private static IEnumerable<(string Owner, string Code)> AllCs2jCodes()
    {
        foreach (var table in CodeTables)
        {
            foreach (var field in table
                         .GetFields(BindingFlags.Public | BindingFlags.Static)
                         .Where(field => field.IsLiteral
                             && field.FieldType == typeof(string)))
            {
                var value = (string)field.GetRawConstantValue()!;
                if (value.StartsWith("CS2J", StringComparison.Ordinal))
                {
                    yield return (table.Name, value);
                }
            }
        }
    }

    [Fact]
    public void Every_cs2j_diagnostic_code_is_globally_unique_across_pass_families()
    {
        // RED before the W-03 migration (four CS2J1203 constants plus the
        // shared 1200/1201/1202/1204/1205/1206/1210/1220/1230/1231/1290 and
        // 1301-1303/1310/1320 band collisions); GREEN after.
        var all = AllCs2jCodes().ToList();
        Assert.True(all.Count > 40, "expected the six code tables to declare their codes");

        var duplicates = all
            .GroupBy(entry => entry.Code, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            "duplicate CS2J codes across families: "
            + string.Join(
                "; ",
                duplicates.Select(group =>
                    $"{group.Key} claimed by {string.Join(", ", group.Select(e => e.Owner))}")));
    }

    [Fact]
    public void CS2J1203_is_now_owned_by_closed_metadata_only()
    {
        var owners = AllCs2jCodes()
            .Where(entry => entry.Code == "CS2J1203")
            .Select(entry => entry.Owner)
            .ToList();

        var owner = Assert.Single(owners);
        Assert.Equal(nameof(ClosedMetadataCodes), owner);
    }

    [Fact]
    public void Migrated_families_left_the_colliding_segments()
    {
        var all = AllCs2jCodes().ToList();

        Assert.DoesNotContain(all, entry =>
            entry.Owner is nameof(ExtensionMethodCodes) or nameof(ExtensionBlockCodes)
            && entry.Code.StartsWith("CS2J12", StringComparison.Ordinal));
        Assert.DoesNotContain(all, entry =>
            entry.Owner == nameof(ThreadStaticCodes)
            && entry.Code.StartsWith("CS2J12", StringComparison.Ordinal));
        Assert.DoesNotContain(all, entry =>
            entry.Owner == nameof(LinqChannelDiagnosticCodes)
            && entry.Code.StartsWith("CS2J13", StringComparison.Ordinal));
        // The segment owners keep their bands.
        Assert.Contains(all, entry =>
            entry.Owner == nameof(ClosedMetadataCodes) && entry.Code == "CS2J1203");
        Assert.Contains(all, entry =>
            entry.Owner == nameof(AttributeNormalizeDiagnosticCode) && entry.Code == "CS2J1300");
    }

    [Fact]
    public void LegacyCodeOf_maps_every_migrated_code_back_to_its_12xx_13xx_ancestor()
    {
        // One-version transition window (W-03/Q8): report consumers matching
        // the pre-migration codes can translate them for one release.
        Assert.Equal("CS2J1203", ExtensionMethodCodes.LegacyCodeOf(
            ExtensionMethodCodes.ExternalHostNoMapping));
        Assert.Equal("CS2J1203", ExtensionBlockCodes.LegacyCodeOf(
            ExtensionBlockCodes.ExternalMetadata));
        Assert.Equal("CS2J1203", ThreadStaticCodes.LegacyCodeOf(
            ThreadStaticCodes.StructMutationNotLiftable));
        Assert.Equal("CS2J1220", ExtensionMethodCodes.LegacyCodeOf(
            ExtensionMethodCodes.ResidualFound));
        Assert.Equal("CS2J1290", ThreadStaticCodes.LegacyCodeOf(
            ThreadStaticCodes.PlanNotSelfContained));
        Assert.Equal("CS2J1310", LinqChannelDiagnosticCodes.LegacyCodeOf(
            LinqChannelDiagnosticCodes.MixedChainPreserved));
        Assert.Equal("CS2J1340", LinqChannelDiagnosticCodes.LegacyCodeOf(
            LinqChannelDiagnosticCodes.ResidualMismatch));
    }

    [Fact]
    public void LegacyCodeOf_returns_null_for_unknown_or_non_migrated_codes()
    {
        Assert.Null(ExtensionMethodCodes.LegacyCodeOf(ExtensionMethodCodes.Info));
        Assert.Null(ExtensionMethodCodes.LegacyCodeOf("CS2J9999"));
        Assert.Null(ThreadStaticCodes.LegacyCodeOf(ThreadStaticCodes.FieldRewritten));
        Assert.Null(LinqChannelDiagnosticCodes.LegacyCodeOf("CS2J1702-unknown"));
    }

    [Fact]
    public void LegacyCodeOf_ranges_stay_inside_the_old_segments()
    {
        // Every mapped legacy code really is a pre-migration 12xx/13xx code —
        // the map can never invent codes from another family's new band.
        foreach (var entry in AllCs2jCodes())
        {
            string? legacy = entry.Owner switch
            {
                nameof(ExtensionMethodCodes) => ExtensionMethodCodes.LegacyCodeOf(entry.Code),
                nameof(ExtensionBlockCodes) => ExtensionBlockCodes.LegacyCodeOf(entry.Code),
                nameof(ThreadStaticCodes) => ThreadStaticCodes.LegacyCodeOf(entry.Code),
                nameof(LinqChannelDiagnosticCodes) => LinqChannelDiagnosticCodes.LegacyCodeOf(entry.Code),
                _ => null,
            };

            if (legacy is null)
            {
                continue;
            }

            Assert.True(
                legacy.StartsWith("CS2J12", StringComparison.Ordinal)
                || legacy.StartsWith("CS2J13", StringComparison.Ordinal),
                $"{entry.Owner}.{entry.Code} maps to legacy '{legacy}' outside the old segments.");
        }
    }
}
