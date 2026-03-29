using Microsoft.CodeAnalysis.CSharp;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.TypeMapping;

namespace CSharpToJava.Tests;

public class Cs2jLibraryFactoryTests
{
    [Fact]
    public void CreateSingleFile_CreatesSingleDocumentLibrary()
    {
        var sourceCode = "class Sample { }";
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode, path: "Sample.cs");
        var compilation = CSharpCompilation.Create("SampleAssembly", [syntaxTree]);

        var library = Cs2jLibraryFactory.CreateSingleFile(sourceCode, "Sample.cs", syntaxTree, compilation);

        Assert.Equal(Cs2jLibraryInputKind.SingleFile, library.InputKind);
        Assert.True(library.IsSingleProject);
        Assert.True(library.IsSingleDocument);
        Assert.Equal(compilation, library.PrimaryCompilation);
        Assert.Equal("Sample.cs", library.Documents[0].FilePath);
        Assert.Equal(sourceCode, library.Documents[0].Content);
    }

    [Fact]
    public void CreateFromSourceFiles_CreatesSyntheticProjectLibrary()
    {
        var sourceFiles = new[]
        {
            new SourceFile { FilePath = "A.cs", Content = "class A { }" },
            new SourceFile { FilePath = "B.cs", Content = "class B { }" },
        };

        var context = new ConversionContext(new ConversionOptions(), new TypeMappingRegistry(new TypeMappingConfig()));
        var compilation = ProjectCompilationBuilder.BuildCompilation(sourceFiles, context);

        Assert.NotNull(compilation);

        var library = Cs2jLibraryFactory.CreateFromSourceFiles(sourceFiles, compilation!);

        Assert.Equal(Cs2jLibraryInputKind.SourceSet, library.InputKind);
        Assert.True(library.IsSingleProject);
        Assert.Equal(2, library.Documents.Count);
        Assert.Equal(2, library.Projects[0].Documents.Count);
        Assert.Equal(compilation, library.PrimaryCompilation);
    }

    [Fact]
    public void CreateFromCompilation_ExcludesSyntheticSyntaxTrees()
    {
        var sourceTree = CSharpSyntaxTree.ParseText("class C { }", path: "C.cs");
        var syntheticTree = CSharpSyntaxTree.ParseText("global using System;", path: "<synthetic>");
        var compilation = CSharpCompilation.Create("CompilationAssembly", [sourceTree, syntheticTree]);

        var library = Cs2jLibraryFactory.CreateFromCompilation(compilation, projectName: "CompilationAssembly");

        Assert.Equal(Cs2jLibraryInputKind.Compilation, library.InputKind);
        Assert.Single(library.Documents);
        Assert.Equal("C.cs", library.Documents[0].FilePath);
        Assert.Equal("CompilationAssembly", library.Projects[0].Name);
    }
}