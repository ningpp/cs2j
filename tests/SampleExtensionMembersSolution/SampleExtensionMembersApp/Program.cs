using SampleExtensionMembers;

var box = new Box<int>(21);
Console.WriteLine(box.Describe());
Console.WriteLine(box.Peek);
Console.WriteLine(Box<int>.PairWith<Box<int>, string>("x"));

var a = new Vec(1, 2);
var b = new Vec(3, 4);
var sum = a + b;
var diff = b - a;
var neg = -a;
Console.WriteLine($"({sum.X},{sum.Y}) ({diff.X},{diff.Y}) ({neg.X},{neg.Y})");
Console.WriteLine(Vec.Describe());
Console.WriteLine(Vec.Unit);

var num = new Num(10);
num.Bump(5);
Console.WriteLine(num.Triple);

Console.WriteLine(4.Square());
Console.WriteLine("hi".Shout());
