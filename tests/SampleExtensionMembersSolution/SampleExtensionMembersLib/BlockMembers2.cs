namespace SampleExtensionMembers;

// Partial host, part 2: EMD-OP operators (binary/unary/checked), EMD-STATIC
// members qualified by the receiver type name, and a scoped-ref receiver.
public static partial class BlockHosts
{
    extension(Vec vec)
    {
        public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y);

        public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y);

        public static Vec operator checked +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y);

        public static Vec operator -(Vec v) => new(-v.X, -v.Y);

        public static string Describe() => nameof(Vec);

        public static int Unit => 42;
    }

    extension(scoped ref Num n)
    {
        public void Bump(int amount) => n.Value += amount;

        public int Triple => n.Value * 3;
    }
}
