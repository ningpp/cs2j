// Cs2jPrelude.Cs2jResource：大文本/大数组资源加载器（big-literal-resource 预处理唯一真源）。
// 分发方式与 CsPrelude.cs 相同：既是本库项目的源码，又被 CLI 以 EmbeddedResource
// 嵌入；big-literal-resource 预处理把它释放到中间树 Cs2jPrelude/ 下，主转换器跳过
// 该项目的转换，调用点经 TypeMappings 映射到 compat 手写类 io.github.ningpp.compat.Cs2jResource。
//
// 载荷格式（C# 与 Java 两侧逐字节一致）：
// - 定长元素数组：按元素宽度小端定宽槽位表（bool 1 字节 0/1，char 为 UTF-16 码元 2 字节）；
// - string[]：每个元素「int32 小端字节数 + UTF-8 字节」；
// - string：整个文件即 UTF-8 字节。
// 资源以 EmbeddedResource + LogicalName 编译进宿主程序集，因此运行时按
// LogicalName 在已加载程序集中查找清单资源流。
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace Cs2jPrelude
{
    /// <summary>
    /// 从程序集清单资源读取 <c>big-literal-resource</c> 预处理外置的载荷。
    /// 方法名与 Java 侧 <c>io.github.ningpp.compat.Cs2jResource</c> 一一对应
    /// （TypeMappings 按名字映射静态调用）。
    /// </summary>
    public static class Cs2jResource
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, byte[]> Cache =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);

        static Cs2jResource()
        {
            if (!BitConverter.IsLittleEndian)
            {
                throw new InvalidOperationException(
                    "cs2j resource payloads are little-endian; this host is big-endian and unsupported.");
            }
        }

        /// <summary>读取 <c>byte[]</c> 载荷。</summary>
        public static byte[] LoadBytes(string name) => ReadAll(name);

        /// <summary>读取 <c>short[]</c> 载荷。</summary>
        public static short[] LoadInt16(string name)
        {
            var bytes = ReadAll(name);
            var values = new short[bytes.Length / 2];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
            }
            return values;
        }

        /// <summary>读取 <c>int[]</c> 载荷。</summary>
        public static int[] LoadInt32(string name)
        {
            var bytes = ReadAll(name);
            var values = new int[bytes.Length / 4];
            for (int i = 0; i < values.Length; i++)
            {
                int offset = i * 4;
                values[i] = bytes[offset]
                    | (bytes[offset + 1] << 8)
                    | (bytes[offset + 2] << 16)
                    | (bytes[offset + 3] << 24);
            }
            return values;
        }

        /// <summary>读取 <c>long[]</c> 载荷。</summary>
        public static long[] LoadInt64(string name)
        {
            var bytes = ReadAll(name);
            var values = new long[bytes.Length / 8];
            for (int i = 0; i < values.Length; i++)
            {
                int offset = i * 8;
                ulong bits = 0;
                for (int b = 0; b < 8; b++)
                {
                    bits |= (ulong)bytes[offset + b] << (8 * b);
                }
                values[i] = unchecked((long)bits);
            }
            return values;
        }

        /// <summary>读取 <c>float[]</c> 载荷。</summary>
        public static float[] LoadSingle(string name)
        {
            var bytes = ReadAll(name);
            var values = new float[bytes.Length / 4];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = BitConverter.ToSingle(bytes, i * 4);
            }
            return values;
        }

        /// <summary>读取 <c>double[]</c> 载荷。</summary>
        public static double[] LoadDouble(string name)
        {
            var bytes = ReadAll(name);
            var values = new double[bytes.Length / 8];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = BitConverter.ToDouble(bytes, i * 8);
            }
            return values;
        }

        /// <summary>读取 <c>char[]</c> 载荷（UTF-16 码元，含未配对代理项）。</summary>
        public static char[] LoadChars(string name)
        {
            var bytes = ReadAll(name);
            var values = new char[bytes.Length / 2];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = (char)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
            }
            return values;
        }

        /// <summary>读取 <c>bool[]</c> 载荷。</summary>
        public static bool[] LoadBooleans(string name)
        {
            var bytes = ReadAll(name);
            var values = new bool[bytes.Length];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = bytes[i] != 0;
            }
            return values;
        }

        /// <summary>读取 <c>string[]</c> 载荷。</summary>
        public static string[] LoadStrings(string name)
        {
            var bytes = ReadAll(name);
            var values = new List<string>();
            int offset = 0;
            while (offset < bytes.Length)
            {
                int length = bytes[offset]
                    | (bytes[offset + 1] << 8)
                    | (bytes[offset + 2] << 16)
                    | (bytes[offset + 3] << 24);
                offset += 4;
                values.Add(Encoding.UTF8.GetString(bytes, offset, length));
                offset += length;
            }
            return values.ToArray();
        }

        /// <summary>读取单个 <c>string</c> 载荷。</summary>
        public static string LoadString(string name) => Encoding.UTF8.GetString(ReadAll(name));

        private static byte[] ReadAll(string name)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            lock (Gate)
            {
                byte[] cached;
                if (Cache.TryGetValue(name, out cached))
                {
                    return cached;
                }

                var stream = OpenManifestResource(name);
                if (stream == null)
                {
                    throw new InvalidOperationException(
                        "cs2j resource '" + name + "' was not found in any loaded assembly. "
                        + "big-literal-resource embeds it as an EmbeddedResource with that LogicalName; "
                        + "a missing entry means the project file lost the item or the assembly "
                        + "was not loaded.");
                }

                using (stream)
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    var bytes = buffer.ToArray();
                    Cache[name] = bytes;
                    return bytes;
                }
            }
        }

        private static Stream OpenManifestResource(string name)
        {
            var own = typeof(Cs2jResource).GetTypeInfo().Assembly;
            var stream = own.GetManifestResourceStream(name);
            if (stream != null)
            {
                return stream;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (ReferenceEquals(assembly, own))
                {
                    continue;
                }

                try
                {
                    stream = assembly.GetManifestResourceStream(name);
                }
                catch (NotSupportedException)
                {
                    // Dynamic assemblies cannot expose manifest resources.
                    continue;
                }

                if (stream != null)
                {
                    return stream;
                }
            }

            return null;
        }
    }
}
