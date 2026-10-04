namespace CSharpToJava.Core.AsyncNormalize;

/// <summary>
/// 本 pass 合成节点（1b 起的提升方法/同步丢弃 wrapper）挂载的
/// <see cref="Microsoft.CodeAnalysis.SyntaxAnnotation"/> 种类（设计 §4.2/§4.4、D6）：
/// residual 闸门凭该 annotation 识别唯一被豁免的 async-void 等价物（合成 wrapper）。
/// 1a 阶段只建立常量，尚无节点挂载。
/// </summary>
public static class AsyncNormalizeAnnotations
{
    public const string AnnotationKind = "Cs2jAsyncNormalize";
}
