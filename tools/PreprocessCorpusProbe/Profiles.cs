// Profile drivers: each profile selects a family rule set and returns, per
// compiled file, a flattened metric dictionary (reflection over the family
// Statistics shape) plus its diagnostics. Counters are additive, so new
// buckets added by later milestones show up without touching this probe.

using System.Collections;
using System.Reflection;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.CollectionExpr;
using CSharpToJava.Core.ExpressionDesugar.Interpolations;
using CSharpToJava.Core.ForeachSource;
using CSharpToJava.Core.LexicalNormalize;
using CSharpToJava.Core.NumericBoundary;
using Microsoft.CodeAnalysis;

internal sealed record Diag(string Severity, string Code, string Message, int Line = 0);

internal sealed record FileOutcome(
    Dictionary<string, long> Metrics,
    List<Diag> Diagnostics,
    bool Changed);

internal abstract class ProbeProfile
{
    public abstract string Id { get; }
    public abstract string Family { get; }
    public abstract string Description { get; }

    /// <summary>true = in-process Core driver; false = CLI whole-tree driver.</summary>
    public abstract bool InProcess { get; }

    public abstract FileOutcome RunFile(string source, SemanticModel model, string filePath);

    public virtual Dictionary<string, long> FinalizeMetrics(Dictionary<string, long> metrics) => metrics;
}

internal static class MetricExtractor
{
    public static Dictionary<string, long> Flatten(object? stats)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        if (stats is null)
            return result;
        Visit(stats, "", result, 0);
        return result;
    }

    private static void Visit(object? obj, string prefix, Dictionary<string, long> result, int depth)
    {
        if (obj is null || depth > 4)
            return;

        foreach (var member in EnumerateMembers(obj.GetType()))
        {
            object? value;
            try
            {
                value = member.GetMemberValue(obj);
            }
            catch
            {
                continue;
            }

            if (value is null)
                continue;

            var name = string.IsNullOrEmpty(prefix) ? member.Name : prefix + "." + member.Name;

            switch (value)
            {
                case int i:
                    Add(result, name, i);
                    break;
                case long l:
                    Add(result, name, l);
                    break;
                case uint u:
                    Add(result, name, u);
                    break;
                case IDictionary { } dict when IsStringIntDictionary(member):
                    foreach (DictionaryEntry entry in dict)
                        Add(result, $"{name}.{entry.Key}", Convert.ToInt64(entry.Value));
                    break;
                default:
                    if (member.MemberType.IsClass
                        && member.MemberType.Namespace?.StartsWith("CSharpToJava.Core", StringComparison.Ordinal) == true
                        && member.MemberType != typeof(string))
                    {
                        Visit(value, name, result, depth + 1);
                    }
                    break;
            }
        }
    }

    private static bool IsStringIntDictionary(MemberInfoInfo member) =>
        member.MemberType.IsGenericType
        && member.MemberType.GetGenericTypeDefinition() is var t
        && (t == typeof(Dictionary<,>) || t == typeof(IReadOnlyDictionary<,>) || t == typeof(IDictionary<,>))
        && member.MemberType.GenericTypeArguments[0] == typeof(string)
        && member.MemberType.GenericTypeArguments[1] == typeof(int);

    private static void Add(Dictionary<string, long> result, string name, long value)
        => result[name] = result.GetValueOrDefault(name) + value;

    private static IEnumerable<MemberInfoInfo> EnumerateMembers(Type type)
    {
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (!f.IsStatic)
                yield return new MemberInfoInfo(f.Name, f.FieldType, o => f.GetValue(o));
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.GetIndexParameters().Length == 0 && p.CanRead)
                yield return new MemberInfoInfo(p.Name, p.PropertyType, o => p.GetValue(o));
    }

    private sealed record MemberInfoInfo(string Name, Type MemberType, Func<object?, object?> GetMemberValue);
}

internal static class FamilyDiagnostics
{
    public static List<Diag> Read(object? diagnosticsHolder, object source)
    {
        var result = new List<Diag>();
        if (diagnosticsHolder is not IEnumerable enumerable)
            return result;

        foreach (var item in enumerable)
        {
            if (item is null)
                continue;
            var type = item.GetType();
            var severity = ReadString(type.GetProperty("Severity")?.GetValue(item)) ?? "Info";
            var code = ReadString(type.GetProperty("Code")?.GetValue(item)) ?? "";
            var message = ReadString(type.GetProperty("Message")?.GetValue(item)) ?? "";
            var lineValue = type.GetProperty("Line")?.GetValue(item);
            var line = lineValue switch
            {
                int i => i,
                null => 0,
                _ => (int)lineValue,
            };
            result.Add(new Diag(severity, code, message, line));
        }

        return result;
    }

    private static string? ReadString(object? value) => value?.ToString();
}

// ── expression-desugar (C23 interpolation, C36 collection expressions) ──────

internal sealed class ExpressionDesugarProfile : ProbeProfile
{
    private readonly bool _interpolation;
    private readonly ExpressionDesugarPreprocessor _preprocessor = new();

