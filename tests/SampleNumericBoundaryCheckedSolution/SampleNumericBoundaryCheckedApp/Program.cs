// Project-wide CheckForOverflowUnderflow=true: numeric-boundary M1 must reject
// this project with CS2J5326 before any output is consumed downstream.
var values = new[] { 1, 2, 3 };
var sum = 0;
foreach (var v in values)
{
    sum += v;
}

Console.WriteLine(sum);
