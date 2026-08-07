using System.Collections.Generic;

namespace CSharpToJava.Core.TypeScript;

/// <summary>
/// 单个测试方法元数据（Task 39，spec §6.1）：xUnit/MSTest 特性 → vitest 映射所需
/// 信息——C# 原名（it 显示名）、是否为参数化测试（[Theory]/[DataTestMethod]）、
/// 参数名与 InlineData/DataRow 的 TS 字面量行（it.each 数据）。
/// 由语法树层 TsTestCollectorRewriter 收集，发射器据 IR 方法名（camelCase）做
/// 忽略大小写匹配。
/// </summary>
public sealed class TsTestMethodInfo
{
    /// <summary>C# 方法原名（it 显示名，如 "Add_AddsTwoNumbers"）。</summary>
    public required string MethodName { get; init; }

    /// <summary>参数化测试（[Theory]/[DataTestMethod]）→ it.each；否则 → it。</summary>
    public bool IsTheory { get; init; }

    /// <summary>C# 参数名（it.each 回调参数名；IR 参数名经 Java 命名管线消毒，语义一致）。</summary>
    public IReadOnlyList<string> ParameterNames { get; init; } = [];

    /// <summary>InlineData/DataRow 参数行，每行为一行的 TS 字面量文本（如 ["1", "2", "3"]）。</summary>
    public IReadOnlyList<IReadOnlyList<string>> InlineData { get; init; } = [];

    /// <summary>参数化测试但无可用 InlineData 数据（如 MemberData/ClassData）→ 跳过发射 + 诊断。</summary>
    public bool HasInlineData => InlineData.Count > 0;
}

/// <summary>
/// 测试类元数据（Task 39）：键为 "命名空间.类名"（全局命名空间键为类名），
/// 由 TsTestCollectorRewriter 按类登记进 ConversionContext.TsTestClasses，
/// TypeScriptEmitter 据此把测试类发射为 vitest describe/it 结构。
/// </summary>
public sealed class TsTestClassInfo
{
    public required string ClassName { get; init; }

    public IReadOnlyList<TsTestMethodInfo> Methods { get; init; } = [];
}
