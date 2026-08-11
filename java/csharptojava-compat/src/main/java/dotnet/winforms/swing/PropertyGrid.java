package dotnet.winforms.swing;

/**
 * System.Windows.Forms.PropertyGrid compatibility class.
 */
public class PropertyGrid extends Control {

    private PropertySort propertySort = PropertySort.NoSort;
    private Object selectedObject;

    public PropertyGrid() {
    }

    public PropertySort getPropertySort() {
        return propertySort;
    }

    public void setPropertySort(PropertySort value) {
        propertySort = value;
    }

    public Object getSelectedObject() {
        return selectedObject;
    }

    public void setSelectedObject(Object value) {
        selectedObject = value;
    }
}
