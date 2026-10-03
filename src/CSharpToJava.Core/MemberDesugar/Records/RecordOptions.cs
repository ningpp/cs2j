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

    /// <summary>force：接受非密封 record 的树外派生克隆退化（CS2J5221 解除，决策 4）。
    /// 默认 false：存在元数据派生 record 时整 plan 跳过；true 时仍发 Info 级风险提示，
    /// 但放行虚工厂（树外派生若未覆写工厂会回退到基类工厂，结果类型可能改变——用户知情）。
    /// 与族 <see cref="MemberDesugarOptions.ForceQualifiedNames"/> 互不重叠：本开关为
    /// record 面全局 force，不按类型名过滤。</summary>
    public bool AllowMetadataDerivedCloneFallback { get; init; }
}
