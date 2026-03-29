using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EnumMemberAccessTests
{
    [Fact]
    public void ConversionPipeline_FormatsQualifiedRegularEnumMembers_AcrossExpressionsAndSwitchLabels()
    {
        var result = Convert(@"
using Core.Geometry;

namespace Core.Geometry
{
    public enum Direction { North, South, East, West }
}

public class Sample
{
    public Direction Flip(Direction dir)
    {
        if (dir == Core.Geometry.Direction.South)
        {
            return Core.Geometry.Direction.North;
        }

        switch (dir)
        {
            case Core.Geometry.Direction.West:
                Use(Core.Geometry.Direction.East);
                dir = Core.Geometry.Direction.North;
                break;
        }

        return dir;
    }

    void Use(Direction dir) { }
}");

        Assert.True(result.Success);
        Assert.Contains("if (dir == Direction.South)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return Direction.North;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("case West:", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("use(Direction.East);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("dir = Direction.North;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Core.Geometry.Direction.North", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Core.Geometry.Direction.East", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("case Direction.West:", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_KeepsFlagsEnumMembers_QualifiedInExpressionsAndSwitchLabels()
    {
        var result = Convert(@"
using System;

[Flags]
public enum Permissions
{
    Read = 1,
    Write = 2,
    Execute = 4
}

public class Sample
{
    public Permissions Normalize(Permissions perms)
    {
        if (perms == Permissions.Execute)
        {
            return Permissions.Read;
        }

        switch (perms)
        {
            case Permissions.Read:
                Use(Permissions.Write);
                perms = Permissions.Write;
                break;
        }

        return perms;
    }

    void Use(Permissions perms) { }
}");

        Assert.True(result.Success);
        Assert.Contains("if (perms == Permissions.Execute)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return Permissions.Read;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("case Permissions.Read:", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("use(Permissions.Write);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("perms = Permissions.Write;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("case Read:", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConversionPipeline_ResolvesRelativeNamespaceEnumPaths_BySemanticSymbol()
    {
        var result = Convert(@"
namespace Microsoft.Msagl.Core.Geometry
{
    public enum Direction { North, South, East, West }
}

namespace Microsoft.Msagl.Layout
{
    public class Sample
    {
        public Microsoft.Msagl.Core.Geometry.Direction Flip(Microsoft.Msagl.Core.Geometry.Direction dir)
        {
            if (dir == Core.Geometry.Direction.South)
            {
                return Core.Geometry.Direction.North;
            }

            switch (dir)
            {
                case Core.Geometry.Direction.West:
                    dir = Core.Geometry.Direction.East;
                    break;
            }

            return dir;
        }
    }
}");

        Assert.True(result.Success);
        Assert.Contains("if (dir == Direction.South)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return Direction.North;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("case West:", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("dir = Direction.East;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("if (dir == Core.Geometry.Direction.South)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("return Core.Geometry.Direction.North;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("case Core.Geometry.Direction.West:", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("dir = Core.Geometry.Direction.East;", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}