    public ExpressionDesugarProfile(bool interpolation)
        => _interpolation = interpolation;

    public override string Id => _interpolation ? "c23" : "c36";
    public override string Family => _interpolation ? "C23" : "C36";
    public override string Description => _interpolation
        ? "expression-desugar / interpolation expansion (C23; opt-in rule, non-strict)"
        : "expression-desugar / collection expressions (C36; non-strict)";
    public override bool InProcess => true;

    public override FileOutcome RunFile(string source, SemanticModel model, string filePath)
    {
        var options = new ExpressionDesugarOptions
        {
            // NOTE: subsection presence follows production defaults (the
            // rule dirty-gates dereference these objects); isolation comes
            // from InterpolationOptions.Enabled=false on the c36 side.
            CollectionExpr = _interpolation
                ? new CollectionExprOptions()
                : new CollectionExprOptions { Strict = false },
            Interpolations = _interpolation
                ? new InterpolationOptions
                {
                    Enabled = true,
                    Strict = false,
                    HandlerPolicy = HandlerUnsupportedPolicy.Error,
                    KeepFormattableString = true,
                    RewriteInExpressionTrees = false,
                    FoldConstants = true,
                }
                : new InterpolationOptions { Enabled = false },
            Initializers = null,
            NullConditional = null,
        };

        var result = _preprocessor.Preprocess(source, model, options);
        return new FileOutcome(
            MetricExtractor.Flatten(result.Statistics),
            FamilyDiagnostics.Read(result.Diagnostics, result),
            result.Changed);
    }
}

// ── numeric-boundary (C17 increment, C22 nullable lift, C34 decimal) ─────────

internal sealed class NumericBoundaryProfile : ProbeProfile
{
    private readonly NumericBoundaryPreprocessor _preprocessor = new();

    public override string Id => "c17-c22-c34";
    public override string Family => "C17/C22/C34";
    public override string Description =>
        "numeric-boundary: increment-targets (C17) + nullable lift (C22) + decimal ops (C34), non-strict";
    public override bool InProcess => true;

    public override FileOutcome RunFile(string source, SemanticModel model, string filePath)
    {
        var options = new NumericBoundaryOptions
        {
            EnableIncrementTargets = true,
            EnableNullable = true,
            EnableDecimalOps = true,
            Strict = false,
            SourceFilePath = filePath,
        };
        var result = _preprocessor.Rewrite(source, model, options);
        return new FileOutcome(
            MetricExtractor.Flatten(result.Statistics),
            FamilyDiagnostics.Read(result.Diagnostics, result),
            result.Changed);
    }
}

// ── lexical-normalize (C28 directives/file-scoped, C43 literals) ─────────────

internal sealed class LexicalNormalizeProfile : ProbeProfile
{
    private readonly LexicalNormalizePreprocessor _preprocessor = new();
    private readonly bool _literals;
    private readonly bool _fileScoped;

    public LexicalNormalizeProfile(bool literals, bool fileScoped)
    {
        _literals = literals;
        _fileScoped = fileScoped;
    }

    public override string Id => _literals ? "c43" : (_fileScoped ? "c28-file-scoped" : "c28");
    public override string Family => _literals ? "C43" : "C28";
    public override string Description => _literals
        ? "lexical-normalize raw/u8/verbatim/\\e literal normalization (C43; non-strict)"
        : "lexical-normalize directive audit (#line/#error/extern-alias/file-scoped; C28)";
    public override bool InProcess => true;

    public override FileOutcome RunFile(string source, SemanticModel model, string filePath)
    {
        var options = new LexicalNormalizeOptions
        {
            StripLineDirectives = true,
            NormalizeFileScopedNamespaces = _fileScoped,
            NormalizeRawLiterals = _literals,
            NormalizeUtf8Literals = _literals,
            NormalizeVerbatimLiterals = _literals,
            NormalizeEscEscapes = _literals,
            Strict = false,
            ReportInfo = true,
            ExternAliasSeverity = LexicalNormalizeSeverity.Warning,
        };
        var result = _preprocessor.Normalize(source, options);
        return new FileOutcome(
            MetricExtractor.Flatten(result.Statistics),
            FamilyDiagnostics.Read(result.Diagnostics, result),
            result.Changed);
    }
}

// ── foreach-source (C16 string/tuple residual, C40 enumerator/ref foreach) ───

internal sealed class ForeachSourceProfile : ProbeProfile
{
    private readonly ForeachSourcePreprocessor _preprocessor = new();

    public override string Id => "c16-c40";
    public override string Family => "C16/C40";
    public override string Description =>
        "foreach-source: string wrap/residual deconstruction (C16) + enumerator adapter/ref foreach (C40)";
    public override bool InProcess => true;

    public override FileOutcome RunFile(string source, SemanticModel model, string filePath)
    {
        var options = new ForeachSourceOptions { Strict = false, ReportInfo = true };
        var result = _preprocessor.Preprocess(source, model, options);
        return new FileOutcome(
            MetricExtractor.Flatten(result.Statistics),
            FamilyDiagnostics.Read(result.Diagnostics, result),
            result.Changed);
    }
}
