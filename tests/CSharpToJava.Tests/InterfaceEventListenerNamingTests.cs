using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for interface-qualified event listener naming and backing-store skipping.
/// A class implementing an interface event (or declaring one explicitly) must generate
/// add/remove/fire members prefixed with the interface name (e.g. addIViewerMouseDownListener)
/// so they do not collide with same-erasure listener methods inherited from a base class
/// (e.g. System.Windows.Forms.Control.addMouseDownListener(BiConsumer&lt;Object, MouseEventArgs&gt;)).
/// A field-like event used only as the backing store of an explicit event must not be
/// generated as a second listener set.
/// </summary>
public class InterfaceEventListenerNamingTests
{
    [Fact]
    public void EventDeclaredInInterfaceBody_UsesInterfaceQualifiedNames()
    {
        var result = Convert(@"
using System;
public interface IViewer {
    event EventHandler<EventArgs> MouseDown;
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("addIViewerMouseDownListener", result.GeneratedCode);
        Assert.Contains("removeIViewerMouseDownListener", result.GeneratedCode);
        // Interface bodies emit only abstract add/remove listener signatures.
        Assert.DoesNotContain("fireIViewerMouseDown", result.GeneratedCode);
        Assert.DoesNotContain("addMouseDownListener", result.GeneratedCode);
    }

    [Fact]
    public void ImplicitInterfaceEventImplementation_UsesInterfaceQualifiedNames()
    {
        var result = Convert(@"
using System;
public interface IViewer {
    event EventHandler<EventArgs> MouseDown;
}
public class Viewer : IViewer {
    public event EventHandler<EventArgs> MouseDown;
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("addIViewerMouseDownListener", result.GeneratedCode);
        Assert.Contains("removeIViewerMouseDownListener", result.GeneratedCode);
        Assert.Contains("_iViewermouseDownListeners", result.GeneratedCode);
        Assert.Contains("fireIViewerMouseDown", result.GeneratedCode);
        Assert.DoesNotContain("addMouseDownListener", result.GeneratedCode);
    }

    [Fact]
    public void ExplicitInterfaceEvent_UsesInterfaceQualifiedNames()
    {
        var result = Convert(@"
using System;
public interface IViewer {
    event EventHandler<EventArgs> MouseDown;
}
public class Viewer : IViewer {
    private event EventHandler<EventArgs> mouseDown;
    event EventHandler<EventArgs> IViewer.MouseDown {
        add { mouseDown += value; }
        remove { mouseDown -= value; }
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("addIViewerMouseDownListener", result.GeneratedCode);
        Assert.Contains("removeIViewerMouseDownListener", result.GeneratedCode);
        Assert.DoesNotContain("addMouseDownListener", result.GeneratedCode);
    }

    [Fact]
    public void BackingStoreEventOfExplicitEvent_IsSkipped()
    {
        var result = Convert(@"
using System;
public class Viewer {
    private event EventHandler<EventArgs> mouseMove;
    public event EventHandler<EventArgs> MouseMove {
        add { mouseMove += value; }
        remove { mouseMove -= value; }
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        // Only the explicit event may generate a listener set: one add method, one
        // remove method and one fire loop. A second set from the backing field-like
        // event (mouseMove) would duplicate these.
        Assert.Equal(1, CountOccurrences(result.GeneratedCode, "public void addMouseMoveListener("));
        Assert.Equal(1, CountOccurrences(result.GeneratedCode, "public void removeMouseMoveListener("));
        Assert.Equal(1, CountOccurrences(result.GeneratedCode, "for (var _handler : _mouseMoveListeners)"));
    }

    [Fact]
    public void InterfaceEventSubscriptionCallSite_UsesQualifiedNames()
    {
        var result = Convert(@"
using System;
public interface IViewer {
    event EventHandler<EventArgs> MouseDown;
}
public class Editor {
    private readonly IViewer viewer;
    public Editor(IViewer viewer) { this.viewer = viewer; }
    public void Attach() {
        viewer.MouseDown += OnMouseDown;
        viewer.MouseDown -= OnMouseDown;
    }
    void OnMouseDown(object sender, EventArgs e) { }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("viewer.addIViewerMouseDownListener(", result.GeneratedCode);
        Assert.Contains("viewer.removeIViewerMouseDownListener(", result.GeneratedCode);
        Assert.DoesNotContain("viewer.addMouseDownListener(", result.GeneratedCode);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
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
}
