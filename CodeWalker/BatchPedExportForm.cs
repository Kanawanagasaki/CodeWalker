using CodeWalker.GameFiles;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace CodeWalker
{
    /// <summary>
    /// Dialog for selecting multiple peds to batch export as glTF/GLB.
    /// Each ped will be exported with geometry, textures, and skeleton only — no animations.
    /// </summary>
    public class BatchPedExportForm : Form
    {
        private CheckedListBox PedsCheckedListBox;
        private Button SelectAllButton;
        private Button DeselectAllButton;
        private Button OKButton;
        private Button CancelBtn;
        private Label LabelInfo;
        private ComboBox FilterComboBox;
        private Label FilterLabel;

        private readonly GameFileCache _gfc;
        private readonly List<CPedModelInfo__InitData> _peds = new List<CPedModelInfo__InitData>();

        public BatchPedExportForm(GameFileCache gfc)
        {
            _gfc = gfc;
            InitializeComponent();
            PopulatePeds();
        }

        private void InitializeComponent()
        {
            this.Text = "Batch Export Peds as glTF";
            this.Size = new System.Drawing.Size(520, 580);
            this.MinimumSize = new System.Drawing.Size(400, 400);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;

            // Info label
            this.LabelInfo = new Label
            {
                Text = "Select peds to batch export as glTF/GLB.\n" +
                       "Each ped will be exported with geometry, textures, and skeleton only (no animations).\n" +
                       "Each ped will be saved as a separate file in the chosen output folder.",
                Location = new System.Drawing.Point(12, 12),
                Size = new System.Drawing.Size(480, 52),
                AutoSize = false,
            };
            this.Controls.Add(this.LabelInfo);

            // Filter label
            this.FilterLabel = new Label
            {
                Text = "Filter:",
                Location = new System.Drawing.Point(12, 68),
                AutoSize = true,
            };
            this.Controls.Add(this.FilterLabel);

            // Filter combo box
            this.FilterComboBox = new ComboBox
            {
                Location = new System.Drawing.Point(52, 65),
                Size = new System.Drawing.Size(440, 21),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            this.FilterComboBox.TextChanged += (s, e) => ApplyFilter();
            this.Controls.Add(this.FilterComboBox);

            // Checked list box
            this.PedsCheckedListBox = new CheckedListBox
            {
                Location = new System.Drawing.Point(12, 94),
                Size = new System.Drawing.Size(480, 360),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                CheckOnClick = true,
            };
            this.Controls.Add(this.PedsCheckedListBox);

            // Select All button
            this.SelectAllButton = new Button
            {
                Text = "Select All",
                Location = new System.Drawing.Point(12, 462),
                Size = new System.Drawing.Size(90, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            this.SelectAllButton.Click += (s, e) =>
            {
                for (int i = 0; i < PedsCheckedListBox.Items.Count; i++)
                    PedsCheckedListBox.SetItemChecked(i, true);
            };
            this.Controls.Add(this.SelectAllButton);

            // Deselect All button
            this.DeselectAllButton = new Button
            {
                Text = "Deselect All",
                Location = new System.Drawing.Point(108, 462),
                Size = new System.Drawing.Size(90, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            this.DeselectAllButton.Click += (s, e) =>
            {
                for (int i = 0; i < PedsCheckedListBox.Items.Count; i++)
                    PedsCheckedListBox.SetItemChecked(i, false);
            };
            this.Controls.Add(this.DeselectAllButton);

            // OK button
            this.OKButton = new Button
            {
                Text = "Export",
                DialogResult = DialogResult.OK,
                Location = new System.Drawing.Point(320, 462),
                Size = new System.Drawing.Size(84, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            this.Controls.Add(this.OKButton);

            // Cancel button
            this.CancelBtn = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new System.Drawing.Point(410, 462),
                Size = new System.Drawing.Size(84, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            this.Controls.Add(this.CancelBtn);

            this.AcceptButton = this.OKButton;
            this.CancelButton = this.CancelBtn;
        }

        private void PopulatePeds()
        {
            if (_gfc?.PedsInitDict == null) return;

            PedsCheckedListBox.BeginUpdate();
            PedsCheckedListBox.Items.Clear();
            _peds.Clear();

            var peds = _gfc.PedsInitDict.Values.ToList();
            peds.Sort((a, b) => String.Compare(a.Name, b.Name, StringComparison.Ordinal));

            foreach (var ped in peds)
            {
                _peds.Add(ped);
                PedsCheckedListBox.Items.Add(ped.Name, false);
            }

            PedsCheckedListBox.EndUpdate();
        }

        private void ApplyFilter()
        {
            var filter = FilterComboBox.Text.Trim().ToLowerInvariant();
            PedsCheckedListBox.BeginUpdate();

            // Remember checked state by ped name
            var checkedNames = new HashSet<string>();
            for (int i = 0; i < PedsCheckedListBox.Items.Count; i++)
            {
                if (PedsCheckedListBox.GetItemChecked(i))
                    checkedNames.Add((string)PedsCheckedListBox.Items[i]);
            }

            PedsCheckedListBox.Items.Clear();

            foreach (var ped in _peds)
            {
                if (!string.IsNullOrEmpty(filter) && !ped.Name.ToLowerInvariant().Contains(filter))
                    continue;

                PedsCheckedListBox.Items.Add(ped.Name, checkedNames.Contains(ped.Name));
            }

            PedsCheckedListBox.EndUpdate();
        }

        /// <summary>
        /// Returns the list of ped init data entries that are checked in the dialog.
        /// </summary>
        public IEnumerable<CPedModelInfo__InitData> GetSelectedPeds()
        {
            var result = new List<CPedModelInfo__InitData>();
            for (int i = 0; i < PedsCheckedListBox.Items.Count; i++)
            {
                if (PedsCheckedListBox.GetItemChecked(i))
                {
                    var name = (string)PedsCheckedListBox.Items[i];
                    var ped = _peds.FirstOrDefault(p => p.Name == name);
                    if (ped != null)
                        result.Add(ped);
                }
            }
            return result;
        }
    }
}
