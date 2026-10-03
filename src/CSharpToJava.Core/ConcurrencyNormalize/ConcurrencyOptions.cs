namespace CSharpToJava.Core.ConcurrencyNormalize;

/// <summary>
/// C29 concurrency-normalize 选项（设计 §4.1）。Strict 对接
/// <c>PassChainConfiguration.Strict</c>；FrontLoadLock/FrontLoadVolatile 是 M3
/// 评审项，重写能力按设计明确不接实现（CN-1/CN-7 保留 lock/volatile 直映），
/// 置 true 即抛 <see cref="NotSupportedException"/>，不预埋半成品。
/// </summary>
public sealed record ConcurrencyOptions
{
    /// <summary>Warning 升 Error（残余/闸门类诊断的 strict 升级）。</summary>
    public bool Strict { get; init; } = true;

    /// <summary>CN-2：手写 Enter/Exit 配对逆向归一为 lock（M2 默认开）。</summary>
    public bool RewriteEnterExitPairs { get; init; } = true;

    /// <summary>CN-3：TryEnter 归一为 3 参 int 毫秒 + lockTaken 规范形（M2 默认开）。</summary>
    public bool CanonicalizeTryEnter { get; init; } = true;

    /// <summary>CN-4：Wait 重载归一（TimeSpan→int 毫秒临时量、exitContext:false 折叠）。</summary>
    public bool CanonicalizeWait { get; init; } = true;

    /// <summary>CN-6：锁对象身份审计（this/typeof/string/Type 变量 → CS2J2931）。</summary>
    public bool AuditLockIdentity { get; init; } = true;

    /// <summary>CN-7/CN-9：volatile 字段与 System.Threading.Volatile 静态 API 审计。</summary>
    public bool AuditVolatile { get; init; } = true;

    /// <summary>是否输出 Info 级诊断（2931/2940/2960/2961/CN001/CN002）。</summary>
    public bool ReportInfo { get; init; } = true;

    private bool _frontLoadLock;

    /// <summary>M3 评审项：lock 语句前置为 Enter/try/finally 降低形。设计明确不接实现。</summary>
    public bool FrontLoadLock
    {
        get => _frontLoadLock;
        init => _frontLoadLock = value
            ? throw new NotSupportedException(
                "FrontLoadLock rewrite is not implemented by design (CN-1 keeps the 'lock' " +
                "direct mapping; the option is a permanent M3 review item).")
            : value;
    }

    private bool _frontLoadVolatile;

    /// <summary>M3 评审项：volatile 字段前置为 Volatile.Read/Write。设计明确不接实现。</summary>
    public bool FrontLoadVolatile
    {
        get => _frontLoadVolatile;
        init => _frontLoadVolatile = value
            ? throw new NotSupportedException(
                "FrontLoadVolatile rewrite is not implemented by design (CN-7 keeps the " +
                "'volatile' direct mapping; the option is a permanent M3 review item).")
            : value;
    }
}
