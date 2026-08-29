import dotnet.text.json.*;

public class CheckPrim {
    public static void main(String[] args) {
        System.out.println("None.ordinal=" + JsonTokenType.None.ordinal());
        System.out.println("String.ordinal=" + JsonTokenType.String.ordinal());
        System.out.println("Null.ordinal=" + JsonTokenType.Null.ordinal());
        System.out.println("isTokenTypePrimitive(None)=" + JsonReaderHelper.isTokenTypePrimitive(JsonTokenType.None));
        System.out.println("isTokenTypePrimitive(Comment)=" + JsonReaderHelper.isTokenTypePrimitive(JsonTokenType.Comment));
        System.out.println("isTokenTypePrimitive(True)=" + JsonReaderHelper.isTokenTypePrimitive(JsonTokenType.True));
    }
}
