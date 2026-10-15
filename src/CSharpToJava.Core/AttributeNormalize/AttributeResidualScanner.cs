using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.AttributeNormalize;

/// <summary>
/// 重写后 residual 闸门与 inventory 稳定性对账（design §4.4 AN-C7）：
/// ①残余 Caller attribute：按文件计数，期望值 = 输入 caller 数 − 已应用
/// SourceDeclaration plan 登记移除的声明端位置数；超出即 CS2J1322；
/// ②非 Caller attribute：输入/输出每文件计数必须相等；
/// ③skipped/excluded/从未调用方法上的合法保留不登记告警。
/// 插入点之后 span 会漂移，因此对账按「文件 + 计数」而非精确 span。
/// </summary>
internal sealed class AttributeResidualScanner
{
    private readonly AttributeNormalizeOptions _options;

    public AttributeResidualScanner(AttributeNormalizeOptions options)
    {
        _options = options;
    }

    public List<AttributeNormalizeDiagnostic> Scan(
        Compilation outputCompilation,
        AttributeInventoryReport inputInventory,
        IReadOnlyCollection<CallerInfoPlan> appliedSourcePlans,
        AttributeNormalizeStatistics statistics,
        bool strict)
    {
        var diagnostics = new List<AttributeNormalizeDiagnostic>();
        var severity = strict
            ? AttributeNormalizeSeverity.Error
            : AttributeNormalizeSeverity.Warning;

        // ----- 输入侧按文件计数 -----
        var inputCaller = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var inputNonCaller = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var occurrence in inputInventory.Occurrences)
        {
            var file = occurrence.Location.SourceTree?.FilePath;
            if (file == null) continue;
            var bucket = occurrence.Family == AttributeFamily.CallerInfo
                ? inputCaller
                : inputNonCaller;
            bucket.TryGetValue(file, out var cnt);
            bucket[file] = cnt + 1;
        }

        // ----- 已应用 plan 登记移除数 -----
        var removedByFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var plan in appliedSourcePlans)
        {
            foreach (var location in plan.DeclarationCallerAttributeLocations)
            {
                var file = location.SourceTree?.FilePath;
                if (file == null) continue;
                removedByFile.TryGetValue(file, out var cnt);
                removedByFile[file] = cnt + 1;
            }
        }

        // ----- 输出侧枚举（按树并行，再按树序归并：per-file 计数相加、
        // per-file Caller 属性列表按树序拼接，与串行遍历结果一致） -----
        var outputCaller = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var outputNonCaller = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var callerAttributesByFile
            = new Dictionary<string, List<AttributeSyntax>>(StringComparer.OrdinalIgnoreCase);

        var outputTrees = outputCompilation.SyntaxTrees.ToList();
        var perTreeCaller = new Dictionary<string, int>[outputTrees.Count];
        var perTreeNonCaller = new Dictionary<string, int>[outputTrees.Count];
        var perTreeCallerAttributes = new List<AttributeSyntax>[outputTrees.Count];
        var perTreeFile = new string[outputTrees.Count];

        System.Threading.Tasks.Parallel.For(0, outputTrees.Count, index =>
        {
            var tree = outputTrees[index];
            var root = tree.GetRoot();
            var model = outputCompilation.GetSemanticModel(tree);
            var file = tree.FilePath;

            var caller = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var nonCaller = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var callerAttributes = new List<AttributeSyntax>();

            foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
            {
                var attributeType = ResolveAttributeType(attribute, model);

                if (KnownAttributeCatalog.TryGetCallerKind(attributeType, out _))
                {
                    Increment(caller, file);
                    callerAttributes.Add(attribute);
                }
                else
                {
                    var classification = KnownAttributeCatalog.Classify(
                        attributeType, attribute, _options);
                    if (classification.Status == BridgeStatus.Ignored) continue;
                    Increment(nonCaller, file);
                }
            }

            perTreeCaller[index] = caller;
            perTreeNonCaller[index] = nonCaller;
            perTreeCallerAttributes[index] = callerAttributes;
            perTreeFile[index] = file;
        });

        for (var index = 0; index < outputTrees.Count; index++)
        {
            foreach (var entry in perTreeCaller[index])
            {
                outputCaller.TryGetValue(entry.Key, out var count);
                outputCaller[entry.Key] = count + entry.Value;
            }

            foreach (var entry in perTreeNonCaller[index])
            {
                outputNonCaller.TryGetValue(entry.Key, out var count);
                outputNonCaller[entry.Key] = count + entry.Value;
            }

            if (perTreeCallerAttributes[index].Count == 0) continue;

            var file = perTreeFile[index];
            if (!callerAttributesByFile.TryGetValue(file, out var list))
            {
                list = new List<AttributeSyntax>();
                callerAttributesByFile[file] = list;
            }

            list.AddRange(perTreeCallerAttributes[index]);
        }

        // ----- 对账：Caller residual -----
        var allFiles = new HashSet<string>(
            inputCaller.Keys.Concat(outputCaller.Keys).Concat(removedByFile.Keys),
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in allFiles)
        {
            inputCaller.TryGetValue(file, out var inCaller);
            outputCaller.TryGetValue(file, out var outCaller);
            removedByFile.TryGetValue(file, out var removed);

            var expected = inCaller - removed;
            if (outCaller == expected) continue;

            var attribute = outCaller > expected
                ? callerAttributesByFile.TryGetValue(file, out var list) && list.Count > expected
                    ? list[expected]
                    : null
                : null;

            var (line, column) = GetLineColumn(attribute);
            var message = outCaller > expected
                ? $"Residual Caller attribute without skip registration after rewrite (expected {expected}, found {outCaller})"
                : $"Caller attribute count unexpectedly decreased after rewrite (expected {expected}, found {outCaller})";

            diagnostics.Add(new AttributeNormalizeDiagnostic(
                severity,
                AttributeNormalizeDiagnosticCode.CS2J1322,
                message,
                file,
                line,
                column));
        }

        // ----- 对账：非 Caller inventory 稳定性 -----
        var nonCallerFiles = new HashSet<string>(
            inputNonCaller.Keys.Concat(outputNonCaller.Keys),
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in nonCallerFiles)
        {
            inputNonCaller.TryGetValue(file, out var inCnt);
            outputNonCaller.TryGetValue(file, out var outCnt);
            if (inCnt == outCnt) continue;

            diagnostics.Add(new AttributeNormalizeDiagnostic(
                severity,
                AttributeNormalizeDiagnosticCode.CS2J1322,
                $"Attribute inventory occurrence count changed unexpectedly (non-Caller expected {inCnt}, found {outCnt})",
                file));
        }

        statistics.ResidualCount = diagnostics.Count;
        return diagnostics;
    }

    private static INamedTypeSymbol? ResolveAttributeType(
        AttributeSyntax attribute,
        SemanticModel model)
    {
        var symbolInfo = model.GetSymbolInfo(attribute);
        var ctor = symbolInfo.Symbol as Microsoft.CodeAnalysis.IMethodSymbol
            ?? symbolInfo.CandidateSymbols.OfType<Microsoft.CodeAnalysis.IMethodSymbol>().FirstOrDefault();
        return ctor?.ContainingType;
    }

    private static void Increment(Dictionary<string, int> bucket, string file)
    {
        bucket.TryGetValue(file, out var cnt);
        bucket[file] = cnt + 1;
    }

    private static (int Line, int Column) GetLineColumn(AttributeSyntax? attribute)
    {
        if (attribute == null) return (0, 0);
        var pos = attribute.GetLocation().GetLineSpan().StartLinePosition;
        return (pos.Line + 1, pos.Character + 1);
    }
}
