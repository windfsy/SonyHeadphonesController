using System.Windows;
using System.Windows.Controls.Primitives;

namespace SonyCtrl.UI;

public partial class ControlPanelWindow : Window
{
    public ControlPanelWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    /// <summary>Anchor the flyout above the taskbar, right-aligned (near the tray corner).</summary>
    public void PositionNearTray()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 12;
        Top = workArea.Bottom - ActualHeight - 8;
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        // Flyout behaviour: hide when it loses focus (click anywhere else).
        base.Hide();
        // Release Topmost so it doesn't linger over other windows while hidden.
        Topmost = false;
    }

    private void OnAmbientDragCompleted(object sender, DragCompletedEventArgs e)
    {
        // Commit the slider value only when the user releases the thumb: avoids flooding
        // the device with per-tick SET commands during the drag.
        (DataContext as MainViewModel)?.CommitAmbientLevel();
    }
}
