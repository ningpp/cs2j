using CSharpToJava.CLI.Preprocessing.Adapters;
using Xunit;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// Regression for CS0200 in the unified solution build: the make-readonly
/// pass converted the deliberately-mutable cs2j tuple replacement structs
/// (_Cs2jTupleTypes.&lt;project&gt;.cs) into readonly structs with get-only
/// properties, so rewritten tuple field writes (--state.Item5, t.Item = x)
/// failed as assignments to a read-only property. Generated _Cs2j* support
/// files must pass through untouched.
/// </summary>
public class ReadOnlyStructGeneratedSupportFileTests
{
    [Theory]
    [InlineData(@"d:\tree\_Cs2jTupleTypes.dotnet.text.regularexpressions.cs")]
    [InlineData("_Cs2jTupleTypes.dotnet-uri.cs")]
    [InlineData(@"d:\tree\_Cs2jClosedTypes.dotnet-uri.cs")]
    [InlineData("_Cs2jAnonymousTypes.MyProject.cs")]
    public void Generated_Cs2j_File_Is_Exempt(string path)
        => Assert.True(MakeReadonlyPassAdapter.IsGeneratedSupportFile(path));

    [Theory]
    [InlineData(@"d:\tree\Regex.Cache.cs")]
    [InlineData("_Cs2jNotes.txt")]
    [InlineData("My_Cs2jTupleTypes.cs")]
    public void User_File_Is_Not_Exempt(string path)
        => Assert.False(MakeReadonlyPassAdapter.IsGeneratedSupportFile(path));
}
