using System.Text;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.MemberDesugar;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C13 project-level coverage (design §7.2)：直接构造 MemberDesugarPassAdapter +
/// ProjectPassRunner。VerifyBuild/VerifyTests 默认开，run.Success 即证明中间树通过
/// dotnet build + dotnet test——同时实证事件改写后适配器的 Cs2jPrelude 释放与
/// ProjectReference 注入（事件访问器体引用 CsPrelude.Combine/Remove）。
/// </summary>
/// <remarks>
/// 与 <see cref="MemberDesugarProjectTests"/> 同集合串行：两个类的输出目录都嵌在
/// 共享 fixture 目录（bin\...\SampleMemberDesugarSolution\.test-output-*）下，并行运行时
/// 一方的 fixture 拷贝会扫入另一方的活动输出树，随后被其 finally 删除打断
/// （DirectoryNotFoundException）。同集合保证互斥。
/// </remarks>
[Collection("SampleMemberDesugarSolution")]
public class MemberDesugarEventProjectTests
{
    private static string FixtureDir
        => Path.Combine(AppContext.BaseDirectory, "SampleMemberDesugarSolution");
}
