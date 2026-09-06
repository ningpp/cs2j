using SampleStripDefaultLib;

var s = new Service();
s.Process(1);              // omit b and c
s.Process(1, "hi");        // omit c
s.Process(1, "hi", false); // all args

var result = s.Calculate(5);          // omit y and z
var result2 = s.Calculate(5, 100);    // omit z
var result3 = s.Calculate(5, 100, 200);

var greeting = Service.Greet();       // omit name
var greeting2 = Service.Greet("Alice");

var named = new Service("my-service");
