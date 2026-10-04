namespace SampleExtensionMembers;

// Partial host, part 1: instance method/property members (EMD-M) on a generic
// receiver, plus a second block that adds its own type parameter (n+m mapping).
public static partial class BlockHosts
{
    extension<T>(Box<T> box)
    {
        public string Describe() => $"Box<{typeof(T).Name}>({box.Value})";

        public T Peek => box.Value;
    }

    // Two-level n+m generic mapping: n on the block (receiver T), m on the
    // member (U). Call sites qualify by the receiver type name and spell out
    // n+m type arguments explicitly.
    extension<T>(T r) where T : class
    {
        public static string PairWith<U>(U other)
            => typeof(T).Name + " + " + other;
    }
}
