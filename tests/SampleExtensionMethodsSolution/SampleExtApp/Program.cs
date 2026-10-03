using SampleExtLib.Collections;
using SampleExtLib.Shapes;
using SampleExtLib.Text;

// Basic + multi-argument user extension calls.
var shouted = "hi".Shout();
if (shouted != "HI!") return 1;
if ("ab".Repeat(2) != "abab") return 2;

// All-extension chain (EM-CHAIN): generic inference on Echo, then Shout.
var chained = "go".Echo().Shout();
if (chained != "GO!") return 3;

// Instance/extension name collision: zero-arg instance member wins and stays
// untouched; the one-arg form cannot bind to the instance and falls back to
// the extension.
var counter = new Counter();
if (counter.Report() != 1) return 4;
if (counter.Report(10) != 12) return 5;

// Cross-project extension chain over an IEnumerable receiver.
var joined = new[] { "x", "y" }.RequireNonEmpty().JoinWith(",");
if (joined != "x,y") return 6;

// LINQ sugar is owned by Enumerable and must survive the pass unchanged.
var linq = new[] { 1, 2, 3 }.Where(n => n > 1).Select(n => n * 2).Sum();
if (linq != 10) return 7;

return 0;
