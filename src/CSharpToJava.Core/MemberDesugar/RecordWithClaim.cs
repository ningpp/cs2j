using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.MemberDesugar.Records;

namespace CSharpToJava.Core.MemberDesugar;

/// <summary>
/// C11 PropertyRule 与 C27 RecordRule 共用的 with 站点认领谓词（开放问题 6 裁决的
/// 最小共享面钩子）。唯一入口、同一 <see cref="RecordClaimIndex"/> 结论：PropertyRule
/// 的 CS2J5124 闸门只据此让位，RecordRule 据此产 plan，两侧不可能分歧。
/// Records 子集开关关闭时恒为 false（默认关零行为）。
/// </summary>
internal static class RecordWithClaim
{
    /// <summary>
    /// 该 with 初始化器直接赋值是否会被 Records 规则改写为克隆体上的 set_X 调用。
    /// 仅对「with 初始化器的直接可写属性赋值 + 接收者为可行源内显式 record class」
    /// 返回 true；嵌套对象创建初始化器内的赋值、字段赋值、跳过族站点均为 false。
    /// </summary>
    public static bool IsClaimedByRecords(
        AssignmentExpressionSyntax assign, SemanticModel model, MemberDesugarOptions options)
    {
        if (!options.Records.Enabled) return false;
        if (model.Compilation is not CSharpCompilation compilation) return false;
        return RecordClaimIndex.For(compilation).IsAssignmentClaimed(assign, model);
    }
}
