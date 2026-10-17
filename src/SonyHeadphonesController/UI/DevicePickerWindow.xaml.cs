using System.Windows;
using System.Windows.Input;
using SonyCtrl.Transport;

namespace SonyCtrl.UI;

public partial class DevicePickerWindow : Window
{
    public sealed record DeviceEntry(string Name, string AddressText, ulong Address, bool Connected);

    public DevicePickerWindow()
    {
        InitializeComponent();
        LoadDevices();
    }

    private void LoadDevices()
    {
        var devices = BluetoothDeviceEnumerator.EnumeratePairedDevices();

        // Prefer likely Sony headphone candidates first, but keep the full list selectable.
        var ordered = devices
            .OrderByDescending(d => d.Name.Contains("WF-", StringComparison.OrdinalIgnoreCase)
                                 || d.Name.Contains("WH-", StringComparison.OrdinalIgnoreCase)
                                 || d.Name.Contains("Sony", StringComparison.OrdinalIgnoreCase))
            .ThenBy(d => d.Name);

        DeviceList.ItemsSource = ordered.Select(d => new DeviceEntry(d.Name, $"{d.Address:X12}", d.Address, d.Connected)).ToList();
        if (DeviceList.Items.Count > 0) DeviceList.SelectedIndex = 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is DeviceEntry entry)
        {
            App.Config.TargetName = entry.Name;
            App.Config.TargetAddress = entry.Address;
            App.Config.Save();
        }
        DialogResult = DeviceList.SelectedItem is DeviceEntry;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void DeviceList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Ok_Click(sender, e);
    }
}
