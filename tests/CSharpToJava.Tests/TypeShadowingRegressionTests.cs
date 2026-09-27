namespace CSharpToJava.Tests;

using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

/// <summary>
/// Regression tests for the syntax-based type fallback: when semantic lookups fail
/// (members of merged partial-type syntax trees are detached from the compilation
/// tree, so GetTypeInfo returns null), a compat simple-name mapping must not shadow
/// a type the project itself defines in source.
/// </summary>
public class TypeShadowingRegressionTests
{
    private static async Task<IReadOnlyList<ConversionResult>> ConvertAsync(params SourceFile[] sourceFiles)
    {
        var options = new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };
        var pipeline = new ProjectConversionPipeline(options);
        return await pipeline.ConvertProjectAsync(sourceFiles);
    }

    [Fact]
    public async Task SyntaxFallback_PrefersProjectTypeOverCompatSimpleName()
    {
        var results = await ConvertAsync(
            new SourceFile
            {
                FilePath = "Geometry/Rectangle.cs",
                Content = "namespace Microsoft.Msagl.Core.Geometry { public struct Rectangle { public int Width { get; set; } } }",
            },
            new SourceFile
            {
                FilePath = "Layout/IObstacle.cs",
                Content = "using Microsoft.Msagl.Core.Geometry; namespace Microsoft.Msagl.Core.Layout { interface IObstacle { Rectangle Rectangle { get; } } }",
            });

        Assert.All(results, r => Assert.True(r.Success, string.Join("; ", r.Diagnostics.Select(d => d.Message))));

        var obstacle = results.Single(r => r.FileName == "IObstacle.java");
        // The project's own Rectangle (Microsoft.Msagl.Core.Geometry) must win over
        // the compat mapping (System.Drawing.Rectangle → dotnet.drawing.swing.Rectangle).
        Assert.DoesNotContain("dotnet.drawing.swing.Rectangle", obstacle.GeneratedCode);
        Assert.Contains("import Microsoft.Msagl.Core.Geometry.*;", obstacle.GeneratedCode);
    }

    [Fact]
    public async Task SyntaxFallback_StillMapsUnshadowedCompatSimpleName()
    {
        // No source-defined project type named Bitmap: the compat simple-name
        // mapping (System.Drawing.Bitmap → dotnet.drawing.swing.Bitmap) must still apply.
        var results = await ConvertAsync(
            new SourceFile
            {
                FilePath = "Drawing/IThing.cs",
                Content = "using System.Drawing; namespace Microsoft.Msagl.Drawing { interface IThing { Bitmap Bmp { get; } } }",
            });

        Assert.All(results, r => Assert.True(r.Success, string.Join("; ", r.Diagnostics.Select(d => d.Message))));

        var thing = results.Single(r => r.FileName == "IThing.java");
        Assert.Contains("dotnet.drawing.swing.Bitmap", thing.GeneratedCode);
    }

    [Fact]
    public async Task AliasCollision_InheritedStaticProperty_ShadowsAliasType()
    {
        // Control.MouseButtons (inherited static property) shadows the using alias in
        // expression position: C# member lookup wins over using aliases. The identifier
        // must become getMouseButtons(), not the [Flags] type mapping (int).
        var results = await ConvertAsync(
            new SourceFile
            {
                FilePath = "Control.cs",
                Content = """
namespace System.Windows.Forms {
    [System.Flags] public enum MouseButtons { None = 0, Left = 1048576 }
    public class Control {
        public static MouseButtons MouseButtons { get; set; }
    }
}
""",
            },
            new SourceFile
            {
                FilePath = "GViewer.cs",
                Content = """
using MouseButtons = System.Windows.Forms.MouseButtons;
namespace Microsoft.Msagl.GraphViewerGdi {
    public class GViewer : System.Windows.Forms.Control {
        bool LeftPressed() { return MouseButtons == MouseButtons.Left; }
    }
}
""",
            });

        Assert.All(results, r => Assert.True(r.Success, string.Join("; ", r.Diagnostics.Select(d => d.Message))));

        var gviewer = results.Single(r => r.FileName == "GViewer.java");
        Assert.Contains("getMouseButtons() == MouseButtons.Left", gviewer.GeneratedCode);
        Assert.DoesNotContain("int == MouseButtons.Left", gviewer.GeneratedCode);
    }
}

