using System.IO;
using System.Threading.Tasks;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class EventAndStaticPropertyRegressionTests
{
    [Fact]
    public void NewEventWithBackingField_GeneratesListenerSet()
    {
        var result = Convert(@"
using System;
public class ControlBase {
    public event EventHandler<EventArgs> MouseMove;
}
public class Viewer : ControlBase {
    private event EventHandler<EventArgs> mouseMove;
    public new event EventHandler<EventArgs> MouseMove {
        add { mouseMove += value; }
        remove { mouseMove -= value; }
    }
    internal void RaiseRegularMouseMove(EventArgs args) {
        if (mouseMove != null)
            mouseMove(this, args);
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("_mouseMoveListeners", result.GeneratedCode);
        Assert.Contains("fireMouseMove", result.GeneratedCode);
    }

    [Fact]
    public void ConditionalInvokeOnEvent_UsesListenersField()
    {
        var result = Convert(@"
using System;
public class Viewer {
    public event EventHandler<EventArgs> GraphChanged;
    internal void Set() {
        GraphChanged?.Invoke(this, null);
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.DoesNotContain("fireGraphChanged != null", result.GeneratedCode);
        Assert.Contains("if (!_graphChangedListeners.isEmpty())", result.GeneratedCode);
    }

    [Fact]
    public void InheritedEventSubscription_UsesAddListenerMethod()
    {
        var result = Convert(@"
using System;
public class ControlBase {
    public event EventHandler<MouseEventArgs> MouseClick;
}
public class MouseEventArgs : EventArgs { }
public class DrawingPanel : ControlBase {
    public void SetCms() {
        MouseClick += delegate(object sender, MouseEventArgs e) { OnMouseMove(e); };
    }
    protected void OnMouseMove(MouseEventArgs e) { }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        // The subclass must subscribe via the inherited listener method, not a local field.
        Assert.Contains("addMouseClickListener((sender, e) ->", result.GeneratedCode);
        var panelPart = result.GeneratedCode.Substring(result.GeneratedCode.IndexOf("public class DrawingPanel"));
        Assert.DoesNotContain("_mouseClickListeners", panelPart);
    }

    [Fact]
    public void DelegateCastOfAnonymousMethod_IsStripped()
    {
        var result = Convert(@"
using System;
public class Viewer {
    public delegate void Invoker();
    public void Invoke(Delegate d) { }
    public void Run() {
        Invoke((Invoker)delegate { System.Console.WriteLine(1); });
        Invoke((Invoker)Fix);
    }
    void Fix() { }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.DoesNotContain("(Invoker)", result.GeneratedCode);
        Assert.Contains("invoke(() ->", result.GeneratedCode);
    }

    [Fact]
    public void ExplicitInterfaceEventAndClassEvent_BothGenerated()
    {
        var result = Convert(@"
using System;
public interface IViewer {
    event EventHandler<EventArgs> MouseMove;
}
public class Viewer : IViewer {
    private event EventHandler<EventArgs> mouseMove;
    event EventHandler<EventArgs> IViewer.MouseMove {
        add { mouseMove += value; }
        remove { mouseMove -= value; }
    }
    public new event EventHandler<EventArgs> MouseMove {
        add { mouseMove += value; }
        remove { mouseMove -= value; }
    }
    internal void RaiseRegularMouseMove(EventArgs args) {
        if (mouseMove != null)
            mouseMove(this, args);
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("addIViewerMouseMoveListener", result.GeneratedCode);
        Assert.Contains("addMouseMoveListener", result.GeneratedCode);
        Assert.Contains("fireMouseMove", result.GeneratedCode);
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

    private static async Task<IReadOnlyList<ConversionResult>> ConvertAsync(params SourceFile[] sourceFiles)
    {
        var options = new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };
        var pipeline = new ProjectConversionPipeline(options);
        return await pipeline.ConvertProjectAsync(sourceFiles);
    }
}
