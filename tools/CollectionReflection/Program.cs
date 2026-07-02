using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CollectionReflection;

public static class TypeExtensions
{
    private static readonly Dictionary<Type, string> s_keywordAliases = new()
    {
        { typeof(void), "void" },
        { typeof(bool), "bool" },
        { typeof(int), "int" },
        { typeof(long), "long" },
        { typeof(float), "float" },
        { typeof(double), "double" },
        { typeof(decimal), "decimal" },
        { typeof(string), "string" },
        { typeof(object), "object" },
        { typeof(char), "char" },
        { typeof(byte), "byte" },
    };

    public static string GetDisplayName(this Type type)
    {
        if (s_keywordAliases.TryGetValue(type, out var alias))
            return alias;

        if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            var rank = type.GetArrayRank();
            var rankStr = rank == 1 ? "" : $"[{new string(',', rank - 1)}]";
            return $"{elementType.GetDisplayName()}{rankStr}[]";
        }

        if (type.IsByRef)
        {
            return $"ref {type.GetElementType()!.GetDisplayName()}";
        }

        if (type.IsPointer)
        {
            return $"{type.GetElementType()!.GetDisplayName()}*";
        }

        if (type.IsGenericTypeDefinition)
        {
            var name = type.Name;
            var tickIndex = name.IndexOf('`');
            if (tickIndex >= 0)
                name = name[..tickIndex];
            var paramCount = type.GetGenericArguments().Length;
            var genericParams = string.Join(", ", Enumerable.Range(0, paramCount).Select(_ => "T"));
            if (type.IsNested)
            {
                return $"{type.DeclaringType!.GetDisplayName()}+{name}<{genericParams}>";
            }
            var ns = type.Namespace;
            return string.IsNullOrEmpty(ns) ? $"{name}<{genericParams}>" : $"{ns}.{name}<{genericParams}>";
        }

        if (type.IsGenericType)
        {
            var name = type.Name;
            var tickIndex = name.IndexOf('`');
            if (tickIndex >= 0)
                name = name[..tickIndex];

            var typeArgs = type.GetGenericArguments();
            var displayName = $"{name}<{string.Join(", ", typeArgs.Select(a => a.GetDisplayName()))}>";

            if (type.IsNested)
            {
                return $"{type.DeclaringType!.GetDisplayName()}+{displayName}";
            }

            var ns2 = type.Namespace;
            return string.IsNullOrEmpty(ns2) ? displayName : $"{ns2}.{displayName}";
        }

        if (type.IsNested)
        {
            return $"{type.DeclaringType!.GetDisplayName()}+{type.Name}";
        }

        var ns3 = type.Namespace;
        return string.IsNullOrEmpty(ns3) ? type.Name : $"{ns3}.{type.Name}";
    }
}

