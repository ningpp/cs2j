package dotnet.wpf.swing.media.imaging;

import dotnet.wpf.swing.media.BitmapSource;

import java.io.File;
import java.io.IOException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;

import javax.imageio.ImageIO;

/**
 * System.Windows.Media.Imaging.BitmapImage compatibility class.
 * Supports BeginInit/EndInit sequencing and UriSource (file or http) loading via ImageIO.
 */
public final class BitmapImage extends BitmapSource {

    private boolean initializing;
    private int createOptions = BitmapCreateOptions.None;
    private BitmapCacheOption cacheOption = BitmapCacheOption.Default;
    private Object uriSource;
    private Object uriCachePolicy;
    private Object streamSource;
    private boolean frozen;

    public BitmapImage() {
        super(1, 1, 96, 96);
    }

    public BitmapImage(URI uriSource) {
        super(1, 1, 96, 96);
        this.uriSource = uriSource;
        loadFromUri(uriSource);
    }

    public void beginInit() {
        this.initializing = true;
    }

    public void endInit() {
        this.initializing = false;
        Object uri = uriSource;
        if (uri != null && !(uri instanceof URI) && uri instanceof String) {
            loadFromUri(URI.create((String) uri));
        } else if (uri instanceof URI) {
            loadFromUri((URI) uri);
        }
    }

    public int getCreateOptions() {
        return createOptions;
    }

    public void setCreateOptions(int value) {
        createOptions = value;
    }

    public BitmapCacheOption getCacheOption() {
        return cacheOption;
    }

    public void setCacheOption(BitmapCacheOption value) {
        cacheOption = value == null ? BitmapCacheOption.Default : value;
    }

    public Object getUriSource() {
        return uriSource;
    }

    public void setUriSource(Object value) {
        uriSource = value;
    }

    public Object getUriCachePolicy() {
        return uriCachePolicy;
    }

    public void setUriCachePolicy(Object value) {
        uriCachePolicy = value;
    }

    public Object getStreamSource() {
        return streamSource;
    }

    public void setStreamSource(Object value) {
        streamSource = value;
    }

    public void freeze() {
        frozen = true;
    }

    public boolean isFrozen() {
        return frozen;
    }

    private void loadFromUri(URI uri) {
        if (uri == null) {
            return;
        }
        try {
            if ("file".equalsIgnoreCase(uri.getScheme()) || uri.getScheme() == null) {
                File f = uri.getScheme() == null ? new File(uri.getPath() != null ? uri.getPath() : uri.toString())
                        : new File(uri);
                java.awt.image.BufferedImage img = ImageIO.read(f);
                if (img != null) {
                    replaceImage(img);
                }
            } else if ("http".equalsIgnoreCase(uri.getScheme()) || "https".equalsIgnoreCase(uri.getScheme())) {
                HttpClient client = HttpClient.newHttpClient();
                HttpRequest req = HttpRequest.newBuilder(uri).GET().build();
                HttpResponse<byte[]> resp = client.send(req, HttpResponse.BodyHandlers.ofByteArray());
                if (resp.statusCode() == 200) {
                    java.awt.image.BufferedImage img = ImageIO.read(new java.io.ByteArrayInputStream(resp.body()));
                    if (img != null) {
                        replaceImage(img);
                    }
                }
            }
        } catch (IOException | InterruptedException ignored) {
            // leave default image
        }
    }

    private void replaceImage(java.awt.image.BufferedImage img) {
        java.awt.image.BufferedImage copy = new java.awt.image.BufferedImage(
                Math.max(1, img.getWidth()), Math.max(1, img.getHeight()),
                java.awt.image.BufferedImage.TYPE_INT_ARGB);
        java.awt.Graphics2D g = copy.createGraphics();
        try {
            g.drawImage(img, 0, 0, null);
        } finally {
            g.dispose();
        }
        replaceInternal(copy);
    }
}
