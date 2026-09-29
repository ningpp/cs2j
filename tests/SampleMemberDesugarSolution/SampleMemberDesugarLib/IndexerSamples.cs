using System.Runtime.CompilerServices;

namespace SampleMemberDesugarLib;

public interface IRow
{
    int this[int index] { get; }
}

public interface IGrid
{
    int this[int row, int col] { get; set; }
}

public abstract class MatrixBase
{
    public abstract int this[int row, int col] { get; set; }
}

public sealed class Grid : MatrixBase
{
    private readonly int[] _cells = new int[16];

    public override int this[int row, int col]
    {
        get => _cells[row * 4 + col];
        set => _cells[row * 4 + col] = value;
    }

    public int Sum() => this[0, 0] + this[1, 1] + this[2, 2];
}

public sealed class NamedIndexer
{
    private readonly string[] _vals = new string[8];

    [IndexerName("Slot")]
    public string this[int i]
    {
        get => _vals[i];
        set => _vals[i] = value;
    }
}

public sealed class ExplicitGrid : IRow, IGrid
{
    private readonly int[] _cells = new int[16];

    int IRow.this[int index] => _cells[index];

    int IGrid.this[int row, int col]
    {
        get => _cells[row * 4 + col];
        set => _cells[row * 4 + col] = value;
    }

    public int Total(IRow row, IGrid grid)
    {
        grid[0, 1] = 5;
        return row[0] + grid[0, 1];
    }
}

public sealed class RefCells
{
    private readonly int[] _data = new int[4];

    public ref int this[int i] => ref _data[i];

    public int Bump()
    {
        this[1] = 7;
        this[1] += 5;
        this[2]++;
        return _data[1] + _data[2];
    }
}

public sealed class SideEffectIndexer
{
    private readonly int[] _vals = new int[8];
    private int _seed;

    public int Next() => _seed++;

    public int this[int i]
    {
        get => _vals[i];
        set => _vals[i] = value;
    }

    public int Bump()
    {
        this[Next()] += 10;
        return _vals[1] + _seed;
    }
}