public static class SignatureGenerator
{
    public static string GenerateTypeSignatures(IEnumerable<Type> types)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var type in types)
        {
            GenerateTypeSignature(sb, type);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void GenerateTypeSignature(System.Text.StringBuilder sb, Type type)
    {
        sb.AppendLine($"// {type.GetDisplayName()}");
        sb.AppendLine($"class {GetSimpleTypeName(type)}");

        // Constructors
        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(c => c.GetParameters().Length)
            .ToList();
        if (ctors.Count > 0)
        {
            sb.AppendLine("  Constructors:");
            foreach (var ctor in ctors)
            {
                var params2 = FormatParameters(ctor.GetParameters());
                sb.AppendLine($"    .ctor({params2})");
            }
        }

        // Static Fields / Constants
        var staticFields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(f => f.Name)
            .ToList();
        if (staticFields.Count > 0)
        {
            sb.AppendLine("  Static Fields:");
            foreach (var field in staticFields)
            {
                var constValue = field.IsLiteral ? $" = {FormatLiteralValue(field.GetValue(null))}" : "";
                var modifiers = field.IsLiteral ? "const" : (field.IsInitOnly ? "static readonly" : "static");
                sb.AppendLine($"    {modifiers} {field.FieldType.GetDisplayName()} {field.Name}{constValue}");
            }
        }

        // Static Properties
        var staticProps = type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .OrderBy(p => p.Name)
            .ToList();
        if (staticProps.Count > 0)
        {
            sb.AppendLine("  Static Properties:");
            foreach (var prop in staticProps)
            {
                var accessors = FormatPropertyAccessors(prop);
                sb.AppendLine($"    {prop.PropertyType.GetDisplayName()} {prop.Name} {{ {accessors} }}");
            }
        }

        // Instance Properties (including indexers)
        var instanceProps = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(p => p.Name)
            .ToList();
        if (instanceProps.Count > 0)
        {
            sb.AppendLine("  Instance Properties:");
            foreach (var prop in instanceProps)
            {
                if (prop.GetIndexParameters().Length > 0)
                {
                    var indexParams = FormatParameters(prop.GetIndexParameters());
                    var accessors = FormatPropertyAccessors(prop);
                    sb.AppendLine($"    {prop.PropertyType.GetDisplayName()} this[{indexParams}] {{ {accessors} }}");
                }
                else
                {
                    var accessors = FormatPropertyAccessors(prop);
                    sb.AppendLine($"    {prop.PropertyType.GetDisplayName()} {prop.Name} {{ {accessors} }}");
                }
            }
        }

        // Static Methods
        var staticMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .OrderBy(m => m.Name)
            .ThenBy(m => m.GetParameters().Length)
            .ToList();
        if (staticMethods.Count > 0)
        {
            sb.AppendLine("  Static Methods:");
            foreach (var method in staticMethods)
            {
                sb.AppendLine($"    {method.ReturnType.GetDisplayName()} {method.Name}({FormatParameters(method.GetParameters())})");
            }
        }

        // Instance Methods
        var instanceMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .OrderBy(m => m.Name)
            .ThenBy(m => m.GetParameters().Length)
            .ToList();
        if (instanceMethods.Count > 0)
        {
            sb.AppendLine("  Instance Methods:");
            foreach (var method in instanceMethods)
            {
                sb.AppendLine($"    {method.ReturnType.GetDisplayName()} {method.Name}({FormatParameters(method.GetParameters())})");
            }
        }

        // Implemented Interfaces
        var interfaces = type.GetInterfaces()
            .Select(i => i.GetDisplayName())
            .OrderBy(n => n)
            .ToList();
        if (interfaces.Count > 0)
        {
            sb.AppendLine("  Implements:");
            foreach (var iface in interfaces)
            {
                sb.AppendLine($"    {iface}");
            }
        }
    }

    private static string GetSimpleTypeName(Type type)
    {
        if (type.IsGenericType)
        {
            var name = type.Name;
            var tickIndex = name.IndexOf('`');
            if (tickIndex >= 0)
                name = name[..tickIndex];
            var typeArgs = type.GetGenericArguments();
            return $"{name}<{string.Join(", ", typeArgs.Select(a => a.GetDisplayName()))}>";
        }
        if (type.IsGenericTypeDefinition)
        {
            var name = type.Name;
            var tickIndex = name.IndexOf('`');
            if (tickIndex >= 0)
                name = name[..tickIndex];
            var paramCount = type.GetGenericArguments().Length;
            var genericParams = string.Join(", ", Enumerable.Range(0, paramCount).Select(_ => "T"));
            return $"{name}<{genericParams}>";
        }
        return type.Name;
    }

    private static string FormatParameters(ParameterInfo[] parameters)
    {
        return string.Join(", ", parameters.Select(p =>
        {
            var refKind = p.ParameterType.IsByRef
                ? (p.IsOut ? "out " : "ref ")
                : "";
            var paramType = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
            var hasParams = p.GetCustomAttribute<ParamArrayAttribute>() != null;
            var prefix = hasParams ? "params " : "";
            var optional = p.IsOptional ? "?" : "";
            return $"{prefix}{refKind}{paramType.GetDisplayName()} {p.Name}{optional}";
        }));
    }

    private static string FormatPropertyAccessors(PropertyInfo prop)
    {
        var parts = new List<string>();
        if (prop.GetMethod != null && prop.GetMethod.IsPublic)
            parts.Add("get");
        if (prop.SetMethod != null && prop.SetMethod.IsPublic)
            parts.Add("set");
        return string.Join("; ", parts);
    }

    private static string FormatLiteralValue(object? value)
    {
        if (value == null) return "null";
        if (value is string s) return $"\"{s}\"";
        if (value is bool b) return b ? "true" : "false";
        if (value is Enum e) return $"{e.GetType().Name}.{e}";
        return value.ToString() ?? "null";
    }
}

