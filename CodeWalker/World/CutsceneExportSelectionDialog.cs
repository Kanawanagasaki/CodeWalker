using CodeWalker.GameFiles;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace CodeWalker.World
{
    /// <summary>
    /// Dialog for selecting which cutscene objects (characters, props, weapons, vehicles)
    /// to export as glTF/GLB. When exactly one ped is selected, a model override dropdown
    /// appears allowing the user to swap the ped's visual model (mesh + textures) while
    /// keeping the original skeleton and animation from the cutscene.
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

        // Ped model override controls
        private Label PedOverrideLabel;
        private ComboBox PedOverrideComboBox;

        // Root motion option controls
        private GroupBox RootMotionGroupBox;
        private CheckBox EnableRootPositionCheckbox;
        private CheckBox EnableRootRotationCheckbox;

        private readonly Cutscene _cutscene;
        private readonly GameFileCache _gfc;
        private readonly List<CutsceneObject> _objects = new List<CutsceneObject>();

        /// <summary>
        /// The ped model name selected for override, or null/empty if no override.
        /// </summary>
        public string PedModelOverride => PedOverrideComboBox?.SelectedItem as string;

        /// <summary>
        /// When true, the ped root node's translation is animated across camera cuts
        /// using track 5 (RootPosition). When false, the ped root stays at its static
        /// position from BuildPedArmature for the entire timeline.
        /// Checked by default.
        /// </summary>
        public bool EnableRootPosition => EnableRootPositionCheckbox?.Checked ?? true;

        /// <summary>
        /// When true, the ped root node's rotation is animated across camera cuts
        /// using track 6 (RootRotation). When false, the ped root keeps its static
        /// rotation from BuildPedArmature for the entire timeline.
        /// Checked by default.
        /// </summary>
        public bool EnableRootRotation => EnableRootRotationCheckbox?.Checked ?? true;

        /// <summary>
        /// The single ped CutsceneObject that will have its model overridden,
        /// or null if zero or multiple peds are selected.
        /// </summary>
        public CutsceneObject OverrideTargetPed { get; private set; }

        public CutsceneExportSelectionDialog(Cutscene cutscene, GameFileCache gfc)
        {
            _cutscene = cutscene;
            _gfc = gfc;
            InitializeComponent();
            PopulateObjects();
            PopulatePedOverrideList();
            UpdatePedOverrideVisibility();
        }

        private void InitializeComponent()
        {
            this.Text = "Select Objects to Export";
            this.Size = new System.Drawing.Size(520, 640);
            this.MinimumSize = new System.Drawing.Size(440, 540);
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
                Size = new System.Drawing.Size(480, 44),
                AutoSize = false,
            };
            this.Controls.Add(this.LabelInfo);

            // Checked list box
            this.ObjectsCheckedListBox = new CheckedListBox
            {
                Location = new System.Drawing.Point(12, 62),
                Size = new System.Drawing.Size(480, 270),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                CheckOnClick = true,
            };
            this.ObjectsCheckedListBox.ItemCheck += ObjectsCheckedListBox_ItemCheck;
            this.Controls.Add(this.ObjectsCheckedListBox);

            // Root motion options group box.
            // These checkboxes control whether the ped root node's translation and/or
            // rotation is animated across camera cuts during export. Both default to
            // checked — animating the root is what makes peds move to the correct
            // position for each camera cut. Uncheck one if you want the root to stay
            // static on that axis (e.g., export only root rotation, keep position fixed).
            this.RootMotionGroupBox = new GroupBox
            {
                Text = "Root motion (per ped)",
                Location = new System.Drawing.Point(12, 340),
                Size = new System.Drawing.Size(480, 64),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            this.Controls.Add(this.RootMotionGroupBox);

            this.EnableRootPositionCheckbox = new CheckBox
            {
                Text = "Enable root position (track 5)",
                Location = new System.Drawing.Point(12, 24),
                Size = new System.Drawing.Size(220, 24),
                Checked = true,
                AutoSize = false,
            };
            this.RootMotionGroupBox.Controls.Add(this.EnableRootPositionCheckbox);

            this.EnableRootRotationCheckbox = new CheckBox
            {
                Text = "Enable root rotation (track 6)",
                Location = new System.Drawing.Point(240, 24),
                Size = new System.Drawing.Size(220, 24),
                Checked = true,
                AutoSize = false,
            };
            this.RootMotionGroupBox.Controls.Add(this.EnableRootRotationCheckbox);

            // Ped model override label
            this.PedOverrideLabel = new Label
            {
                Text = "Override ped model:",
                Location = new System.Drawing.Point(12, 412),
                Size = new System.Drawing.Size(120, 20),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Visible = false,
            };
            this.Controls.Add(this.PedOverrideLabel);

            // Ped model override combobox
            this.PedOverrideComboBox = new ComboBox
            {
                Location = new System.Drawing.Point(138, 410),
                Size = new System.Drawing.Size(354, 22),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Sorted = false, // already sorted during population
                Visible = false,
            };
            this.Controls.Add(this.PedOverrideComboBox);

            // Select All button
            this.SelectAllButton = new Button
            {
                Text = "Select All",
                Location = new System.Drawing.Point(12, 444),
                Size = new System.Drawing.Size(85, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            this.SelectAllButton.Click += (s, e) =>
            {
                for (int i = 0; i < ObjectsCheckedListBox.Items.Count; i++)
                    ObjectsCheckedListBox.SetItemChecked(i, true);
                UpdatePedOverrideVisibility();
            };
            this.Controls.Add(this.SelectAllButton);

            // Deselect All button
            this.DeselectAllButton = new Button
            {
                Text = "Deselect All",
                Location = new System.Drawing.Point(103, 444),
                Size = new System.Drawing.Size(85, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };
            this.DeselectAllButton.Click += (s, e) =>
            {
                for (int i = 0; i < ObjectsCheckedListBox.Items.Count; i++)
                    ObjectsCheckedListBox.SetItemChecked(i, false);
                UpdatePedOverrideVisibility();
            };
            this.Controls.Add(this.DeselectAllButton);

            // Select Peds Only button
            this.SelectPedsButton = new Button
            {
                Text = "Peds Only",
                Location = new System.Drawing.Point(194, 444),
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
                UpdatePedOverrideVisibility();
            };
            this.Controls.Add(this.SelectPedsButton);

            // OK button
            this.OKButton = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new System.Drawing.Point(308, 444),
                Size = new System.Drawing.Size(90, 26),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            };
            this.Controls.Add(this.OKButton);

            // Cancel button
            this.CancelBtn = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new System.Drawing.Point(404, 444),
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

                // Build a human-readable display name. For peds, include the ped model
                // name (from ped.Name if set, otherwise from ped.NameHash which always
                // resolves to the model name like "player_one" / "cs_lamardavis" via
                // JenkIndex/MetaNames lookup) so the user can tell peds apart in the
                // list — without this, every ped just shows as "Object N [Ped]".
                string displayName = $"Object {obj.ObjectID} {typeLabel}";

                if (obj.Ped != null)
                {
                    string pedModel = obj.Ped.Name;
                    if (string.IsNullOrEmpty(pedModel))
                        pedModel = obj.Ped.NameHash.ToString();
                    if (!string.IsNullOrEmpty(pedModel))
                        displayName += $" <{pedModel}>";
                }

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
        /// Populate the ped model override ComboBox with all available ped models
        /// from GameFileCache.PedsInitDict, sorted alphabetically.
        /// The first item is always "(Original — no override)".
        /// </summary>
        private void PopulatePedOverrideList()
        {
            if (PedOverrideComboBox == null) return;

            PedOverrideComboBox.BeginUpdate();
            PedOverrideComboBox.Items.Clear();

            // Default option: no override
            PedOverrideComboBox.Items.Add("(Original — no override)");

            // Add all ped models from the game file cache
            if (_gfc?.PedsInitDict != null)
            {
                var peds = _gfc.PedsInitDict.Values
                    .Where(p => !string.IsNullOrEmpty(p.Name))
                    .OrderBy(p => p.Name)
                    .ToList();

                foreach (var ped in peds)
                {
                    PedOverrideComboBox.Items.Add(ped.Name);
                }
            }

            // Select the default (no override)
            PedOverrideComboBox.SelectedIndex = 0;
            PedOverrideComboBox.EndUpdate();
        }

        /// <summary>
        /// Show/hide the ped model override dropdown based on how many peds are checked.
        /// The dropdown is only visible when exactly one ped is selected.
        /// </summary>
        private void UpdatePedOverrideVisibility()
        {
            // Find all checked peds
            var checkedPeds = new List<CutsceneObject>();
            for (int i = 0; i < ObjectsCheckedListBox.Items.Count; i++)
            {
                if (ObjectsCheckedListBox.GetItemChecked(i) && i < _objects.Count)
                {
                    if (_objects[i].Ped != null)
                        checkedPeds.Add(_objects[i]);
                }
            }

            bool showOverride = checkedPeds.Count == 1;
            PedOverrideLabel.Visible = showOverride;
            PedOverrideComboBox.Visible = showOverride;
            OverrideTargetPed = showOverride ? checkedPeds[0] : null;

            // Reset to "no override" when hiding
            if (!showOverride && PedOverrideComboBox.Items.Count > 0)
                PedOverrideComboBox.SelectedIndex = 0;
        }

        private void ObjectsCheckedListBox_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            // Use BeginInvoke to defer the visibility update until after the check state changes.
            // Guard with IsHandleCreated because this event fires during PopulateObjects()
            // in the constructor, before the native window handle exists.
            if (IsHandleCreated)
                BeginInvoke((Action)(() => UpdatePedOverrideVisibility()));
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
