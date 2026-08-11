package dotnet.wpf.swing.media;

/**
 * System.Windows.Media.HitTestResultCallback compatibility functional interface.
 */
@FunctionalInterface
public interface HitTestResultCallback {

    HitTestResultBehavior invoke(HitTestResult result);
}
