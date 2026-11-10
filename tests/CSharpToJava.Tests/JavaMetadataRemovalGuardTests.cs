using System;
using System.IO;
using System.Linq;
using CSharpToJava.Core.Context;
using CSharpToJava.TypeMapping;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Guards the removal of the JDK metadata subsystem (<c>config/java/</c> + JavaLibraryIndex).
/// Type and method mapping is driven solely by <c>config/TypeMappings.json</c>; the old
/// design (per-module lazy loading of 4336 JSON descriptors, metadata-driven method-name
/// auto-deduction, API validation and platform-module attribution) must not come back.
/// </summary>
public class JavaMetadataRemovalGuardTests
{
    [Fact]
    public void ConversionOptions_HasNoJavaMetadataPath()
        => Assert.Null(typeof(ConversionOptions).GetProperty("JavaMetadataPath"));

    [Fact]
    public void CoreAssembly_HasNoJavaLibraryTypes()
    {
        var names = typeof(TypeMappingRegistry).Assembly
            .GetTypes()
            .Select(type => type.FullName ?? type.Name)
            .ToArray();
        Assert.DoesNotContain(names, name => name.Contains("JavaLibrary", StringComparison.Ordinal));
        Assert.DoesNotContain(names, name => name.Contains("JavaModel", StringComparison.Ordinal));
    }

    [Fact]
    public void Repository_HasNoJavaMetadataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CSharpToJavaConverter.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        Assert.False(Directory.Exists(Path.Combine(dir!.FullName, "config", "java")));
    }

    [Fact]
    public void MapMethod_DoesNotAutoDeduceUnmappedMethods()
    {
        var registry = new TypeMappingRegistry(new TypeMappingConfig());
        Assert.Null(registry.MapMethod("System.Collections.Generic.Dictionary`2", "ContainsKey", 1));
    }
}
