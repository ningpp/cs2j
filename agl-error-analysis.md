# AGL Error Analysis

## Iteration 1 — EventArgs/EventHandler 找不到符号

- **Java 文件**: IViewer.java, IViewerObject.java
- **行号**: IViewer.java:33,34,46,47; IViewerObject.java:26,27,28,29
- **错误信息**: 找不到符号 - 类 EventArgs / 类 EventHandler
- **代码片段**:
  ```java
  public void addViewChangeEventListener(BiConsumer<Object, EventArgs> handler);
  public void addGraphChangedListener(EventHandler handler);
  ```
- **对应 C# 文件**: E:\agl-master\GraphLayout\Drawing\LayoutEditing\IViewer.cs, IViewerObject.cs
- **根因分类**: Transformer
- **涉及组件**: src/CSharpToJava.Core/Transformers/Member/EventFieldTransformer.cs
- **分析**: EventFieldTransformer 的 DetermineListenerSignature 方法在 fallback 路径中，当语义模型无法解析类型时：(1) 对于 EventHandler<T>，当 argTypeInfo.Type 为 null 时直接使用 argTypeSyntax.ToString() 而非 context.MapTypeFromSyntax()，导致 EventArgs 未被映射为 Object；(2) 对于非泛型 EventHandler，直接使用标识符文本 "EventHandler" 而未转换为 BiConsumer<Object, Object>。

✅ Fixed
