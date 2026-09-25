// Cs2jArray.ArrayHelper：数组语义收口辅助类（唯一真源）。
// 本文件既是本库项目的源码，又被 CLI 以 EmbeddedResource 嵌入分发：
// array-method 预处理把它释放到中间树 Cs2jArray/ 下，主转换器
// 跳过该项目的转换，调用点经 TypeMappings 映射到 compat 手写类。
// 单文件模式（array-method -i/-o）会把本文件原样写到输出目录（文件名
// _Cs2jArrayHelper.cs），用户自行编译后再走主转换，因此内容须保持
// "可转换 C# 子集"：
// - 不得出现 Array.* 静态调用或 System.Array 接收者成员（会被主转换器
//   映射到 CSharpArray 包装形态，与运行时裸数组不符）；
// - 实现体只用白名单原语：一维/交错数组、arr.Length、arr[i]、数组 foreach、
//   基本控制流、IComparable/IComparer/委托调用、(T)/(T[]) 强转、
//   (T[])new object[n] 泛型数组创建模式。
namespace Cs2jArray
{
    public static class ArrayHelper
    {
        // ---- 元数据（§3.1）----

        public static int GetLength(System.Array array)
        {
            int total = 0;
            foreach (var row in ToRows(array))
            {
                total += row.Length;
            }
            return total;
        }

        public static long GetLongLength(System.Array array)
        {
            return GetLength(array);
        }

        public static int GetLength(System.Array array, int dimension)
        {
            if (dimension < 0) throw new System.IndexOutOfRangeException();
            int d = 0;
            var current = array;
            while (d < dimension)
            {
                if (current.Length == 0) return 0;
                current = (System.Array)current.GetValue(0)!;
                d++;
            }
            return current.Length;
        }

        public static long GetLongLength(System.Array array, int dimension)
        {
            return GetLength(array, dimension);
        }

        public static int GetLowerBound(System.Array array, int dimension)
        {
            if (dimension < 0) throw new System.IndexOutOfRangeException();
            GetLength(array, dimension);
            return 0;
        }

        public static int GetUpperBound(System.Array array, int dimension)
        {
            return GetLowerBound(array, dimension) + GetLength(array, dimension) - 1;
        }

        // ---- 实例成员收口（§3.1）----

        public static System.Array Clone(System.Array array)
        {
            var elementType = array.GetType().GetElementType()!;
            var copy = System.Array.CreateInstance(elementType, array.Length);
            for (int i = 0; i < array.Length; i++)
            {
                copy.SetValue(array.GetValue(i), i);
            }
            return copy;
        }

        public static void CopyTo(System.Array source, System.Array destination, long index)
        {
            for (int i = 0; i < source.Length; i++)
            {
                destination.SetValue(source.GetValue(i), (int)index + i);
            }
        }

        public static T GetValue<T>(T[] array, int index) => array[index];

        public static T GetValue<T>(T[][] array, int index0, int index1) => array[index0][index1];

        public static T GetValue<T>(T[][][] array, int i0, int i1, int i2) => array[i0][i1][i2];

        public static object GetValue(System.Array array, params int[] indices)
        {
            object current = array;
            foreach (var idx in indices)
            {
                current = ((System.Array)current).GetValue(idx);
            }
            return current;
        }

        public static void SetValue<T>(T[] array, object value, int index) => array[index] = (T)value;

        public static void SetValue<T>(T[][] array, object value, int i0, int i1) => array[i0][i1] = (T)value;

        public static void SetValue<T>(T[][][] array, object value, int i0, int i1, int i2) => array[i0][i1][i2] = (T)value;

        public static void SetValue(System.Array array, object value, params int[] indices)
        {
            System.Array current = array;
            for (int i = 0; i < indices.Length - 1; i++)
            {
                current = (System.Array)current.GetValue(indices[i])!;
            }
            current.SetValue(value, indices[indices.Length - 1]);
        }

        // ---- System.Array 静态收口（§3.3）----

        public static void Sort(System.Array array) => System.Array.Sort(array);

        public static void Sort(System.Array array, System.Collections.IComparer comparer) => System.Array.Sort(array, comparer);

        public static void Sort(System.Array array, int index, int length) => System.Array.Sort(array, index, length);

        public static void Sort(System.Array array, int index, int length, System.Collections.IComparer comparer) => System.Array.Sort(array, index, length, comparer);

        public static void Sort(System.Array keys, System.Array items) => System.Array.Sort(keys, items);

        public static void Sort(System.Array keys, System.Array items, System.Collections.IComparer comparer) => System.Array.Sort(keys, items, comparer);

        public static int BinarySearch(System.Array array, object value) => System.Array.BinarySearch(array, value);

        public static int BinarySearch(System.Array array, object value, System.Collections.IComparer comparer) => System.Array.BinarySearch(array, value, comparer);