public static class TestDataGenerator
{
    public static string GenerateTestData()
    {
        var testData = new List<object>
        {
            GenerateArrayListTestData(),
            GenerateHashtableTestData(),
            GenerateListStringTestData(),
            GenerateDictionaryStringIntTestData(),
            GenerateStackIntTestData(),
            GenerateQueueStringTestData(),
            GenerateHashSetStringTestData(),
            GenerateSortedSetIntTestData(),
            GenerateSortedDictionaryStringIntTestData(),
            GenerateSortedListStringIntTestData(),
            GenerateLinkedListStringTestData(),
            GenerateBitArrayTestData(),
            GeneratePriorityQueueTestData(),
            GenerateKeyValuePairTestData(),
            GenerateStackNonGenericTestData(),
            GenerateQueueNonGenericTestData(),
            GenerateSortedListNonGenericTestData(),
        };

        var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new ObjectJsonConverter() } };
        return JsonSerializer.Serialize(testData, options);
    }

    private static Dictionary<string, object> MakeEntry(string typeName, List<Dictionary<string, object>> steps)
    {
        return new Dictionary<string, object>
        {
            ["type"] = typeName,
            ["steps"] = steps
        };
    }

    private static Dictionary<string, object> Step(string operation, string description, object? input, object? output)
    {
        var dict = new Dictionary<string, object>
        {
            ["operation"] = operation,
            ["description"] = description
        };
        if (input != null) dict["input"] = input;
        if (output != null) dict["output"] = output;
        return dict;
    }

    private static object WrapForJson(object? obj)
    {
        if (obj == null) return "null";
        if (obj is IDictionary dict)
        {
            var result = new Dictionary<string, object>();
            foreach (DictionaryEntry entry in dict)
            {
                result[entry.Key?.ToString() ?? ""] = WrapForJson(entry.Value);
            }
            return result;
        }
        if (obj is ICollection col)
        {
            var list = new List<object>();
            foreach (var item in col)
            {
                list.Add(WrapForJson(item));
            }
            return list;
        }
        if (obj is IEnumerable enumerable and not string)
        {
            var list = new List<object>();
            foreach (var item in enumerable)
            {
                list.Add(WrapForJson(item));
            }
            return list;
        }
        return obj;
    }

    private static Dictionary<string, object> GenerateArrayListTestData()
    {
        var steps = new List<Dictionary<string, object>>();
        var list = new ArrayList();

        steps.Add(Step("new", "Create new ArrayList", null, "[]"));
        steps.Add(Step("Add", "Add 'cherry'", "cherry", 0));
        list.Add("cherry");

        steps.Add(Step("Add", "Add 'apple'", "apple", 1));
        list.Add("apple");

        steps.Add(Step("Add", "Add 'banana'", "banana", 2));
        list.Add("banana");

        steps.Add(Step("Count", "Get count", null, list.Count));
        steps.Add(Step("Capacity", "Get capacity", null, list.Capacity));
        steps.Add(Step("this[]", "Get index 0", 0, "cherry"));
        steps.Add(Step("Contains", "Contains 'apple'", "apple", true));
        steps.Add(Step("Contains", "Contains 'missing'", "missing", false));
        steps.Add(Step("IndexOf", "IndexOf 'banana'", "banana", 2));
        steps.Add(Step("Insert", "Insert 'date' at 1", new { index = 1, item = "date" }, "void"));
        list.Insert(1, "date");

        steps.Add(Step("Count", "Get count after insert", null, list.Count));
        steps.Add(Step("Remove", "Remove 'cherry'", "cherry", true));
        list.Remove("cherry");

        steps.Add(Step("RemoveAt", "RemoveAt 0", 0, "void"));
        list.RemoveAt(0);

        steps.Add(Step("ToArray", "ToArray", null, WrapForJson(list.ToArray())));
        steps.Add(Step("Clone", "Clone", null, "ArrayList"));
        var clone = (ArrayList)list.Clone();

        steps.Add(Step("Sort", "Sort", null, "void"));
        list.Sort();

        steps.Add(Step("Reverse", "Reverse", null, "void"));
        list.Reverse();

        var arr = (object?[])list.ToArray();
        steps.Add(Step("FinalState", "Final state", null, WrapForJson(arr)));

        return MakeEntry("ArrayList", steps);
    }

    private static Dictionary<string, object> GenerateHashtableTestData()
    {
        var steps = new List<Dictionary<string, object>>();
        var ht = new Hashtable();

        steps.Add(Step("new", "Create new Hashtable", null, "{}"));
        steps.Add(Step("Add", "Add key='name' value='Alice'", new { key = "name", value = "Alice" }, "void"));
        ht.Add("name", "Alice");

        steps.Add(Step("Add", "Add key='age' value=30", new { key = "age", value = 30 }, "void"));
        ht.Add("age", 30);

        steps.Add(Step("Count", "Get count", null, ht.Count));
        steps.Add(Step("this[]", "Get key 'name'", "name", "Alice"));
        steps.Add(Step("this[]", "Set key 'age' to 31", new { key = "age", value = 31 }, "void"));
        ht["age"] = 31;

        steps.Add(Step("ContainsKey", "ContainsKey 'name'", "name", true));
        steps.Add(Step("ContainsKey", "ContainsKey 'missing'", "missing", false));
        steps.Add(Step("ContainsValue", "ContainsValue 'Alice'", "Alice", true));
        steps.Add(Step("Keys", "Get Keys", null, WrapForJson(ht.Keys)));
        steps.Add(Step("Values", "Get Values", null, WrapForJson(ht.Values)));
        steps.Add(Step("Remove", "Remove 'age'", "age", "void"));
        ht.Remove("age");

        steps.Add(Step("Count", "Count after remove", null, ht.Count));

        return MakeEntry("Hashtable", steps);
    }

    private static Dictionary<string, object> GenerateListStringTestData()
    {
        var steps = new List<Dictionary<string, object>>();
        var list = new List<string>();

        steps.Add(Step("new", "Create new List<string>", null, "[]"));
        steps.Add(Step("Add", "Add 'alpha'", "alpha", 0));
        list.Add("alpha");

        steps.Add(Step("Add", "Add 'beta'", "beta", 1));
        list.Add("beta");

        steps.Add(Step("Add", "Add 'gamma'", "gamma", 2));
        list.Add("gamma");

        steps.Add(Step("Count", "Get count", null, list.Count));
        steps.Add(Step("this[]", "Get index 1", 1, "beta"));
        steps.Add(Step("Contains", "Contains 'beta'", "beta", true));
        steps.Add(Step("IndexOf", "IndexOf 'gamma'", "gamma", 2));
        steps.Add(Step("Insert", "Insert 'delta' at 1", new { index = 1, item = "delta" }, "void"));
        list.Insert(1, "delta");

        steps.Add(Step("Remove", "Remove 'beta'", "beta", true));
        list.Remove("beta");

        steps.Add(Step("RemoveAt", "RemoveAt 0", 0, "void"));
        list.RemoveAt(0);

        steps.Add(Step("Sort", "Sort", null, "void"));
        list.Sort();

        steps.Add(Step("Reverse", "Reverse", null, "void"));
        list.Reverse();

        steps.Add(Step("ToArray", "ToArray", null, WrapForJson(list.ToArray())));
        steps.Add(Step("FinalState", "Final state", null, WrapForJson(list)));

        return MakeEntry("List<string>", steps);
    }

    private static Dictionary<string, object> GenerateDictionaryStringIntTestData()
    {
        var steps = new List<Dictionary<string, object>>();
        var dict = new Dictionary<string, int>();

        steps.Add(Step("new", "Create new Dictionary<string,int>", null, "{}"));
        steps.Add(Step("Add", "Add key='a' value=1", new { key = "a", value = 1 }, "void"));
        dict.Add("a", 1);

        steps.Add(Step("Add", "Add key='b' value=2", new { key = "b", value = 2 }, "void"));
        dict.Add("b", 2);

        steps.Add(Step("Count", "Get count", null, dict.Count));
        steps.Add(Step("this[]", "Get key 'a'", "a", 1));
        steps.Add(Step("this[]", "Set key 'c' to 3", new { key = "c", value = 3 }, "void"));
        dict["c"] = 3;

        steps.Add(Step("ContainsKey", "ContainsKey 'b'", "b", true));
        steps.Add(Step("ContainsKey", "ContainsKey 'z'", "z", false));
        steps.Add(Step("ContainsValue", "ContainsValue 2", 2, true));
        steps.Add(Step("TryGetValue", "TryGetValue 'a'", "a", true));
        steps.Add(Step("Keys", "Get Keys", null, WrapForJson(dict.Keys)));
        steps.Add(Step("Values", "Get Values", null, WrapForJson(dict.Values)));
        steps.Add(Step("Remove", "Remove 'b'", "b", true));
        dict.Remove("b");

        steps.Add(Step("Count", "Count after remove", null, dict.Count));
        steps.Add(Step("FinalState", "Final state", null, WrapForJson(dict)));

        return MakeEntry("Dictionary<string,int>", steps);
    }

    private static Dictionary<string, object> GenerateStackIntTestData()
    {
        var steps = new List<Dictionary<string, object>>();
        var stack = new Stack<int>();

        steps.Add(Step("new", "Create new Stack<int>", null, "[]"));
        steps.Add(Step("Push", "Push 10", 10, "void"));
        stack.Push(10);

        steps.Add(Step("Push", "Push 20", 20, "void"));
        stack.Push(20);

        steps.Add(Step("Push", "Push 30", 30, "void"));
        stack.Push(30);

        steps.Add(Step("Count", "Get count", null, stack.Count));
        steps.Add(Step("Peek", "Peek", null, 30));
        steps.Add(Step("Pop", "Pop", null, 30));
        stack.Pop();

        steps.Add(Step("Contains", "Contains 10", 10, true));
        steps.Add(Step("Contains", "Contains 99", 99, false));
        steps.Add(Step("ToArray", "ToArray", null, WrapForJson(stack.ToArray())));
        steps.Add(Step("FinalState", "Final state", null, WrapForJson(stack)));

        return MakeEntry("Stack<int>", steps);
    }

    private static Dictionary<string, object> GenerateQueueStringTestData()
    {
        var steps = new List<Dictionary<string, object>>();
        var queue = new Queue<string>();

        steps.Add(Step("new", "Create new Queue<string>", null, "[]"));
        steps.Add(Step("Enqueue", "Enqueue 'first'", "first", "void"));
        queue.Enqueue("first");

        steps.Add(Step("Enqueue", "Enqueue 'second'", "second", "void"));
        queue.Enqueue("second");

        steps.Add(Step("Enqueue", "Enqueue 'third'", "third", "void"));
        queue.Enqueue("third");

        steps.Add(Step("Count", "Get count", null, queue.Count));
        steps.Add(Step("Peek", "Peek", null, "first"));
        steps.Add(Step("Dequeue", "Dequeue", null, "first"));
        queue.Dequeue();

        steps.Add(Step("Contains", "Contains 'second'", "second", true));
        steps.Add(Step("ToArray", "ToArray", null, WrapForJson(queue.ToArray())));
        steps.Add(Step("FinalState", "Final state", null, WrapForJson(queue)));

        return MakeEntry("Queue<string>", steps);
    }

    private static Dictionary<string, object> GenerateHashSetStringTestData()
    {
        var steps = new List<Dictionary<string, object>>();
        var set = new HashSet<string>();

        steps.Add(Step("new", "Create new HashSet<string>", null, "[]"));
        steps.Add(Step("Add", "Add 'apple'", "apple", true));
        set.Add("apple");

        steps.Add(Step("Add", "Add 'banana'", "banana", true));
        set.Add("banana");

        steps.Add(Step("Add", "Add 'apple' (duplicate)", "apple", false));
        set.Add("apple");

        steps.Add(Step("Count", "Get count", null, set.Count));
        steps.Add(Step("Contains", "Contains 'apple'", "apple", true));
        steps.Add(Step("Contains", "Contains 'cherry'", "cherry", false));
        steps.Add(Step("Remove", "Remove 'banana'", "banana", true));
        set.Remove("banana");

        steps.Add(Step("Count", "Count after remove", null, set.Count));
        steps.Add(Step("FinalState", "Final state", null, WrapForJson(set)));

        return MakeEntry("HashSet<string>", steps);
    }

    private static Dictionary<string, object> GenerateSortedSetIntTestData()
    {
        var steps = new List<Dictionary<string, object>>();
        var set = new SortedSet<int>();

        steps.Add(Step("new", "Create new SortedSet<int>", null, "[]"));
        steps.Add(Step("Add", "Add 30", 30, true));
        set.Add(30);

        steps.Add(Step("Add", "Add 10", 10, true));
        set.Add(10);

        steps.Add(Step("Add", "Add 20", 20, true));
        set.Add(20);

        steps.Add(Step("Count", "Get count", null, set.Count));
        steps.Add(Step("Min", "Get Min", null, 10));
        steps.Add(Step("Max", "Get Max", null, 30));
        steps.Add(Step("Contains", "Contains 20", 20, true));
        steps.Add(Step("Remove", "Remove 20", 20, true));
        set.Remove(20);

        steps.Add(Step("FinalState", "Final state", null, WrapForJson(set)));

        return MakeEntry("SortedSet<int>", steps);
    }

    private static Dictionary<string, object> GenerateSortedDictionaryStringIntTestData()
    {
        var steps = new List<Dictionary<string, object>>();
        var dict = new SortedDictionary<string, int>();

        steps.Add(Step("new", "Create new SortedDictionary<string,int>", null, "{}"));
        steps.Add(Step("Add", "Add key='cherry' value=3", new { key = "cherry", value = 3 }, "void"));
        dict.Add("cherry", 3);

        steps.Add(Step("Add", "Add key='apple' value=1", new { key = "apple", value = 1 }, "void"));
        dict.Add("apple", 1);

        steps.Add(Step("Add", "Add key='banana' value=2", new { key = "banana", value = 2 }, "void"));
        dict.Add("banana", 2);

        steps.Add(Step("Count", "Get count", null, dict.Count));
        steps.Add(Step("this[]", "Get key 'apple'", "apple", 1));
        steps.Add(Step("ContainsKey", "ContainsKey 'banana'", "banana", true));
        steps.Add(Step("Keys", "Get Keys (sorted)", null, WrapForJson(dict.Keys)));
        steps.Add(Step("Values", "Get Values (sorted)", null, WrapForJson(dict.Values)));
        steps.Add(Step("Remove", "Remove 'cherry'", "cherry", true));
        dict.Remove("cherry");

        steps.Add(Step("FinalState", "Final state", null, WrapForJson(dict)));

        return MakeEntry("SortedDictionary<string,int>", steps);
    }

    private static Dictionary<string, object> GenerateSortedListStringIntTestData()
    {
        var steps = new List<Dictionary<string, object>>();
        var list = new SortedList<string, int>();

        steps.Add(Step("new", "Create new SortedList<string,int>", null, "{}"));
        steps.Add(Step("Add", "Add key='delta' value=4", new { key = "delta", value = 4 }, "void"));
        list.Add("delta", 4);

        steps.Add(Step("Add", "Add key='alpha' value=1", new { key = "alpha", value = 1 }, "void"));
        list.Add("alpha", 1);

        steps.Add(Step("Add", "Add key='charlie' value=3", new { key = "charlie", value = 3 }, "void"));
        list.Add("charlie", 3);

        steps.Add(Step("Count", "Get count", null, list.Count));
        steps.Add(Step("this[]", "Get key 'alpha'", "alpha", 1));
        steps.Add(Step("Keys", "Get Keys", null, WrapForJson(list.Keys)));
        steps.Add(Step("Values", "Get Values", null, WrapForJson(list.Values)));
        steps.Add(Step("IndexOfKey", "IndexOfKey 'charlie'", "charlie", 1));
        steps.Add(Step("Remove", "Remove 'delta'", "delta", true));
        list.Remove("delta");

        steps.Add(Step("FinalState", "Final state", null, WrapForJson(list)));

        return MakeEntry("SortedList<string,int>", steps);
    }

    private static Dictionary<string, object> GenerateLinkedListStringTestData()
    {
        var steps = new List<Dictionary<string, object>>();
        var list = new LinkedList<string>();

        steps.Add(Step("new", "Create new LinkedList<string>", null, "[]"));
        var node1 = list.AddLast("middle");
        steps.Add(Step("AddLast", "AddLast 'middle'", "middle", "LinkedListNode"));
        var node2 = list.AddFirst("first");
        steps.Add(Step("AddFirst", "AddFirst 'first'", "first", "LinkedListNode"));
        list.AddLast("last");
        steps.Add(Step("AddLast", "AddLast 'last'", "last", "LinkedListNode"));

        steps.Add(Step("Count", "Get count", null, list.Count));
        steps.Add(Step("First", "Get First.Value", null, "first"));
        steps.Add(Step("Last", "Get Last.Value", null, "last"));
        steps.Add(Step("Contains", "Contains 'middle'", "middle", true));
        steps.Add(Step("Find", "Find 'middle'", "middle", "LinkedListNode"));
        steps.Add(Step("FindLast", "FindLast 'middle'", "middle", "LinkedListNode"));
        steps.Add(Step("Remove", "Remove 'first'", "first", true));
        list.Remove("first");

        steps.Add(Step("Count", "Count after remove", null, list.Count));
        steps.Add(Step("FinalState", "Final state", null, WrapForJson(list)));

        return MakeEntry("LinkedList<string>", steps);
    }

    private static Dictionary<string, object> GenerateBitArrayTestData()
    {
        var steps = new List<Dictionary<string, object>>();
        var bits = new BitArray(5);

        steps.Add(Step("new", "Create new BitArray(5)", 5, "[false,false,false,false,false]"));
        bits[0] = true;
        bits[2] = true;
        bits[4] = true;
        steps.Add(Step("this[]", "Set bits 0,2,4 to true", new[] { 0, 2, 4 }, "[true,false,true,false,true]"));
        steps.Add(Step("this[]", "Get index 0", 0, true));
        steps.Add(Step("this[]", "Get index 1", 1, false));
        steps.Add(Step("Count", "Get count", null, bits.Count));
        steps.Add(Step("Length", "Get length", null, bits.Length));

        var bits2 = new BitArray(new[] { true, true, false, false, true });
        steps.Add(Step("And", "And with [true,true,false,false,true]", "[true,true,false,false,true]", "BitArray"));
        bits.And(bits2);

        steps.Add(Step("Not", "Not", null, "BitArray"));
        bits.Not();

        steps.Add(Step("FinalState", "Final state (as booleans)", null, WrapForJson(bits)));

        return MakeEntry("BitArray", steps);
    }

    private static Dictionary<string, object> GeneratePriorityQueueTestData()
    {
        var steps = new List<Dictionary<string, object>>();
        var pq = new PriorityQueue<string, int>();

        steps.Add(Step("new", "Create new PriorityQueue<string,int>", null, "empty"));
        steps.Add(Step("Enqueue", "Enqueue 'low' with priority 3", new { element = "low", priority = 3 }, "void"));
        pq.Enqueue("low", 3);

        steps.Add(Step("Enqueue", "Enqueue 'high' with priority 1", new { element = "high", priority = 1 }, "void"));
        pq.Enqueue("high", 1);

        steps.Add(Step("Enqueue", "Enqueue 'medium' with priority 2", new { element = "medium", priority = 2 }, "void"));
        pq.Enqueue("medium", 2);

        steps.Add(Step("Count", "Get count", null, pq.Count));
        steps.Add(Step("Peek", "Peek", null, "high"));
        steps.Add(Step("Dequeue", "Dequeue", null, "high"));
        pq.Dequeue();

        steps.Add(Step("Peek", "Peek after dequeue", null, "medium"));
        steps.Add(Step("Count", "Count after dequeue", null, pq.Count));
        steps.Add(Step("FinalState", "Remaining count", null, pq.Count));

        return MakeEntry("PriorityQueue<string,int>", steps);
    }

    private static Dictionary<string, object> GenerateKeyValuePairTestData()
    {
        var steps = new List<Dictionary<string, object>>();
        var kvp = new KeyValuePair<string, int>("test", 42);

        steps.Add(Step("new", "Create new KeyValuePair<string,int>", new { key = "test", value = 42 }, "KeyValuePair"));
        steps.Add(Step("Key", "Get Key", null, "test"));
        steps.Add(Step("Value", "Get Value", null, 42));
        steps.Add(Step("ToString", "ToString", null, kvp.ToString()));

        return MakeEntry("KeyValuePair<string,int>", steps);
    }

    private static Dictionary<string, object> GenerateStackNonGenericTestData()
    {
        var steps = new List<Dictionary<string, object>>();
        var stack = new Stack();

        steps.Add(Step("new", "Create new Stack", null, "[]"));
        steps.Add(Step("Push", "Push 'a'", "a", "void"));
        stack.Push("a");

        steps.Add(Step("Push", "Push 42", 42, "void"));
        stack.Push(42);

        steps.Add(Step("Push", "Push null", null, "void"));
        stack.Push(null);

        steps.Add(Step("Count", "Get count", null, stack.Count));
        steps.Add(Step("Peek", "Peek", null, "null"));
        steps.Add(Step("Pop", "Pop", null, "null"));
        stack.Pop();

        steps.Add(Step("Contains", "Contains 'a'", "a", true));
        steps.Add(Step("ToArray", "ToArray", null, WrapForJson(stack.ToArray())));
        steps.Add(Step("FinalState", "Final state", null, WrapForJson(stack)));

        return MakeEntry("Stack", steps);
    }

    private static Dictionary<string, object> GenerateQueueNonGenericTestData()
    {
        var steps = new List<Dictionary<string, object>>();
        var queue = new Queue();

        steps.Add(Step("new", "Create new Queue", null, "[]"));
        steps.Add(Step("Enqueue", "Enqueue 'x'", "x", "void"));
        queue.Enqueue("x");

        steps.Add(Step("Enqueue", "Enqueue 99", 99, "void"));
        queue.Enqueue(99);

        steps.Add(Step("Count", "Get count", null, queue.Count));
        steps.Add(Step("Peek", "Peek", null, "x"));
        steps.Add(Step("Dequeue", "Dequeue", null, "x"));
        queue.Dequeue();

        steps.Add(Step("Contains", "Contains 99", 99, true));
        steps.Add(Step("ToArray", "ToArray", null, WrapForJson(queue.ToArray())));
        steps.Add(Step("FinalState", "Final state", null, WrapForJson(queue)));

        return MakeEntry("Queue", steps);
    }

    private static Dictionary<string, object> GenerateSortedListNonGenericTestData()
    {
        var steps = new List<Dictionary<string, object>>();
        var list = new SortedList();

        steps.Add(Step("new", "Create new SortedList", null, "{}"));
        steps.Add(Step("Add", "Add key='b' value=2", new { key = "b", value = 2 }, "void"));
        list.Add("b", 2);

        steps.Add(Step("Add", "Add key='a' value=1", new { key = "a", value = 1 }, "void"));
        list.Add("a", 1);

        steps.Add(Step("Add", "Add key='c' value=3", new { key = "c", value = 3 }, "void"));
        list.Add("c", 3);

        steps.Add(Step("Count", "Get count", null, list.Count));
        steps.Add(Step("this[]", "Get key 'a'", "a", 1));
        steps.Add(Step("GetByIndex", "GetByIndex 0", 0, 1));
        steps.Add(Step("GetKey", "GetKey 0", 0, "a"));
        steps.Add(Step("ContainsKey", "ContainsKey 'b'", "b", true));
        steps.Add(Step("Keys", "Get Keys", null, WrapForJson(list.Keys)));
        steps.Add(Step("Values", "Get Values", null, WrapForJson(list.Values)));
        steps.Add(Step("Remove", "Remove 'c'", "c", "void"));
        list.Remove("c");

        steps.Add(Step("Count", "Count after remove", null, list.Count));
        steps.Add(Step("FinalState", "Final state", null, WrapForJson(list)));

        return MakeEntry("SortedList", steps);
    }
}

