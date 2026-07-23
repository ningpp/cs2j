using System.Diagnostics;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class CSharpXmlCompileRegressionTests
{
    [Fact]
    public void OutDecimalParameter_AssignedIntLiteral_WrapsWithDecimalValueOf()
    {
        var result = Convert("""
class Sample
{
    void Calculate(out decimal minOccurs, out decimal maxOccurs)
    {
        minOccurs = 0;
        maxOccurs = 0;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains(".value = Decimal.valueOf(0)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".value = 0;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DecimalPropertyCompoundAssignment_ViaMemberAccess_UsesCompatMultiply()
    {
        var result = Convert("""
class Particle
{
    public decimal MinOccurs { get; set; }
    public decimal MaxOccurs { get; set; }
}

class Sample
{
    void Multiply(Particle baseParticle, Particle baseGroupBase)
    {
        baseParticle.MinOccurs *= baseGroupBase.MinOccurs;
        if (baseParticle.MaxOccurs != decimal.MaxValue)
        {
            baseParticle.MaxOccurs *= baseGroupBase.MaxOccurs;
        }
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("baseParticle.setMinOccurs(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".multiply(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("baseParticle.getMinOccurs() * baseGroupBase.getMinOccurs()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DecimalFieldMemberAccess_AssignedIntLiteral_WrapsWithDecimalValueOf()
    {
        var result = Convert("""
class Sample
{
    decimal _value;

    void SetZero()
    {
        this._value = 0;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("this._value = Decimal.valueOf(0)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("this._value = 0;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DecimalConditionalExpression_IntLiteralBranch_WrapsWithDecimalValueOf()
    {
        var result = Convert("""
class Container
{
    public decimal MaxOccurs { get; set; }
}

class Sample
{
    void SetOccurs(Container container, bool repeats)
    {
        container.MaxOccurs = repeats ? decimal.MaxValue : 1;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Decimal.valueOf(1)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("? Decimal.MAX_VALUE : 1", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Type_Assembly_And_Assembly_GetName_BridgedToCompatHelpers()
    {
        var result = Convert("""
            using System;
            using System.Reflection;

            class Sample
            {
                void Check(Type type, Assembly assembly)
                {
                    var a1 = type.Assembly;
                    var a2 = typeof(string).Assembly;
                    var name = assembly.GetName();
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("TypeHelper.getAssembly", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("AssemblyCompat", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("assembly.getPackage()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("type.getPackage()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FunctionQuery_Ctor_AssignsListToGenericIList_DoesNotWrapWithFrom()
    {
        var result = Convert("""
            using System.Collections.Generic;

            class Query { }

            class Sample
            {
                private IList<Query> _args;

                public Sample(List<Query> args)
                {
                    _args = args;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("_args = args", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CSharpGenericIList.from(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void XmlSchemas_Merge_IListLocalToIListParameter_WrapsWithFrom()
    {
        var result = Convert("""
            using System.Collections;
            using System.Collections.Generic;

            class Sample
            {
                private ICollection GetRawCollection() => null;

                private void Merge(IList originals)
                {
                }

                private void M()
                {
                    IList originals = (IList)GetRawCollection();
                    Merge(originals);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("CSharpGenericIList.from(originals)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ICollectionProperty_ReturnsDictionaryValues_WrapsWithCSharpICollectionFrom()
    {
        var result = Convert("""
            using System.Collections;
            using System.Collections.Generic;

            class Sample
            {
                private Dictionary<string, object> _table = new Dictionary<string, object>();

                internal ICollection Values
                {
                    get { return _table.Values; }
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("CSharpICollection.from(_table.values())", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void PropertyWrapperMethods_GetXxx_AreConvertedToJavaBeanGetters()
    {
        var result = Convert("""
            class XmlRootAttribute
            {
                internal bool IsNullableSpecified { get { return false; } }
                internal string Key { get { return ""; } }

                internal bool GetIsNullableSpecified() { return IsNullableSpecified; }
                internal string GetKey() { return this.Key; }
            }

            class XmlAnyElementAttribute
            {
                internal bool NamespaceSpecified { get { return false; } }
                internal bool GetNamespaceSpecified() { return NamespaceSpecified; }
            }

            class Sample
            {
                void M(XmlRootAttribute root, XmlAnyElementAttribute any)
                {
                    bool a = root.GetIsNullableSpecified();
                    bool b = any.GetNamespaceSpecified();
                    string k = root.GetKey();
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("root.getIsNullableSpecified()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("any.getNamespaceSpecified()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("root.getKey()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("root.GetIsNullableSpecified()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("any.GetNamespaceSpecified()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("root.GetKey()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedClassWithDelegateFields_GeneratesAccessorsAndUsages()
    {
        var result = Convert("""
            using System;

            class Outer
            {
                internal class Member
                {
                    public Action<object> Source;
                    public Func<object> GetSource;
                    public Action<object> ArraySource;
                    public Action<object> CheckSpecifiedSource;
                    public Action<object> ChoiceSource;

                    public Member(string name) { }
                }

                void Use(Member member, object value)
                {
                    member?.ChoiceSource?.Invoke("x");
                    if (member?.ArraySource != null)
                    {
                        member?.ArraySource(value);
                    }
                    else
                    {
                        member?.Source?.Invoke(value);
                        member?.CheckSpecifiedSource?.Invoke(true);
                    }
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // Fields must be emitted.
        Assert.Contains("public Consumer<Object> Source;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public Consumer<Object> ArraySource;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public Consumer<Object> CheckSpecifiedSource;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public Consumer<Object> ChoiceSource;", result.GeneratedCode, StringComparison.Ordinal);

        // Getters/setters must be emitted for the public fields.
        Assert.Contains("public Consumer<Object> getSource()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public void setSource(Consumer<Object> value)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public Consumer<Object> getArraySource()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public void setArraySource(Consumer<Object> value)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public Consumer<Object> getCheckSpecifiedSource()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public void setCheckSpecifiedSource(Consumer<Object> value)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public Consumer<Object> getChoiceSource()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("public void setChoiceSource(Consumer<Object> value)", result.GeneratedCode, StringComparison.Ordinal);

        // Conditional access usages must use the getters and SAM method invocations.
        Assert.Contains("member.getChoiceSource().accept(\"x\")", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("member.getArraySource().accept(value)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("member.getSource().accept(value)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("member.getCheckSpecifiedSource().accept(true)", result.GeneratedCode, StringComparison.Ordinal);

        Console.WriteLine(result.GeneratedCode);
    }

    [Fact]
    public void BoolVariable_GetType_And_ObjectAssignment_BoxesToBoolean()
    {
        var result = Convert("""
            using System;

            class Sample
            {
                void M()
                {
                    bool b = true;
                    Type t = b.GetType();
                    object o = b;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Console.WriteLine("BOOL:\n" + result.GeneratedCode);
        Assert.Contains("Boolean.valueOf(b).getClass()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Object o = Boolean.valueOf(b)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CharVariable_CompareTo_And_ObjectAssignment_BoxesToCharacter()
    {
        var result = Convert("""
            class Sample
            {
                void M()
                {
                    char c = 'a';
                    int r = c.CompareTo('b');
                    object o = c;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Console.WriteLine("CHAR:\n" + result.GeneratedCode);
        Assert.Contains("Character.compare(c, 'b')", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Object o = Character.valueOf(c)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NullableCharDefault_AssignedToObject_GeneratesNullNotCharacterValueOfNull()
    {
        var result = Convert("""
            class Sample
            {
                void M()
                {
                    object value = null;
                    value = default(System.Nullable<char>);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Console.WriteLine("NULLABLE_CHAR:\n" + result.GeneratedCode);
        Assert.DoesNotContain("Character.valueOf(null)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("value = null", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DateTimeStyles_NoCurrentDateDefault_MappedToIntegerHelper()
    {
        var result = Convert("""
            using System.Globalization;

            class Sample
            {
                void M()
                {
                    var style = DateTimeStyles.AllowLeadingWhite | DateTimeStyles.AllowTrailingWhite | DateTimeStyles.NoCurrentDateDefault | DateTimeStyles.RoundtripKind;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("IntegerHelper.NoCurrentDateDefault", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UIntMaxValue_AssignedToUIntVariable_GetsIntCast()
    {
        var result = Convert("""
            class Sample
            {
                void M()
                {
                    uint u = uint.MaxValue;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Console.WriteLine("UINT:\n" + result.GeneratedCode);
        Assert.DoesNotContain("= 4294967295L;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("(int) 4294967295L", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LongValue_AssignedToIntVariable_GetsExplicitCast()
    {
        var result = Convert("""
            class Sample
            {
                void M()
                {
                    long l = 100L;
                    int i = (int)l;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Console.WriteLine("LONG:\n" + result.GeneratedCode);
        Assert.Contains("int i = (int)(l)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ValueTypeInNonGenericForeach_BoxesIterableTypeArgument()
    {
        var result = Convert("""
            using System.Collections;

            class Sample
            {
                void M(ICollection c)
                {
                    foreach (int symbol in c)
                    {
                    }
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Console.WriteLine("FOREACH:\n" + result.GeneratedCode);
        Assert.Contains("Iterable<Integer>)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Iterable<int>", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeArgument_PassedToMemberInfoParameter_WrapsWithAsMemberInfo()
    {
        var result = Convert("""
            using System;
            using System.Reflection;

            class XmlAttributes
            {
                public static object GetAttr(MemberInfo memberInfo, Type attrType)
                {
                    return null;
                }
            }

            class Sample
            {
                string GenerateKey(Type type)
                {
                    return (string)XmlAttributes.GetAttr(type, typeof(ObsoleteAttribute));
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("TypeHelper.asMemberInfo(type)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.TypeHelper;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitInterfaceIndexer_INameScope_GeneratesObjectSetMethod()
    {
        var result = Convert(@"
public interface INameScope
{
    object this[object name, object ns] { get; set; }
}

public class NameTable : INameScope
{
    public object this[string name, string ns]
    {
        get { return null; }
        set { }
    }

    object INameScope.this[object name, object ns]
    {
        get { return null; }
        set { }
    }
}

public class StructMapping : INameScope
{
    object INameScope.this[object name, object ns]
    {
        get { return null; }
        set { }
    }
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode;
        System.IO.File.WriteAllText(@"d:\code\cs2j\indexer-task25-output.java", code);

        // The Java interface requires set(Object, Object, Object); both explicit implementations must match.
        Assert.Contains("public Object set(Object name, Object ns, Object value)", code, StringComparison.Ordinal);
        // StructMapping must implement the indexer as a setter, not the dictionary-style put method.
        Assert.DoesNotContain("public Object put(Object name, Object ns, Object value)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void StringInterface_Indexer_ExplicitImplementation_MatchesInterfaceSignature()
    {
        var result = Convert(@"
internal interface INameScope
{
    object this[string name, string ns] { get; set; }
}

internal class NameTable : INameScope
{
    internal object this[string name, string ns]
    {
        get { return null; }
        set { }
    }

    object INameScope.this[string name, string ns]
    {
        get { return null; }
        set { }
    }
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        System.IO.File.WriteAllText(@"d:\code\cs2j\temp-string-interface.java", code);

        // Interface and explicit implementation must agree on parameter types.
        Assert.Contains("public Object set(String name, String ns, Object value)", code, StringComparison.Ordinal);
        Assert.Contains("public Object get(String name, String ns)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("public Object set(Object name, Object ns, Object value)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("public Object get(Object name, Object ns)", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Project_StringInterface_Indexer_ExplicitImplementation_MatchesInterfaceSignature()
    {
        var result = await ConvertProjectAsync(new[]
        {
            ("INameScope.cs", @"
namespace System.Xml.Serialization
{
    internal interface INameScope
    {
        object this[string name, string ns] { get; set; }
    }
}
"),
            ("NameTable.cs", @"
namespace System.Xml.Serialization
{
    internal class NameTable : INameScope
    {
        private System.Collections.Generic.Dictionary<NameKey, object> _table = new System.Collections.Generic.Dictionary<NameKey, object>();

        internal object this[string name, string ns]
        {
            get { return null; }
            set { }
        }

        object INameScope.this[string name, string ns]
        {
            get { return null; }
            set { }
        }
    }

    internal struct NameKey
    {
        public NameKey(string name, string ns) { }
    }
}
"),
            ("StructMapping.cs", @"
namespace System.Xml.Serialization
{
    internal class StructMapping : INameScope
    {
        object INameScope.this[string name, string ns]
        {
            get { return null; }
            set { }
        }
    }
}
"),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        System.IO.File.WriteAllText(@"d:\code\cs2j\project-string-interface.java", code);

        // All generated types in the same compilation unit must share the same signature.
        Assert.Contains("public Object set(String name, String ns, Object value)", code, StringComparison.Ordinal);
        Assert.Contains("public Object get(String name, String ns)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("public Object set(Object name, Object ns, Object value)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("public Object get(Object name, Object ns)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionBase_With_IEnumerableT_GeneratesCompilableJava()
    {
        var result = Convert(@"
using System.Collections;
using System.Collections.Generic;

public class MySchema { }

public class MySchemaEnumerator : IEnumerator<MySchema>
{
    public MySchema Current => null;
    object IEnumerator.Current => null;
    public bool MoveNext() => false;
    public void Reset() { }
    public void Dispose() { }
}

public class MySchemaCollection : CollectionBase, IEnumerable<MySchema>
{
    public int Add(MySchema schema) { return List.Add(schema); }
    public void Remove(MySchema schema) { List.Remove(schema); }
    public bool Contains(MySchema schema) { return List.Contains(schema); }
    public void CopyTo(MySchema[] array, int index) { }
    public int IndexOf(MySchema schema) { return List.IndexOf(schema); }
    public void Insert(int index, MySchema schema) { }

    IEnumerator<MySchema> IEnumerable<MySchema>.GetEnumerator() { return new MySchemaEnumerator(); }
}

public class Sample
{
    public void Iterate(MySchemaCollection coll)
    {
        foreach (MySchema schema in coll) { }
        foreach (MySchema schema in coll.List) { }
    }
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode;
        // CSharpCollectionBase already provides the non-generic IEnumerable contract; adding
        // CSharpGenericIterable<T> would create conflicting add/remove/iterator signatures.
        Assert.DoesNotContain("implements CSharpGenericIterable<", code, StringComparison.Ordinal);
        // The generated iterator() must be compatible with CSharpCollectionBase.iterator().
        Assert.Contains("public CSharpEnumerator iterator()", code, StringComparison.Ordinal);
        // Iterating a CollectionBase-derived class with a typed variable needs a wildcard cast.
        Assert.Contains("(Iterable<MySchema>)(Iterable<?>)(coll)", code, StringComparison.Ordinal);
        Assert.Contains("(Iterable<MySchema>)(Iterable<?>)(coll.getList())", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Temp_Diagnostics_FailingForeachCases()
    {
        // Diagnostic output for failing tests in SameTypeErasureConflictTests,
        // LinqConcatSetOperatorTests, and ArrayForeachNotCollectedTests.
        var cases = new[] {
            (@"
using System.Collections.Generic;

class Rectangle { }
class Point { }

class Box
{
    public int Count;

    public Box(IEnumerable<Point> points)
    {
        Count = 100;
        foreach (var p in points)
            Count++;
    }

    public Box(IEnumerable<Rectangle> rectangles)
    {
        Count = 200;
        foreach (var r in rectangles)
            Count++;
    }

    public static Box FromRectangles(IEnumerable<Rectangle> rectangles)
    {
        return new Box(rectangles);
    }
}
", "erasure"),
            (@"
using System.Collections.Generic;
using System.Linq;

class Sample {
    public static IEnumerable<string> ConcatLists(IList<string> strs1, IList<string> strs2) {
        return strs1.Select(str => str).Concat(strs2.Select(str => str));
    }
}
", "concat"),
            (@"
using System.Collections.Generic;
using System.Linq;
class Point { public int X, Y; }
class Sample
{
    void Test(IEnumerable<Point> source, Point[] arr)
    {
        {
            var pts = source.Where(p => p.X > 0);
            foreach (var p in pts) { }
        }
        {
            Point[] pts = arr;
            foreach (var p in pts) { }
        }
    }
}
", "arrayvar")
        };

        foreach (var (src, name) in cases)
        {
            var result = Convert(src);
            System.IO.File.WriteAllText($@"d:\code\cs2j\temp-diag-{name}.java", result.GeneratedCode);
            Assert.True(result.Success, $"{name}: " + string.Join("\n", result.Diagnostics));
        }
    }

    [Fact]
    public void SystemArrayCast_ToStringArray_UsesToArray()
    {
        var result = Convert("""
            using System;

            class Sample
            {
                Array EnsureArrayIndex(Array a, int index, Type elementType) => a;

                void M()
                {
                    string[] ids = (string[])EnsureArrayIndex(null, 0, typeof(string));
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains(".toArray(String.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(String[])(ensureArrayIndex", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NullArgument_OverloadWithStructParameter_GetsExplicitCast()
    {
        var result = Convert("""
            public struct DeserializationEvents { }

            public class Serializer
            {
                public object Deserialize(object reader)
                {
                    return Deserialize(reader, null);
                }

                public object Deserialize(object reader, DeserializationEvents events)
                {
                    return null;
                }

                public object Deserialize(object reader, string encodingStyle)
                {
                    return null;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("deserialize(reader, (String) null)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("deserialize(reader, null)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NameTableToArray_TypeOfElementAccessor_ChainsToArray()
    {
        var result = Convert("""
            using System;

            class ElementAccessor { }

            class NameTable
            {
                public Array ToArray(Type type) => null;
            }

            class Sample
            {
                void M(NameTable table)
                {
                    ElementAccessor[] elements = (ElementAccessor[])table.ToArray(typeof(ElementAccessor));
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("table.toArray(ElementAccessor.class).toArray(ElementAccessor.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(ElementAccessor[])(table.toArray", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NameTableToArray_TypeOfXmlSchemaObject_ChainsToArray()
    {
        var result = Convert("""
            using System;

            namespace System.Xml.Schema
            {
                public class XmlSchemaObject { }
            }

            class NameTable
            {
                public Array ToArray(Type type) => null;
            }

            class Sample
            {
                void M(NameTable table)
                {
                    System.Xml.Schema.XmlSchemaObject[] items = (System.Xml.Schema.XmlSchemaObject[])table.ToArray(typeof(System.Xml.Schema.XmlSchemaObject));
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("table.toArray(XmlSchemaObject.class).toArray(XmlSchemaObject.class)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(XmlSchemaObject[])(table.toArray", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericEnumeratorWithImplicitTypedCurrentAndExplicitObjectCurrent_GetCurrentReturnsTyped()
    {
        var result = Convert("""
            using System.Collections;
            using System.Collections.Generic;

            public class Schema { }

            public class SchemaEnumerator : IEnumerator<Schema>, System.Collections.IEnumerator
            {
                private Schema[] _data;
                private int _pos;

                public SchemaEnumerator(Schema[] data)
                {
                    _data = data;
                    _pos = -1;
                }

                public bool MoveNext()
                {
                    if (_pos >= _data.Length - 1) return false;
                    _pos++;
                    return true;
                }

                public Schema Current => _data[_pos];

                object System.Collections.IEnumerator.Current => _data[_pos];

                public void Reset() { _pos = -1; }

                public void Dispose() { }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode;

        // Must implement generic enumerator with typed element.
        Assert.Contains("implements CSharpGenericEnumerator<Schema>", code, StringComparison.Ordinal);
        // The typed Current property must keep the standard getCurrent() name to satisfy the interface.
        Assert.Contains("public Schema getCurrent()", code, StringComparison.Ordinal);
        // The non-generic explicit Current must not hijack the standard name.
        Assert.DoesNotContain("public Object getCurrent()", code, StringComparison.Ordinal);
        // The typed accessor must not be renamed to the $Class suffix.
        Assert.DoesNotContain("getCurrent$Class()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void IEnumerableT_WithNonGenericGetEnumerator_IteratorReturnsGenericEnumerator()
    {
        var result = Convert("""
using System;
using System.Collections;
using System.Collections.Generic;

class StringCollection : IEnumerable<string>
{
    private string[] _items;
    public StringCollection(string[] items) { _items = items; }

    public IEnumerator GetEnumerator()
    {
        return new StringEnumerator(_items);
    }

    IEnumerator<string> IEnumerable<string>.GetEnumerator()
    {
        return new StringGenericEnumerator(_items);
    }

    private class StringEnumerator : IEnumerator
    {
        private string[] _items;
        private int _pos;
        public StringEnumerator(string[] items) { _items = items; _pos = -1; }
        public bool MoveNext()
        {
            _pos++;
            return _pos < _items.Length;
        }
        public object Current => _pos < 0 || _pos >= _items.Length ? null : _items[_pos];
        public void Reset() { _pos = -1; }
    }

    private class StringGenericEnumerator : IEnumerator<string>
    {
        private string[] _items;
        private int _pos;
        public StringGenericEnumerator(string[] items) { _items = items; _pos = -1; }
        public bool MoveNext()
        {
            _pos++;
            return _pos < _items.Length;
        }
        public string Current => _pos < 0 || _pos >= _items.Length ? null : _items[_pos];
        object IEnumerator.Current => Current;
        public void Reset() { _pos = -1; }
        public void Dispose() { }
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("implements CSharpGenericIterable<String>", code, StringComparison.Ordinal);
        Assert.Contains("public CSharpGenericEnumerator<String> iterator()", code, StringComparison.Ordinal);
        Assert.Contains("class StringEnumerator implements CSharpGenericEnumerator<String>", code, StringComparison.Ordinal);
        Assert.Contains("public String getCurrent()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("public Object getCurrent()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void CharIsDigit_StringOverload_GeneratesCharAtInvocation()
    {
        var result = Convert("""
            using System;

            class Sample
            {
                bool Check(string name, int index)
                {
                    return Char.IsDigit(name, index);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("Character.isDigit(name.charAt(index))", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Character.isDigit(name, index)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void PropertyWithExplicitBackingField_SetViaExplicitSetterMethod_WritesBackingField()
    {
        var result = Convert("""
            class MemberInfo { }

            class XmlChoiceIdentifierAttribute
            {
                private MemberInfo _memberInfo;

                internal MemberInfo MemberInfo
                {
                    get { return _memberInfo; }
                    set { _memberInfo = value; }
                }

                internal void SetMemberInfo(MemberInfo memberInfo)
                {
                    MemberInfo = memberInfo;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("this._memberInfo = memberInfo", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("this.memberInfo = memberInfo", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Type_IsDefined_WithAttributeType_BridgedToTypeHelper()
    {
        var result = Convert("""
            using System;

            class Sample
            {
                bool CheckFlags(Type type)
                {
                    return type.IsDefined(typeof(FlagsAttribute), false);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("TypeHelper.isDefined(type, FlagsAttribute.class, false)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.TypeHelper;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("type.isDefined(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeOfNullableUnboundGeneric_MapsToOptionalClassLiteral()
    {
        var result = Convert("""
            using System;

            class Sample
            {
                bool IsNullable(Type type)
                {
                    return type.IsGenericType
                        && type.GetGenericTypeDefinition() == typeof(Nullable<>).GetGenericTypeDefinition();
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Console.WriteLine("NULLABLE UNBOUND:\n" + result.GeneratedCode);
        Assert.Contains("Optional.class", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("T.class", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeOfArraySegmentUnboundGeneric_MapsToCompatArraySegmentClassLiteral()
    {
        var result = Convert("""
            using System;

            class Sample
            {
                bool IsArraySegment(Type type)
                {
                    return type.IsGenericType
                        && type.GetGenericTypeDefinition() == typeof(ArraySegment<>).GetGenericTypeDefinition();
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("ArraySegment.class", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("T.class", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TypeGetConstructor_BindingFlagsAndParameterTypes_BridgedToTypeHelper()
    {
        var result = Convert("""
            using System;
            using System.Reflection;

            class Sample
            {
                ConstructorInfo GetCtor(Type type)
                {
                    return type.GetConstructor(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic, Array.Empty<Type>());
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("TypeHelper.getConstructor(type, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic, new Class[0])", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("type.getConstructor(BindingFlags", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IConvertibleCast_ToInt64_GeneratesStaticCompatCall()
    {
        var result = Convert("""
            using System;
            using System.Reflection;

            class Sample
            {
                long GetConstantValue(FieldInfo fieldInfo)
                {
                    return ((IConvertible)fieldInfo.GetValue(null)).ToInt64(null);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("IConvertible.toInt64(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("fieldInfo.getValue(null)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("((IConvertible)(fieldInfo.getValue(null))).toInt64(null)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ObsoleteAttribute_IsError_UsesCompatAnnotation()
    {
        var result = Convert("""
            using System;
            using System.Reflection;

            class Sample
            {
                bool IsError(ConstructorInfo ctor)
                {
                    object[] attrs = ctor.GetCustomAttributes(typeof(ObsoleteAttribute), false);
                    if (attrs != null && attrs.Length > 0)
                    {
                        ObsoleteAttribute obsolete = (ObsoleteAttribute)attrs[0];
                        return obsolete.IsError;
                    }
                    return false;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("ObsoleteAttribute.class", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("ObsoleteAttribute obsolete", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("obsolete.getIsError()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Deprecated.class", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void AssemblyName_NameProperty_Getter_IsBridgedToCompatGetter()
    {
        var result = Convert("""
            using System.Reflection;

            class Sample
            {
                string GetTempName(AssemblyName parent, string ns)
                {
                    return parent.Name + ".XmlSerializers" + (ns == null || ns.Length == 0 ? "" : "." + ns.GetHashCode());
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("AssemblyCompat.AssemblyNameCompat parent", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("parent.getName()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Object parent", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("parent.get_Name()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void AssemblyName_Ctor_String_LoadedByCompat()
    {
        var result = Convert("""
            using System.Reflection;

            class Sample
            {
                void Load()
                {
                    var originalAssembly = Assembly.Load(new AssemblyName("MyAssembly, Version=1.0.0.0"));
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("AssemblyCompat.load(new AssemblyCompat.AssemblyNameCompat(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new Object(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void AssemblyName_GetPublicKeyToken_BridgedToCompatGetter()
    {
        var result = Convert("""
            using System.Reflection;

            class Sample
            {
                byte[] GetToken(AssemblyName name)
                {
                    return name.GetPublicKeyToken();
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("name.getPublicKeyToken()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("AssemblyCompat.AssemblyNameCompat name", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FileNotFoundException_TwoStringCtor_UsesCompatClass()
    {
        var result = Convert("""
            using System.IO;

            class Sample
            {
                void Throw(string fileName)
                {
                    throw new FileNotFoundException(null, fileName);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("import io.github.ningpp.compat.FileNotFoundException;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("new FileNotFoundException(null, fileName)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingMethodException_Thrown_MapsToUncheckedCompatClass()
    {
        var result = Convert("""
            using System;

            class Sample
            {
                void Throw()
                {
                    throw new MissingMethodException("type::method");
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        var code = result.GeneratedCode;
        Assert.Contains("import io.github.ningpp.compat.MissingMethodException;", code, StringComparison.Ordinal);
        Assert.Contains("new MissingMethodException(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("NoSuchMethodException", code, StringComparison.Ordinal);
        Assert.DoesNotContain("throws NoSuchMethodException", code, StringComparison.Ordinal);

        using var temp = new TempDir();
        var outDir = Path.Combine(temp.Path, "classes");
        Directory.CreateDirectory(outDir);
        var javaPath = Path.Combine(temp.Path, "Sample.java");
        File.WriteAllText(javaPath, code);

        var compatClasses = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "java", "csharptojava-compat", "target", "classes"));
        var javac = RunProcess(FindRequiredExecutable("javac"), $"-cp \"{compatClasses}\" -d \"{outDir}\" \"{javaPath}\"");
        Assert.True(javac.ExitCode == 0, javac.Output);
    }

    [Fact]
    public void FileLoadException_Thrown_MapsToUncheckedCompatClass()
    {
        var result = Convert("""
            using System.IO;

            class Sample
            {
                void Throw()
                {
                    throw new FileLoadException("assembly load failed");
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        var code = result.GeneratedCode;
        Assert.Contains("import io.github.ningpp.compat.FileLoadException;", code, StringComparison.Ordinal);
        Assert.Contains("new FileLoadException(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("throws FileLoadException", code, StringComparison.Ordinal);

        using var temp = new TempDir();
        var outDir = Path.Combine(temp.Path, "classes");
        Directory.CreateDirectory(outDir);
        var javaPath = Path.Combine(temp.Path, "Sample.java");
        File.WriteAllText(javaPath, code);

        var compatClasses = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "java", "csharptojava-compat", "target", "classes"));
        var javac = RunProcess(FindRequiredExecutable("javac"), $"-cp \"{compatClasses}\" -d \"{outDir}\" \"{javaPath}\"");
        Assert.True(javac.ExitCode == 0, javac.Output);
    }

    [Fact]
    public void EnumToObject_StaticCall_MapsToEnumHelper()
    {
        var result = Convert("""
            using System;

            class Sample
            {
                enum Color { Red, Green, Blue }

                object ConvertToEnum(Type enumType, long value)
                {
                    return Enum.ToObject(enumType, value);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("EnumHelper.toObject(enumType, value)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.EnumHelper;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Enum.toObject(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrentDictionaryTryAdd_InstanceCall_MapsToConcurrentHashMapHelper()
    {
        var result = Convert("""
            using System.Collections.Concurrent;

            class Sample
            {
                bool Add(ConcurrentDictionary<string, object> dict, string key, object value)
                {
                    return dict.TryAdd(key, value);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("ConcurrentHashMapHelper.tryAdd(dict, key, value)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ConcurrentHashMapHelper;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("dict.tryAdd(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NamedArguments_SkippedOptionalParameters_AreFilledWithDefaults()
    {
        var result = Convert("""
            class Sample
            {
                private object WriteElement(object element, bool checkSpecified, bool checkForNull, bool readOnly, string defaultNamespace, int fixupIndex = -1, int elementIndex = -1, object fixup = null, object member = null)
                {
                    return null;
                }

                object Call(object element, object fixup, object member)
                {
                    return WriteElement(element, false, false, false, "", fixup: fixup, member: member);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("writeElement(element, false, false, false, \"\", -1, -1, fixup, member)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("writeElement(element, false, false, false, \"\", fixup, member)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpressionTree_FieldAssignmentLambda_GeneratesCompatExpressionCalls()
    {
        var result = Convert("""
            using System;
            using System.Linq.Expressions;
            using System.Reflection;

            class Sample
            {
                Action<object, object> BuildSetter(FieldInfo field)
                {
                    var objParam = Expression.Parameter(typeof(object));
                    var valParam = Expression.Parameter(typeof(object));
                    var fieldExpr = Expression.Field(objParam, field);
                    var assignExpr = Expression.Assign(fieldExpr, valParam);
                    return (Action<object, object>)Expression.Lambda(assignExpr, objParam, valParam).Compile();
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("Expression.parameter(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Expression.field(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Expression.assign(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Expression.lambda(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".compile()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpressionTree_LambdaCompile_AssignedToGenericAction_UsesCompatUniversalConsumer()
    {
        var result = Convert("""
            using System;
            using System.Linq.Expressions;
            using System.Reflection;

            class Helper
            {
                public delegate void SetMemberValueDelegate(object o, object val);

                public static SetMemberValueDelegate Build<TObj, TParam>(MemberInfo memberInfo)
                {
                    Action<TObj, TParam> setTypedDelegate = null;
                    if (memberInfo is FieldInfo fieldInfo)
                    {
                        var objectParam = Expression.Parameter(typeof(TObj));
                        var valueParam = Expression.Parameter(typeof(TParam));
                        var fieldExpr = Expression.Field(objectParam, fieldInfo);
                        var assignExpr = Expression.Assign(fieldExpr, valueParam);
                        setTypedDelegate = Expression.Lambda<Action<TObj, TParam>>(assignExpr, objectParam, valueParam).Compile();
                    }
                    Action<TObj, TParam> local = setTypedDelegate;
                    return (o, p) => local((TObj)o, (TParam)p);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("BiConsumer<TObj, TParam>", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Expression.lambda(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(".compile()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(BiConsumer<TObj, TParam>)Expression.lambda", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericCapturedVariable_ExternallyReassigned_UsesUncheckedObjectArrayHolder()
    {
        var result = Convert("""
            using System;

            class Sample
            {
                Action<T> Build<T>()
                {
                    Action<T> action = null;
                    action = x => { };
                    return x => action(x);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("@SuppressWarnings(\"unchecked\")", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("(Consumer<T>[]) new Object[]", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConditionalAccess_PropertyChain_GeneratesJavaBeanGetters()
    {
        var result = Convert("""
            class TypeDesc
            {
                public string FullName { get; set; }
            }

            class TypeMapping
            {
                public TypeDesc TypeDesc { get; set; }
            }

            class Choice
            {
                public TypeMapping Mapping { get; set; }
            }

            class Sample
            {
                string GetName(Choice choice)
                {
                    return choice?.Mapping.TypeDesc.FullName;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("choice.getMapping().getTypeDesc().getFullName()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".TypeDesc", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".FullName", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NullableBool_Value_GeneratesPrimitiveUnboxMethod()
    {
        var result = Convert("""
            class Sample
            {
                void M(bool? b)
                {
                    if (b == null || b.Value)
                    {
                    }
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("b == null || b.booleanValue()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".getValue()", result.GeneratedCode, StringComparison.Ordinal);
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

    private static async Task<ConversionResult> ConvertProjectAsync(IEnumerable<(string FilePath, string Content)> sourceFiles)
    {
        var files = sourceFiles.Select(s => new SourceFile
        {
            FilePath = s.FilePath,
            Content = s.Content,
        }).ToArray();

        var options = new ConversionOptions();
        var pipeline = new ProjectConversionPipeline(options);
        var results = await pipeline.ConvertProjectAsync(files);

        var success = results.Any() && results.All(r => r.Success);
        var generated = string.Join("\n\n", results.Where(r => r.Success).Select(r => $"// ----- {r.FileName} -----\n{r.GeneratedCode}"));
        var diagnostics = results.SelectMany(r => r.Diagnostics).ToList();

        return new ConversionResult
        {
            Success = success,
            GeneratedCode = generated,
            Diagnostics = diagnostics,
        };
    }

    [Fact]
    public void BooleanLambdaCapture_Mutated_UsesPrimitiveArrayHolder()
    {
        var result = Convert("""
            using System;

            class Sample
            {
                void M()
                {
                    bool isReferenced = true;
                    Action<object> unrecognizedElementSource = x => isReferenced = false;
                    Console.WriteLine(isReferenced);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("boolean[] _isReferenced = new boolean[] { isReferenced }", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("(boolean[]) new Object[]", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LambdaCapture_ExternallyReassignedViaRef_UsesArrayHolder()
    {
        var result = Convert("""
            using System;

            class Sample
            {
                void Mutate(ref object value) { value = new object(); }

                void M()
                {
                    object o = new object();
                    Action<object> action = x => Console.WriteLine(o);
                    Mutate(ref o);
                    Console.WriteLine(o);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("Object[] _o = new Object[] { o }", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("_o[0]", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LambdaCapture_ShadowedLocalExternallyReassignedViaRef_UsesArrayHolderForCorrectInstance()
    {
        var result = Convert("""
            using System;

            class Sample
            {
                void Mutate(ref object value) { value = new object(); }

                void M(bool condition)
                {
                    if (condition)
                    {
                        object o = null;
                        Mutate(ref o);
                    }

                    object o = new object();
                    Action<object> action = x => Console.WriteLine(o);
                    Mutate(ref o);
                    Console.WriteLine(o);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        // The outer 'o' must use a holder; the shadowed inner 'o' must not create a stray holder.
        Assert.Contains("Object[] _o = new Object[] {", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("_o[0]", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ForLoopIterator_CapturedByLambdaAndIncremented_UsesArrayHolder()
    {
        var result = Convert("""
            using System;

            class Sample
            {
                string WritePrimitive(Func<object, string> getter, object state) => getter(state);

                void M(string[] vals)
                {
                    for (int i = 0; i < vals.Length; i++)
                    {
                        var value = WritePrimitive((state) => ((string[])state)[i], vals);
                    }
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("int[] _i = new int[] { 0 }", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("_i[0]", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateNewMethodHidingProtectedBase_PromotedToBaseAccessInJava()
    {
        var result = Convert("""
            using System;

            class BaseConverter
            {
                protected Exception CreateInvalidClrMappingException(Type sourceType, Type destinationType) => null;
            }

            class ListConverter : BaseConverter
            {
                private new Exception CreateInvalidClrMappingException(Type sourceType, Type destinationType) => null;
            }

            class UntypedConverter : ListConverter
            {
                Exception Use(Type sourceType, Type destinationType)
                {
                    return CreateInvalidClrMappingException(sourceType, destinationType);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        // The hiding method must not be emitted as private; otherwise Java reports
        // "attempting to assign weaker access privileges" and derived classes cannot see it.
        Assert.DoesNotContain("private RuntimeException createInvalidClrMappingException", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("createInvalidClrMappingException(sourceType, destinationType)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GotoInsideTryCatch_LabelAfterTry_DoesNotGenerateUnreachableJava()
    {
        var source = """
            class Sample
            {
                public object ParseValue(string s)
                {
                    try
                    {
                        if (s == null) goto Error;
                        return s;
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                Error:
                    throw new Exception("invalid");
                }
            }
            """;

        var eliminator = new CSharpToJava.Core.GotoEliminator.GotoEliminator();
        var preprocessed = eliminator.Eliminate(source);
        Assert.True(preprocessed.Diagnostics.All(d => d.Severity != CSharpToJava.Core.GotoEliminator.GotoEliminatorSeverity.Error),
            string.Join("\n", preprocessed.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("goto", preprocessed.OutputCode, StringComparison.Ordinal);

        var result = await ConvertProjectAsync(new[] { ("Sample.cs", preprocessed.OutputCode) });
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        using var temp = new TempDir();
        var outDir = Path.Combine(temp.Path, "classes");
        Directory.CreateDirectory(outDir);
        var javaPath = Path.Combine(temp.Path, "Sample.java");
        File.WriteAllText(javaPath, result.GeneratedCode);

        var javac = RunProcess(FindRequiredExecutable("javac"), $"-d \"{outDir}\" \"{javaPath}\"");
        Assert.True(javac.ExitCode == 0, javac.Output);
    }

    private static string FindRequiredExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim(), name + ext);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException($"{name} is required for this test.");
    }

    private static (int ExitCode, string Output) RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs2j-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
