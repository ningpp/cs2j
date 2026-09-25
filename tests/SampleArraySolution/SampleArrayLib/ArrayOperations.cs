using System;

namespace SampleArrayLib;

public class ArrayOperations
{
    public static int SumMultiDim(int[,] arr)
    {
        int sum = 0;
        foreach (var x in arr)
        {
            sum += x;
        }
        return sum;
    }

    public static int GetFirstRowLength(int[,] arr)
    {
        return arr.GetLength(0);
    }

    public static int GetRank(int[,] arr)
    {
        return arr.Rank;
    }

    public static int GetTotalLength(int[,] arr)
    {
        return arr.Length;
    }

    public static int[] CloneArray(int[] arr)
    {
        return (int[])arr.Clone();
    }

    public static void SortArray(int[] arr)
    {
        Array.Sort(arr);
    }

    public static void ResizeArray(ref int[] arr, int newSize)
    {
        Array.Resize(ref arr, newSize);
    }

    public static bool ContainsValue(int[] arr, int target)
    {
        return Array.Exists(arr, x => x == target);
    }

    public static void SetValue(int[,] arr, int value, int i, int j)
    {
        arr.SetValue(value, i, j);
    }

    public static object GetValue(int[,] arr, int i, int j)
    {
        return arr.GetValue(i, j);
    }
}
