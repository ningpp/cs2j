namespace SampleNumericBoundaryLib;

/// <summary>
/// M1 retention boundary (NB-3/NB-5/NB-10 arrive with M2): built-in integer
/// checked expressions and checked blocks stay intact in the intermediate tree.
/// They remain legal C#, so verify-build passes and runtime overflow behavior
/// is unchanged.
/// </summary>
public static class CheckedSamples
{
    public static int CheckedAdd(int a, int b) => checked(a + b);

    public static int CheckedBlockSum(int[] values)
    {
        int sum = 0;
        checked
        {
            foreach (int v in values)
            {
                sum += v;
            }
        }

        return sum;
    }

    public static byte CheckedNarrow(int v) => checked((byte)v);
}
