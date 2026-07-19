using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# class declarations (members, constructors, modifiers,
/// inheritance, nesting, partial classes) to Java.
/// </summary>
public class ClassTests : ConversionTestBase
{
    [Fact]
    public void SimpleClass_WithFieldAndMethod()
    {
        var result = Convert("class C { public int Value; public int GetValue() { return Value; } }");
        AssertConversion(result, "public class C {", "public int Value;", "public int getValue() {");
    }

    [Fact]
    public void Constructor_ConvertsToJavaConstructor()
    {
        var result = Convert("class C { public int X; public C(int x) { X = x; } }");
        AssertConversion(result, "public C(int x) {", "X = x;");
    }

    [Fact]
    public void StaticMethod_ConvertsToJavaStaticMethod()
    {
        var result = Convert("class C { public static int Add(int a, int b) { return a + b; } }");
        AssertConversion(result, "public static int add(int a, int b) {");
    }

    [Fact]
    public void Inheritance_ConvertsToExtends()
    {
        var result = Convert("class Animal { public int Legs; } class Dog : Animal { public string Name; }");
        AssertConversion(result, "public class Dog extends Animal {");
    }

    [Fact]
    public void NestedClass_ConvertsToStaticNestedClass()
    {
        var result = Convert("class Outer { public class Inner { public int X; } }");
        AssertConversion(result, "public static class Inner {", "public int X;");
    }

    [Fact]
    public void AbstractClass_ConvertsToJavaAbstractClass()
    {
        var result = Convert("abstract class Shape { public abstract int Area(); }");
        AssertConversion(result, "public abstract class Shape {", "public abstract int area();");
    }

    [Fact]
    public void SealedClass_ConvertsToFinalClass()
    {
        var result = Convert("sealed class C { public int X; }");
        AssertConversion(result, "public final class C {");
    }

    [Fact]
    public void StaticConstructor_ConvertsToStaticBlock()
    {
        var result = Convert("class C { public static int Counter; static C() { Counter = 0; } }");
        AssertConversion(result, "static {", "Counter = 0;");
    }

    [Fact]
    public void MultipleFields_ConvertsToJava()
    {
        var result = Convert("class C { public int A; public string B; public bool C; }");
        AssertConversion(result, "public int A;", "public String B;", "public boolean C;");
    }

    [Fact]
    public void MultipleMethods_ConvertsToJava()
    {
        var result = Convert("class C { public int A() { return 1; } public int B() { return 2; } public int D() { return 3; } }");
        AssertConversion(result, "public int a() {", "public int b() {", "public int d() {");
    }

    [Fact]
    public void PartialClass_MergesIntoSingleClass()
    {
        var result = Convert("partial class C { public int A() { return 1; } } partial class C { public int B() { return 2; } }");
        AssertConversion(result, "public class C {", "public int a() {", "public int b() {");
    }

    [Fact]
    public void PartialInterface_MergesIntoSingleInterface()
    {
        var result = Convert("partial interface I { void A(); } partial interface I { void B(); }");
        AssertConversion(result, "public interface I {", "void a();", "void b();");
    }

    [Fact]
    public void ClassImplementingInterface_ConvertsToImplements()
    {
        var result = Convert("interface IAnimal { void Speak(); } class Dog : IAnimal { public void Speak() { } }");
        AssertConversion(result, "public class Dog implements IAnimal {", "public void speak() {");
    }

    [Fact]
    public void ClassWithBaseAndInterface_ConvertsToExtendsImplements()
    {
        var result = Convert("class Base { } interface ILog { void Log(); } class Derived : Base, ILog { public void Log() { } }");
        AssertConversion(result, "public class Derived extends Base implements ILog {");
    }

    [Fact]
    public void GenericClass_ConvertsToJavaGeneric()
    {
        var result = Convert("class Box<T> { public T Value; }");
        AssertConversion(result, "public class Box<T> {", "public T Value;");
    }

    [Fact]
    public void InternalClass_ConvertsToPackagePrivate()
    {
        var result = Convert("internal class C { public int X; }");
        AssertConversion(result, "class C {", "public int X;");
    }

    [Fact]
    public void PublicClass_ConvertsToPublic()
    {
        var result = Convert("public class C { }");
        AssertConversion(result, "public class C {");
    }

    [Fact]
    public void ClassWithConst_ConvertsToStaticFinal()
    {
        var result = Convert("class C { public const string Name = \"test\"; }");
        AssertConversion(result, "public static final String Name = \"test\";");
    }

    [Fact]
    public void VirtualOverride_ConvertsToJavaOverride()
    {
        var result = Convert("class A { public virtual int V() { return 1; } } class B : A { public override int V() { return 2; } }");
        AssertConversion(result, "public class B extends A {", "public int v() {", "return 2;");
    }

    [Fact]
    public void AbstractMethodImplementation_ConvertsToJava()
    {
        var result = Convert("abstract class Base { public abstract int Calc(); } class Impl : Base { public override int Calc() { return 42; } }");
        AssertConversion(result, "public int calc() {", "return 42;");
    }

    [Fact]
    public void ClassWithFieldInitializer()
    {
        var result = Convert("class C { public int Count = 10; public string Name = \"x\"; }");
        AssertConversion(result, "public int Count = 10;", "public String Name = \"x\";");
    }

