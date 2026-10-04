namespace SampleNullConditionalLib;

public sealed class Simple
{
    public int B;
}

public sealed class CallTarget
{
    public int SideCalls;

    public int Next()
    {
        SideCalls++;
        return SideCalls;
    }

    public int M(int a, int b) => a * 10 + b;
}

public sealed class Leaf
{
    public int Value;
    public int FireCalls;

    public void Fire(int seed)
    {
        FireCalls++;
    }

    public override string ToString() => "L" + Value;
}

public sealed class TreeNode
{
    public Leaf? Leaf;
    public TreeNode? JumpTarget;

    public TreeNode? Jump(int step) => JumpTarget;
}
