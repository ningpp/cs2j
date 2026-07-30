public interface IFunction {
    Object invoke(Object context, Object[] args, Object doc);
}

public class Test {
    private IFunction function;

    Object m(Object context, Object[] args, Object doc) {
        return function.invoke(context, args, doc);
    }
}

