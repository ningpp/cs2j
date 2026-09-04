using System.Text.RegularExpressions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for the C# non-generic IList.Add(object) → Java CSharpIList.add(Object)
/// return type bridge. C# IList.Add returns int (insertion index), but the
/// Java compat CSharpGenericIList.add() returns boolean. When a class implements
/// CSharpIList and delegates to a CSharpGenericIList, the generated method body
/// must convert boolean → int (return insertion index).
/// </summary>
public class NonGenericIListAddReturnTypeTests
{
    [Fact]
    public void NonGenericIListAdd_DelegatingToGenericList_ConvertsBooleanToInt()
    {
        // C# WrapperForIList implements IList and delegates Add to ((IList)_list).Add.
        // In Java, CSharpIList.add(Object) returns int, but CSharpGenericIList.add()
        // returns boolean. The generated body must not do:
        //   return ((CSharpGenericIList<Object>)(_list)).add(value);  // boolean → int ERROR
        // Instead it should return the insertion index.
        var result = Convert(@"
using System.Collections;
using System.Collections.Generic;
public class WrapperForIList : IList {
    private IList _list;
    public WrapperForIList() { _list = new List<object>(); }
    public int Add(object value) { return ((IList)_list).Add(value); }
    public void Clear() { ((IList)_list).Clear(); }
    public bool Contains(object value) { return ((IList)_list).Contains(value); }
    public void CopyTo(Array array, int index) { ((IList)_list).CopyTo(array, index); }
    public int Count { get { return ((IList)_list).Count; } }
    public bool IsFixedSize { get { return ((IList)_list).IsFixedSize; } }
    public bool IsReadOnly { get { return ((IList)_list).IsReadOnly; } }
    public bool IsSynchronized { get { return ((IList)_list).IsSynchronized; } }
    public object SyncRoot { get { return ((IList)_list).SyncRoot; } }
    public IEnumerator GetEnumerator() { return ((IList)_list).GetEnumerator(); }
    public int IndexOf(object value) { return ((IList)_list).IndexOf(value); }
    public void Insert(int index, object value) { ((IList)_list).Insert(index, value); }
    public void Remove(object value) { ((IList)_list).Remove(value); }
    public void RemoveAt(int index) { ((IList)_list).RemoveAt(index); }
    public object this[int index] { get { return ((IList)_list)[index]; } set { ((IList)_list)[index] = value; } }
}");
        Assert.True(result.Success, result.GeneratedCode ?? string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;

        // Find the add method that returns int — it must NOT directly return
        // a boolean-producing .add() call without conversion.
        var addMethodPattern = new Regex(
            @"public\s+int\s+add\s*\(\s*Object\s+\w+\s*\)\s*\{([^}]*)\}",
            RegexOptions.Singleline);
        var addMatch = addMethodPattern.Match(code);
        Assert.True(addMatch.Success, $"Expected 'int add(Object)' method in:\n{code}");

        var addBody = addMatch.Groups[1].Value.Trim();
        // The body must NOT be a bare "return X.add(Y);" where add() returns boolean.
        // CSharpList.add() and CSharpGenericIList.add() both return boolean, not int.
        var bareBooleanReturn = new Regex(@"^return\s+.*\.add\([^)]*\)\s*;\s*$", RegexOptions.Singleline);
        Assert.False(
            bareBooleanReturn.IsMatch(addBody),
            $"int add(Object) body directly returns boolean add() result without int conversion:\n{addBody}\nFull code:\n{code}");
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
