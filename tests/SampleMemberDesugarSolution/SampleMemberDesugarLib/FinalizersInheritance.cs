namespace SampleMemberDesugarLib;

// C39 继承链样本（设计 §7.4）：基类终结器含可观测逻辑（bodyKind=Other，B 档 5204 保留），
// 派生类空终结器（Empty，B 档删除）。行为闸门验证派生实例回收时基类终结器仍执行 ——
// 该行为在 A/B 两档产物上都必须成立。

public class FinalizerBaseWithFinalizer
{
    public static int BaseFinalizerRuns;

    ~FinalizerBaseWithFinalizer()
    {
        BaseFinalizerRuns++;
    }
}

public sealed class FinalizerDerived : FinalizerBaseWithFinalizer
{
    ~FinalizerDerived()
    {
    }
}
