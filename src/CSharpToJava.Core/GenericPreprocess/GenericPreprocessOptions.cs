namespace CSharpToJava.Core.GenericPreprocess;

public sealed class GenericPreprocessOptions
{
    /// <summary>
    /// 是否将 default(T) 重写为 default 字面量（C# 7.2+）。默认 true。
    /// 在 `default(T) == null` 这类二义性上下文中，重写器会自动使用
    /// `(object)default` 形式来规避 CS0034，确保语义等价。
    /// </summary>
    public bool RewriteDefaultExpression { get; init; } = true;

    /// <summary>是否将 new T()（类型参数实例化）重写为 Activator.CreateInstance&lt;T&gt;()。</summary>
    public bool RewriteNewTypeParam { get; init; } = true;

    /// <summary>是否将 new T[n]（类型参数数组）重写为 (T[])Array.CreateInstance(typeof(T), n)。</summary>
    public bool RewriteNewTypeParamArray { get; init; } = true;

    /// <summary>是否统计 typeof(T) 用法（不做改写）。</summary>
    public bool TrackTypeOfExpression { get; init; } = true;

    /// <summary>是否统计 is T / as T 用法（不做改写）。</summary>
    public bool TrackIsAsTypeParam { get; init; } = true;

    /// <summary>
    /// 是否对「仅作为泛型转发类型参数使用的类型参数」在方法体前部插入
    /// var __cs2j_typeof_&lt;T&gt; = typeof(T); 语句，以触发转换器的
    /// AddTypeOfDefaultRequirements 识别，让 Java 方法签名正确添加 _cs2j_T
    /// 前导参数。默认 true。
    /// </summary>
    public bool InsertTypeOfForForwardedTypeParams { get; init; } = true;

    /// <summary>
    /// 是否在泛型 override 方法间同步 typeof(T) 哨兵语句。
    /// 当一个 override 方法体中使用了 typeof(TProperty) 等运行时类型参数模式时，
    /// 转换器会为该 override 和 base/abstract 方法签名注入 _cs2j_T 前导参数。
    /// 但兄弟 override 方法（方法体中不使用 typeof(TProperty)）不会获得相同参数，
    /// 导致 Java override 签名不匹配。
    ///
    /// 启用此选项后，预处理器会在全局扫描阶段识别所有兄弟 override 方法中的
    /// typeof(T) 使用，并为缺少这些使用的 override 方法体开头注入哨兵语句
    /// var __cs2j_typeof_&lt;T&gt; = typeof(&lt;T&gt;); 以确保转换器统一注入参数。
    /// 默认 true。
    /// </summary>
    public bool SyncTypeOfForOverrideSiblings { get; init; } = true;

    /// <summary>是否输出 Info 级诊断</summary>
    public bool ReportInfo { get; init; } = false;

    /// <summary>严格模式：未改写的模式视为错误</summary>
    public bool Strict { get; init; }
}
