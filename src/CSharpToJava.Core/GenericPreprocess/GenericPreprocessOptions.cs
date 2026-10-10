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

    /// <summary>是否输出 Info 级诊断</summary>
    public bool ReportInfo { get; init; } = false;

    /// <summary>严格模式：未改写的模式视为错误</summary>
    public bool Strict { get; init; }
}
