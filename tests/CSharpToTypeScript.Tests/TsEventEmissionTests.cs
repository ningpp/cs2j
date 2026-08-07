using CSharpToJava.Core.Context;

namespace CSharpToTypeScript.Tests;

/// <summary>
/// Task 27：事件发射（spec §4.3.7/§5.3）。事件声明 → 兼容库 `CsEvent&lt;T&gt;` 字段
/// （EventFieldTransformer TS 分叉：不再生成 Java 的监听器列表/add/remove/fire 方法）；
/// `b.Click += h` → `b.Click.Add(h)`、`-=` → `Remove`（AssignmentTransformer TS 分叉）；
/// `Click?.Invoke(...)` → `this.Click.Invoke(...)`（条件访问 TS 分叉，CsEvent.Invoke
/// 空事件安全无需 null 守卫）；`EventArgs.Empty` 归一化为 null。全部用例经 L3。
/// </summary>
public class TsEventEmissionTests : TsConversionTestBase
{
    [Fact]
    public void Event_Declares_CsEvent_Field_And_Add_Remove_CallSites()
    {
        var result = Convert("""
using System;
public class Button
{
    public event EventHandler Click;
    public void Wire()
    {
        Click += OnClick;
        Click -= OnClick;
    }
    public void OnClick(object sender, EventArgs e) { }
}
public class Use
{
    public void Wire(Button b) { b.Click += b.OnClick; b.Click -= b.OnClick; }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public Click: CsEvent<(sender: any, args: any) => void> = new CsEvent<(sender: any, args: any) => void>();", result.GeneratedCode);
        Assert.Contains("this.Click.Add((sender, e) => this.onClick(sender, e));", result.GeneratedCode);
        Assert.Contains("this.Click.Remove((sender, e) => this.onClick(sender, e));", result.GeneratedCode);
        Assert.Contains("b.Click.Add((sender, e) => b.onClick(sender, e));", result.GeneratedCode);
        Assert.Contains("b.Click.Remove((sender, e) => b.onClick(sender, e));", result.GeneratedCode);
        Assert.DoesNotContain("addClickListener", result.GeneratedCode);
        Assert.DoesNotContain("_clickListeners", result.GeneratedCode);
        Assert.Contains("import { CsEvent } from \"@csharptotypescript/compat\";", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Event_Invoke_Emits_CsEvent_Invoke_With_Empty_Args_Normalized()
    {
        var result = Convert("""
using System;
public class Button
{
    public event EventHandler Click;
    public void Raise() { Click?.Invoke(this, EventArgs.Empty); }
    public void Raise2() { Click(this, EventArgs.Empty); }
}
""");
        Assert.True(result.Success);
        Assert.Contains("this.Click.Invoke(this, null);", result.GeneratedCode);
        Assert.DoesNotContain("fireClick", result.GeneratedCode);
        Assert.DoesNotContain("Object.Empty", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }

    [Fact]
    public void Event_With_Custom_Delegate_And_Static_Event()
    {
        var result = Convert("""
using System;
public delegate void TickHandler(int seconds);
public class Timer
{
    public static event TickHandler GlobalTick;
    public event TickHandler Tick;
    public void Raise() { Tick?.Invoke(5); }
    public static void RaiseGlobal() { GlobalTick?.Invoke(3); }
}
public class Use
{
    public void Wire(Timer t) { t.Tick += OnTick; Timer.GlobalTick += OnTick; }
    void OnTick(int seconds) { }
}
""");
        Assert.True(result.Success);
        Assert.Contains("public Tick: CsEvent<(seconds: number) => void> = new CsEvent<(seconds: number) => void>();", result.GeneratedCode);
        Assert.Contains("public static GlobalTick: CsEvent<(seconds: number) => void> = new CsEvent<(seconds: number) => void>();", result.GeneratedCode);
        Assert.Contains("this.Tick.Invoke(5);", result.GeneratedCode);
        Assert.Contains("Timer.GlobalTick.Invoke(3);", result.GeneratedCode);
        Assert.Contains("t.Tick.Add((seconds) => this.onTick(seconds));", result.GeneratedCode);
        Assert.Contains("Timer.GlobalTick.Add((seconds) => this.onTick(seconds));", result.GeneratedCode);
        AssertCompilesWithCompat(result.GeneratedCode);
    }
}
