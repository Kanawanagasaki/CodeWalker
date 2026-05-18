using CodeWalker.GameFiles;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace CodeWalker.World
{
    /// <summary>
    /// Dialog for selecting which cutscene objects (characters, props, weapons, vehicles)
    /// to export as glTF/GLB.
    /// </summary>
    public class CutsceneExportSelectionDialog : Form
    {
        private CheckedListBox ObjectsCheckedListBox;
        private Button SelectAllButton;
        private Button DeselectAllButton;
        private Button SelectPedsButton;
        private Button OKButton;
        private Button CancelBtn;
        private Label LabelInfo;

        private readonly Cutscene _cutscene;
        private readonly List<CutsceneObject> _objects = new List<CutsceneObject>();

        public CutsceneExportSelectionDialog(Cutscene cutscene)
        {
            _cutscene = cutscene;
            InitializeComponent();
            PopulateObjects();
        }

        private void InitializeComponent()
        {
            this.Text = "Select Objects to Export";
            this.Size = new System.Drawing.Size(480, 520);
            this.MinimumSize = new System.Drawing.Size(400, 400);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;

            // Info label
            this.LabelInfo = new Label
            {
                Text = "Select the cutscene objects you want to export as glTF/GLB.\n" +
                       "Only objects with geometry (characters, props, weapons, vehicles) can be exported.",
                Location = new System.Drawing.Point(12, 12),
                Size = new System.Drawing.Size(440, 44),
                AutoSize = false,
            };
            this.Controls.Add(this.LabelInfo);

            // Checked list box
            this.ObjectsCheckedListBox = new CheckedListBox
            {
                Location = new System.Drawing.Point(12, 62),
                Size = new System.Drawing.Size(440, 310),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                CheckOnClick = true,
            };
            this.Controls.Add(this.ObjectsCheckedListBox);

            // Select All button
            this.SelectAllButton = new Button
            {
                Text = "Select All",
                Location = new System.Drawing.Point(12, 382),
                Size = new System.Drawing.Size(85, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            this.SelectAllButton.Click += (s, e) =>
            {
                for (int i = 0; i < ObjectsCheckedListBox.Items.Count; i++)
                    ObjectsCheckedListBox.SetItemChecked(i, true);
            };
            this.Controls.Add(this.SelectAllButton);

            // Deselect All button
            this.DeselectAllButton = new Button
            {
                Text = "Deselect All",
                Location = new System.Drawing.Point(103, 382),
                Size = new System.Drawing.Size(85, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            this.DeselectAllButton.Click += (s, e) =>
            {
                for (int i = 0; i < ObjectsCheckedListBox.Items.Count; i++)
                    ObjectsCheckedListBox.SetItemChecked(i, false);
            };
            this.Controls.Add(this.DeselectAllButton);

            // Select Peds Only button
            this.SelectPedsButton = new Button
            {
                Text = "Peds Only",
                Location = new System.Drawing.Point(194, 382),
                Size = new System.Drawing.Size(85, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            this.SelectPedsButton.Click += (s, e) =>
            {
                for (int i = 0; i < ObjectsCheckedListBox.Items.Count; i++)
                {
                    var obj = _objects[i];
                    ObjectsCheckedListBox.SetItemChecked(i, obj.Ped != null);
                }
            };
            this.Controls.Add(this.SelectPedsButton);

            // OK button
            this.OKButton = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new System.Drawing.Point(268, 382),
                Size = new System.Drawing.Size(90, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            this.Controls.Add(this.OKButton);

            // Cancel button
            this.CancelBtn = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new System.Drawing.Point(364, 382),
                Size = new System.Drawing.Size(90, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            this.Controls.Add(this.CancelBtn);

            this.AcceptButton = this.OKButton;
            this.CancelButton = this.CancelBtn;
        }

        private void PopulateObjects()
        {
            if (_cutscene?.SceneObjects == null) return;

            ObjectsCheckedListBox.BeginUpdate();
            ObjectsCheckedListBox.Items.Clear();
            _objects.Clear();

            foreach (var obj in _cutscene.SceneObjects.Values.OrderBy(o => o.ObjectID))
            {
                // Determine what type of object this is and whether it's exportable
                string typeLabel = "";
                bool exportable = false;

                if (obj.Ped != null)
                {
                    typeLabel = "[Ped]";
                    exportable = true;
                }
                else if (obj.Prop != null)
                {
                    typeLabel = "[Prop]";
                    exportable = true;
                }
                else if (obj.Weapon != null)
                {
                    typeLabel = "[Weapon]";
                    exportable = true;
                }
                else if (obj.Vehicle != null)
                {
                    typeLabel = "[Vehicle]";
                    exportable = true;
                }
                else
                {
                    typeLabel = "[Other]";
                    exportable = false;
                }

                if (!exportable) continue;

                string displayName = $"Object {obj.ObjectID} {typeLabel}";
                if (obj.Name != 0)
                    displayName += $" ({obj.Name})";

                string status = obj.Enabled == false ? " [disabled]" : "";
                displayName += status;

                _objects.Add(obj);
                ObjectsCheckedListBox.Items.Add(displayName, obj.Enabled != false);
            }

            ObjectsCheckedListBox.EndUpdate();
        }

        /// <summary>
        /// Returns the list of CutsceneObject instances that are checked in the dialog.
        /// </summary>
        public IEnumerable<CutsceneObject> GetSelectedObjects()
        {
            var result = new List<CutsceneObject>();
            for (int i = 0; i < ObjectsCheckedListBox.Items.Count; i++)
            {
                if (ObjectsCheckedListBox.GetItemChecked(i) && i < _objects.Count)
                {
                    result.Add(_objects[i]);
                }
            }
            return result;
        }
    }
}
