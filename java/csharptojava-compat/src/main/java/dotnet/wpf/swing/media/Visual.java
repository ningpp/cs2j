package dotnet.wpf.swing.media;

import dotnet.wpf.swing.Point;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

/**
 * System.Windows.Media.Visual compatibility base class.
 * Swing-backed scene-graph node: holds visual children and a render transform.
 */
public abstract class Visual {

    private Transform renderTransform;
    private final List<Visual> visualChildren = new ArrayList<>();
    private Visual visualParent;

    public Transform getRenderTransform() {
        return renderTransform;
    }

    public void setRenderTransform(Transform value) {
        this.renderTransform = value;
    }

    public List<Visual> getVisualChildren() {
        return Collections.unmodifiableList(visualChildren);
    }

    public void addVisualChild(Visual child) {
        if (child != null) {
            child.visualParent = this;
            visualChildren.add(child);
        }
    }

    public void removeVisualChild(Visual child) {
        if (child != null && visualChildren.remove(child)) {
            child.visualParent = null;
        }
    }

    public void clearVisualChildren() {
        for (Visual c : visualChildren) {
            c.visualParent = null;
        }
        visualChildren.clear();
    }

    public Visual getVisualParent() {
        return visualParent;
    }

    public int getVisualChildrenCount() {
        return visualChildren.size();
    }

    public Visual getVisualChild(int index) {
        return visualChildren.get(index);
    }

    public boolean isAncestorOf(Visual descendant) {
        for (Visual v = descendant; v != null; v = v.visualParent) {
            if (v == this) return true;
        }
        return false;
    }

    public boolean isDescendantOf(Visual ancestor) {
        return ancestor != null && ancestor.isAncestorOf(this);
    }

    public Point pointFromScreen(Point point) {
        return point;
    }

    public Point pointToScreen(Point point) {
        return point;
    }

    /** Transforms a point from this visual's coordinates to its parent's coordinates. */
    public Point applyRenderTransform(Point point) {
        if (renderTransform != null) {
            return renderTransform.transform(point);
        }
        return point;
    }

    /** .NET Visual.TransformToAncestor: returns the accumulated transform to the ancestor. */
    public GeneralTransform transformToAncestor(Visual ancestor) {
        return new MatrixTransform();
    }
}
