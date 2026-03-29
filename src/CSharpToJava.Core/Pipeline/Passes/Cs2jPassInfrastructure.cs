using System.Diagnostics;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.Pipeline;

public enum Cs2jPassStage
{
    Desugar,
    Check,
    Normalize,
    Emit,
}

public sealed record Cs2jPassMetric(
    string Name,
    Cs2jPassStage Stage,
    TimeSpan Elapsed,
    int DiagnosticCountBefore,
    int DiagnosticCountAfter,
    long ManagedMemoryBytesBefore,
    long ManagedMemoryBytesAfter)
{
    public int DiagnosticDelta => DiagnosticCountAfter - DiagnosticCountBefore;
    public long ManagedMemoryDelta => ManagedMemoryBytesAfter - ManagedMemoryBytesBefore;
}

public sealed class Cs2jPassExecutionException : Exception
{
    public Cs2jPassExecutionException(string passName, Cs2jPassStage stage, Exception innerException)
        : base($"Pass '{passName}' ({stage}) failed: {innerException.Message}", innerException)
    {
        PassName = passName;
        Stage = stage;
    }

    public string PassName { get; }
    public Cs2jPassStage Stage { get; }
}

public interface ICs2jPass<TState>
{
    string Name { get; }
    Cs2jPassStage Stage { get; }
    void Execute(TState state);
}

public static class Cs2jPassExecutor
{
    public static IReadOnlyList<Cs2jPassMetric> Execute<TState>(
        TState state,
        ConversionContext context,
        IEnumerable<ICs2jPass<TState>> passes,
        List<Cs2jPassMetric>? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(passes);

        metrics ??= [];

        foreach (var pass in passes)
        {
            var diagnosticCountBefore = context.Diagnostics.Messages.Count;
            var managedMemoryBefore = GC.GetTotalMemory(forceFullCollection: false);
            var stopwatch = Stopwatch.StartNew();

            try
            {
                pass.Execute(state);
            }
            catch (Exception ex) when (ex is not Cs2jPassExecutionException)
            {
                throw new Cs2jPassExecutionException(pass.Name, pass.Stage, ex);
            }
            finally
            {
                stopwatch.Stop();
                metrics.Add(new Cs2jPassMetric(
                    pass.Name,
                    pass.Stage,
                    stopwatch.Elapsed,
                    diagnosticCountBefore,
                    context.Diagnostics.Messages.Count,
                    managedMemoryBefore,
                    GC.GetTotalMemory(forceFullCollection: false)));
            }
        }

        return metrics;
    }
}