public class ObjectJsonConverter : JsonConverter<object>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        if (value == null || (value is string s && s == "null"))
        {
            writer.WriteNullValue();
            return;
        }

        if (value is string str)
        {
            writer.WriteStringValue(str);
            return;
        }

        if (value is bool b)
        {
            writer.WriteBooleanValue(b);
            return;
        }

        if (value is int i)
        {
            writer.WriteNumberValue(i);
            return;
        }

        if (value is long l)
        {
            writer.WriteNumberValue(l);
            return;
        }

        if (value is double d)
        {
            writer.WriteNumberValue(d);
            return;
        }

        if (value is decimal dec)
        {
            writer.WriteNumberValue(dec);
            return;
        }

        if (value is float f)
        {
            writer.WriteNumberValue(f);
            return;
        }

        if (value is IDictionary idict)
        {
            writer.WriteStartObject();
            foreach (DictionaryEntry entry in idict)
            {
                writer.WritePropertyName(entry.Key?.ToString() ?? "");
                Write(writer, entry.Value, options);
            }
            writer.WriteEndObject();
            return;
        }

        if (value is IList ilist)
        {
            writer.WriteStartArray();
            foreach (var item in ilist)
            {
                Write(writer, item, options);
            }
            writer.WriteEndArray();
            return;
        }

        if (value is IEnumerable ienum and not string)
        {
            writer.WriteStartArray();
            foreach (var item in ienum)
            {
                Write(writer, item, options);
            }
            writer.WriteEndArray();
            return;
        }

        // Fallback: serialize as string
        writer.WriteStringValue(value.ToString());
    }
}

