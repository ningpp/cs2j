using SampleMemberDesugarLib;

// 跨文件使用点：初始化器提升 / 读 / 复合 / 自增 / 方法组 / 接口与 DIM / 显式实现 / 静态。

var line = new OrderLine { Quantity = 3, Price = 2.5m };
var total = line.Total;
line.Quantity += 2;
line.Price++;
Console.WriteLine($"line: q={line.Quantity} p={line.Price} total={line.Total} note={line.Note}");

var box = new Box<string> { Value = "v" };
box.Value = box.Value + "!";
Console.WriteLine($"box: {box.Value}");

var person = new PersonRecord { FirstName = "Grace", LastName = "Hopper" };
IPerson ip = person;
Console.WriteLine($"person: {ip.FirstName} {ip.LastName} full={ip.FullName}");

var ep = new ExplicitPerson();
Console.WriteLine($"explicit: {((IPerson)ep).FirstName} {((IPerson)ep).LastName}");

StaticHolder.Bump();
StaticHolder.Bump();
Console.WriteLine($"counter: {StaticHolder.Counter}");

var delegateHolder = new DelegateHolder();
Console.WriteLine($"delegate: {delegateHolder.Op(41)}");
delegateHolder.Op = x => x * 3;
Console.WriteLine($"delegate reassigned: {delegateHolder.Op(7)}");

HidingBase hb = new HidingDerived();
var hd = (HidingDerived)hb;
Console.WriteLine($"hiding: {hb.Name}/{hd.Name}");

var req = new RequiredDto { Id = 7, Name = "req" };
Console.WriteLine($"required: {req.Id} {req.Name}");

var collision = new CollisionSample { Age = 3, Count = 4 };
Console.WriteLine($"collision: {collision.Sum}");

var getOnly = new GetOnlyWithInitializer();
Console.WriteLine($"getOnly: {getOnly.Created:O} {getOnly.Bits.Count}");

var skipSample = new ExpressionTreeSample { Erp = 42 };
Console.WriteLine($"expression-tree skip: {skipSample.Selector.Compile()()}");
Console.WriteLine($"static-initializer skip: {StaticInitializerSample.Shared.Label}");

var indexerLine = IndexerUseSite.UseIndexers();
Console.WriteLine($"indexers: {indexerLine}");

public static class IndexerUseSite
{
    public static string UseIndexers()
    {
        var grid = new SampleMemberDesugarLib.Grid();
        grid[1, 2] = 9;
        var named = new SampleMemberDesugarLib.NamedIndexer();
        named[3] = "abc";
        var cells = new SampleMemberDesugarLib.RefCells();
        var total = cells.Bump();
        var se = new SampleMemberDesugarLib.SideEffectIndexer();
        var seed = se.Bump();
        return $"{grid[1, 2]}|{named[3]}|{total}|{seed}";
    }
}
