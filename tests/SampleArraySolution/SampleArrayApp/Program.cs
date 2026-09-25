using System;
using SampleArrayLib;

var arr = new int[,] { { 1, 2, 3 }, { 4, 5, 6 } };
Console.WriteLine(ArrayOperations.SumMultiDim(arr));
Console.WriteLine(ArrayOperations.GetRank(arr));
Console.WriteLine(ArrayOperations.GetTotalLength(arr));

var flat = new int[] { 3, 1, 2 };
ArrayOperations.SortArray(flat);
Console.WriteLine(string.Join(",", flat));

ArrayOperations.ResizeArray(ref flat, 5);
Console.WriteLine(flat.Length);