public static class ConstantsGenerator
{
    public static string GenerateConstants(IEnumerable<Type> types)
    {
        var allConstants = new List<Dictionary<string, object>>();

        foreach (var type in types)
        {
            var constants = new List<Dictionary<string, object>>();

            // Static literal fields (const)
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(f => f.IsLiteral)
                .OrderBy(f => f.Name))
            {
                var value = field.GetValue(null);
                constants.Add(new Dictionary<string, object>
                {
                    ["name"] = field.Name,
                    ["kind"] = "const",
                    ["type"] = field.FieldType.GetDisplayName(),
                    ["value"] = value ?? "null"
                });
            }

            // Static readonly fields
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(f => !f.IsLiteral && f.IsInitOnly)
                .OrderBy(f => f.Name))
            {
                try
                {
                    var value = field.GetValue(null);
                    if (value != null)
                    {
                        if (value is int || value is long || value is float || value is double ||
                            value is decimal || value is bool || value is string || value is Enum)
                        {
                            constants.Add(new Dictionary<string, object>
                            {
                                ["name"] = field.Name,
                                ["kind"] = "static readonly",
                                ["type"] = field.FieldType.GetDisplayName(),
                                ["value"] = value
                            });
                        }
                        else
                        {
                            // Reference-type static readonly fields: record the runtime type
                            constants.Add(new Dictionary<string, object>
                            {
                                ["name"] = field.Name,
                                ["kind"] = "static readonly",
                                ["type"] = field.FieldType.GetDisplayName(),
                                ["value"] = $"{value.GetType().GetDisplayName()} instance"
                            });
                        }
                    }
                }
                catch
                {
                    // Skip fields that throw on access
                }
            }