    [Fact]
    public void ClassWithMultipleConstructors()
    {
        var result = Convert("class C { public C() { } public C(int x) { } }");
        AssertConversion(result, "public C() {", "public C(int x) {");
    }

    [Fact]
    public void ConstructorChaining_ConvertsToThis()
    {
        var result = Convert("class C { public C() : this(0) { } public C(int x) { } }");
        AssertConversion(result, "public C() {", "this(0);");
    }

    [Fact]
    public void BaseConstructorCall_ConvertsToSuper()
    {
        var result = Convert("class A { public A(int x) { } } class B : A { public B() : base(5) { } }");
        AssertConversion(result, "public B() {", "super(5);");
    }

    [Fact]
    public void MethodHiding_ConvertsToJava()
    {
        var result = Convert("class A { public int X() { return 1; } } class B : A { public new int X() { return 2; } }");
        AssertConversion(result, "public class B extends A {", "public int x() {");
    }

    [Fact]
    public void EmptyClass_ConvertsToJava()
    {
        var result = Convert("class C { }");
        AssertConversion(result, "public class C {");
    }

    [Fact]
    public void ClassWithStaticField()
    {
        var result = Convert("class C { public static int Total; }");
        AssertConversion(result, "public static int Total;");
    }

    [Fact]
    public void NestedGenericClass_ConvertsToJava()
    {
        var result = Convert("class Outer { public class Inner<T> { public T Value; } }");
        AssertConversion(result, "public static class Inner<T> {", "public T Value;");
    }

    [Fact]
    public void ClassWithPrivateMethod()
    {
        var result = Convert("class C { private int Secret() { return 1; } }");
        AssertConversion(result, "private int secret() {");
    }

    [Fact]
    public void ClassWithProtectedMethod()
    {
        var result = Convert("class C { protected int Helper() { return 1; } }");
        AssertConversion(result, "protected int helper() {");
    }

    [Fact]
    public void TopLevelStatementsClass_ConvertsToJava()
    {
        var result = Convert("class Program { public static void Main() { } }");
        AssertConversion(result, "public class Program {", "public static void main() {");
    }

    [Fact]
    public void ClassWithObjectField()
    {
        var result = Convert("class C { public object Data; }");
        AssertConversion(result, "public Object Data;");
    }

    [Fact]
    public void SealedOverride_ConvertsToFinalMethod()
    {
        var result = Convert("class A { public virtual int V() { return 1; } } class B : A { public sealed override int V() { return 2; } }");
        AssertConversion(result, "public final int v() {");
    }

    [Fact]
    public void ClassWithGenericMethod()
    {
        var result = Convert("class C { public T Id<T>(T x) { return x; } }");
        AssertConversion(result, "public <T> T id(T x) {");
    }

    [Fact]
    public void ClassImplementingTwoInterfaces()
    {
        var result = Convert("interface I1 { void A(); } interface I2 { void B(); } class C : I1, I2 { public void A() { } public void B() { } }");
        AssertConversion(result, "public class C implements I1, I2 {", "public void a() {", "public void b() {");
    }

    [Fact]
    public void AbstractProperty_ConvertsWithOverride()
    {
        var result = Convert("abstract class Base { public abstract int Value { get; } } class Impl : Base { public override int Value { get { return 1; } } }");
        AssertConversion(result, "public int getValue() {", "return 1;");
    }

    [Fact]
    public void ClassWithEvent_ConvertsToListenerPattern()
    {
        var result = Convert("class C { public event System.EventHandler Changed; }");
        AssertConversion(result, "CopyOnWriteArrayList", "addChangedListener");
    }

    [Fact]
    public void ClassWithIndexer()
    {
        var result = Convert("class C { private int[] a = new int[3]; public int this[int i] { get { return a[i]; } set { a[i] = value; } } }");
        AssertConversion(result, "public int get(int i) {", "public int set(int i, int value) {");
    }

    [Fact]
    public void StaticReadonlyField_ConvertsToStaticFinal()
    {
        var result = Convert("class C { public static readonly int Max = 100; }");
        AssertConversion(result, "public static final int Max = 100;");
    }

    [Fact]
    public void ClassWithDisposablePattern()
    {
        var result = Convert("class C : System.IDisposable { public void Dispose() { } }");
        AssertConversion(result, "public class C implements AutoCloseable {", "public void close() {");
    }

    [Fact]
    public void ClassWithExplicitInterfaceImpl()
    {
        var result = Convert("interface ILog { void Log(); } class C : ILog { void ILog.Log() { } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
    }

    [Fact]
    public void ClassWithNullableField()
    {
        var result = Convert("class C { public int? Maybe; }");
        AssertConversion(result, "public Integer Maybe;");
    }

    [Fact]
    public void ClassWithGenericListField()
    {
        var result = Convert("class C { public System.Collections.Generic.List<string> Names = new(); }");
        AssertConversion(result, "import io.github.ningpp.compat.CSharpList;", "public CSharpList<String> Names");
    }

    [Fact]
    public void ClassWithTupleField()
    {
        var result = Convert("class C { public (int, string) Pair; }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "Tuple2");
    }
}
