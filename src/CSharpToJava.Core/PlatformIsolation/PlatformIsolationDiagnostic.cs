using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.PlatformIsolation;

/// <summary>
/// PI0xx 动作/报告码（设计 §6 诊断策略）。M1 只发射
/// PI001 / PI060 / PI061 / PI070 / PI090；检测码（CS2J3001/3101/3102/3201/
/// 3202/3203）沿用三 analyzer 原文原码，不在此重复定义。
/// </summary>
public static class PlatformIsolationDiagnosticCodes
{
    /// <summary>站点入清单（每站点一条，含 family/身份/位置/默认档）。Info。</summary>
    public const string SiteRecorded = "PI001";

    /// <summary>站点命中配置 suppress（strict 升 Warning，永不静默）。Info/Warning。</summary>
    public const string ConfigSuppressed = "PI060";

    /// <summary>配置陈旧条目（源内已无对应站点）。Warning。</summary>
    public const string StaleConfigEntry = "PI061";

    /// <summary>报告写出（路径、站点数、对账行）。Info。</summary>
    public const string ReportWritten = "PI070";

    /// <summary>单文件/无工程编译下跨文件 plan 跳过或身份降级。Warning（D10）。</summary>
    public const string CrossFilePlanSkipped = "PI090";
}

/// <summary>platform-isolation 通道自身诊断（PI0xx）。检测码不在本类型承载。</summary>
public sealed record PlatformIsolationDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string? FilePath = null);