            // Static properties that act as singletons/factories
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(p => p.GetMethod != null && p.GetMethod.IsPublic && p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.Name))
            {
                try
                {
                    var value = prop.GetValue(null);
                    if (value != null)
                    {
                        if (value is int || value is long || value is float || value is double ||
                            value is decimal || value is bool || value is string || value is Enum)
                        {
                            constants.Add(new Dictionary<string, object>
                            {
                                ["name"] = prop.Name,
                                ["kind"] = "static property",
                                ["type"] = prop.PropertyType.GetDisplayName(),
                                ["value"] = value
                            });
                        }
                        else
                        {
                            constants.Add(new Dictionary<string, object>
                            {
                                ["name"] = prop.Name,
                                ["kind"] = "static property",
                                ["type"] = prop.PropertyType.GetDisplayName(),
                                ["value"] = $"{value.GetType().GetDisplayName()} instance"
                            });
                        }
                    }
                }
                catch
                {
                    // Skip properties that throw on access
                }
            }

            if (constants.Count > 0)
            {
                allConstants.Add(new Dictionary<string, object>
                {
                    ["type"] = type.GetDisplayName(),
                    ["constants"] = constants
                });
            }
        }

        var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new ObjectJsonConverter() } };
        return JsonSerializer.Serialize(allConstants, options);
    }
}

