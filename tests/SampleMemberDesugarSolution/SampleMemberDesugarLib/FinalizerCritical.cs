using System.Runtime.ConstrainedExecution;

namespace SampleMemberDesugarLib;

// C39 关键终结器样本（设计 §7.4 / 决策矩阵）：CriticalFinalizerObject 派生类的终结器
// 在任何策略下都只报告 CS2J5205（A 档）或拒绝删除（B 档），forceRemoveTypes 不可解除。

public sealed class FinalizerCritical : CriticalFinalizerObject
{
    ~FinalizerCritical()
    {
    }
}
