using SampleRecordLib;

var p = new Point(1, 2);
p.Z = 3;
Console.WriteLine($"Point: {p.X}, {p.Y}, {p.Z}");

var person = new Person("Alice");
person.Age = 30;
Console.WriteLine($"{person.Name}, {person.Age}");