public class Program
{
    private static readonly Type[] NonGenericCollectionTypes = new[]
    {
        typeof(IEnumerable),
        typeof(IEnumerator),
        typeof(ICollection),
        typeof(IList),
        typeof(IDictionary),
        typeof(IDictionaryEnumerator),
        typeof(IComparer),
        typeof(IEqualityComparer),
        typeof(IStructuralComparable),
        typeof(IStructuralEquatable),
        typeof(ArrayList),
        typeof(BitArray),
        typeof(Hashtable),
        typeof(Queue),
        typeof(Stack),
        typeof(SortedList),
        typeof(DictionaryEntry),
        typeof(Comparer),
        typeof(CaseInsensitiveComparer),
        typeof(CollectionBase),
        typeof(DictionaryBase),
        typeof(ReadOnlyCollectionBase),
        typeof(StructuralComparisons),
    };

    private static readonly Type[] GenericCollectionTypes = new[]
    {
        typeof(List<string>),
        typeof(Dictionary<string, int>),
        typeof(HashSet<string>),
        typeof(SortedSet<int>),
        typeof(SortedDictionary<string, int>),
        typeof(Queue<string>),
        typeof(Stack<int>),
        typeof(LinkedList<string>),
        typeof(KeyValuePair<string, int>),
        typeof(Comparer<string>),
        typeof(EqualityComparer<string>),
        typeof(PriorityQueue<string, int>),
        typeof(ICollection<string>),
        typeof(IList<string>),
        typeof(IDictionary<string, int>),
        typeof(IEnumerable<string>),
        typeof(IEnumerator<string>),
        typeof(IComparer<string>),
        typeof(IEqualityComparer<string>),
        typeof(IReadOnlyCollection<string>),
        typeof(IReadOnlyList<string>),
        typeof(IReadOnlyDictionary<string, int>),
        typeof(IReadOnlySet<string>),
        typeof(ISet<string>),
    };

