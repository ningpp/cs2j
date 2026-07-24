using System.Text;

namespace CSharpToJava.Core.Pipeline.Planning;

/// <summary>
/// Maven POM 文件生成器。从 Program.cs 中提取的 POM 生成逻辑。
/// </summary>
public sealed class MavenPomGenerator : IBuildFileGenerator
{
    public string BuildFileName => "pom.xml";

    public string GenerateMavenConfig() => """
# Keep selected-module builds inside the generated reactor.
# Equivalent to -am.
--also-make
""";

    public string GenerateRootBuildFile(JavaWorkspacePlan plan, bool genParentPom)
    {
        if (!genParentPom && plan.IsSingleModule)
            return GenerateSingleModulePom(plan, plan.Modules[0]);

        return GenerateParentPom(plan);
    }

    public string GenerateModuleBuildFile(JavaWorkspacePlan plan, JavaModulePlan module)
    {
        return GenerateChildModulePom(plan, module);
    }

    private static string GenerateSingleModulePom(JavaWorkspacePlan plan, JavaModulePlan module)
    {
        int javaVer = ParseJavaVersion(plan.JavaVersion);
        bool includeTests = module.HasTestSources;

        var deps = BuildDependencySection(module.Dependencies);
        var testDeps = includeTests ? JUnitDependencies() : string.Empty;
        var surefirePlugin = includeTests ? SurefirePlugin() : string.Empty;

        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<project xmlns=""http://maven.apache.org/POM/4.0.0""
    xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
    xsi:schemaLocation=""http://maven.apache.org/POM/4.0.0 http://maven.apache.org/xsd/maven-4.0.0.xsd"">

    <modelVersion>4.0.0</modelVersion>
    <groupId>{plan.GroupId}</groupId>
    <artifactId>{plan.ArtifactId}</artifactId>
    <version>{plan.Version}</version>
    <packaging>jar</packaging>

    <properties>
        <java.version>{javaVer}</java.version>
        <project.build.sourceEncoding>UTF-8</project.build.sourceEncoding>
        <maven.compiler.source>${{java.version}}</maven.compiler.source>
        <maven.compiler.target>${{java.version}}</maven.compiler.target>
    </properties>

    <build>
        <plugins>
            {CompilerPlugin(javaVer)}
{surefirePlugin}
        </plugins>
    </build>

    <dependencies>
{deps}{testDeps}
    </dependencies>

</project>
";
    }

    private static string GenerateParentPom(JavaWorkspacePlan plan)
    {
        int javaVer = ParseJavaVersion(plan.JavaVersion);
        var moduleSection = string.Join(Environment.NewLine,
            plan.Modules.Select(m => $"        <module>{m.ModuleName}</module>"));

        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<project xmlns=""http://maven.apache.org/POM/4.0.0""
    xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
    xsi:schemaLocation=""http://maven.apache.org/POM/4.0.0 http://maven.apache.org/xsd/maven-4.0.0.xsd"">

    <modelVersion>4.0.0</modelVersion>
    <groupId>{plan.GroupId}</groupId>
    <artifactId>{plan.ArtifactId}</artifactId>
    <version>{plan.Version}</version>
    <packaging>pom</packaging>

    <properties>
        <java.version>{javaVer}</java.version>
        <project.build.sourceEncoding>UTF-8</project.build.sourceEncoding>
        <maven.compiler.source>${{java.version}}</maven.compiler.source>
        <maven.compiler.target>${{java.version}}</maven.compiler.target>
    </properties>

    <modules>
{moduleSection}
    </modules>

</project>
";
    }

