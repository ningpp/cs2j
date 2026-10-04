using AssignmentLib;

// Null disks: every site must no-op without throwing.
AssignmentSites.Member(null, 1);
AssignmentSites.Property(null, 1);
AssignmentSites.Index(null, 0, 1);
AssignmentSites.Add(null, 1);
AssignmentSites.EventOn(null, () => { });
AssignmentSites.Coalesce(null, "x");
AssignmentSites.Chain(null, 1);
AssignmentSites.ValueForm(null, 1);
AssignmentSites.CoalesceValue(null, "x");

// Non-null disk: assignments land, value forms return the assigned value.
var node = new Node { Next = new Node() };
AssignmentSites.Member(node, 42);
AssignmentSites.Property(node, 7);
AssignmentSites.Index(node, 0, 9);
AssignmentSites.Add(node, 1);
AssignmentSites.EventOn(node, () => { });
AssignmentSites.Coalesce(node, "first");
AssignmentSites.Chain(node, 5);

Console.WriteLine(node.Field);
Console.WriteLine(node.Prop);
Console.WriteLine(node.Next.Field);
Console.WriteLine(node.Label);
Console.WriteLine(AssignmentSites.ValueForm(node, 3));
Console.WriteLine(AssignmentSites.CoalesceValue(node, "second"));

var plain = new Node();
AssignmentSites.PlainCoalesce(plain, "plain");
Console.WriteLine(plain.Label);