    public static void Main(string[] args)
    {
        var outputDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "output");
        Directory.CreateDirectory(outputDir);
        outputDir = Path.GetFullPath(outputDir);

        Console.WriteLine($"Output directory: {outputDir}");

        var allTypes = NonGenericCollectionTypes.Concat(GenericCollectionTypes).ToList();

        // 1. Generate collection-api-signatures.txt
        Console.WriteLine("Generating collection-api-signatures.txt...");
        var signatures = SignatureGenerator.GenerateTypeSignatures(allTypes);
        File.WriteAllText(Path.Combine(outputDir, "collection-api-signatures.txt"), signatures);
        Console.WriteLine("  Done.");

        // 2. Generate collection-test-data.json
        Console.WriteLine("Generating collection-test-data.json...");
        var testData = TestDataGenerator.GenerateTestData();
        File.WriteAllText(Path.Combine(outputDir, "collection-test-data.json"), testData);
        Console.WriteLine("  Done.");

        // 3. Generate collection-constants.json
        Console.WriteLine("Generating collection-constants.json...");
        var constants = ConstantsGenerator.GenerateConstants(allTypes);
        File.WriteAllText(Path.Combine(outputDir, "collection-constants.json"), constants);
        Console.WriteLine("  Done.");

        Console.WriteLine("All files generated successfully!");
        Console.WriteLine();
        Console.WriteLine("Output files:");
        foreach (var file in Directory.GetFiles(outputDir))
        {
            var fi = new FileInfo(file);
            Console.WriteLine($"  {fi.Name} ({fi.Length:N0} bytes)");
        }
    }
}
