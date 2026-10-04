namespace CSharpToJava.Core.AsyncNormalize;

/// <summary>
/// C18 async-normalize 选项（设计 §2 D14、§4.5）。翻转后默认关、长期 opt-in；
/// <see cref="Strict"/> 对接 <c>PassChainConfiguration.Strict</c>。
/// </summary>
/// <param name="Strict">true（默认）时不可归一形态为 Error；false 时 1801-1808 降 Warning，节点原样保留。</param>
/// <param name="Mode">归一形态；<see cref="AsyncNormalizeMode.Heavyweight"/> 为保留值，入口选择即抛。</param>
/// <param name="LiftAsyncLambdas">async lambda 提升总开关（1b 阶段起消费；1a 仅保留选项面）。</param>
/// <param name="StripConfigureAwait">是否把 <c>ConfigureAwait(bool)</c> 归一剥离为 receiver（非严格模式也照剥）。</param>
/// <param name="RejectAsyncVoid">是否对用户书写的 async void 方法/局部函数发 CS2J1803。</param>
public sealed record AsyncNormalizeOptions(
    bool Strict = true,
    AsyncNormalizeMode Mode = AsyncNormalizeMode.Lightweight,
    bool LiftAsyncLambdas = true,
    bool StripConfigureAwait = true,
    bool RejectAsyncVoid = true);
