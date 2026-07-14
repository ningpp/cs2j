using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# properties (auto, expression-bodied, get/set bodies,
/// init accessors, indexers) to Java getters/setters or fields.
/// </summary>
public class PropertyTests : ConversionTestBase
{
    [Fact]
    public void AutoProperty_ConvertsToGetterSetter()
    {
        var result = Convert("class C { public int X { get; set; } }");
        AssertConversion(result, "private int x;", "public int getX() {", "public void setX(int value) {");
    }

    [Fact]
    public void ReadOnlyAutoProperty_ConvertsToGetterOnly()
    {
        var result = Convert("class C { public int X { get; } }");
        AssertConversion(result, "public int getX() {", "private int x;");
        AssertJavaDoesNotContain(result, "setX");
    }

    [Fact]
    public void ReadOnlyAutoProperty_AssignedInConstructor_UsesFieldAssignment()
    {
        var result = Convert("class C { public int X { get; } public C(int x) { X = x; } }");
        AssertConversion(result, "this.x = x;");
        AssertJavaDoesNotContain(result, "setX(", "Read-only property assignment in constructor must use field assignment, not setter call");
    }

    [Fact]
    public void ReadOnlyAutoProperty_AssignedViaThisInConstructor_UsesFieldAssignment()
    {
        var result = Convert("class C { public int X { get; } public C(int x) { this.X = x; } }");
        AssertConversion(result, "this.x = x;");
        AssertJavaDoesNotContain(result, "setX(", "Read-only property assignment via this in constructor must use field assignment, not setter call");
    }

    [Fact]
    public void WriteOnlyProperty_ConvertsToSetterOnly()
    {
        var result = Convert("class C { public int X { set; } }");
        AssertConversion(result, "public void setX(int value) {");
        AssertJavaDoesNotContain(result, "public int getX()");
    }

    [Fact]
    public void PropertyWithGetSetBodies_Converts()
    {
        var result = Convert("class C { private int x; public int X { get { return x; } set { x = value; } } }");
        AssertConversion(result, "public int getX() {", "return x;", "public void setX(int value) {", "x = value;");
    }

    [Fact]
    public void InitProperty_ConvertsToSetterWithNote()
    {
        var result = Convert("class C { public int X { get; init; } }");
        AssertConversion(result, "public int getX() {", "public void setX(int value) {");
    }

    [Fact]
    public void ExpressionBodiedProperty_ConvertsToGetter()
    {
        var result = Convert("class C { public int X => 42; }");
        AssertConversion(result, "public int getX() {", "return 42;");
    }

    [Fact]
    public void ExpressionBodiedStringProperty_ConvertsToGetter()
    {
        var result = Convert("class C { public string Name => \"x\"; }");
        AssertConversion(result, "public String getName() {", "return \"x\";");
    }

    [Fact]
    public void PropertyWithBackingFieldAssignment()
    {
        var result = Convert("class C { private int _x; public int X { get { return _x; } set { _x = value; } } }");
        AssertConversion(result, "public int getX() {", "public void setX(int value) {", "_x = value;");
    }

    [Fact]
    public void StaticProperty_ConvertsToStaticGetter()
    {
        var result = Convert("class C { public static int Count { get; set; } }");
        AssertConversion(result, "public static int getCount() {", "public static void setCount(int value) {");
    }

    [Fact]
    public void PropertyUsedInMethod_ConvertsToGetterCall()
    {
        var result = Convert("class C { public int X { get; set; } public int M() { X = 5; return X; } }");
        AssertConversion(result, "setX(5);", "return getX();");
    }

    [Fact]
    public void Indexer_ConvertsToGetSetMethods()
    {
        var result = Convert("class C { private int[] a = new int[3]; public int this[int i] { get { return a[i]; } set { a[i] = value; } } }");
        AssertConversion(result, "public int get(int i) {", "public int set(int i, int value) {");
    }

    [Fact]
    public void StringIndexer_ConvertsToGetSet()
    {
        var result = Convert("class C { private System.Collections.Generic.Dictionary<string,int> d = new(); public int this[string k] { get { return d[k]; } set { d[k] = value; } } }");
        AssertConversion(result, "public int get(String k) {", "public int set(String k, int value) {");
    }

