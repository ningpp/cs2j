using SampleStructValueSemanticsLib;

// VS-1/VS-2：局部副本 + 值参。
var a = new Point(1, 2);
var b = a;
b.Translate(10, 10);
Console.WriteLine($"{a.X},{a.Y}");

// VS-1a/VS-4b：struct 数组元素读出与写回。
var points = new[] { new Point(0, 0), new Point(5, 5) };
var first = points[0];
first.Translate(3, 3);
points[0] = first;
Console.WriteLine(points[0].Peek());

Geometry.MutateCopy(a);
Geometry.MutateArrayFirst(points);

// VS-5b：接口装箱。
Geometry.Move(a);
Console.WriteLine($"{a.X},{a.Y}");

// VS-5a/VS-5c：装箱与拆箱。
object boxed = Geometry.Box(a);
var back = Geometry.Unbox(boxed);
back.Translate(-100, -100);
Console.WriteLine(((Point)boxed).X);

// VS-3：in 参接收者、readonly 字段、get-only 属性、rvalue。
Geometry.NudgeIn(a);
var holder = new ReadOnlyHolder(a);
holder.Nudge();
holder.Anchor.Translate(7, 7);
Geometry.Origin.Translate(1, 1);
Console.WriteLine(Geometry.Origin.Peek());

// VS-3：is-pattern 只读局部。
if (boxed is Point pattern)
{
    pattern.Translate(1, 1);
    Console.WriteLine(pattern.X);
}

// VS-3：foreach 迭代变量。
foreach (var p in points)
{
    p.Translate(1, 1);
}

// VS-1c：链式赋值。
Point left, right;
left = right = a;
Console.WriteLine($"{left.X},{right.X}");

// 可写局部的可变字段：直接变异，不提升。
var line = new Line(a, new Point(3, 4));
line.Start.Translate(1, 0);
Console.WriteLine(line.Start.X);

var poly = new Polygon(points);
poly.ShiftAll(1, 1);
Console.WriteLine(poly.Vertices[0].X);

// 属性 setter struct 的 with 模式。
var settings = new BuildSettings { Name = "app", Retries = 1 };
var next = settings.WithRetries(4);
Console.WriteLine($"{settings.Retries},{next.Retries}");

// SV010：复用既有 Clone()。
var reusable = new Reusable { V = 11 };
var reused = reusable;
Console.WriteLine(reused.V);

// SV051 残余类型：不插桩，原始语义保留。
var counter = new Counter();
counter.Tick();
Console.WriteLine(counter.Count);
