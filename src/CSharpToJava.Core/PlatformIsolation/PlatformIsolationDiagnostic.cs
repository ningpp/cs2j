using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.PlatformIsolation;

/// <summary>
/// PI0xx 动作/报告码（设计 §6 诊断策略）。M1 发射 PI001/060/061/070/090；
/// M2 追加 PI010/011/012/020/030/031/040。检测码（CS2J3001/3101/3102/3201/
/// 3202/3203）沿用三 analyzer 原文原码，不在此重复定义。
/// </summary>
public static class PlatformIsolationDiagnosticCodes
{
    /// <summary>站点入清单（每站点一条，含 family/身份/位置/默认档）。Info。</summary>
    public const string SiteRecorded = "PI001";

    /// <summary>PNSE 抛错成员桩生成（附签名移植明细 D5）。Info（apply）。</summary>
    public const string ThrowStubGenerated = "PI010";

    /// <summary>OS 探针调用点重定向至 Cs2jPlatformProbe（附原值→保守值）。Info（apply）。</summary>
    public const string ProbeRedirected = "PI011";

    /// <summary>OS guard 属性擦除（含 AssemblyInfo 登记）。Info（apply）。</summary>
    public const string GuardAttributeStripped = "PI012";

    /// <summary>Facade 端口/partial 委托缝生成（M3）。Info（apply）。</summary>
    public const string FacadeGenerated = "PI020";

    /// <summary>Reject 裁决执行（必带原 CS2J3xxx 码、身份、建议）。Error（apply）。</summary>
    public const string RejectExecuted = "PI030";

    /// <summary>residual 闸门违规：输出树仍有非登记平台身份。Info（report）/Error（apply）。</summary>
    public const string ResidualGateViolated = "PI031";

    /// <summary>生成树语法校验失败，原文保留。Error（apply）。</summary>
    public const string GeneratedSyntaxInvalid = "PI040";

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