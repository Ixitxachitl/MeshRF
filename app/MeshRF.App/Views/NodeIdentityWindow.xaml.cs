// SPDX-License-Identifier: GPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MeshRF.App.Views;

public partial class NodeIdentityWindow : Window
{
    public NodeIdentityWindow()
    {
        InitializeComponent();
    }

    // When a ComboBox dropdown is open, PreviewMouseWheel (tunneling) fires
    // here before the event reaches either the ComboBox (which would change
    // selection via Selector.OnMouseWheel) or the outer ScrollViewer (which
    // would scroll the page).  We redirect it to scroll the popup instead.
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var openCombo = FindOpenDropDown(this);
        if (openCombo == null) return;
        if (FindDropDownScrollViewer(openCombo) is ScrollViewer sv)
            if (e.Delta > 0) sv.LineUp(); else sv.LineDown();
        e.Handled = true;
    }

    private static ComboBox? FindOpenDropDown(DependencyObject root)
    {
        if (root is ComboBox cb && cb.IsDropDownOpen)
            return cb;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindOpenDropDown(VisualTreeHelper.GetChild(root, i)) is ComboBox found)
                return found;
        }
        return null;
    }

    private static ScrollViewer? FindDropDownScrollViewer(ComboBox cb)
    {
        if (cb.Template?.FindName("DropDownScrollViewer", cb) is ScrollViewer named)
            return named;
        if (cb.Template?.FindName("PART_Popup", cb) is Popup { Child: { } child })
            return FindVisualDescendant<ScrollViewer>(child);
        return null;
    }

    private static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            if (FindVisualDescendant<T>(child) is T found) return found;
        }
        return null;
    }
}
