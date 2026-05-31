using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Pipeline.Compatibility;

/// <summary>
/// 兼容层 Pack 注册表 — 管理所有 Pack 并协调按需生成。
/// </summary>
public class CompatibilityPackRegistry
{
    private readonly List<ICompatibilityPack> _packs = new();

    /// <summary>
    /// 注册默认的所有 Pack。
    /// </summary>
    public static CompatibilityPackRegistry CreateDefault()
    {
        var registry = new CompatibilityPackRegistry();
        registry.Register(new RefHolderPack());
        registry.Register(new DotNetCorePack());
        registry.Register(new RegexPack());
        registry.Register(new JsonPack());
        registry.Register(new TracePack());
        registry.Register(new IoPack());
        registry.Register(new TestPack());
        return registry;
    }

    public void Register(ICompatibilityPack pack)
    {
        _packs.Add(pack);
    }

    /// <summary>
    /// 获取所有适用的 Pack。
    /// </summary>
    public IReadOnlyList<ICompatibilityPack> GetApplicablePacks(IReadOnlyList<ConversionResult> convertedResults, string targetPackage)
    {
        var context = new CompatibilityPackContext(convertedResults, targetPackage);
        return _packs.Where(pack => pack.IsApplicable(context)).ToList();
    }
}
