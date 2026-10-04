namespace AssignmentLib;

/// <summary>
/// Carrier for every C51 assignment-terminal shape: field/property/indexer,
/// compound, event accessors, coalesce targets and a chainable next node.
/// </summary>
public sealed class Node
{
    public int Field;

    public int Prop { get; set; }

    public int this[int i]
    {
        get => Field;
        set => Field = value;
    }

    public Node? Next;

    public string? Label;

    public int? Number;

    public int Adds;

    public int Removes;

    public event Action? Ping
    {
        add => Adds++;
        remove => Removes++;
    }

    public static int FactoryCalls;

    public static Node? Make()
    {
        FactoryCalls++;
        return new Node();
    }
}
