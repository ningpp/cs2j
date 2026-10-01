using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SampleMemberDesugarLib;

// C39 终结器消除样本（设计 §7.4）：B 档删除矩阵的各种 bodyKind / pairing / 拒绝原因。
// 行为闸门 FinalizerBehaviorTests 在 A 档（仅报告）与 B 档（删除）产物上都必须通过。

/// <summary>Empty：B 档直接删除（无可见行为，仅 GC 簿记）。</summary>
public sealed class FinalizerEmpty
{
    ~FinalizerEmpty()
    {
    }
}

/// <summary>DisposePattern + Paired：标准 IDisposable 配对 SuppressFinalize，B 档删除终结器。</summary>
public class FinalizerDisposePattern : IDisposable
{
    private MemoryStream? _stream;

    public bool IsDisposed { get; private set; }

    public FinalizerDisposePattern()
    {
        _stream = new MemoryStream();
    }

    ~FinalizerDisposePattern()
    {
        Dispose(false);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        if (disposing)
        {
            _stream?.Dispose();
        }

        _stream = null;
        IsDisposed = true;
    }
}

internal sealed class FinalizerSampleHandle : SafeHandle
{
    public FinalizerSampleHandle()
        : base(IntPtr.Zero, true)
    {
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle() => true;
}

/// <summary>SafeHandleOnly：终结器只 Dispose SafeHandle 字段，B 档删除（SafeHandle 自带可靠释放）。</summary>
public sealed class FinalizerSafeHandle
{
    private readonly FinalizerSampleHandle _handle = new();

    ~FinalizerSafeHandle()
    {
        _handle.Dispose();
    }
}

/// <summary>NativeRelease：终结器释放非托管句柄，B 档保守拒绝（5204），需 forceRemoveTypes 逃生口。
/// 构造不分配，测试不触发真实释放。</summary>
public sealed class FinalizerNativeRelease
{
    private readonly IntPtr _p;

    public FinalizerNativeRelease(IntPtr p)
    {
        _p = p;
    }

    ~FinalizerNativeRelease()
    {
        Marshal.FreeHGlobal(_p);
    }
}

/// <summary>Other：终结器含 flush/日志类托管逻辑，B 档保守拒绝（5204）。</summary>
public sealed class FinalizerOther
{
    private readonly StringBuilder _log = new();

    ~FinalizerOther()
    {
        _log.Append('x');
    }

    public int LogLength => _log.Length;
}

/// <summary>Resurrect：类型内存在 GC.ReRegisterForFinalize 复活站点，即使终结器空体 B 档也拒绝（5204）。</summary>
public sealed class FinalizerResurrect
{
    ~FinalizerResurrect()
    {
    }

    public void Revive()
    {
        GC.ReRegisterForFinalize(this);
    }
}

/// <summary>Extern：extern 终结器无托管方法体，B 档保守拒绝（5204）。</summary>
public sealed class FinalizerExtern
{
    // 终结器不允许显式可访问性修饰符（CS0106）；extern 分号形态产生 CS0626 警告（非错误），
    // 语义上正是 Extern bodyKind 样本：无托管方法体。
    extern ~FinalizerExtern();
}
