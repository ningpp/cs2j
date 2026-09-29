namespace CSharpToJava.Core.MemberDesugar.Events;

/// <summary>CS2J5150-5158 消息文案（镜像 PropertyDiagnostics）。</summary>
internal static class EventDiagnostics
{
    public static string Unsupported(string name, string reason)
        => $"'{name}': {reason}; the event plan was skipped atomically.";

    public static string Dynamic(string name)
        => $"subscription target '{name}' is reached through a dynamic receiver; the event plan was skipped atomically.";

    public static string Unbindable(string name)
        => $"a reference to '{name}' could not be bound by the semantic model; the event plan was skipped atomically.";

    public static string AttributeUnroutable(string name, string attribute)
        => $"'{name}': attribute '{attribute}' cannot be routed to the generated field/methods; the event plan was skipped atomically.";

    public static string NameCollision(string name)
        => $"'{name}': the event name collides with the reserved parameter name 'value'; the event plan was skipped atomically.";

    public static string StructCasDegraded(string name)
        => $"'{name}': struct events never use Interlocked.CompareExchange (this may be a copy); the accessor was generated in Simple mode.";

    public static string Residual(string name)
        => $"'{name}' has neither a plan nor a skip diagnostic (residual gate).";
}
