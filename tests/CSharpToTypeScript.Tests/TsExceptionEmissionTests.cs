namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 21 异常发射与兼容异常类映射测试（M2，spec §5.3 M2 组）。
/// TS 路径复用 C#→Java 前端，IR 中异常类型为 Java 风味名（Java config/TypeMappings.json
/// 映射：InvalidOperationException→IllegalStateException、Exception→RuntimeException、
/// NullReferenceException→NullPointerException 等），发射期经 TsMemberMappingTable 反向
/// 映射到兼容库异常类（spec §4.3.9）：IllegalStateException→InvalidOperationException、
/// RuntimeException→CsException；恒等兼容类（ArgumentException/ArgumentNullException/
/// FormatException/IOException/KeyNotFoundException 等）保持原名；无精确兼容类的 BCL
/// 异常（NullPointerException/TimeoutException 等）降级 CsException 并追加
/// /* cs2j: unmapped exception */ 诊断注释。imports 经 WithImports 机制自动收集。
/// 全部用例经 L3 tsc --noEmit（compat 桩含 §4.3.9 异常类层次）编译。
/// </summary>
public class TsExceptionEmissionTests : TsConversionTestBase
{
    // ─── throw：Java 风味异常名 → 兼容库异常类 ──────────────────────

    [Fact]
    public void Throw_Mapped_Exception_Emits_Compat_Class_And_Import()
    {
        var result = Convert("""
class X
{
    void ThrowInvalid() { throw new InvalidOperationException("bad state"); }
    void ThrowArg() { throw new ArgumentException("bad"); }
    void ThrowArgNull() { throw new ArgumentNullException("p"); }
    void ThrowBase() { throw new Exception("boom"); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("throw new InvalidOperationException(\"bad state\");", result.GeneratedCode);
        Assert.Contains("throw new ArgumentException(\"bad\");", result.GeneratedCode);
        Assert.Contains("throw new ArgumentNullException(\"p\");", result.GeneratedCode);
        // C# Exception → IR RuntimeException → 兼容库基类 CsException（确定性映射，无降级注释）
        Assert.Contains("throw new CsException(\"boom\");", result.GeneratedCode);
        Assert.DoesNotContain("IllegalStateException", result.GeneratedCode);
        Assert.DoesNotContain("RuntimeException", result.GeneratedCode);
        Assert.Contains("import { ArgumentException, ArgumentNullException, CsException, InvalidOperationException } from \"@csharptotypescript/compat\";", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Throw_Identity_Compat_Exception_Keeps_Name()
    {
        var result = Convert("""
class X
{
    void ThrowFormat() { throw new FormatException("bad format"); }
    void ThrowIo() { throw new IOException("io"); }
    void ThrowKey() { throw new KeyNotFoundException("k"); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("throw new FormatException(\"bad format\");", result.GeneratedCode);
        Assert.Contains("throw new IOException(\"io\");", result.GeneratedCode);
        Assert.Contains("throw new KeyNotFoundException(\"k\");", result.GeneratedCode);
        Assert.DoesNotContain("/* cs2j: unmapped exception */", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Throw_Unmapped_Bcl_Exception_Falls_Back_To_CsException_With_Marker()
    {
        var result = Convert("""
class X
{
    void ThrowNullRef() { throw new NullReferenceException("null"); }
    void ThrowTimeout() { throw new TimeoutException("t"); }
}
""");
        Assert.True(result.Success);
        // NullReferenceException → IR NullPointerException → 无兼容类 → CsException + 诊断注释
        //（raw 文本归一化形态：注释位于名字与括号之间）
        Assert.Contains("throw new CsException /* cs2j: unmapped exception */(\"null\");", result.GeneratedCode);
        Assert.Contains("throw new CsException /* cs2j: unmapped exception */(\"t\");", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    // ─── catch：按类型 instanceof 链 + 兼容异常类配合 ──────────────

    [Fact]
    public void Catch_Typed_Clauses_Emits_Mapped_Instanceof_Chain()
    {
        var result = Convert("""
class X
{
    int TryCatch()
    {
        try { return 1; }
        catch (InvalidOperationException ex) { return 2; }
        catch (Exception ex) { return 3; }
        finally { }
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("if (ex instanceof InvalidOperationException) {", result.GeneratedCode);
        // C# catch (Exception) → IR RuntimeException → 兼容库基类
        Assert.Contains("else if (ex instanceof CsException) {", result.GeneratedCode);
        Assert.Contains("} else {", result.GeneratedCode);
        Assert.Contains("throw ex;", result.GeneratedCode);
        Assert.Contains("finally", result.GeneratedCode);
        Assert.DoesNotContain("IllegalStateException", result.GeneratedCode);
        Assert.DoesNotContain("RuntimeException", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Catch_When_Filter_Emits_Guard_Inside_Instanceof()
    {
        var result = Convert("""
class X
{
    int CatchFilter()
    {
        try { }
        catch (InvalidOperationException ex) when (ex.Message != null) { return 2; }
        catch { return 3; }
        return 1;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("if (ex instanceof InvalidOperationException) {", result.GeneratedCode);
        Assert.Contains("ex.getMessage() !== null", result.GeneratedCode);
        // 裸 catch 走链尾 else 分支（when 过滤为 false 的降级路径经尾部 return 兜底，
        // 保证 strict 下函数全路径有返回值；完整 when 语义归 M3 模式匹配任务）
        Assert.Contains("} else {", result.GeneratedCode);
        Assert.DoesNotContain("throw ex;", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Bare_Catch_Emits_No_Guard()
    {
        var result = Convert("""
class X
{
    int Bare()
    {
        try { return 1; }
        catch { return 2; }
    }
}
""");
        Assert.True(result.Success);
        Assert.DoesNotContain("instanceof", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    // ─── 类型位置（字段/参数/返回值/局部变量）──────────────────────

    [Fact]
    public void Exception_Type_Positions_Map_To_Compat_Names()
    {
        var result = Convert("""
class X
{
    Exception field;
    Exception M(Exception p)
    {
        Exception local = p;
        return local;
    }
}
""");
        Assert.True(result.Success);
        Assert.Contains("field: CsException", result.GeneratedCode);
        Assert.Contains("m(p: CsException): CsException", result.GeneratedCode);
        Assert.Contains("let local: CsException = p;", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    // ─── 用户定义异常：不误映射 ────────────────────────────────────

    [Fact]
    public void User_Defined_Exception_Stays_Unchanged_With_Mapped_Base()
    {
        var result = Convert("""
class MyException : Exception
{
    public MyException(string m) : base(m) { }
}
class X
{
    void M()
    {
        throw new MyException("custom");
    }
    int N()
    {
        try { return 1; }
        catch (MyException ex) { return 2; }
    }
}
""");
        Assert.True(result.Success);
        // 继承链基类经类型位置映射为兼容基类
        Assert.Contains("class MyException extends CsException", result.GeneratedCode);
        // 用户类型本身不做异常映射（无 compat 导入）
        Assert.Contains("throw new MyException(\"custom\");", result.GeneratedCode);
        Assert.Contains("if (ex instanceof MyException) {", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    // ─── imports 合并 ──────────────────────────────────────────────

    [Fact]
    public void Compat_Import_Emitted_Once_For_Multiple_Exceptions()
    {
        var result = Convert("""
class X
{
    void A() { throw new ArgumentException("a"); }
    void B() { throw new InvalidOperationException("b"); }
    void C() { throw new FormatException("c"); }
}
""");
        Assert.True(result.Success);
        var importLines = result.GeneratedCode.Split('\n').Count(l => l.Contains("from \"@csharptotypescript/compat\""));
        Assert.Equal(1, importLines);
        AssertCompilesWithCompat(result.GeneratedCode);
    }
}
