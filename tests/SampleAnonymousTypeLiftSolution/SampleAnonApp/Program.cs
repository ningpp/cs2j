using SampleAnonLib;

// 与 Lib.Projections.ProjectAsObjects 内部完全同形（Name:String + Length:Int32）的
// 独立创建：跨工程只经 object 签名交互，两个工程各得一份同名 internal 副本。
var local = new { Name = "app", Length = 3 };
Console.WriteLine(local);

// foreach over 投影：提升后 o 静态类型是 object，Console.WriteLine 走 ToString 虚分发。
foreach (var o in Projections.ProjectAsObjects(new[] { "a", "bb" }))
{
    Console.WriteLine(o);
}

// 方法链 + 查询式混合：投影（含重复行，验证 Distinct 的值相等语义）。
var rows = new[] { 1, 2, 2, 3 }.Select(x => new { K = x % 2, V = x }).ToArray();
Console.WriteLine($"distinct={rows.Distinct().Count()}");

var byKey = rows.GroupBy(r => r.K).ToDictionary(g => g.Key, g => g.Count());
Console.WriteLine($"groups={byKey.Count}");

// V=2 重复，直接 ToDictionary 会抛重复键（与原生匿名类型语义一致）；先 Distinct 再建字典。
var byV = rows.Distinct().ToDictionary(r => r.V);
Console.WriteLine($"byV={byV.Count}");

var query = from r in rows
            where r.K == 0
            select new { Z = r.V };

// Join：内外键选择器 + 结果投影又是一个匿名形状。
var joins = rows
    .Join(query, a => a.K, b => b.Z % 2, (a, b) => new { A = a.V, B = b.Z })
    .ToList();
Console.WriteLine($"joins={joins.Count}");

// transparent identifier 豁免（设计 §7.4）：let 不产生创建语法节点，
// select 的投影形状正常提升。
var letRows = (from r in rows
               let doubled = r.V * 2
               select new { Orig = r, Doubled = doubled }).ToList();
Console.WriteLine($"letRows={letRows.Count} doubled0={letRows[0].Doubled}");

// 隐式数组：元素类型推断为提升后的类。
var arr = new[] { new { X = 1 }, new { X = 2 } };
Console.WriteLine($"arrLen={arr.Length}");

// nameof 零改写。
Console.WriteLine(nameof(arr));

// Lib 全部分支各调一次；返回值一律以 object 承接（dynamic 返回值不做动态成员访问）。
object first = Projections.FirstProjection(new[] { 1, 2, 3 });
Console.WriteLine(first);

object generic = Projections.GenericProjection(new[] { "a", "bb" }, s => s.Length);
Console.WriteLine(generic);

object nested = Projections.NestedProjection(new[] { 1, 2, 3 });
Console.WriteLine(nested);

object dynamicShape = Projections.DynamicShape(7);
Console.WriteLine(dynamicShape);
