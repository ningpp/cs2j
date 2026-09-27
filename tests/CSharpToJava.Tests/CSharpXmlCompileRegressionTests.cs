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

        // All generated types in the same compilation unit must share the same signature.
        Assert.Contains("public Object set(String name, String ns, Object value)", code, StringComparison.Ordinal);
        Assert.Contains("public Object get(String name, String ns)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("public Object set(Object name, Object ns, Object value)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("public Object get(Object name, Object ns)", code, StringComparison.Ordinal);
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
            Assert.True(result.Success, $"{name}: " + string.Join("\n", result.Diagnostics));
        }
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
    public async Task CrossClassPropertyWrite_ProjectMode_UsesSetterNotBackingField()
    {
        // Regression (AGL RectangleNode/RTree): a public auto-property write on an external
        // generic class instance must become a setter call (setCount) in the project pipeline,
        // not a direct backing-field write (n.count = ...). Mirrors RectangleNode<T,P>.Count
        // being assigned from RTree.AddNodeToTreeRecursive.
        // Key: RTree itself has a get-only Count property with the same name; the converter
        // must not confuse it with RectangleNode.Count (which has a setter).
        var result = await ConvertProjectAsync(new[]
        {
            ("RectangleNode.cs", @"
public class RectangleNode<T, P> {
    public int Count { get; set; }
    public T UserData { get; set; }
    RectangleNode<T, P> left;
    public RectangleNode<T, P> Left { get { return left; } set { left = value; } }
    public RectangleNode() {}
    public RectangleNode(T data) { UserData = data; Count = 1; }
}"),
            ("RTree.cs", @"
public class RTree {
    RectangleNode<int, int> _rootNode;
    public int Count { get { return _rootNode == null ? 0 : _rootNode.Count; } }
    public static void M<T, P>(RectangleNode<T, P> n) {
        n.Left = new RectangleNode<T, P>();
        n.Count = 2;
        n.UserData = default(T);
    }
}"),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("n.setCount(2)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("n.count = 2", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("n.setUserData(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObjectInitializer_UnresolvedProperty_DefaultsToSetterCall()
    {
        // Regression (AGL GeometryGraphReader): when an object initializer sets a property
        // on a type from an unreferenced external assembly (e.g. System.Xml.XmlReaderSettings),
        // the semantic model cannot resolve the member symbol. The converter must default to
        // a setter call (setXxx) rather than a direct backing-field write, because Java compat
        // classes have private fields with public setters.
        var result = await ConvertProjectAsync(new[]
        {
            ("Sample.cs", @"
class Sample {
    void M() {
        var settings = new ExternalConfig { Timeout = 42, Enabled = true };
    }
}"),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("setTimeout(42)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("setEnabled(true)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".timeout = 42", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".enabled = true", result.GeneratedCode, StringComparison.Ordinal);
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
