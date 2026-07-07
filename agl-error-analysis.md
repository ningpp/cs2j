# AGL Drawing Error Analysis

## Iteration 1 — CSharpDictionary Copy Constructor

- **Java 文件**: d:\agl202607-Drawing\automaticgraphlayout-drawing\src\main\java\Microsoft\Msagl\Drawing\ObjectDragUndoRedoAction.java
- **行号**: 77
- **错误信息**: 对于CSharpDictionary(CSharpDictionary<GeometryObject, RestoreData>), 找不到合适的构造器
- **代码片段**:
  ```java
  CSharpDictionary<GeometryObject, RestoreData> cloneRestoreDictionary() {
      return new CSharpDictionary<GeometryObject, RestoreData>(restoreDataDictionary);
  }
  ```
- **对应 C# 文件**: E:\agl-master\GraphLayout\Drawing\LayoutEditing\ObjectDragUndoRedoAction.cs
- **根因分类**: 语义丢失
- **涉及组件**: java/csharptojava-compat/src/main/java/io/github/ningpp/compat/CSharpDictionary.java
- **分析**: C# Dictionary有拷贝构造器 Dictionary(Dictionary)，但CSharpDictionary不实现java.util.Map，故传入CSharpDictionary参数无法匹配Map<? extends K,? extends V>构造器。需要添加CSharpDictionary(CSharpDictionary)拷贝构造器。
- **状态**: ✅ Fixed

## Iteration 1 — StringHelper.join Varargs

- **Java 文件**: d:\agl202607-Drawing\automaticgraphlayout-drawing\src\main\java\Microsoft\Msagl\Drawing\SvgGraphWriter.java
- **行号**: 479
- **错误信息**: 对于join(String,String,String,String,String,String,String), 找不到合适的方法
- **代码片段**:
  ```java
  return StringHelper.join(" ", "A", ellipseRadiuses(ellipse), doubleToString(...), largeArc, sweepFlag, pointsToString(ellipse.getEnd()));
  ```
- **对应 C# 文件**: E:\agl-master\GraphLayout\Drawing\SvgGraphWriter.cs
- **根因分类**: 语义丢失
- **涉及组件**: java/csharptojava-compat/src/main/java/io/github/ningpp/compat/StringHelper.java
- **分析**: C# String.Join有params变参，但Java StringHelper.join只有(String,String[])重载，没有varargs。转换器把params展开为多参数直接传递，需在StringHelper中添加varargs重载。
- **状态**: ✅ Fixed
