using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// 显式接口实现方法（桥）的 Java 返回类型必须采用映射接口成员的形状
/// （接口映射权威）。C# 侧 `void IList.RemoveAt(int) => throw ...` 的槽位
/// 映射到 compat CSharpIList 后是 `Object removeAt(int)`（removeAt 协变），
/// `void IList.Remove(object?)` 是 `boolean remove(Object)`——void 实现与
/// 同擦除签名的接口 default 成员构成 javac"返回类型不兼容"硬错
/// （真实链 dotnet-text-regularexpressions 的 Capture/Group/MatchCollection 六站）。
/// </summary>
public class ExplicitInterfaceMethodReturnTypeTests
{
    [Fact]
    public async Task ExplicitRemoveAtAndRemove_AdoptMappedInterfaceReturnTypes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cs2j-explicit-iface-shape-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "ReadOnlyList.cs"), @"
using System;
using System.Collections;
using System.Collections.Generic;

namespace RePro
{
    public class ReadOnlyList : IList<string>, IList
    {
        private readonly object _syncRoot = new object();
        private readonly List<string> _items = new List<string>();

        public int Count => _items.Count;
        public bool IsSynchronized => false;
        public object SyncRoot => _syncRoot;
        public bool IsFixedSize => true;
        public bool IsReadOnly => true;

        public string this[int index] => _items[index];

        public IEnumerator<string> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        int IList<string>.IndexOf(string item) => _items.IndexOf(item);
        void IList<string>.Insert(int index, string item) => throw new NotSupportedException();
        void IList<string>.RemoveAt(int index) => throw new NotSupportedException();

        void ICollection<string>.Add(string item) => _items.Add(item);
        void ICollection<string>.Clear() => _items.Clear();
        bool ICollection<string>.Contains(string item) => _items.Contains(item);
        void ICollection<string>.CopyTo(string[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
        bool ICollection<string>.Remove(string item) => _items.Remove(item);

        int IList.Add(object value) => throw new NotSupportedException();
        void IList.Clear() => throw new NotSupportedException();
        bool IList.Contains(object value) => value is string s && _items.Contains(s);
        int IList.IndexOf(object value) => value is string s ? _items.IndexOf(s) : -1;
        void IList.Insert(int index, object value) => throw new NotSupportedException();
        void IList.Remove(object value) => throw new NotSupportedException();
        void IList.RemoveAt(int index) => throw new NotSupportedException();

        void ICollection.CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    }
}
");

            var options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            };

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(tempDir, options);

            var java = Assert.Single(results, r =>
                string.Equals(r.FileName, "ReadOnlyList.java", StringComparison.OrdinalIgnoreCase));
            Assert.True(java.Success, string.Join("\n", java.Diagnostics));

            // removeAt 槽位（IList<string>.RemoveAt / IList.RemoveAt）→ Object removeAt(int)
            Assert.Contains("Object removeAt(", java.GeneratedCode, StringComparison.Ordinal);
            Assert.DoesNotContain("void removeAt(", java.GeneratedCode, StringComparison.Ordinal);

            // remove 槽位（IList.Remove）→ boolean remove(Object)
            Assert.Contains("boolean remove(Object", java.GeneratedCode, StringComparison.Ordinal);
            Assert.DoesNotContain("void remove(", java.GeneratedCode, StringComparison.Ordinal);

            // 纯抛出体保持原样：Object removeAt 仍以 throw 结尾，无补默认返回
            Assert.Contains("throw new UnsupportedOperationException", java.GeneratedCode, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitNonThrowBody_GetsDefaultReturnForMappedShape()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cs2j-explicit-iface-default-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // remove 槽位体不是纯抛出体（普通语句体）时，返回类型变为 boolean
            // 后必须补默认返回 false，否则 Java 缺失返回语句编译错。
            await File.WriteAllTextAsync(Path.Combine(tempDir, "MutatingList.cs"), @"
using System;
using System.Collections;
using System.Collections.Generic;

namespace RePro
{
    public class MutatingList : IList<string>, IList
    {
        private readonly object _syncRoot = new object();
        private readonly List<string> _items = new List<string>();

        public int Count => _items.Count;
        public bool IsSynchronized => false;
        public object SyncRoot => _syncRoot;
        public bool IsFixedSize => false;
        public bool IsReadOnly => false;

        public string this[int index] { get => _items[index]; set => _items[index] = value; }

        public IEnumerator<string> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        int IList<string>.IndexOf(string item) => _items.IndexOf(item);
        void IList<string>.Insert(int index, string item) => _items.Insert(index, item);
        void IList<string>.RemoveAt(int index) => _items.RemoveAt(index);

        void ICollection<string>.Add(string item) => _items.Add(item);
        void ICollection<string>.Clear() => _items.Clear();
        bool ICollection<string>.Contains(string item) => _items.Contains(item);
        void ICollection<string>.CopyTo(string[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
        bool ICollection<string>.Remove(string item) => _items.Remove(item);

        int IList.Add(object value)
        {
            _items.Add((string)value);
            return _items.Count;
        }
        void IList.Clear() => _items.Clear();
        bool IList.Contains(object value) => value is string s && _items.Contains(s);
        int IList.IndexOf(object value) => value is string s ? _items.IndexOf(s) : -1;
        void IList.Insert(int index, object value) => _items.Insert(index, (string)value);
        void IList.Remove(object value)
        {
            if (value is string s)
            {
                _items.Remove(s);
            }
        }
        void IList.RemoveAt(int index)
        {
            _items.RemoveAt(index);
        }

        void ICollection.CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    }
}
");

            var options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            };

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(tempDir, options);

            var java = Assert.Single(results, r =>
                string.Equals(r.FileName, "MutatingList.java", StringComparison.OrdinalIgnoreCase));
            Assert.True(java.Success, string.Join("\n", java.Diagnostics));

            Assert.Contains("Object removeAt(", java.GeneratedCode, StringComparison.Ordinal);
            Assert.DoesNotContain("void removeAt(", java.GeneratedCode, StringComparison.Ordinal);
            Assert.Contains("boolean remove(Object", java.GeneratedCode, StringComparison.Ordinal);
            // 非终结块体补默认返回，保证非 void 形状可编译（remove 槽位 → boolean）
            Assert.Contains("return false;", java.GeneratedCode, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
