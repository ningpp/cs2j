using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.Normalization;

/// <summary>
/// Java 归约残留契约的默认注册表。Descriptors/Probes 顺序固定，
/// normalization-audit 直接复用同一份定义扫描最终树。
/// </summary>
public sealed class ResidualRegistry
{
    private static readonly ResidualDescriptor[] DefaultDescriptors =
    {
        new("NORM-ENUM-ARITH", "enum-declaration", ResidualPolicy.Fail, EnumArithmeticResidualProbe.Gate),
        new("NORM-STACKALLOC", "stackalloc-lowering", ResidualPolicy.Fail, StackAllocResidualProbe.Gate),
        new("NORM-REF-STRUCT", "unsafe-ffm", ResidualPolicy.Fail, RefStructResidualProbe.Gate),
        new("NORM-REF-LOCAL", "unsafe-ref-lowering", ResidualPolicy.Fail, RefLocalResidualProbe.Gate),
        new("NORM-ASYNC-LAMBDA", "async-normalize", ResidualPolicy.Fail, AsyncLambdaResidualProbe.Gate),
        new("NORM-TUPLE-LOCAL", "tuple", ResidualPolicy.Fail, TupleLocalResidualProbe.Gate),
        new("NORM-POINTER-OP", "unsafe-ffm", ResidualPolicy.Fail, PointerOperatorResidualProbe.Gate),
    };

    private readonly IReadOnlyList<ResidualDescriptor> _descriptors;
    private readonly IReadOnlyList<IResidualProbe> _probes;

    private ResidualRegistry(
        IReadOnlyList<ResidualDescriptor> descriptors,
        IReadOnlyList<IResidualProbe> probes)
    {
        _descriptors = descriptors;
        _probes = probes;
    }

    public IReadOnlyList<ResidualDescriptor> Descriptors => _descriptors;

    public IReadOnlyList<IResidualProbe> Probes => _probes;

    public static ResidualRegistry CreateDefault()
    {
        var probes = new IResidualProbe[]
        {
            new EnumArithmeticResidualProbe(),
            new StackAllocResidualProbe(),
            new RefStructResidualProbe(),
            new RefLocalResidualProbe(),
            new AsyncLambdaResidualProbe(),
            new TupleLocalResidualProbe(),
            new PointerOperatorResidualProbe(),
        };

        return new ResidualRegistry(DefaultDescriptors, probes);
    }

    /// <summary>
    /// 按 descriptor/probe 的固定顺序扫描，同一 probe 在相同位置只返回一次。
    /// </summary>
    public IEnumerable<ResidualHit> Scan(SemanticModel model, string relativePath)
    {
        var hits = new List<ResidualHit>();
        foreach (var descriptor in _descriptors)
        {
            var probe = _probes.FirstOrDefault(p => p.Code == descriptor.Code);
            if (probe is null)
                continue;

            var seenLocations = new HashSet<(int Line, int Column, string Snippet)>();
            foreach (var hit in probe.Scan(model, relativePath))
            {
                if (!seenLocations.Add((hit.Line, hit.Column, hit.Snippet)))
                    continue;

                // 以注册表为单一事实源，保证 Code/Owner/Policy 与 descriptor 一致。
                hits.Add(hit with
                {
                    Code = descriptor.Code,
                    OwnerPassId = descriptor.OwnerPassId,
                    Policy = descriptor.Policy,
                });
            }
        }

        return hits;
    }
}
