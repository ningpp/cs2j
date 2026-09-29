using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.MemberDesugar;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C12 indexer project-level coverage（设计 §7.2）：SampleMemberDesugarSolution
/// 跑 member-desugar 适配器，断言索引器分项统计与中间树文本；VerifyBuild/VerifyTests
/// 默认开 → run.Success 已含中间树 dotnet build + dotnet test（含跨工程 App→Lib 用点）。
/// </summary>
public class MemberDesugarIndexerProjectTests
{
    private static string FixtureDir
        => Path.Combine(AppContext.BaseDirectory, "SampleMemberDesugarSolution");
}
