using System.Text.RegularExpressions;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Preprocessing.Jcs;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// VS-0 step 5 first cut (upgrade plan R14 / 附录 H.3 / 附录 I.1): the JM-ID
/// inventory for Java compensation branches plus the read-only file-level
/// reconciliation gate. The registry is data only: it never changes a pass,
/// the chain order or the manifest schema.
/// </summary>
public sealed class JmRegistryTests
{
    private static readonly string[] ExpectedJmIds =
    {
        "JM-CALL-DELEGATE-INVOKE",
        "JM-CALL-METHOD-GROUP",
        "JM-CALL-RAW-RECEIVER-RENDER",
        "JM-COERCION-COLLECTION-BRIDGE",
        "JM-COLLECTION-CONFORMANCE",
        "JM-EVAL-GETTER-PRE-STATEMENT",
        "JM-EVAL-IF-BYREF",
        "JM-EVAL-WHILE-EFFECT",
        "JM-GENERATED-EQUALITY-COMPARER",
        "JM-JSON-DESERIALIZE-TEXT-REWRITE",
        "JM-VALUE-COPY-SITE",
    };

    // H.3 says "12 个" in its note, but the table itself lists 11 rows; the
    // table is authoritative (see also the plan's own discrepancy note).
    [Fact]
    public void Default_Matches_H3_Eleven_JmIds_And_Is_Ordinal_Sorted()
    {
        Assert.Equal(11, JmRegistry.Default.Count);
        Assert.Equal(ExpectedJmIds, JmRegistry.Default.Select(entry => entry.JmId));

        var sorted = JmRegistry.Default
            .Select(entry => entry.JmId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(sorted, JmRegistry.Default.Select(entry => entry.JmId).ToArray());
    }

    [Fact]
    public void Default_Entries_Are_Unique_And_Well_Formed()
    {
        Assert.Equal(
            JmRegistry.Default.Count,
            JmRegistry.Default.Select(entry => entry.JmId).Distinct(StringComparer.Ordinal).Count());

        foreach (var entry in JmRegistry.Default)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.JmId));
            Assert.False(string.IsNullOrWhiteSpace(entry.InitialLocation));
            Assert.False(string.IsNullOrWhiteSpace(entry.LocationFile));
            Assert.DoesNotContain('\\', entry.LocationFile);
            Assert.NotEmpty(entry.Anchors);
            Assert.All(entry.Anchors, anchor => Assert.False(string.IsNullOrWhiteSpace(anchor)));
            Assert.True(
                JcsCapabilityIds.TryParse(entry.RequiredCapability, out _),
                $"{entry.JmId}: '{entry.RequiredCapability}' is not a JCS.<ID> capability");
            Assert.Matches(new Regex(@"^JE-\d{2}$"), entry.Rule);
            Assert.True(Enum.IsDefined(entry.RetirementState), $"{entry.JmId}: invalid retirement state");
            Assert.Equal(0, entry.Hits);
        }
    }

    [Fact]
    public void Default_Retirement_States_Are_Inventory_Except_Raw_Receiver_Ratchet()
    {
        foreach (var entry in JmRegistry.Default)
        {
            var expected = entry.JmId == "JM-CALL-RAW-RECEIVER-RENDER"
                ? JmRetirementState.Ratchet
                : JmRetirementState.Inventory;
            Assert.Equal(expected, entry.RetirementState);
        }

        // Ratchet is a permanent anti-regression guard, not a Retire state:
        // no implementation branch has actually been retired in this wave.
        Assert.DoesNotContain(
            JmRegistry.Default,
            entry => entry.RetirementState == JmRetirementState.Retire);
    }

    [Fact]
    public void RetiredStates_And_IsRetired_Cover_Retire_And_Ratchet()
    {
        Assert.Contains(JmRetirementState.Retire, JmRegistry.RetiredStates);
        Assert.Contains(JmRetirementState.Ratchet, JmRegistry.RetiredStates);
        Assert.DoesNotContain(JmRetirementState.Inventory, JmRegistry.RetiredStates);
        Assert.DoesNotContain(JmRetirementState.Shadow, JmRegistry.RetiredStates);
        Assert.DoesNotContain(JmRetirementState.Enforce, JmRegistry.RetiredStates);

        Assert.True(JmRegistry.IsRetired(JmRetirementState.Retire));
        Assert.True(JmRegistry.IsRetired(JmRetirementState.Ratchet));
        Assert.False(JmRegistry.IsRetired(JmRetirementState.Inventory));
        Assert.False(JmRegistry.IsRetired(JmRetirementState.Frozen));
    }

    [Fact]
    public void Every_Entry_File_Exists_And_Has_At_Least_One_Anchor()
    {
        var root = FindRepoRoot();
        foreach (var entry in JmRegistry.Default)
        {
            var path = Path.Combine(
                root,
                entry.LocationFile.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"{entry.JmId}: missing {entry.LocationFile}");
            var text = File.ReadAllText(path);
            Assert.Contains(
                entry.Anchors,
                anchor => text.Contains(anchor, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void CoverageFiles_Exactly_Matches_Distinct_Entry_Files()
    {
        var entryFiles = JmRegistry.Default
            .Select(entry => entry.LocationFile)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(entryFiles.Count, JmRegistry.CoverageFiles.Count);
        Assert.True(JmRegistry.CoverageFiles.SetEquals(entryFiles));
        // The three InvocationExpressionTransformer IDs share one physical file.
        Assert.Equal(
            3,
            JmRegistry.Default.Count(entry =>
                entry.LocationFile.EndsWith(
                    "InvocationExpressionTransformer.cs", StringComparison.Ordinal)));
    }

    [Fact]
    public void Validate_Returns_No_Errors_For_Default_Registry()
        => Assert.Empty(JmRegistry.Validate());

    [Fact]
    public void Validate_Reports_Invalid_Capability_Empty_Anchors_And_Missing_File()
    {
        var root = FindRepoRoot();
        const string existingFile =
            "src/CSharpToJava.Core/Transformers/StructCloneHelper.cs";

        var entries = new[]
        {
            new JmEntry(
                "JM-TEST-BAD-CAPABILITY",
                "test",
                existingFile,
                new[] { "IsUserDefinedStruct" },
                "VALUE",
                "JE-99",
                JmRetirementState.Inventory,
                0),
            new JmEntry(
                "JM-TEST-EMPTY-ANCHORS",
                "test",
                existingFile,
                Array.Empty<string>(),
                "JCS.VALUE",
                "JE-99",
                JmRetirementState.Inventory,
                0),
            new JmEntry(
                "JM-TEST-MISSING-FILE",
                "test",
                "src/does/not/exist/NoSuchFile.cs",
                new[] { "Whatever" },
                "JCS.VALUE",
                "JE-99",
                JmRetirementState.Inventory,
                0),
        };

        var errors = JmRegistry.Validate(entries, root);

        Assert.Contains(errors, error => error.Contains("JM-TEST-BAD-CAPABILITY", StringComparison.Ordinal)
            && error.Contains("RequiredCapability", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("JM-TEST-EMPTY-ANCHORS", StringComparison.Ordinal)
            && error.Contains("Anchors", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("JM-TEST-MISSING-FILE", StringComparison.Ordinal)
            && error.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_Reports_Duplicate_Ids_And_Malformed_Rules()
    {
        var root = FindRepoRoot();
        const string existingFile =
            "src/CSharpToJava.Core/Transformers/StructCloneHelper.cs";

        var entries = new[]
        {
            new JmEntry(
                "JM-TEST-DUPLICATE",
                "test",
                existingFile,
                new[] { "IsUserDefinedStruct" },
                "JCS.VALUE",
                "JE-10",
                JmRetirementState.Inventory,
                0),
            new JmEntry(
                "JM-TEST-DUPLICATE",
                "test",
                existingFile,
                new[] { "CloneStructValueIfNeeded" },
                "JCS.VALUE",
                "JE-1",
                JmRetirementState.Inventory,
                0),
        };

        var errors = JmRegistry.Validate(entries, root);

        Assert.Contains(errors, error => error.Contains("duplicate JM-ID", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Rule", StringComparison.Ordinal)
            && error.Contains("JE-1", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_Detects_CoverageFiles_Both_Directions()
    {
        var root = FindRepoRoot();
        const string valueCopyFile =
            "src/CSharpToJava.Core/Transformers/StructCloneHelper.cs";

        var dangling = new HashSet<string>(JmRegistry.CoverageFiles, StringComparer.Ordinal)
        {
            "src/CSharpToJava.Core/Transformers/NotRegistered.cs",
        };
        var danglingErrors = JmRegistry.Validate(JmRegistry.Default, root, dangling);
        Assert.Contains(danglingErrors, error =>
            error.Contains("NotRegistered.cs", StringComparison.Ordinal)
            && error.Contains("no JM entry", StringComparison.Ordinal));

        var missing = new HashSet<string>(
            JmRegistry.CoverageFiles.Where(file => file != valueCopyFile),
            StringComparer.Ordinal);
        var missingErrors = JmRegistry.Validate(JmRegistry.Default, root, missing);
        Assert.Contains(missingErrors, error =>
            error.Contains(valueCopyFile, StringComparison.Ordinal)
            && error.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RetiredJmIds_Matches_Registry_And_No_Entry_Is_In_Retire_State()
    {
        var expected = JmRegistry.Default
            .Where(entry => JmRegistry.IsRetired(entry.RetirementState))
            .Select(entry => entry.JmId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, JavaConversionRequirements.RetiredJmIds);

        // Current wave: exactly the ratchet guard, no Retire-state entry.
        Assert.Equal(new[] { "JM-CALL-RAW-RECEIVER-RENDER" }, JavaConversionRequirements.RetiredJmIds);
        Assert.DoesNotContain(
            JmRegistry.Default,
            entry => entry.RetirementState == JmRetirementState.Retire);
    }

    [Fact]
    public void RequiredCapabilityForJm_Returns_Known_Capability_And_Null_For_Unknown()
    {
        Assert.Equal(
            JcsCapabilityId.Value,
            JavaConversionRequirements.RequiredCapabilityForJm("JM-VALUE-COPY-SITE"));
        Assert.Equal(
            JcsCapabilityId.Call,
            JavaConversionRequirements.RequiredCapabilityForJm("JM-CALL-METHOD-GROUP"));
        Assert.Equal(
            JcsCapabilityId.Conversion,
            JavaConversionRequirements.RequiredCapabilityForJm("JM-COLLECTION-CONFORMANCE"));

        Assert.Null(JavaConversionRequirements.RequiredCapabilityForJm("JM-DOES-NOT-EXIST"));
        Assert.Null(JavaConversionRequirements.RequiredCapabilityForJm(string.Empty));
        Assert.False(JavaConversionRequirements.IsJmRetired("JM-DOES-NOT-EXIST"));
        Assert.True(JavaConversionRequirements.IsJmRetired("JM-CALL-RAW-RECEIVER-RENDER"));
        Assert.False(JavaConversionRequirements.IsJmRetired("JM-VALUE-COPY-SITE"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CSharpToJavaConverter.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
