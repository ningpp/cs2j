using System;

namespace SampleMemberDesugarLib;

// C13 事件样本（设计 §7.2）：field-style / custom 转发 / 接口 / 显式实现 / 虚链 / 静态 / struct /
// 初始化器 / 直接触发 NRE / 保留名 skip 对照。行为闸门在去糖前后同样通过。

public class SampleArgs : EventArgs
{
    public string Message { get; }
    public SampleArgs(string message) => Message = message;
}

public class EventSamples
{
    public event EventHandler? Basic;
    public event EventHandler<SampleArgs>? Generic;
    public event Action<string>? Logged;
    public event Func<int> Counted = () => 0;
    public event Action? First, Second;

    public event Action? NoSubscribers;

    protected virtual void RaiseAll(string message)
    {
        Basic?.Invoke(this, EventArgs.Empty);
        Generic?.Invoke(this, new SampleArgs(message));
        Logged?.Invoke(message);
        Counted();
        First?.Invoke();
        Second?.Invoke();
    }

    public void Raise(string message) => RaiseAll(message);

    public void RaiseDirectNoSubscribers() => NoSubscribers();

    public int CountNow() => Counted();
}

public class CustomEventSample
{
    private event Action? _inner;

    public event Action? Forwarded
    {
        add { _inner += value; }
        remove { _inner -= value; }
    }

    public void RaiseForwarded() => _inner?.Invoke();
}

public interface IEventSource
{
    event EventHandler? Changed;
}

public class ImplicitEventSource : IEventSource
{
    public event EventHandler? Changed;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}

public class ExplicitEventSource : IEventSource
{
    private EventHandler? _explicitHandler;

    event EventHandler? IEventSource.Changed
    {
        add { _explicitHandler += value; }
        remove { _explicitHandler -= value; }
    }

    public void RaiseExplicit() => _explicitHandler?.Invoke(this, EventArgs.Empty);
}

public abstract class EventBase
{
    public abstract event EventHandler? Abstract;
    public virtual event EventHandler? Virtual;

    public void RaiseBase() => Virtual?.Invoke(this, EventArgs.Empty);
}

public class EventDerived : EventBase
{
    public override event EventHandler? Abstract;
    public override event EventHandler? Virtual;

    public void RaiseDerived() => Virtual?.Invoke(this, EventArgs.Empty);
}

public static class StaticEventHolder
{
    public static event Action? Ticked;

    public static void Tick() => Ticked?.Invoke();
}

public struct StructEventHolder
{
    public event Action? Changed;

    public void Raise() => Changed?.Invoke();
}

public class ReservedNameSample
{
    // 保留名 value：事件规则整事件跳过（CS2J5157），去糖后原样保留。
    public event Action? value;

    public void Raise() => value?.Invoke();
}
