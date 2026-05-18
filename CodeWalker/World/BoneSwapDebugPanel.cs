using CodeWalker.GameFiles;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CodeWalker.World
{
    /// <summary>
    /// Debug panel for swapping facial bones in the cutscene renderer.
    /// Allows the user to swap which skeleton bone receives facial animation data,
    /// without modifying the underlying data structures.
    /// </summary>
    public class BoneSwapDebugPanel : Form
    {
        private Cutscene _cutscene;
        private Dictionary<ushort, Bone> _facialBones = new Dictionary<ushort, Bone>();

        private ListBox BoneListA;
        private ListBox BoneListB;
        private Button SwapButton;
        private Button RemoveSwapButton;
        private ListBox ActiveSwapsList;
        private Button ClearAllButton;
        private CheckBox EnableSwapCheckBox;
        private Label LabelA;
        private Label LabelB;
        private Label LabelSwaps;
        private ComboBox PedSelector;
        private Label PedLabel;

        public BoneSwapDebugPanel(Cutscene cutscene)
        {
            _cutscene = cutscene;
            InitializeComponent();
            LoadPeds();
        }

        private void InitializeComponent()
        {
            this.Text = "Facial Bone Swap Debug Panel";
            this.Size = new Size(720, 520);
            this.FormBorderStyle = FormBorderStyle.SizableToolWindow;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MinimizeBox = false;
            this.MaximizeBox = false;

            // Ped selector
            PedLabel = new Label();
            PedLabel.Text = "Ped:";
            PedLabel.Location = new Point(12, 12);
            PedLabel.AutoSize = true;
            this.Controls.Add(PedLabel);

            PedSelector = new ComboBox();
            PedSelector.Location = new Point(50, 9);
            PedSelector.Size = new Size(250, 21);
            PedSelector.DropDownStyle = ComboBoxStyle.DropDownList;
            PedSelector.SelectedIndexChanged += PedSelector_SelectedIndexChanged;
            this.Controls.Add(PedSelector);

            // Enable checkbox
            EnableSwapCheckBox = new CheckBox();
            EnableSwapCheckBox.Text = "Enable Bone Swapping";
            EnableSwapCheckBox.Location = new Point(320, 10);
            EnableSwapCheckBox.AutoSize = true;
            EnableSwapCheckBox.Checked = FacialBoneSwapMap.Enabled;
            EnableSwapCheckBox.CheckedChanged += EnableSwapCheckBox_CheckedChanged;
            this.Controls.Add(EnableSwapCheckBox);

            // Bone list A
            LabelA = new Label();
            LabelA.Text = "Bone A:";
            LabelA.Location = new Point(12, 45);
            LabelA.AutoSize = true;
            this.Controls.Add(LabelA);

            BoneListA = new ListBox();
            BoneListA.Location = new Point(12, 65);
            BoneListA.Size = new Size(330, 300);
            BoneListA.Sorted = false;
            BoneListA.SelectedIndexChanged += BoneList_SelectedIndexChanged;
            this.Controls.Add(BoneListA);

            // Bone list B
            LabelB = new Label();
            LabelB.Text = "Bone B:";
            LabelB.Location = new Point(358, 45);
            LabelB.AutoSize = true;
            this.Controls.Add(LabelB);

            BoneListB = new ListBox();
            BoneListB.Location = new Point(358, 65);
            BoneListB.Size = new Size(330, 300);
            BoneListB.Sorted = false;
            BoneListB.SelectedIndexChanged += BoneList_SelectedIndexChanged;
            this.Controls.Add(BoneListB);

            // Swap button
            SwapButton = new Button();
            SwapButton.Text = "Swap A <-> B";
            SwapButton.Location = new Point(12, 375);
            SwapButton.Size = new Size(120, 28);
            SwapButton.Enabled = false;
            SwapButton.Click += SwapButton_Click;
            this.Controls.Add(SwapButton);

            // Remove swap button
            RemoveSwapButton = new Button();
            RemoveSwapButton.Text = "Remove Selected Swap";
            RemoveSwapButton.Location = new Point(142, 375);
            RemoveSwapButton.Size = new Size(160, 28);
            RemoveSwapButton.Enabled = false;
            RemoveSwapButton.Click += RemoveSwapButton_Click;
            this.Controls.Add(RemoveSwapButton);

            // Clear all button
            ClearAllButton = new Button();
            ClearAllButton.Text = "Clear All Swaps";
            ClearAllButton.Location = new Point(312, 375);
            ClearAllButton.Size = new Size(120, 28);
            ClearAllButton.Click += ClearAllButton_Click;
            this.Controls.Add(ClearAllButton);

            // Active swaps list
            LabelSwaps = new Label();
            LabelSwaps.Text = "Active Bone Swaps:";
            LabelSwaps.Location = new Point(12, 410);
            LabelSwaps.AutoSize = true;
            this.Controls.Add(LabelSwaps);

            ActiveSwapsList = new ListBox();
            ActiveSwapsList.Location = new Point(12, 430);
            ActiveSwapsList.Size = new Size(676, 50);
            ActiveSwapsList.SelectedIndexChanged += ActiveSwapsList_SelectedIndexChanged;
            this.Controls.Add(ActiveSwapsList);

            RefreshActiveSwaps();
        }

        private void LoadPeds()
        {
            PedSelector.Items.Clear();
            if (_cutscene?.SceneObjects == null) return;

            int pedIndex = 0;
            foreach (var obj in _cutscene.SceneObjects.Values)
            {
                if (obj.Ped != null)
                {
                    var name = string.IsNullOrEmpty(obj.Ped.Name) ? $"Ped {pedIndex}" : obj.Ped.Name;
                    PedSelector.Items.Add(new PedListItem { Index = pedIndex, Name = name, Ped = obj.Ped });
                    pedIndex++;
                }
            }

            if (PedSelector.Items.Count > 0)
                PedSelector.SelectedIndex = 0;
        }

        private void PedSelector_SelectedIndexChanged(object sender, EventArgs e)
        {
            var item = PedSelector.SelectedItem as PedListItem;
            if (item?.Ped == null) return;

            LoadFacialBones(item.Ped);
        }

        private void LoadFacialBones(Ped ped)
        {
            _facialBones.Clear();
            BoneListA.Items.Clear();
            BoneListB.Items.Clear();

            var skeleton = ped.Skeleton;
            if (skeleton?.BonesMap == null) return;

            // Identify facial bones - those that appear in expression tracks
            // or have typical facial bone names
            var facialBoneIds = new HashSet<ushort>();
            var mergedDict = BuildMergedBoneTracksDict(ped);

            // Add bones from expression BoneTracksDict
            if (ped.Expression?.BoneTracksDict != null)
            {
                foreach (var kvp in ped.Expression.BoneTracksDict)
                {
                    facialBoneIds.Add(kvp.Key.BoneId);
                    facialBoneIds.Add(kvp.Value.BoneId);
                }
            }

            if (ped.Expressions != null)
            {
                foreach (var expr in ped.Expressions)
                {
                    if (expr?.BoneTracksDict == null) continue;
                    foreach (var kvp in expr.BoneTracksDict)
                    {
                        facialBoneIds.Add(kvp.Key.BoneId);
                        facialBoneIds.Add(kvp.Value.BoneId);
                    }
                }
            }

            // Also add bones with common facial name patterns
            string[] facialPatterns = {
                "BON", "CH_", "JAW", "MOUTH", "LIP", "BROW", "EYE", "NOSE",
                "CHEEK", "CHIN", "FOREHEAD", "EAR", "TONGUE", "TEETH",
                "FB_", "Facial", "PH_", "LR_", "LL_", "CR_", "CL_",
                "MR_", "ML_", "RR_", "RL_", "NR_", "NL_"
            };

            foreach (var bone in skeleton.BonesMap.Values)
            {
                var name = bone.Name ?? "";
                var nameUpper = name.ToUpperInvariant();

                bool isFacial = facialBoneIds.Contains(bone.Tag);

                if (!isFacial)
                {
                    foreach (var pattern in facialPatterns)
                    {
                        if (nameUpper.Contains(pattern.ToUpperInvariant()))
                        {
                            isFacial = true;
                            break;
                        }
                    }
                }

                if (isFacial)
                {
                    _facialBones[bone.Tag] = bone;
                }
            }

            // Sort by tag for consistent display
            var sortedBones = _facialBones.Values.OrderBy(b => b.Tag).ToList();

            foreach (var bone in sortedBones)
            {
                var display = $"{bone.Tag} - {bone.Name}";
                BoneListA.Items.Add(new BoneListItem { Tag = bone.Tag, Name = bone.Name, Display = display });
                BoneListB.Items.Add(new BoneListItem { Tag = bone.Tag, Name = bone.Name, Display = display });
            }
        }

        private void BoneList_SelectedIndexChanged(object sender, EventArgs e)
        {
            SwapButton.Enabled = BoneListA.SelectedItem != null && BoneListB.SelectedItem != null;
        }

        private void SwapButton_Click(object sender, EventArgs e)
        {
            var itemA = BoneListA.SelectedItem as BoneListItem;
            var itemB = BoneListB.SelectedItem as BoneListItem;

            if (itemA == null || itemB == null) return;
            if (itemA.Tag == itemB.Tag)
            {
                MessageBox.Show("Cannot swap a bone with itself.", "Invalid Swap",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            FacialBoneSwapMap.SwapBones(itemA.Tag, itemB.Tag);
            RefreshActiveSwaps();
        }

        private void RemoveSwapButton_Click(object sender, EventArgs e)
        {
            var selected = ActiveSwapsList.SelectedItem as SwapListItem;
            if (selected == null) return;

            FacialBoneSwapMap.RemoveSwap(selected.BoneA);
            RefreshActiveSwaps();
        }

        private void ClearAllButton_Click(object sender, EventArgs e)
        {
            if (FacialBoneSwapMap.Count > 0)
            {
                var result = MessageBox.Show(
                    "Remove all bone swap mappings?",
                    "Confirm Clear",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    FacialBoneSwapMap.Clear();
                    RefreshActiveSwaps();
                }
            }
        }

        private void ActiveSwapsList_SelectedIndexChanged(object sender, EventArgs e)
        {
            RemoveSwapButton.Enabled = ActiveSwapsList.SelectedItem != null;
        }

        private void EnableSwapCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            FacialBoneSwapMap.Enabled = EnableSwapCheckBox.Checked;
        }

        private void RefreshActiveSwaps()
        {
            ActiveSwapsList.Items.Clear();
            RemoveSwapButton.Enabled = false;

            var swapMap = FacialBoneSwapMap.GetSwapMap();
            var skeleton = GetSelectedSkeleton();

            // Track which pairs we've already shown (since swaps are bidirectional)
            var shownPairs = new HashSet<string>();

            foreach (var kvp in swapMap)
            {
                string nameA = kvp.Key.ToString();
                string nameB = kvp.Value.ToString();

                if (skeleton?.BonesMap != null)
                {
                    if (skeleton.BonesMap.TryGetValue(kvp.Key, out var boneA))
                        nameA = boneA.Name ?? kvp.Key.ToString();
                    if (skeleton.BonesMap.TryGetValue(kvp.Value, out var boneB))
                        nameB = boneB.Name ?? kvp.Value.ToString();
                }

                // Create a canonical pair key to avoid duplicates
                var pairKey = kvp.Key < kvp.Value
                    ? $"{kvp.Key}_{kvp.Value}"
                    : $"{kvp.Value}_{kvp.Key}";

                if (!shownPairs.Contains(pairKey))
                {
                    shownPairs.Add(pairKey);
                    ActiveSwapsList.Items.Add(new SwapListItem
                    {
                        BoneA = kvp.Key,
                        BoneB = kvp.Value,
                        Display = $"{kvp.Key} ({nameA}) <-> {kvp.Value} ({nameB})"
                    });
                }
            }

            EnableSwapCheckBox.Checked = FacialBoneSwapMap.Enabled;
        }

        private Skeleton GetSelectedSkeleton()
        {
            var item = PedSelector.SelectedItem as PedListItem;
            return item?.Ped?.Skeleton;
        }

        /// <summary>
        /// Build a merged BoneTracksDict from all per-component expressions of a ped.
        /// </summary>
        private static Dictionary<ExpressionTrack, ExpressionTrack> BuildMergedBoneTracksDict(Ped ped)
        {
            var merged = new Dictionary<ExpressionTrack, ExpressionTrack>();

            if (ped.Expression?.BoneTracksDict != null)
            {
                foreach (var kvp in ped.Expression.BoneTracksDict)
                {
                    if (!merged.ContainsKey(kvp.Key))
                        merged[kvp.Key] = kvp.Value;
                }
            }

            if (ped.Expressions != null)
            {
                foreach (var expr in ped.Expressions)
                {
                    if (expr?.BoneTracksDict == null) continue;
                    foreach (var kvp in expr.BoneTracksDict)
                    {
                        merged[kvp.Key] = kvp.Value;
                    }
                }
            }

            return merged.Count > 0 ? merged : null;
        }

        private class PedListItem
        {
            public int Index;
            public string Name;
            public Ped Ped;
            public override string ToString() => Name;
        }

        private class BoneListItem
        {
            public ushort Tag;
            public string Name;
            public string Display;
            public override string ToString() => Display;
        }

        private class SwapListItem
        {
            public ushort BoneA;
            public ushort BoneB;
            public string Display;
            public override string ToString() => Display;
        }
    }
}