    [Fact]
    public void PropertyWithDefaultInitializer()
    {
        var result = Convert("class C { public int X { get; set; } = 10; }");
        AssertConversion(result, "private int x = 10;", "public int getX() {");
    }

    [Fact]
    public void GetOnlyPropertyFromField()
    {
        var result = Convert("class C { public int Length { get { return 5; } } }");
        AssertConversion(result, "public int getLength() {", "return 5;");
    }

    [Fact]
    public void PropertyInStruct_ConvertsToGetterSetter()
    {
        var result = Convert("struct P { public int X { get; set; } }");
        AssertConversion(result, "public int getX() {", "public void setX(int value) {");
    }

    [Fact]
    public void AbstractProperty_ConvertsToAbstractGetter()
    {
        var result = Convert("abstract class Base { public abstract int Value { get; } } class Impl : Base { public override int Value { get { return 1; } } }");
        AssertConversion(result, "public abstract int getValue();", "public int getValue() {");
    }

    [Fact]
    public void InterfaceProperty_ConvertsToGetterSetter()
    {
        var result = Convert("interface IConfig { int Timeout { get; set; } }");
        AssertConversion(result, "int getTimeout();", "void setTimeout(int value);");
    }

    [Fact]
    public void PropertyWithSideEffectGetter()
    {
        var result = Convert("class C { private int x; public int X { get { x++; return x; } } }");
        AssertConversion(result, "public int getX() {", "x++;", "return x;");
    }

    [Fact]
    public void ComputedProperty_ConvertsToGetter()
    {
        var result = Convert("class C { public int A { get; set; } public int B { get; set; } public int Sum => A + B; }");
        AssertConversion(result, "public int getSum() {", "return getA() + getB();");
    }

    [Fact]
    public void PropertyChainedSetter()
    {
        var result = Convert("class C { private int x; public int X { set { x = value * 2; } } public int GetX() { return x; } }");
        AssertConversion(result, "public void setX(int value) {", "x = value * 2;");
    }

    [Fact]
    public void ProtectedProperty_Converts()
    {
        var result = Convert("class C { public int X { get; protected set; } }");
        AssertConversion(result, "public int getX() {", "protected void setX(int value) {");
    }

    [Fact]
    public void InternalProperty_Converts()
    {
        var result = Convert("class C { public int X { get; internal set; } }");
        AssertConversion(result, "public int getX() {", "void setX(int value) {");
    }

    [Fact]
    public void AutoPropertyBoolean_Converts()
    {
        var result = Convert("class C { public bool Enabled { get; set; } }");
        AssertConversion(result, "private boolean enabled;", "public boolean getEnabled() {", "public void setEnabled(boolean value) {");
    }

    [Fact]
    public void AutoPropertyString_Converts()
    {
        var result = Convert("class C { public string Name { get; set; } }");
        AssertConversion(result, "private String name;", "public String getName() {", "public void setName(String value) {");
    }

    [Fact]
    public void PropertyReferencingOtherProperty()
    {
        var result = Convert("class C { public int X { get; set; } public int DoubleX => X * 2; }");
        AssertConversion(result, "public int getDoubleX() {", "return getX() * 2;");
    }

    [Fact]
    public void VirtualProperty_Converts()
    {
        var result = Convert("class A { public virtual int V { get; set; } } class B : A { public override int V { get; set; } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "getV()");
    }

    [Fact]
    public void PropertyWithAttribute_Converts()
    {
        var result = Convert("class C { public int X { get; set; } }");
        AssertConversion(result, "public int getX() {");
    }

    [Fact]
    public void InitOnlyRecordLikeProperty_Converts()
    {
        var result = Convert("class C { public int Id { get; init; } public string Name { get; init; } }");
        AssertConversion(result, "public int getId() {", "public void setId(int value) {", "public String getName() {");
    }

    [Fact]
    public void PropertyReturningList_Converts()
    {
        var result = Convert("using System.Collections.Generic; class C { public List<int> Items { get; set; } }");
        AssertConversion(result, "private CSharpList<Integer> items;", "public CSharpList<Integer> getItems() {", "public void setItems(CSharpList<Integer> value) {");
    }

    [Fact]
    public void ExpressionBodiedPropertyWithCalculation()
    {
        var result = Convert("class C { public int A { get; set; } public int Squared => A * A; }");
        AssertConversion(result, "public int getSquared() {", "return getA() * getA();");
    }
}
