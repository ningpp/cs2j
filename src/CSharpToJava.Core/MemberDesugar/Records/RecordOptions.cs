namespace CSharpToJava.Core.MemberDesugar.Records;

/// <summary>rules.records 子节选项（C27，设计 §12「默认关子集开关、不抢跑」）。
/// 形状对齐 C39 <c>rules.finalizers</c> / C50 <c>rules.fieldKeyword</c>：
/// 经 Program.cs 配置透传，不新增 pass/适配器/verb。族级 <see cref="MemberDesugarOptions.Strict"/>
/// 不在子节内重复。</summary>
public sealed class RecordOptions
{
    /// <summary>是否启用 record <c>with</c> 去糖。默认 false：Records 规则零分析、零 plan、
    /// 零改写，输出与现状逐字节一致；显式 true 后才执行 RC/WR 改写。
    /// §8 门槛（尤其 Java 半区复制构造发射）达标前不翻默认值。</summary>
    public bool Enabled { get; init; }
}
