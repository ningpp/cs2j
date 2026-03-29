namespace CSharpToJava.Core.Pipeline.Planning;

/// <summary>
/// 顶层 Java 工作区输出规划，包含所有模块及全局构建元数据。
/// </summary>
public sealed class JavaWorkspacePlan
{
    public required string GroupId { get; init; }
    public required string ArtifactId { get; init; }
    public required string Version { get; init; }
    public required string JavaVersion { get; init; }
    public required IReadOnlyList<JavaModulePlan> Modules { get; init; }

    /// <summary>单模块快捷判定。</summary>
    public bool IsSingleModule => Modules.Count == 1;
}

/// <summary>
/// 单个 Java 模块的输出规划。
/// </summary>
public sealed class JavaModulePlan
{
    public required string ModuleName { get; init; }
    public required bool IsTestOnly { get; init; }
    public JavaSourceSets SourceSets { get; init; } = new();
    public IReadOnlyList<JavaDependency> Dependencies { get; init; } = [];
    public IReadOnlyList<string> RequiredCompatPacks { get; init; } = [];
    public bool HasTestSources => SourceSets.TestSources.Count > 0;
}

/// <summary>
/// Maven 标准 source set 布局（main/test 各自含源码与资源）。
/// </summary>
public sealed class JavaSourceSets
{
    public IReadOnlyList<string> MainSources { get; init; } = [];
    public IReadOnlyList<string> TestSources { get; init; } = [];
    public IReadOnlyList<JavaResourceItem> MainResources { get; init; } = [];
    public IReadOnlyList<JavaResourceItem> TestResources { get; init; } = [];
}

/// <summary>
/// 资源文件映射（源路径 → 输出路径）。
/// </summary>
public sealed class JavaResourceItem
{
    public required string SourcePath { get; init; }
    public required string TargetPath { get; init; }
}

/// <summary>
/// Java 依赖声明（Maven 坐标 + scope）。
/// </summary>
public sealed class JavaDependency
{
    public required string GroupId { get; init; }
    public required string ArtifactId { get; init; }
    public required string Version { get; init; }
    public JavaDependencyScope Scope { get; init; } = JavaDependencyScope.Compile;

    /// <summary>是否为工作区内部模块引用（使用 ${project.version}）。</summary>
    public bool IsInternal { get; init; }
}

public enum JavaDependencyScope
{
    Compile,
    Test,
    Runtime,
    Provided
}
