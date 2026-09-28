using SampleJavaKeywordLib;

// 跨文件引用改名后的符号：Lib.True() → TrueValue()，Lib.Null → NullValue
var lib = new KeywordSamples();
lib.TrueValue();
_ = lib.NullValue;

var n = new NullValue();
var t = new Truthiness { V = 1 };
if (Truthiness.IsTrue(t)) { }
