namespace CSharpToJava.Core.MemberDesugar.Properties;

/// <summary>rules.fieldKeyword 子节选项（C50，设计 §12「默认关子集开关、不抢跑」）。
/// 形状对齐 C39 <c>rules.finalizers</c>：经 Program.cs 配置透传，不新增 pass/适配器/verb。
/// 族级 <see cref="MemberDesugarOptions.Strict"/> 不在子节内重复。</summary>
public sealed class FieldKeywordOptions
{
    /// <summary>是否启用 field 关键字归一。默认 false：field 规则零执行，
    /// 含 field 的属性维持 C11 v1 的 CS2J5122 声明级跳过，输出与现状逐字节一致；
    /// 显式 true 后才执行 FK-1~FK-9 声明侧归一。</summary>
    public bool Enabled { get; init; }
}
