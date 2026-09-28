using System.Collections.Generic;

namespace SampleRefOutInV2Lib;

public class Calculator
{
    public static void Swap(ref int a, ref int b)
    {
        int t = a;
        a = b;
        b = t;
    }

    public static bool TryParse(string s, out Point p)
    {
        p = new Point(0, 0);
        return true;
    }

    public void UpdateField(ref int value)
    {
        value = value + 1;
    }

    public bool TryGet(out int result)
    {
        result = 42;
        return true;
    }

    public ref int GetRef(int[] arr, int index)
    {
        return ref arr[index];
    }
}
