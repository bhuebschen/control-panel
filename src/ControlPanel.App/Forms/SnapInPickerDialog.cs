using System.Windows.Forms;
using ControlPanel.App.Hosting;

namespace ControlPanel.App.Forms;

internal sealed class SnapInPickerDialog : Form
{
    private readonly ListView _list;
    private readonly Button _okButton;
    private readonly Button _cancelButton;

    public SnapInInfo? SelectedSnapIn { get; private set; }

    public SnapInPickerDialog(IReadOnlyList<SnapInInfo> snapIns)
    {
        Text = "Add Standalone Snap-in";
        StartPosition = FormStartPosition.CenterParent;
        Width = 640;
        Height = 460;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;

        _list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            Dock = DockStyle.Fill,
        };
        _list.Columns.Add("Snap-in", 220);
        _list.Columns.Add("Vendor", 160);
        _list.Columns.Add("Version", 100);
        _list.DoubleClick += (_, _) => AcceptSelection();

        foreach (var info in snapIns)
        {
            if (!info.Standalone)
            {
                continue;
            }

            var item = new ListViewItem(info.Name) { Tag = info };
            item.SubItems.Add(info.Provider ?? string.Empty);
            item.SubItems.Add(info.Version ?? string.Empty);
            _list.Items.Add(item);
        }

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(8),
        };

        _cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        _okButton = new Button { Text = "Add", DialogResult = DialogResult.OK, Width = 90, Enabled = false };
        _okButton.Click += (_, _) => AcceptSelection();

        buttonPanel.Controls.Add(_cancelButton);
        buttonPanel.Controls.Add(_okButton);

        _list.SelectedIndexChanged += (_, _) => _okButton.Enabled = _list.SelectedItems.Count > 0;

        Controls.Add(_list);
        Controls.Add(buttonPanel);

        AcceptButton = _okButton;
        CancelButton = _cancelButton;
    }

    private void AcceptSelection()
    {
        if (_list.SelectedItems.Count > 0)
        {
            SelectedSnapIn = (SnapInInfo)_list.SelectedItems[0].Tag!;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
