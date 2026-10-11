using CSharpToJava.CLI;

namespace CSharpToJava.Tests;

/// <summary>
/// Covers synthesis of SDK global usings from project files: the SDK default
/// set behind ImplicitUsings=enable plus explicit
/// <c>&lt;Using Include/Static/Alias/Remove&gt;</c> items (the
/// dotnet.text.regularexpressions project declares its whole using set this
/// way, including System.Text — without it semantic passes cannot resolve
/// StringBuilder and leave ?? operators residual).
/// </summary>
public sealed class ImplicitGlobalUsingsTests : IDisposable
{
    private readonly string _root;

    public ImplicitGlobalUsingsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cs2j-implicit-usings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }

    private string WriteProject(string body)
    {
        var path = Path.Combine(_root, "p.csproj");
        File.WriteAllText(path, body);
        return path;
    }

    private const string SdkProjectShell = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            {0}
          </PropertyGroup>
          <ItemGroup>
            {1}
          </ItemGroup>
        </Project>
        """;

    [Fact]
    public void Explicit_Using_Include_Is_Emitted_Without_ImplicitUsings_Enabled()
    {
        var path = WriteProject(string.Format(
            SdkProjectShell,
            "<ImplicitUsings>disable</ImplicitUsings>",
            "<Using Include=\"System.Text\" />"));

        var result = ImplicitGlobalUsings.BuildForProject(path, _root);

        Assert.NotNull(result);
        Assert.Contains("global using global::System.Text;", result);
        // SDK defaults must not appear when ImplicitUsings is disabled.
        Assert.DoesNotContain("global::System.Net.Http;", result);
    }

    [Fact]
    public void Static_And_Alias_Using_Items_Render_Their_Forms()
    {
        var path = WriteProject(string.Format(
            SdkProjectShell,
            "",
            """
            <Using Include="System.Math" Static="true" />
            <Using Include="System.Console" Alias="Out" />
            <Using Include="System.Math" Static="true" Alias="MathAlias" />
            """));

        var result = ImplicitGlobalUsings.BuildForProject(path, _root);

        Assert.NotNull(result);
        Assert.Contains("global using static global::System.Math;", result);
        Assert.Contains("global using Out = global::System.Console;", result);
        Assert.Contains("global using MathAlias = static global::System.Math;", result);
    }

    [Fact]
    public void Using_Remove_Drops_A_Sdk_Default_Namespace()
    {
        var path = WriteProject(string.Format(
            SdkProjectShell,
            "<ImplicitUsings>enable</ImplicitUsings>",
            "<Using Remove=\"System.Net.Http\" />"));

        var result = ImplicitGlobalUsings.BuildForProject(path, _root);

        Assert.NotNull(result);
        Assert.Contains("global using global::System;", result);
        Assert.DoesNotContain("global::System.Net.Http;", result);
    }

    [Fact]
    public void Using_Remove_Drops_An_Explicit_Using_Item()
    {
        var path = WriteProject(string.Format(
            SdkProjectShell,
            "",
            """
            <Using Include="System.Text" />
            <Using Remove="System.Text" />
            """));

        var result = ImplicitGlobalUsings.BuildForProject(path, _root);

        Assert.Null(result);
    }

    [Fact]
    public void Duplicate_Using_Items_Are_Emitted_Once()
    {
        var path = WriteProject(string.Format(
            SdkProjectShell,
            "<ImplicitUsings>enable</ImplicitUsings>",
            "<Using Include=\"System.Text\" />"));

        var result = ImplicitGlobalUsings.BuildForProject(path, _root);

        Assert.NotNull(result);
        Assert.Single(
            System.Text.RegularExpressions.Regex.Matches(result!, "global using global::System\\.Text;"));
    }

    [Fact]
    public void Enabled_ImplicitUsings_Emits_The_Sdk_Default_Set()
    {
        var path = WriteProject(string.Format(
            SdkProjectShell,
            "<ImplicitUsings>enable</ImplicitUsings>",
            ""));

        var result = ImplicitGlobalUsings.BuildForProject(path, _root);

        Assert.NotNull(result);
        Assert.Contains("global using global::System;", result);
        Assert.Contains("global using global::System.Linq;", result);
        Assert.Contains("global using global::System.Threading.Tasks;", result);
    }

    [Fact]
    public void No_ImplicitUsings_And_No_Using_Items_Returns_Null()
    {
        var path = WriteProject(string.Format(
            SdkProjectShell,
            "<ImplicitUsings>disable</ImplicitUsings>",
            ""));

        Assert.Null(ImplicitGlobalUsings.BuildForProject(path, _root));
    }
}
