namespace CSharpToJava.Core.Pipeline.Planning;

/// <summary>
/// 构建文件生成器抽象（Maven POM / Gradle build.gradle）。
/// </summary>
public interface IBuildFileGenerator
{
    /// <summary>构建文件名（如 "pom.xml" 或 "build.gradle"）。</summary>
    string BuildFileName { get; }

    /// <summary>生成根/父构建文件。</summary>
    string GenerateRootBuildFile(JavaWorkspacePlan plan, bool genParentPom);

    /// <summary>生成模块/子模块的构建文件。</summary>
    string GenerateModuleBuildFile(JavaWorkspacePlan plan, JavaModulePlan module);
}
