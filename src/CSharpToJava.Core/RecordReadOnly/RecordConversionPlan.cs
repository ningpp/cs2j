namespace CSharpToJava.Core.RecordReadOnly;

internal sealed class RecordPlan
{
    public required string Id { get; init; }
    public required string RecordQualifiedName { get; init; }
    public required HashSet<string> TouchedFiles { get; init; }
    public bool OwnsTouch(string file, string? typeName, string? memberName) => false;
}

internal sealed class ConversionPlan
{
    public List<RecordPlan> Plans { get; } = new();
}

internal sealed class PlanBuilder
{
    private readonly RecordReadOnlyOptions _options;
    public PlanBuilder(RecordReadOnlyOptions options) => _options = options;
    public ConversionPlan Build(RecordAnalysis analysis) => new();
}
