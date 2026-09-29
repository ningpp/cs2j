using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Full project-level conversion tests for the MSAGL Set&lt;T&gt; operator * method,
/// which uses standalone .Where() calls (no terminal like .ToList()) inside a
/// ternary expression passed to a constructor.
///
/// This reproduces the exact MSAGL pattern that was producing residual .where()
/// in generated Java code.
/// </summary>
public class LinqSetOperatorProjectTests
{
    private static ConversionOptions CreateOptions()
    {
        return new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            PreferStreamApi = false,
        };
    }

    /// <summary>
    /// Minimal ValidateArg stub — the real one is in MSAGL's Core/ValidateArg.cs.
    /// </summary>
    private const string ValidateArgStub = @"
using System;
namespace Microsoft.Msagl.Core.DataStructures {
    internal static class ValidateArg {
        public static void IsNotNull(object obj, string name) {
            if (obj == null) throw new ArgumentNullException(name);
        }
    }
}";

    /// <summary>
    /// The exact Set.cs from MSAGL, stripped of SHARPKIT conditionals.
    /// This preserves the MarshalByRefObject base class, SuppressMessage attributes,
    /// and the exact Where() calls that caused the original errors.
    /// </summary>
    private const string SetCs = @"
using System;
using System.Collections.Generic;
using System.Linq;
namespace Microsoft.Msagl.Core.DataStructures {
    [System.Diagnostics.CodeAnalysis.SuppressMessage(""Microsoft.Naming"", ""CA1710:IdentifiersShouldHaveCorrectSuffix""), System.Diagnostics.CodeAnalysis.SuppressMessage(""Microsoft.Naming"", ""CA1716:IdentifiersShouldNotMatchKeywords"", MessageId = ""Set"")]
    public class Set<T> : MarshalByRefObject, ICollection<T> {
        HashSet<T> hashSet = new HashSet<T>();
        public void Insert(T element) { hashSet.Add(element); }
        void ICollection<T>.Add(T t) { Insert(t); }
        public bool Contains(T item) { return hashSet.Contains(item); }
        public void Delete(T item) { hashSet.Remove(item); }
        public bool Remove(T item) { return hashSet.Remove(item); }
        public int Count { get { return hashSet.Count; } }
        public IEnumerator<T> GetEnumerator() { return hashSet.GetEnumerator(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { return hashSet.GetEnumerator(); }
        public bool IsReadOnly { get { return false; } }
        public void CopyTo(T[] array, int arrayIndex) { hashSet.CopyTo(array, arrayIndex); }
        public void Clear() { this.hashSet.Clear(); }
        public Set() { }
        public Set(IEnumerable<T> enumerableCollection) {
            ValidateArg.IsNotNull(enumerableCollection, ""enumerableCollection"");
            foreach (T j in enumerableCollection) this.Insert(j);
        }
        // === operator * — the method with standalone .Where() calls ===
        static public Set<T> operator *(Set<T> set0, Set<T> set1) {
            ValidateArg.IsNotNull(set0, ""set0"");
            ValidateArg.IsNotNull(set1, ""set1"");
            return new Set<T>(set0.Count < set1.Count
                ? set0.Where(a => set1.Contains(a))
                : set1.Where(a => set0.Contains(a)));
        }
        static public Set<T> operator +(Set<T> set0, Set<T> set1) {
            ValidateArg.IsNotNull(set1, ""set1"");
            Set<T> ret = new Set<T>(set0);
            foreach (T t in set1) ret.Insert(t);
            return ret;
        }
        static public Set<T> operator -(Set<T> set0, Set<T> set1) {
            ValidateArg.IsNotNull(set1, ""set1"");
            Set<T> ret = new Set<T>(set0);
            foreach (T t in set1) ret.Remove(t);
            return ret;
        }
    }
}";

    /// <summary>
    /// Same test but with many additional files to simulate a large project.
    /// This reproduces the MSAGL scenario where the LinqRewriter throws
    /// NotSupportedException in TryVisitInvocationExpression for standalone
    /// Where chains in large compilations.
    /// </summary>
}