    private static string GenerateChildModulePom(JavaWorkspacePlan plan, JavaModulePlan module)
    {
        int javaVer = ParseJavaVersion(plan.JavaVersion);

        var deps = BuildDependencySection(module.Dependencies);
        var testDeps = module.HasTestSources ? JUnitDependencies() : string.Empty;
        var surefirePlugin = module.HasTestSources ? SurefirePlugin() : string.Empty;
        var testJarPlugin = module.ProduceTestJar ? TestJarPlugin() : string.Empty;

        return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<project xmlns=""http://maven.apache.org/POM/4.0.0""
    xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""
    xsi:schemaLocation=""http://maven.apache.org/POM/4.0.0 http://maven.apache.org/xsd/maven-4.0.0.xsd"">

    <modelVersion>4.0.0</modelVersion>

    <parent>
        <groupId>{plan.GroupId}</groupId>
        <artifactId>{plan.ArtifactId}</artifactId>
        <version>{plan.Version}</version>
    </parent>

    <artifactId>{module.ModuleName}</artifactId>
    <packaging>jar</packaging>

    <properties>
        <java.version>{javaVer}</java.version>
        <project.build.sourceEncoding>UTF-8</project.build.sourceEncoding>
        <maven.compiler.source>${{java.version}}</maven.compiler.source>
        <maven.compiler.target>${{java.version}}</maven.compiler.target>
    </properties>

    <build>
        <plugins>
            {CompilerPlugin(javaVer)}
{surefirePlugin}
{testJarPlugin}
        </plugins>
    </build>

    <dependencies>
{deps}{testDeps}
    </dependencies>

</project>
";
    }

    // ── 依赖渲染 ────────────────────────────────────────────

    private static string BuildDependencySection(IReadOnlyList<JavaDependency> dependencies)
    {
        if (dependencies.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var dep in dependencies)
        {
            sb.AppendLine($@"        <dependency>
            <groupId>{dep.GroupId}</groupId>
            <artifactId>{dep.ArtifactId}</artifactId>
            <version>{(dep.IsInternal ? "${project.version}" : dep.Version)}</version>{ScopeTag(dep.Scope)}{TypeTag(dep.Type)}
        </dependency>");
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    private static string ScopeTag(JavaDependencyScope scope) =>
        scope == JavaDependencyScope.Compile
            ? string.Empty
            : $"\n            <scope>{scope.ToString().ToLowerInvariant()}</scope>";

    private static string TypeTag(string? type) =>
        string.IsNullOrWhiteSpace(type)
            ? string.Empty
            : $"\n            <type>{type}</type>";

    // ── 固定片段 ────────────────────────────────────────────

    private static string JUnitDependencies() => @"
        <dependency>
            <groupId>org.junit.jupiter</groupId>
            <artifactId>junit-jupiter</artifactId>
            <version>5.11.4</version>
            <scope>test</scope>
        </dependency>
        <dependency>
            <groupId>org.junit.jupiter</groupId>
            <artifactId>junit-jupiter-params</artifactId>
            <version>5.11.4</version>
            <scope>test</scope>
        </dependency>";

    private static string SurefirePlugin() => @"
            <plugin>
                <groupId>org.apache.maven.plugins</groupId>
                <artifactId>maven-surefire-plugin</artifactId>
                <version>3.3.1</version>
                <configuration>
                    <argLine>-Djdk.net.URLClassPath.disableClassPathURLCheck=true</argLine>
                    <forkedProcessTimeoutInSeconds>120</forkedProcessTimeoutInSeconds>
                    <forkedProcessExitTimeoutInSeconds>120</forkedProcessExitTimeoutInSeconds>
                </configuration>
            </plugin>";

    private static string TestJarPlugin() => @"
            <plugin>
                <groupId>org.apache.maven.plugins</groupId>
                <artifactId>maven-jar-plugin</artifactId>
                <version>3.4.2</version>
                <executions>
                    <execution>
                        <goals>
                            <goal>test-jar</goal>
                        </goals>
                    </execution>
                </executions>
            </plugin>";

    private static string CompilerPlugin(int javaVer) => $@"<plugin>
                <groupId>org.apache.maven.plugins</groupId>
                <artifactId>maven-compiler-plugin</artifactId>
                <version>3.13.0</version>
                <configuration>
                    <source>${{java.version}}</source>
                    <target>${{java.version}}</target>
                    <encoding>UTF-8</encoding>
                    <compilerArgs>
                        <arg>-Xmaxerrs</arg>
                        <arg>1000000</arg>
                    </compilerArgs>
                </configuration>
            </plugin>";

    // ── 工具方法 ────────────────────────────────────────────

    private static int ParseJavaVersion(string javaVersion) =>
        int.Parse(javaVersion.Replace("Java", ""));
}
