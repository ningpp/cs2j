using SampleJavaKeywordLib;

// 跨文件引用改名后的符号：Lib.True() → TrueValue()；属性/类型 Null 不改名
var lib = new KeywordSamples();
lib.TrueValue();
_ = lib.Null;

var n = new Null();
var t = new Truthiness { V = 1 };
if (Truthiness.IsTrue(t)) { }
