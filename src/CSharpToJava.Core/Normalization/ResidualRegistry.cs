using CSharpToJava.Core.Preprocessing;
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
        new(PassConstructIds.EnumArithmetic, "enum-declaration", ResidualPolicy.Fail, EnumArithmeticResidualProbe.Gate),
        new(PassConstructIds.StackAlloc, "stackalloc-lowering", ResidualPolicy.Fail, StackAllocResidualProbe.Gate),
        new(PassConstructIds.RefStruct, "ref-struct-lowering", ResidualPolicy.Fail, RefStructResidualProbe.Gate),
        new(PassConstructIds.RefLocal, "unsafe-ref-lowering", ResidualPolicy.Fail, RefLocalResidualProbe.Gate),
        new(PassConstructIds.AsyncLambda, "async-normalize", ResidualPolicy.Fail, AsyncLambdaResidualProbe.Gate),
        new(PassConstructIds.TupleLocal, "tuple", ResidualPolicy.Fail, TupleLocalResidualProbe.Gate),
        new(PassConstructIds.PointerOp, "unsafe-ffm", ResidualPolicy.Fail, PointerOperatorResidualProbe.Gate),
        new(PassConstructIds.PointerLocal, "unsafe-ffm", ResidualPolicy.Fail, PointerLocalResidualProbe.Gate),
        new(PassConstructIds.UnsafeBlock, "unsafe-ffm", ResidualPolicy.Fail, UnsafeBlockResidualProbe.Gate),
        new(PassConstructIds.FixedStatement, "unsafe-ffm", ResidualPolicy.Fail, FixedStatementResidualProbe.Gate),
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
            new PointerLocalResidualProbe(),
            new UnsafeBlockResidualProbe(),
            new FixedStatementResidualProbe(),
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
