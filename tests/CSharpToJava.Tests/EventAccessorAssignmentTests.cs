using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EventAccessorAssignmentTests
{
    [Fact]
    public void EventAccessor_AddRemoveUseListenerList()
    {
        const string code = """
            using System;

            public class C {
                event EventHandler E;
                public event EventHandler P {
                    add { E += value; }
                    remove { E -= value; }
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("+= value", result.GeneratedCode);
        Assert.DoesNotContain("-= value", result.GeneratedCode);
        Assert.Contains("_eListeners.add(handler)", result.GeneratedCode);
        Assert.Contains("_eListeners.remove(handler)", result.GeneratedCode);
    }
}
