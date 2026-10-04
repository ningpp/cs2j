namespace CSharpToJava.Core.AsyncNormalize;

/// <summary>
/// 本 pass 合成节点（1b 起的提升方法/同步丢弃 wrapper）挂载的
/// <see cref="Microsoft.CodeAnalysis.SyntaxAnnotation"/> 种类（设计 §4.2/§4.4、D6）：
/// residual 闸门凭该 annotation 识别唯一被豁免的 async-void 等价物（合成 wrapper）。
/// 1a 阶段只建立常量，尚无节点挂载。
/// </summary>
public static class AsyncNormalizeAnnotations
{
    /// <summary>挂在 pass 合成的提升方法/wrapper 上（设计 §4.2 D3(c)/D6）。</summary>
    public const string AnnotationKind = "Cs2jAsyncNormalize";

    /// <summary>
    /// 挂在非严格模式下已发 skip 诊断（CS2J1802/1804/1807）、原样保留的节点上。
    /// 仅内存树使用（不影响输出文本），供格式化后的 residual 闸门白名单识别
    /// （annotation 不跨 ToString→重解析存活，skip 节点身份用该内存标注携带）。
    /// </summary>
    public const string SkipAnnotationKind = "Cs2jAsyncNormalizeSkip";
}