        public static int BinarySearch(System.Array array, int index, int length, object value) => System.Array.BinarySearch(array, index, length, value);

        public static int BinarySearch(System.Array array, int index, int length, object value, System.Collections.IComparer comparer) => System.Array.BinarySearch(array, index, length, value, comparer);

        public static void Clear(System.Array array, int index, int length) => System.Array.Clear(array, index, length);

        public static T[] Resize<T>(T[] array, int newSize)
        {
            var result = new T[newSize];
            int copyLen = array.Length < newSize ? array.Length : newSize;
            for (int i = 0; i < copyLen; i++)
            {
                result[i] = array[i];
            }
            return result;
        }

        public static int IndexOf(System.Array array, object value) => System.Array.IndexOf(array, value);

        public static int IndexOf(System.Array array, object value, int startIndex) => System.Array.IndexOf(array, value, startIndex);

        public static int IndexOf(System.Array array, object value, int startIndex, int count) => System.Array.IndexOf(array, value, startIndex, count);

        public static int LastIndexOf(System.Array array, object value) => System.Array.LastIndexOf(array, value);

        public static int LastIndexOf(System.Array array, object value, int startIndex) => System.Array.LastIndexOf(array, value, startIndex);

        public static int LastIndexOf(System.Array array, object value, int startIndex, int count) => System.Array.LastIndexOf(array, value, startIndex, count);

        public static void Reverse(System.Array array) => System.Array.Reverse(array);

        public static void Reverse(System.Array array, int index, int length) => System.Array.Reverse(array, index, length);

        public static T Find<T>(T[] array, System.Predicate<T> match)
        {
            foreach (var item in array)
            {
                if (match(item)) return item;
            }
            return default!;
        }

        public static T[] FindAll<T>(T[] array, System.Predicate<T> match)
        {
            var list = new System.Collections.Generic.List<T>();
            foreach (var item in array)
            {
                if (match(item)) list.Add(item);
            }
            return list.ToArray();
        }

        public static int FindIndex<T>(T[] array, System.Predicate<T> match)
        {
            for (int i = 0; i < array.Length; i++)
            {
                if (match(array[i])) return i;
            }
            return -1;
        }

        public static int FindIndex<T>(T[] array, int startIndex, int count, System.Predicate<T> match)
        {
            int end = startIndex + count;
            for (int i = startIndex; i < end; i++)
            {
                if (match(array[i])) return i;
            }
            return -1;
        }

        public static T FindLast<T>(T[] array, System.Predicate<T> match)
        {
            for (int i = array.Length - 1; i >= 0; i--)
            {
                if (match(array[i])) return array[i];
            }
            return default!;
        }

        public static int FindLastIndex<T>(T[] array, System.Predicate<T> match)
        {
            for (int i = array.Length - 1; i >= 0; i--)
            {
                if (match(array[i])) return i;
            }
            return -1;
        }

        public static int FindLastIndex<T>(T[] array, int startIndex, int count, System.Predicate<T> match)
        {
            for (int i = startIndex + count - 1; i >= startIndex; i--)
            {
                if (match(array[i])) return i;
            }
            return -1;
        }

        public static bool Exists<T>(T[] array, System.Predicate<T> match)
        {
            foreach (var item in array)
            {
                if (match(item)) return true;
            }
            return false;
        }

        public static bool TrueForAll<T>(T[] array, System.Predicate<T> match)
        {
            foreach (var item in array)
            {
                if (!match(item)) return false;
            }
            return true;
        }

        public static TOutput[] ConvertAll<TInput, TOutput>(TInput[] array, System.Converter<TInput, TOutput> converter)
        {
            var result = new TOutput[array.Length];
            for (int i = 0; i < array.Length; i++)
            {
                result[i] = converter(array[i]);
            }
            return result;
        }

        public static void ForEach<T>(T[] array, System.Action<T> action)
        {
            foreach (var item in array)
            {
                action(item);
            }
        }

        public static void Copy(System.Array source, System.Array destination, int length)
            => System.Array.Copy(source, destination, length);

        public static void Copy(System.Array source, int sourceIndex, System.Array destination, int destinationIndex, int length)
            => System.Array.Copy(source, sourceIndex, destination, destinationIndex, length);

        public static void ConstrainedCopy(System.Array source, int sourceIndex, System.Array destination, int destinationIndex, int length)
            => System.Array.ConstrainedCopy(source, sourceIndex, destination, destinationIndex, length);

        // ---- 内部辅助 ----

        private static System.Array[] ToRows(System.Array array)
        {
            if (array is object[] outer && outer.Length > 0 && outer[0] is System.Array)
            {
                var rows = new System.Array[outer.Length];
                for (int i = 0; i < outer.Length; i++)
                {
                    rows[i] = (System.Array)outer[i]!;
                }
                return rows;
            }
            return new[] { array };
        }
    }
}
