using CSharpToJava.CLI;
using CSharpToJava.Core.TypeScript;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 37 NpmProjectPlan（spec §6.1）测试：workspaces 根/模块 package.json、
/// tsconfig.base.json（strict/ES2022/NodeNext/useDefineForClassFields:false）、
/// 模块 tsconfig.json、测试包 vitest 配置、包名规约、产物指纹按目标区分。
/// </summary>
public class TsNpmProjectPlanTests
{
    private static NpmWorkspacePlan BuildSampleWorkspace()
    {
        return new NpmWorkspacePlanBuilder()
            .WorkspaceName("sample-workspace")
            .Version("1.0.0")
            .AddProject("Sample.Core",
                dependencies: [NpmProjectDependency.Workspace("@sample/shared")])
            .AddProject("Sample.Core.Tests", isTestOnly: true)
            .Build();
    }

    [Fact]
    public void RootPackageJson_Declares_Workspaces_And_DevDependencies()
    {
        var plan = BuildSampleWorkspace();
        var json = NpmProjectFiles.GenerateRootPackageJson(plan);

        Assert.Contains("sample-workspace", json, StringComparison.Ordinal);
        Assert.Contains("1.0.0", json, StringComparison.Ordinal);
        Assert.Contains("private", json, StringComparison.Ordinal);
        Assert.Contains("workspaces", json, StringComparison.Ordinal);
        Assert.Contains("modules/*", json, StringComparison.Ordinal);
        Assert.Contains("typescript", json, StringComparison.Ordinal);
        Assert.Contains("^7.0.2", json, StringComparison.Ordinal);
        Assert.Contains("vitest", json, StringComparison.Ordinal);
        Assert.Contains("^4.1.10", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TsconfigBase_Sets_Strict_Target_NodeNext_And_UseDefineForClassFields_False()
    {
        var tsconfig = NpmProjectFiles.GenerateTsconfigBase();

        Assert.Contains("strict", tsconfig, StringComparison.Ordinal);
        Assert.Contains("ES2022", tsconfig, StringComparison.Ordinal);
        Assert.Contains("NodeNext", tsconfig, StringComparison.Ordinal);
        Assert.Contains("declaration", tsconfig, StringComparison.Ordinal);
        // 关键：默认 true 时派生类字段声明会以 define 语义覆写基类构造器赋值（spec §6.1）
        Assert.Contains("useDefineForClassFields", tsconfig, StringComparison.Ordinal);
        Assert.Contains("useDefineForClassFields\": false", tsconfig, StringComparison.Ordinal);
        Assert.DoesNotContain("useDefineForClassFields\": true", tsconfig, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectPackageJson_Declares_Compat_And_Workspace_References()
    {
        var plan = BuildSampleWorkspace();
        var project = plan.Projects.Single(p => p.ModuleName == "sample-core");
        var json = NpmProjectFiles.GenerateProjectPackageJson(plan, project);

        Assert.Contains("sample-core", json, StringComparison.Ordinal);
        Assert.Contains("module", json, StringComparison.Ordinal);
        Assert.Contains("private", json, StringComparison.Ordinal);
        // 自动加入 compat 依赖（spec §6.1 模块 package.json 必含）
        Assert.Contains("@csharptotypescript/compat", json, StringComparison.Ordinal);
        Assert.Contains("0.1.0", json, StringComparison.Ordinal);
        // 项目引用 → workspace 依赖
        Assert.Contains("@sample/shared", json, StringComparison.Ordinal);
        Assert.Contains("workspace:*", json, StringComparison.Ordinal);
        // 非测试包不需要 vitest script
        Assert.DoesNotContain("vitest run", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TestProject_PackageJson_Adds_Vitest_Script_And_Config_Generated()
    {
        var plan = BuildSampleWorkspace();
        var testProject = plan.Projects.Single(p => p.IsTestOnly);
        Assert.True(testProject.RequiresVitestConfig);

        var json = NpmProjectFiles.GenerateProjectPackageJson(plan, testProject);
        Assert.Contains("scripts", json, StringComparison.Ordinal);
        Assert.Contains("vitest run", json, StringComparison.Ordinal);

        var vitest = NpmProjectFiles.GenerateVitestConfig(testProject);
        Assert.Contains("vitest/config", vitest, StringComparison.Ordinal);
        Assert.Contains("src/**/*.test.ts", vitest, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectTsconfig_Extends_Workspace_Base_And_Sets_OutDir()
    {
        var plan = BuildSampleWorkspace();
        var project = plan.Projects.Single(p => p.ModuleName == "sample-core");
        var tsconfig = NpmProjectFiles.GenerateProjectTsconfig(project);

        Assert.Contains("../../tsconfig.base.json", tsconfig, StringComparison.Ordinal);
        Assert.Contains("dist", tsconfig, StringComparison.Ordinal);
        Assert.Contains("rootDir", tsconfig, StringComparison.Ordinal);
        Assert.Contains("src", tsconfig, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_Scope_Produces_Scoped_Package_Names()
    {
        var plan = new NpmWorkspacePlanBuilder()
            .WorkspaceName("proj-workspace")
            .Scope("proj")
            .AddProject("Core")
            .Build();

        var project = Assert.Single(plan.Projects);
        Assert.Equal("@proj/core", project.PackageName);
        Assert.Equal("core", project.ModuleName);
    }

    [Fact]
    public void NormalizePackageName_Lowercases_And_KebabCases()
    {
        Assert.Equal("sample-core", NpmNaming.NormalizePackageName("Sample.Core"));
        Assert.Equal("sample-core-tests", NpmNaming.NormalizePackageName("Sample.Core.Tests"));
        Assert.Equal("yamldotnet-net8-0", NpmNaming.NormalizePackageName("YamlDotNet(net8.0)"));
        Assert.Equal("module", NpmNaming.NormalizePackageName("!@#"));
    }

    [Fact]
    public void Fingerprint_OptionTokens_Distinguish_Target_Language()
    {
        // 产物指纹按目标区分（Task 37）：--target 切换不得被增量缓存错误跳过。
        var javaTokens = Program.GetProjectConversionOptionTokens(new ConvertProjectOptions { Target = "java" });
        var tsTokens = Program.GetProjectConversionOptionTokens(new ConvertProjectOptions { Target = "typescript" });

        Assert.Contains(javaTokens, token => token == "target=java");
        Assert.Contains(tsTokens, token => token == "target=typescript");
        Assert.NotEqual(string.Join("|", javaTokens), string.Join("|", tsTokens));
    }
}
