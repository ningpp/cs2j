package io.github.ningpp.compat;

public final class CSharpStructuralComparisons {
    private CSharpStructuralComparisons() {}

    private static final CSharpComparer STRUCTURAL_COMPARER = new CSharpComparer() {
        @Override
        @SuppressWarnings("unchecked")
        public int compare(Object a, Object b) {
            if (a instanceof Object[] arrA && b instanceof Object[] arrB) {
                int len = Math.min(arrA.length, arrB.length);
                for (int i = 0; i < len; i++) {
                    int cmp = CSharpDefaultComparer.getDefault().compare(arrA[i], arrB[i]);
                    if (cmp != 0) return cmp;
                }
                return Integer.compare(arrA.length, arrB.length);
            }
            return CSharpDefaultComparer.getDefault().compare(a, b);
        }
    };

    private static final CSharpEqualityComparer STRUCTURAL_EQUALITY_COMPARER = new CSharpEqualityComparer() {
        @Override
        public boolean equals(Object x, Object y) {
            if (x instanceof Object[] ax && y instanceof Object[] ay) {
                return java.util.Arrays.equals(ax, ay);
            }
            return java.util.Objects.equals(x, y);
        }
        @Override
        public int hashCode(Object obj) {
            if (obj instanceof Object[] arr) return java.util.Arrays.hashCode(arr);
            return java.util.Objects.hashCode(obj);
        }
    };

    public static CSharpComparer getStructuralComparer() { return STRUCTURAL_COMPARER; }
    public static CSharpEqualityComparer getStructuralEqualityComparer() { return STRUCTURAL_EQUALITY_COMPARER; }
}
