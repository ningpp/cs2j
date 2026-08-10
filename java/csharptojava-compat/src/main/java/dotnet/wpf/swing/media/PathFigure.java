package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;

/**
 * System.Windows.Media.PathFigure compatibility class.
 */
public final class PathFigure {

    private Point startPoint;
    private boolean isClosed;
    private boolean isFilled = true;
    private PathSegmentCollection segments;

    public PathFigure() {
        this(new Point(), new PathSegmentCollection(), false);
    }

    public PathFigure(Point start, Iterable<PathSegment> segments, boolean closed) {
        this.startPoint = start;
        this.segments = segments instanceof PathSegmentCollection
                ? (PathSegmentCollection) segments
                : new PathSegmentCollection(segments);
        this.isClosed = closed;
    }

    public Point getStartPoint() {
        return startPoint;
    }

    public void setStartPoint(Point value) {
        startPoint = value;
    }

    public boolean getIsClosed() {
        return isClosed;
    }

    public void setIsClosed(boolean value) {
        isClosed = value;
    }

    public boolean getIsFilled() {
        return isFilled;
    }

    public void setIsFilled(boolean value) {
        isFilled = value;
    }

    public PathSegmentCollection getSegments() {
        return segments;
    }

    public void setSegments(PathSegmentCollection value) {
        segments = value;
    }

    public boolean mayHaveCurves() {
        return false;
    }

    public PathFigure clone() {
        PathFigure c = new PathFigure(
                startPoint == null ? null : new Point(startPoint),
                segments == null ? null : new PathSegmentCollection(segments),
                isClosed);
        c.setIsFilled(isFilled);
        return c;
    }

    @Override
    public String toString() {
        return "PathFigure Start=" + startPoint + " Segments=" + (segments == null ? 0 : segments.size());
    }
}
