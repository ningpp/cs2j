using SampleVarianceAuditLib;

namespace SampleVarianceAuditApp;

/// <summary>
/// Variance-dependent use sites exercised by the audit (design D3/D3a/D7/D9/D5b).
/// Everything here is legal C#; the audit reports which sites will not survive a
/// Java invariant-type emission (VA010/011/012/015) or lose value semantics (VA027).
/// </summary>
public static class VarianceUses
{
    // Positive/safe use: same closed type, no variance conversion involved.
    public static string Read(IProducer<string> producer) => producer.Produce();

    // Covariant conversion at an argument position -> VA010.
    public static void AcceptProducer(IProducer<object> producer) => _ = producer.Produce();

    public static void CovariantArgument(IProducer<string> producer) => AcceptProducer(producer);

    // Covariant assignment relying on declaration-site variance -> VA015 (both-flow edge).
    public static object CovariantAssignment()
    {
        IProducer<string> narrow = new StringProducer("value");
        IProducer<object> wide = narrow;
        return wide.Produce();
    }

    // Contravariant conversion at an argument position -> VA011.
    public static void AcceptAction(Action<string> action) => action("value");

    public static void ContravariantArgument()
    {
        Action<object> acceptsObject = _ => { };
        AcceptAction(acceptsObject);
    }

    // Method-group contravariant conversion -> VA011.
    private static void HandleObject(object value) => _ = value;

    public static void MethodGroupArgument() => AcceptAction(HandleObject);

    // Reference-type array covariance -> VA015 (both-flow edge).
    public static int ArrayCovariance()
    {
        string[] names = { "a", "b" };
        object[] wider = names;
        return wider.Length;
    }

    // Mapped-mutability: the read-only C# contract maps to a mutable Java type -> VA012.
    public static IReadOnlyList<string> ReadOnlyView() => new List<string>();

    // default(T) on a struct-constrained T -> VA020 + VA027 (value semantics lost).
    public static T Zero<T>() where T : struct => default(T);
}