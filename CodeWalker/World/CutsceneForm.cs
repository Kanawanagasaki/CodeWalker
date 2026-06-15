using CodeWalker.Export;
using CodeWalker.GameFiles;
using CodeWalker.Rendering;
using CodeWalker.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharpDX;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodeWalker.World
{
    public partial class CutsceneForm : Form
    {
        private WorldForm WorldForm;
        private GameFileCache GameFileCache;
        private AudioDatabase AudioDatabase;

        private Cutscene Cutscene = null;

        private bool AnimateCamera = true;
        private bool EnableSubtitles = true;
        private bool EnableAudio = true;
        private bool Playing = false;
        private bool PositionScrolled = false;
        private float Volume = 0.5f;

        class CutsceneDropdownItem
        {
            public RpfEntry RpfEntry { get; set; }

            public override string ToString()
            {
                return RpfEntry?.Path ?? "";
            }
        }

        public CutsceneForm(WorldForm worldForm)
        {
            WorldForm = worldForm;
            GameFileCache = WorldForm.GameFileCache;
            AudioDatabase = new AudioDatabase();
            InitializeComponent();
        }



        public void UpdateAnimation(float elapsed)
        {
            if (Cutscene != null)
            {
                if (Playing)
                {
                    var newt = Cutscene.PlaybackTime + elapsed;
                    Cutscene.Update(newt);

                    if (Cutscene.PlaybackTime != newt)
                    {
                        if (EnableAudio) // handle looping cutscenes, loop the audio also
                        {
                            BeginInvoke(new Action(() => PlayAudio(Cutscene.PlaybackTime)));
                        }
                    }
                }

                if (AnimateCamera && (Cutscene.CameraObject != null))
                {
                    var pos = Cutscene.CameraObject.Position;
                    var rot = Cutscene.CameraObject.Rotation;

                    WorldForm.SetCameraTransform(pos, rot);

                    if (Cutscene.CameraClipUpdate)
                    {
                        //// disabled this because it seems to be causing some rendering issues
                        //WorldForm.SetCameraClipPlanes(Cutscene.CameraNearClip, Cutscene.CameraFarClip);
                        Cutscene.CameraClipUpdate = false;
                    }
                }

            }
        }

        public void GetVisibleYmaps(Camera camera, Dictionary<MetaHash, YmapFile> ymaps)
        {
            //use a temporary ymap for entities?

            var renderer = WorldForm?.Renderer;
            if (renderer == null) return;

            if (Cutscene == null) return;
            Cutscene.Render(renderer);

        }





        private void SelectCutscene(CutsceneDropdownItem dditem)
        {
            Cursor = Cursors.WaitCursor;
            Task.Run(() =>
            {
                CutFile cutFile = null;
                Cutscene cutscene = null;

                if (GameFileCache.IsInited)
                {
                    if (!AudioDatabase.IsInited)
                    {
                        AudioDatabase.Init(GameFileCache);
                    }

                    var entry = dditem?.RpfEntry as RpfFileEntry;
                    if (entry != null)
                    {

                        cutFile = new CutFile(entry);
                        GameFileCache.RpfMan.LoadFile(cutFile, entry);

                        cutscene = new Cutscene();
                        cutscene.Init(cutFile, GameFileCache, WorldForm, AudioDatabase);

                    }
                }

                CutsceneLoaded(cutscene);

            });
        }
        private void CutsceneLoaded(Cutscene cs)
        {
            if (InvokeRequired)
            {
                try
                {
                    Invoke(new Action(() => { CutsceneLoaded(cs); }));
                }
                catch
                { }
                return;
            }

            DisposeAudio();

            Cutscene = cs;

            Playing = false;
            PlayStopButton.Text = "Play";
            PlaybackTimer.Enabled = false;

            if (cs != null)
            {
                cs.EnableSubtitles = EnableSubtitles;
            }

            LoadTreeView(cs);

            TimeTrackBar.Maximum = (int)(cs.Duration * 10.0f);
            TimeTrackBar.Value = 0;
            UpdateTimeLabel();

            Cursor = Cursors.Default;
        }


        private void LoadTreeView(Cutscene cs)
        {
            CutsceneTreeView.Nodes.Clear();

            var cutFile = cs?.CutFile;
            var cf = cutFile?.CutsceneFile2;
            if (cf != null)
            {
                var csnode = CutsceneTreeView.Nodes.Add(cutFile.FileEntry?.Name);
                csnode.Tag = cs;

                if (cs.SceneObjects != null)
                {
                    var objsnode = csnode.Nodes.Add("Objects");
                    objsnode.Name = "Objects";

                    foreach (var obj in cs.SceneObjects.Values)
                    {
                        var objnode = objsnode.Nodes.Add(obj.ToString());
                        objnode.Tag = obj;
                    }
                }
                if (cf.pCutsceneEventList != null)
                {
                    var evtsnode = csnode.Nodes.Add("Events");
                    evtsnode.Name = "Events";

                    foreach (var evt in cf.pCutsceneEventList)
                    {
                        var evtnode = evtsnode.Nodes.Add(evt.ToString());
                        evtnode.Tag = evt;
                    }
                }
                if (cf.pCutsceneLoadEventList != null)
                {
                    var ldesnode = csnode.Nodes.Add("Load Events");
                    ldesnode.Name = "Load Events";

                    foreach (var lev in cf.pCutsceneLoadEventList)
                    {
                        var ldenode = ldesnode.Nodes.Add(lev.ToString());
                        ldenode.Tag = lev;
                    }
                }



                csnode.Expand();
                CutsceneTreeView.SelectedNode = csnode;
            }

        }



        private void UpdateTimeTrackBar()
        {
            var tim = Cutscene?.PlaybackTime ?? 0.0f;
            var itim = (int)(tim * 10.0f);
            TimeTrackBar.Value = itim;
        }
        private void UpdateTimeLabel()
        {
            var tim = Cutscene?.PlaybackTime ?? 0.0f;
            var dur = Cutscene?.Duration ?? 0.0f;
            TimeLabel.Text = tim.ToString("0.00") + " / " + dur.ToString("0.00");
        }



        private void PlayAudio(float playTime = 0.0f)
        {
            StopAudio();
            if (!EnableAudio) return;
            var sp = Cutscene?.SoundPlayer;
            if (sp != null)
            {
                sp.SetVolume(Volume);
                sp.Play(Cutscene.SoundStartOffset + playTime);
            }
        }
        private void StopAudio()
        {
            var sp = Cutscene?.SoundPlayer;
            if (sp != null)
            {
                sp.Stop();
            }
        }
        private void PauseAudio()
        {
            if (!EnableAudio) return;
            var sp = Cutscene?.SoundPlayer;
            if (sp != null)
            {
                sp.Pause();
            }
        }
        private void ResumeAudio()
        {
            if (!EnableAudio) return;
            var sp = Cutscene?.SoundPlayer;
            if (sp != null)
            {
                sp.Resume();
            }
        }
        private void DisposeAudio()
        {
            var sp = Cutscene?.SoundPlayer;
            if (sp != null)
            {
                sp.Stop();
                sp.DisposeAudio();
            }
        }


        private void CutsceneForm_Load(object sender, EventArgs e)
        {
            if (!GameFileCache.IsInited) return;//what to do here?

            var rpfman = GameFileCache.RpfMan;
            var rpflist = rpfman.AllRpfs; //loadedOnly ? gfc.ActiveMapRpfFiles.Values.ToList() :

            var dditems = new List<CutsceneDropdownItem>();
            foreach (var rpf in rpflist)
            {
                foreach (var entry in rpf.AllEntries)
                {
                    if (entry.NameLower.EndsWith(".cut"))
                    {
                        var dditem = new CutsceneDropdownItem();
                        dditem.RpfEntry = entry;
                        dditems.Add(dditem);
                    }
                }
            }

            CutsceneComboBox.Items.Clear();
            CutsceneComboBox.Items.AddRange(dditems.ToArray());


        }

        private void CutsceneForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            DisposeAudio();
        }

        private void CutsceneForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            WorldForm?.OnCutsceneFormClosed();
        }

        private void CutsceneComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            var item = CutsceneComboBox.SelectedItem as CutsceneDropdownItem;
            SelectCutscene(item);
        }

        private void CutsceneTreeView_AfterSelect(object sender, TreeViewEventArgs e)
        {
            InfoPropertyGrid.SelectedObject = CutsceneTreeView.SelectedNode?.Tag;
        }

        private void AnimateCameraCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            AnimateCamera = AnimateCameraCheckBox.Checked;

            if (!AnimateCamera)
            {
                //WorldForm?.ResetCameraClipPlanes();
            }
            else
            {
                if (Cutscene != null)
                {
                    Cutscene.CameraClipUpdate = true;
                }
            }
        }

        private void SubtitlesCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            EnableSubtitles = SubtitlesCheckBox.Checked;
            if (Cutscene != null)
            {
                Cutscene.EnableSubtitles = EnableSubtitles;
            }
        }

        private void AudioCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            EnableAudio = AudioCheckBox.Checked;
            if (!EnableAudio)
            {
                StopAudio();
            }
            else
            {
                if (Playing && (Cutscene != null))
                {
                    PlayAudio(Cutscene.PlaybackTime);
                }
            }
        }

        private void PlayStopButton_Click(object sender, EventArgs e)
        {
            if (Playing)
            {
                Playing = false;
                PlayStopButton.Text = "Play";
                PlaybackTimer.Enabled = false;
                StopAudio();
            }
            else
            {
                Playing = true;
                PlayStopButton.Text = "Stop";
                PlaybackTimer.Enabled = true;
                PlayAudio(Cutscene.PlaybackTime);
            }
        }

        private void PlaybackTimer_Tick(object sender, EventArgs e)
        {
            if (Playing)
            {
                UpdateTimeTrackBar();
                UpdateTimeLabel();
            }
        }

        private void TimeTrackBar_Scroll(object sender, EventArgs e)
        {
            PositionScrolled = true;

            var t = TimeTrackBar.Value / 10.0f;

            if (Cutscene != null)
            {
                Cutscene.Update(t);

                if (Playing)
                {
                    PlayAudio(t);
                }
            }

            if (!Playing)
            {
                UpdateTimeLabel();
            }
        }

        private void TimeTrackBar_MouseUp(object sender, MouseEventArgs e)
        {
            if (PositionScrolled)
            {
                PositionScrolled = false;
                return;
            }
            PositionScrolled = false;

            var f = Math.Min(Math.Max((e.X - 13.0f) / (TimeTrackBar.Width - 26.0f), 0.0f), 1.0f);
            var t = f * (TimeTrackBar.Maximum / 10.0f);

            if (Cutscene != null)
            {
                Cutscene.Update(t);

                if (Playing)
                {
                    StopAudio();
                    PlayAudio(t);
                }
            }

            if (!Playing)
            {
                UpdateTimeTrackBar();
                UpdateTimeLabel();
            }
        }

        private void VolumeTrackBar_Scroll(object sender, EventArgs e)
        {
            Volume = VolumeTrackBar.Value / 100.0f;
            var sp = Cutscene?.SoundPlayer;
            if (sp != null)
            {
                sp.SetVolume(Volume);
            }
        }

        /// <summary>
        /// Ensure all scene objects have their animation clips resolved and
        /// positions/rotations set. This is normally done by Cutscene.Update()
        /// during playback, but for export we need to do it without playing.
        /// Without this, AnimClip is null and objects have zero positions,
        /// causing exports with no animation data.
        /// </summary>
        private void EnsureCutsceneLoaded()
        {
            if (Cutscene == null) return;

            // Run Update to set AnimClips, positions, and rotations for enabled objects.
            // This replicates what happens during the first frame of playback.
            if (Cutscene.PlaybackTime <= 0.0f && Cutscene.Duration > 0.0f)
            {
                Cutscene.Update(0.0f);
            }

            // For any scene objects that still don't have AnimClips (e.g., objects that
            // appear later in the timeline and haven't been enabled yet), resolve them
            // manually by looking up their AnimHash in the YCD CutsceneMap.
            if (Cutscene.Ycds != null && Cutscene.SceneObjects != null)
            {
                foreach (var ycd in Cutscene.Ycds)
                {
                    if (ycd?.CutsceneMap == null) continue;
                    foreach (var obj in Cutscene.SceneObjects.Values)
                    {
                        if (obj.AnimClip != null || obj.AnimHash == 0) continue;

                        ClipMapEntry cme = null;
                        ycd.CutsceneMap.TryGetValue(obj.AnimHash, out cme);
                        obj.AnimClip = cme;

                        if (obj.Ped != null && cme != null)
                        {
                            obj.Ped.AnimClip = cme;
                        }
                    }
                }
            }
        }

        private void ExportGltfButton_Click(object sender, EventArgs e)
        {
            if (Cutscene == null)
            {
                MessageBox.Show("No cutscene loaded. Please select a cutscene first.", "Export Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Ensure cutscene animation data is loaded before export.
            // Normally, animation clips and object positions are resolved during
            // Cutscene.Update() which is called when the user clicks Play.
            // Without this step, AnimClip is null and positions are zero,
            // resulting in exported models with no animation or wrong positions.
            EnsureCutsceneLoaded();

            if (Cutscene.SceneObjects == null || Cutscene.SceneObjects.Count == 0)
            {
                MessageBox.Show("No objects in the cutscene.", "Export Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Show the object selection dialog
            using (var selDlg = new CutsceneExportSelectionDialog(Cutscene))
            {
                if (selDlg.ShowDialog(this) != DialogResult.OK) return;

                var selectedObjects = selDlg.GetSelectedObjects();
                if (selectedObjects == null || !selectedObjects.Any())
                {
                    MessageBox.Show("No objects selected for export.", "Export Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                using (var sfd = new SaveFileDialog())
                {
                    sfd.Title = "Export Cutscene as glTF/GLB";
                    var csName = Cutscene.CutFile?.FileEntry?.GetShortName() ?? "cutscene";
                    sfd.FileName = csName + ".glb";
                    sfd.Filter = "GLB Binary glTF|*.glb|glTF (embedded)|*.gltf|All files|*.*";
                    sfd.DefaultExt = "glb";
                    sfd.AddExtension = true;

                    if (sfd.ShowDialog(this) != DialogResult.OK) return;

                    try
                    {
                        Cursor = Cursors.WaitCursor;
                        CutsceneGltfExporter.Export(Cutscene, selectedObjects, sfd.FileName);
                        Cursor = Cursors.Default;
                        MessageBox.Show("Export completed successfully!\n\nFile: " + sfd.FileName,
                            "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        Cursor = Cursors.Default;
                        MessageBox.Show("Export failed:\n\n" + ex.Message + "\n\n" + ex.StackTrace,
                            "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void ExportDebugDataButton_Click(object sender, EventArgs e)
        {
            if (Cutscene == null)
            {
                MessageBox.Show("No cutscene loaded. Please select a cutscene first.", "Export Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            EnsureCutsceneLoaded();

            if (Cutscene.SceneObjects == null || Cutscene.SceneObjects.Count == 0)
            {
                MessageBox.Show("No objects in the cutscene.", "Export Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Show the object selection dialog (reuse the same one as glTF export)
            using (var selDlg = new CutsceneExportSelectionDialog(Cutscene))
            {
                if (selDlg.ShowDialog(this) != DialogResult.OK) return;

                var selectedObjects = selDlg.GetSelectedObjects();
                if (selectedObjects == null || !selectedObjects.Any())
                {
                    MessageBox.Show("No objects selected for export.", "Export Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                using (var sfd = new SaveFileDialog())
                {
                    sfd.Title = "Export Cutscene Debug Data as JSON";
                    var csName = Cutscene.CutFile?.FileEntry?.GetShortName() ?? "cutscene";
                    sfd.FileName = csName + "_debug.json";
                    sfd.Filter = "JSON files|*.json|All files|*.*";
                    sfd.DefaultExt = "json";
                    sfd.AddExtension = true;

                    if (sfd.ShowDialog(this) != DialogResult.OK) return;

                    try
                    {
                        Cursor = Cursors.WaitCursor;
                        ExportDebugData(selectedObjects, sfd.FileName);
                        Cursor = Cursors.Default;
                        MessageBox.Show("Debug export completed successfully!\n\nFile: " + sfd.FileName,
                            "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        Cursor = Cursors.Default;
                        MessageBox.Show("Debug export failed:\n\n" + ex.Message + "\n\n" + ex.StackTrace,
                            "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private BoneSwapDebugPanel _boneSwapPanel;

        private void BoneDebugButton_Click(object sender, EventArgs e)
        {
            if (Cutscene == null)
            {
                MessageBox.Show("No cutscene loaded. Please select a cutscene first.", "Bone Debug",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            EnsureCutsceneLoaded();

            if (Cutscene.SceneObjects == null || Cutscene.SceneObjects.Count == 0)
            {
                MessageBox.Show("No objects in the cutscene.", "Bone Debug",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            bool hasPed = Cutscene.SceneObjects.Values.Any(o => o.Ped != null);
            if (!hasPed)
            {
                MessageBox.Show("No ped objects in the cutscene. Bone swapping only applies to ped facial bones.", "Bone Debug",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // If panel is already open, just bring it to front
            if (_boneSwapPanel != null && !_boneSwapPanel.IsDisposed)
            {
                _boneSwapPanel.Activate();
                return;
            }

            _boneSwapPanel = new BoneSwapDebugPanel(Cutscene);
            _boneSwapPanel.FormClosed += (s, args) => { _boneSwapPanel = null; };
            _boneSwapPanel.Show(this); // Non-modal: allows camera/cutscene interaction
        }

        /// <summary>
        /// Export all cutscene animation data as a JSON file for debugging facial expressions
        /// and bone animations. Uses Newtonsoft.Json for proper serialization.
        /// </summary>
        private void ExportDebugData(IEnumerable<CutsceneObject> selectedObjects, string filePath)
        {
            var root = new JObject();

            // --- Cutscene metadata ---
            var csMeta = new JObject();
            csMeta["name"] = Cutscene.CutFile?.FileEntry?.GetShortName() ?? "unknown";
            csMeta["duration"] = Cutscene.Duration;
            csMeta["position"] = new JArray(Cutscene.Position.X, Cutscene.Position.Y, Cutscene.Position.Z);
            csMeta["rotation"] = new JArray(Cutscene.Rotation.X, Cutscene.Rotation.Y, Cutscene.Rotation.Z, Cutscene.Rotation.W);

            if (Cutscene.CameraCutList != null)
            {
                var cutArr = new JArray();
                foreach (var t in Cutscene.CameraCutList) cutArr.Add(t);
                csMeta["cameraCutList"] = cutArr;
            }

            if (Cutscene.Ycds != null)
            {
                var ycdArr = new JArray();
                foreach (var ycd in Cutscene.Ycds) ycdArr.Add(ycd?.Name ?? null);
                csMeta["ycdFiles"] = ycdArr;
            }

            root["cutscene"] = csMeta;

            // --- Static object info ---
            var objList = selectedObjects.ToList();
            var objectsArr = new JArray();

            foreach (var obj in objList)
            {
                var objJson = new JObject();
                objJson["objectId"] = obj.ObjectID;
                objJson["nameHash"] = obj.Name.Hash;
                objJson["animHash"] = obj.AnimHash.Hash;
                objJson["enabled"] = obj.Enabled != false;

                string objType = obj.Ped != null ? "Ped" : obj.Prop != null ? "Prop" : obj.Vehicle != null ? "Vehicle" : obj.Weapon != null ? "Weapon" : "Other";
                objJson["type"] = objType;

                if (obj.Ped != null)
                {
                    var ped = obj.Ped;
                    objJson["pedName"] = ped.Name ?? "";
                    objJson["pedNameHash"] = ped.NameHash.Hash;

                    // Skeleton bones
                    var skeleton = ped.Skeleton;
                    if (skeleton?.BonesMap != null)
                    {
                        var skelJson = new JObject();
                        skelJson["boneCount"] = skeleton.BonesMap.Count;

                        var bonesArr = new JArray();
                        foreach (var bone in skeleton.BonesMap.Values.OrderBy(b => b.Tag))
                        {
                            var bJson = new JObject();
                            bJson["tag"] = bone.Tag;
                            bJson["name"] = bone.Name ?? "";
                            bJson["index"] = bone.Index;
                            bJson["parentIndex"] = bone.ParentIndex;
                            bJson["flags"] = (uint)bone.Flags;
                            bJson["bindTranslation"] = new JArray(bone.Translation.X, bone.Translation.Y, bone.Translation.Z);
                            bJson["bindRotation"] = new JArray(bone.Rotation.X, bone.Rotation.Y, bone.Rotation.Z, bone.Rotation.W);
                            bJson["bindScale"] = new JArray(bone.Scale.X, bone.Scale.Y, bone.Scale.Z);
                            bonesArr.Add(bJson);
                        }
                        skelJson["bones"] = bonesArr;

                        var tagToName = new JObject();
                        foreach (var kv in skeleton.BonesMap.OrderBy(kv => kv.Key))
                        {
                            tagToName[kv.Key.ToString()] = kv.Value.Name ?? "";
                        }
                        skelJson["boneTagToName"] = tagToName;

                        objJson["skeleton"] = skelJson;
                    }
                    else
                    {
                        objJson["skeleton"] = null;
                    }

                    // Global expression
                    objJson["globalExpression"] = ped.Expression != null
                        ? BuildExpressionJson(ped.Expression)
                        : null;

                    // Component expressions
                    var compExprArr = new JArray();
                    string[] compNames = { "Head", "Berd", "Hair", "Uppr", "Lowr", "Hand", "Feet", "Teff", "Accs", "Task", "Decl", "Jbib" };
                    for (int ci = 0; ci < ped.Expressions.Length; ci++)
                    {
                        var expr = ped.Expressions[ci];
                        if (expr == null) continue;

                        var ceJson = new JObject();
                        ceJson["componentIndex"] = ci;
                        ceJson["componentName"] = ci < compNames.Length ? compNames[ci] : $"Comp{ci}";
                        ceJson["drawableName"] = ped.DrawableNames?[ci] ?? "";
                        ceJson["expressionName"] = expr.Name?.Value ?? "";
                        ceJson["expressionNameHash"] = expr.NameHash.Hash;
                        MergeExpressionTracksJson(ceJson, expr);
                        compExprArr.Add(ceJson);
                    }
                    objJson["componentExpressions"] = compExprArr;

                    // Merged BoneTracksDict
                    var mergedDict = BuildMergedBoneTracksDict(ped);
                    var mergedArr = new JArray();
                    if (mergedDict != null)
                    {
                        foreach (var kvp in mergedDict)
                        {
                            var mJson = new JObject();
                            mJson["from"] = new JObject { ["boneId"] = kvp.Key.BoneId, ["track"] = kvp.Key.Track, ["flags"] = kvp.Key.Flags };
                            mJson["to"] = new JObject { ["boneId"] = kvp.Value.BoneId, ["track"] = kvp.Value.Track, ["flags"] = kvp.Value.Flags };

                            if (ped.Skeleton?.BonesMap != null && ped.Skeleton.BonesMap.TryGetValue(kvp.Value.BoneId, out var toBone))
                                mJson["toBoneName"] = toBone.Name ?? "";

                            mergedArr.Add(mJson);
                        }
                    }
                    objJson["mergedBoneTracksDict"] = mergedArr;

                    // Animation clip static info
                    var animClip = obj.AnimClip ?? ped.AnimClip;
                    if (animClip != null)
                    {
                        var acJson = new JObject();
                        acJson["hash"] = animClip.Hash.Hash;
                        acJson["clipType"] = animClip.Clip?.GetType().Name ?? "null";

                        var subAnims = GetSubAnimations(animClip);
                        acJson["subAnimationCount"] = subAnims.Count;

                        var subArr = new JArray();
                        foreach (var sa in subAnims)
                        {
                            var saJson = new JObject();
                            saJson["startTime"] = sa.StartTime;
                            saJson["endTime"] = sa.EndTime;
                            saJson["duration"] = sa.Animation.Duration;
                            saJson["frames"] = sa.Animation.Frames;
                            saJson["sequenceFrameLimit"] = sa.Animation.SequenceFrameLimit;

                            var boneIds = sa.Animation.BoneIds?.data_items;
                            if (boneIds != null)
                            {
                                saJson["boneIdCount"] = boneIds.Length;
                                var bidArr = new JArray();
                                foreach (var bid in boneIds)
                                {
                                    var bidJson = new JObject();
                                    bidJson["boneId"] = bid.BoneId;
                                    bidJson["track"] = bid.Track;
                                    bidJson["trackName"] = GetTrackName(bid.Track);
                                    bidJson["unk0"] = bid.Unk0;

                                    ushort effectiveBoneId = bid.BoneId;
                                    if (bid.Track == 24 || bid.Track == 25 || bid.Track == 26)
                                    {
                                        if (mergedDict != null)
                                        {
                                            var exprbt = new ExpressionTrack() { BoneId = bid.BoneId, Track = bid.Track, Flags = bid.Unk0 };
                                            if (mergedDict.TryGetValue(exprbt, out var mapped))
                                                effectiveBoneId = mapped.BoneId;
                                        }
                                    }
                                    bidJson["effectiveBoneId"] = effectiveBoneId;

                                    string boneName = "";
                                    if (ped.Skeleton?.BonesMap != null)
                                    {
                                        ushort lookupId = (bid.Track == 24 || bid.Track == 25 || bid.Track == 26) ? effectiveBoneId : bid.BoneId;
                                        if (ped.Skeleton.BonesMap.TryGetValue(lookupId, out var b))
                                            boneName = b.Name ?? "";
                                    }
                                    bidJson["boneName"] = boneName;

                                    bidArr.Add(bidJson);
                                }
                                saJson["boneIds"] = bidArr;
                            }
                            else
                            {
                                saJson["boneIdCount"] = 0;
                                saJson["boneIds"] = new JArray();
                            }

                            subArr.Add(saJson);
                        }
                        acJson["subAnimations"] = subArr;
                        objJson["animClip"] = acJson;
                    }
                    else
                    {
                        objJson["animClip"] = null;
                    }
                }

                objJson["position"] = new JArray(0, 0, 0);
                objJson["rotation"] = new JArray(0, 0, 0, 1);
                objectsArr.Add(objJson);
            }
            root["objects"] = objectsArr;

            // --- Frame data ---
            float duration = Cutscene.Duration;
            float fps = 30.0f;
            int totalFrames = (int)Math.Ceiling(duration * fps);
            if (totalFrames < 1) totalFrames = 1;
            float frameDelta = duration / totalFrames;

            var framesArr = new JArray();

            for (int frameIdx = 0; frameIdx <= totalFrames; frameIdx++)
            {
                float time = Math.Min(frameIdx * frameDelta, duration);
                var frameJson = new JObject();
                frameJson["time"] = time;
                frameJson["frameIndex"] = frameIdx;

                // Camera data
                var camObj = Cutscene.CameraObject;
                if (camObj != null)
                {
                    var camJson = new JObject();
                    camJson["position"] = new JArray(camObj.Position.X, camObj.Position.Y, camObj.Position.Z);
                    camJson["rotation"] = new JArray(camObj.Rotation.X, camObj.Rotation.Y, camObj.Rotation.Z, camObj.Rotation.W);
                    frameJson["camera"] = camJson;
                }

                // Camera cut info
                int cutIndex = 0;
                float cutStart = 0.0f;
                for (cutIndex = 0; cutIndex < Cutscene.CameraCutList?.Length; cutIndex++)
                {
                    if (Cutscene.CameraCutList[cutIndex] > time) break;
                    cutStart = Cutscene.CameraCutList[cutIndex];
                }
                float cutOffset = time - cutStart;

                frameJson["cutIndex"] = cutIndex;
                frameJson["cutOffset"] = cutOffset;

                // Per-object frame data
                var frameObjs = new JArray();
                foreach (var obj in objList)
                {
                    var foJson = new JObject();
                    foJson["objectId"] = obj.ObjectID;
                    foJson["nameHash"] = obj.Name.Hash;
                    foJson["enabled"] = obj.Enabled != false;

                    // Evaluate object position/rotation
                    var ycd = (cutIndex < (Cutscene.Ycds?.Length ?? 0)) ? Cutscene.Ycds[cutIndex] : null;
                    ClipMapEntry cme = null;
                    ycd?.CutsceneMap?.TryGetValue(obj.AnimHash, out cme);

                    Vector3 objPos = Vector3.Zero;
                    Quaternion objRot = Quaternion.Identity;

                    if (cme != null)
                    {
                        EvaluateClipTransform(cme, cutOffset, 0, 5, 6, ref objPos, ref objRot);
                        var worldPos = Cutscene.Position + Cutscene.Rotation.Multiply(objPos);
                        var worldRot = Cutscene.Rotation * objRot;
                        objPos = worldPos;
                        objRot = worldRot;
                    }

                    foJson["position"] = new JArray(objPos.X, objPos.Y, objPos.Z);
                    foJson["rotation"] = new JArray(objRot.X, objRot.Y, objRot.Z, objRot.W);

                    // Ped-specific bone tracks
                    if (obj.Ped != null)
                    {
                        var ped = obj.Ped;
                        var animClip = obj.AnimClip ?? ped.AnimClip;
                        var mergedDict2 = BuildMergedBoneTracksDict(ped);

                        var boneTracksArr = new JArray();

                        if (animClip != null)
                        {
                            var subAnims = GetSubAnimations(animClip);
                            foreach (var subAnim in subAnims)
                            {
                                var animData = subAnim.Animation;
                                var boneIds = animData.BoneIds?.data_items;
                                if (boneIds == null) continue;

                                float t = GetSubAnimPlaybackTimeDebug(cutOffset, subAnim.StartTime, subAnim.EndTime);
                                var fp = animData.GetFramePosition(t);

                                for (int bi = 0; bi < boneIds.Length; bi++)
                                {
                                    var bid = boneIds[bi];
                                    var btJson = new JObject();
                                    btJson["boneId"] = bid.BoneId;
                                    btJson["track"] = bid.Track;
                                    btJson["trackName"] = GetTrackName(bid.Track);
                                    btJson["unk0"] = bid.Unk0;

                                    ushort effectiveBoneId = bid.BoneId;
                                    bool wasRemapped = false;

                                    if (bid.Track == 24 || bid.Track == 25 || bid.Track == 26)
                                    {
                                        if (mergedDict2 != null)
                                        {
                                            var exprbt = new ExpressionTrack() { BoneId = bid.BoneId, Track = bid.Track, Flags = bid.Unk0 };
                                            if (mergedDict2.TryGetValue(exprbt, out var mapped))
                                            {
                                                wasRemapped = true;
                                                effectiveBoneId = mapped.BoneId;
                                            }
                                        }
                                    }
                                    btJson["effectiveBoneId"] = effectiveBoneId;
                                    btJson["wasRemapped"] = wasRemapped;

                                    string boneName = "";
                                    if (ped.Skeleton?.BonesMap != null)
                                    {
                                        ushort lookupId = (bid.Track == 24 || bid.Track == 25 || bid.Track == 26) ? effectiveBoneId : bid.BoneId;
                                        if (ped.Skeleton.BonesMap.TryGetValue(lookupId, out var b))
                                            boneName = b.Name ?? "";
                                    }
                                    btJson["boneName"] = boneName;

                                    try
                                    {
                                        if (bid.Track == 1 || bid.Track == 26)
                                        {
                                            var q = animData.EvaluateQuaternion(fp, bi, true);
                                            btJson["valueType"] = "quaternion";
                                            btJson["value"] = new JArray(q.X, q.Y, q.Z, q.W);

                                            if (bid.Track == 26 && ped.Skeleton?.BonesMap != null && ped.Skeleton.BonesMap.TryGetValue(effectiveBoneId, out var bone))
                                            {
                                                var animRot = bone.Rotation * q;
                                                btJson["finalRotation"] = new JArray(animRot.X, animRot.Y, animRot.Z, animRot.W);
                                                btJson["bindRotation"] = new JArray(bone.Rotation.X, bone.Rotation.Y, bone.Rotation.Z, bone.Rotation.W);
                                                var diffQ = Quaternion.Invert(bone.Rotation) * animRot;
                                                btJson["deltaFromBind"] = new JArray(diffQ.X, diffQ.Y, diffQ.Z, diffQ.W);
                                            }
                                            else
                                            {
                                                btJson["finalRotation"] = null;
                                            }
                                        }
                                        else if (bid.Track == 25)
                                        {
                                            var v4 = animData.EvaluateVector4(fp, bi, true);
                                            float mult = -0.314159265f;
                                            var q = Quaternion.RotationYawPitchRoll(v4.Z * mult, v4.Y * mult, v4.X * mult);
                                            btJson["valueType"] = "eulerFaceRot";
                                            btJson["rawValue"] = new JArray(v4.X, v4.Y, v4.Z, v4.W);
                                            btJson["convertedQuaternion"] = new JArray(q.X, q.Y, q.Z, q.W);

                                            if (ped.Skeleton?.BonesMap != null && ped.Skeleton.BonesMap.TryGetValue(effectiveBoneId, out var bone))
                                            {
                                                var animRot = bone.Rotation * q;
                                                btJson["finalRotation"] = new JArray(animRot.X, animRot.Y, animRot.Z, animRot.W);
                                                btJson["bindRotation"] = new JArray(bone.Rotation.X, bone.Rotation.Y, bone.Rotation.Z, bone.Rotation.W);
                                                var diffQ = Quaternion.Invert(bone.Rotation) * animRot;
                                                btJson["deltaFromBind"] = new JArray(diffQ.X, diffQ.Y, diffQ.Z, diffQ.W);
                                            }
                                            else
                                            {
                                                btJson["finalRotation"] = null;
                                            }
                                        }
                                        else if (bid.Track == 24)
                                        {
                                            var v4 = animData.EvaluateVector4(fp, bi, true);
                                            btJson["valueType"] = "faceTranslation";
                                            btJson["rawValue"] = new JArray(v4.X, v4.Y, v4.Z, v4.W);

                                            if (ped.Skeleton?.BonesMap != null && ped.Skeleton.BonesMap.TryGetValue(effectiveBoneId, out var bone))
                                            {
                                                var fv = new Vector3(0, v4.X * 0.005f, 0);
                                                var animTrans = bone.Translation + bone.Rotation.Multiply(fv);
                                                btJson["finalTranslation"] = new JArray(animTrans.X, animTrans.Y, animTrans.Z);
                                                btJson["bindTranslation"] = new JArray(bone.Translation.X, bone.Translation.Y, bone.Translation.Z);
                                                var delta = animTrans - bone.Translation;
                                                btJson["deltaFromBind"] = new JArray(delta.X, delta.Y, delta.Z);
                                            }
                                            else
                                            {
                                                btJson["finalTranslation"] = null;
                                            }
                                        }
                                        else
                                        {
                                            var v4 = animData.EvaluateVector4(fp, bi, true);
                                            btJson["valueType"] = "vector";
                                            btJson["value"] = new JArray(v4.X, v4.Y, v4.Z, v4.W);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        btJson["valueType"] = "error";
                                        btJson["error"] = ex.Message;
                                    }

                                    boneTracksArr.Add(btJson);
                                }
                            }
                        }
                        foJson["boneTracks"] = boneTracksArr;

                        // Full skeleton animated state
                        var skelStatesArr = new JArray();
                        if (ped.Skeleton?.BonesMap != null && animClip != null)
                        {
                            var boneStates = EvaluateFullSkeleton(ped, animClip, cutOffset, mergedDict2);
                            foreach (var bs in boneStates.OrderBy(b => b.Tag))
                            {
                                var bsJson = new JObject();
                                bsJson["tag"] = bs.Tag;
                                bsJson["name"] = bs.Name;
                                bsJson["animTranslation"] = new JArray(bs.AnimTranslation.X, bs.AnimTranslation.Y, bs.AnimTranslation.Z);
                                bsJson["animRotation"] = new JArray(bs.AnimRotation.X, bs.AnimRotation.Y, bs.AnimRotation.Z, bs.AnimRotation.W);
                                bsJson["animScale"] = new JArray(bs.AnimScale.X, bs.AnimScale.Y, bs.AnimScale.Z);
                                skelStatesArr.Add(bsJson);
                            }
                        }
                        foJson["skeletonBoneStates"] = skelStatesArr;
                    }

                    frameObjs.Add(foJson);
                }
                frameJson["objects"] = frameObjs;
                framesArr.Add(frameJson);
            }
            root["frames"] = framesArr;

            // Write with proper formatting
            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Include
            };
            var jsonStr = JsonConvert.SerializeObject(root, settings);
            File.WriteAllText(filePath, jsonStr, Encoding.UTF8);
        }

        /// <summary>
        /// Build a JObject representing an Expression's static data.
        /// </summary>
        private JObject BuildExpressionJson(Expression expr)
        {
            var json = new JObject();
            json["name"] = expr.Name?.Value ?? "";
            json["nameHash"] = expr.NameHash.Hash;
            MergeExpressionTracksJson(json, expr);
            return json;
        }

        /// <summary>
        /// Merge expression track data (Tracks, BoneTracksDict, etc.) into an existing JObject.
        /// </summary>
        private static void MergeExpressionTracksJson(JObject json, Expression expr)
        {
            var tracks = expr.Tracks?.data_items;
            json["trackCount"] = tracks?.Length ?? 0;

            var tracksArr = new JArray();
            if (tracks != null)
            {
                foreach (var t in tracks)
                {
                    var tJson = new JObject();
                    tJson["boneId"] = t.BoneId;
                    tJson["track"] = t.Track;
                    tJson["flags"] = t.Flags;
                    tJson["format"] = t.Format;
                    tJson["unkFlag"] = t.UnkFlag;
                    tracksArr.Add(tJson);
                }
            }
            json["tracks"] = tracksArr;

            var btdArr = new JArray();
            if (expr.BoneTracksDict != null)
            {
                foreach (var kvp in expr.BoneTracksDict)
                {
                    var mJson = new JObject();
                    mJson["from"] = new JObject { ["boneId"] = kvp.Key.BoneId, ["track"] = kvp.Key.Track, ["flags"] = kvp.Key.Flags };
                    mJson["to"] = new JObject { ["boneId"] = kvp.Value.BoneId, ["track"] = kvp.Value.Track, ["flags"] = kvp.Value.Flags };
                    btdArr.Add(mJson);
                }
            }
            json["boneTracksDict"] = btdArr;

            json["streamCount"] = expr.Streams?.data_items?.Length ?? 0;

            var vars = expr.Variables?.data_items;
            var varsArr = new JArray();
            if (vars != null)
            {
                foreach (var v in vars) varsArr.Add(v.Hash);
            }
            json["variables"] = varsArr;
            json["signature"] = expr.Signature;
            json["unknown7Ch"] = expr.Unknown_7Ch;
        }

        /// <summary>
        /// Build a merged BoneTracksDict from all per-component expressions of a ped.
        /// Identical to CutsceneGltfExporter.BuildMergedBoneTracksDict.
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

        /// <summary>
        /// Get the list of sub-animations from a ClipMapEntry.
        /// </summary>
        private static List<(Animation Animation, float StartTime, float EndTime)> GetSubAnimations(ClipMapEntry animClip)
        {
            var result = new List<(Animation Animation, float StartTime, float EndTime)>();
            if (animClip?.Clip == null) return result;

            if (animClip.Clip is ClipAnimation clipAnim)
            {
                if (clipAnim.Animation != null)
                    result.Add((clipAnim.Animation, clipAnim.StartTime, clipAnim.EndTime));
            }
            else if (animClip.Clip is ClipAnimationList clipList && clipList.Animations != null)
            {
                foreach (var canim in clipList.Animations)
                {
                    if (canim?.Animation != null)
                        result.Add((canim.Animation, canim.StartTime, canim.EndTime));
                }
            }

            return result;
        }

        /// <summary>
        /// Evaluate object position and rotation from a clip at a given cut offset time.
        /// </summary>
        private static void EvaluateClipTransform(ClipMapEntry cme, float cutOffset, ushort boneTag, byte posTrack, byte rotTrack, ref Vector3 pos, ref Quaternion rot)
        {
            if (cme?.Clip == null) return;

            if (cme.Clip is ClipAnimation canim && canim.Animation != null)
            {
                float t = GetSubAnimPlaybackTimeDebug(cutOffset, canim.StartTime, canim.EndTime);
                var fp = canim.Animation.GetFramePosition(t);
                var pi = canim.Animation.FindBoneIndex(boneTag, posTrack);
                var ri = canim.Animation.FindBoneIndex(boneTag, rotTrack);
                if (pi >= 0) pos = canim.Animation.EvaluateVector4(fp, pi, true).XYZ();
                if (ri >= 0) rot = canim.Animation.EvaluateQuaternion(fp, ri, true);
            }
            else if (cme.Clip is ClipAnimationList alist && alist.Animations?.Data != null)
            {
                foreach (var anim in alist.Animations.Data)
                {
                    if (anim?.Animation == null) continue;
                    float t = GetSubAnimPlaybackTimeDebug(cutOffset, anim.StartTime, anim.EndTime);
                    var fp = anim.Animation.GetFramePosition(t);
                    var pi = anim.Animation.FindBoneIndex(boneTag, posTrack);
                    var ri = anim.Animation.FindBoneIndex(boneTag, rotTrack);
                    if (pi >= 0) pos = anim.Animation.EvaluateVector4(fp, pi, true).XYZ();
                    if (ri >= 0) rot = anim.Animation.EvaluateQuaternion(fp, ri, true);
                }
            }
        }

        /// <summary>
        /// Evaluate the full skeleton bone transforms at a given cut offset time,
        /// replicating the renderer's animation application logic.
        /// </summary>
        private List<BoneAnimState> EvaluateFullSkeleton(Ped ped, ClipMapEntry animClip, float cutOffset, Dictionary<ExpressionTrack, ExpressionTrack> mergedDict)
        {
            var result = new List<BoneAnimState>();
            var skeleton = ped.Skeleton;
            if (skeleton?.BonesMap == null) return result;

            var subAnims = GetSubAnimations(animClip);

            // Initialize all bones with bind pose
            var boneStates = new Dictionary<ushort, BoneAnimState>();
            foreach (var bone in skeleton.BonesMap.Values)
            {
                boneStates[bone.Tag] = new BoneAnimState
                {
                    Tag = bone.Tag,
                    Name = bone.Name ?? "",
                    AnimTranslation = bone.Translation,
                    AnimRotation = bone.Rotation,
                    AnimScale = bone.Scale
                };
            }

            // Apply each sub-animation's tracks sequentially (last writer wins)
            foreach (var subAnim in subAnims)
            {
                var animData = subAnim.Animation;
                var boneIds = animData.BoneIds?.data_items;
                if (boneIds == null) continue;

                float t = GetSubAnimPlaybackTimeDebug(cutOffset, subAnim.StartTime, subAnim.EndTime);
                var fp = animData.GetFramePosition(t);

                for (int bi = 0; bi < boneIds.Length; bi++)
                {
                    var bid = boneIds[bi];
                    ushort effectiveBoneId = bid.BoneId;

                    // Apply expression remapping for facial tracks
                    if (bid.Track == 24 || bid.Track == 25 || bid.Track == 26)
                    {
                        if (mergedDict != null)
                        {
                            var exprbt = new ExpressionTrack() { BoneId = bid.BoneId, Track = bid.Track, Flags = bid.Unk0 };
                            if (mergedDict.TryGetValue(exprbt, out var mapped))
                                effectiveBoneId = mapped.BoneId;
                        }
                    }

                    // Only process if this bone exists in the skeleton
                    if (!boneStates.TryGetValue(effectiveBoneId, out var state)) continue;
                    Bone bone = null;
                    skeleton.BonesMap.TryGetValue(effectiveBoneId, out bone);

                    try
                    {
                        if (bid.Track == 0) // Translation
                        {
                            var v4 = animData.EvaluateVector4(fp, bi, true);
                            state.AnimTranslation = new Vector3(v4.X, v4.Y, v4.Z);
                        }
                        else if (bid.Track == 1) // Rotation
                        {
                            state.AnimRotation = animData.EvaluateQuaternion(fp, bi, true);
                        }
                        else if (bid.Track == 2) // Scale
                        {
                            var v4 = animData.EvaluateVector4(fp, bi, true);
                            state.AnimScale = new Vector3(v4.X, v4.Y, v4.Z);
                        }
                        else if (bid.Track == 24 && bone != null) // Face translation
                        {
                            var v4 = animData.EvaluateVector4(fp, bi, true);
                            var fv = new Vector3(0, v4.X * 0.005f, 0);
                            state.AnimTranslation = bone.Translation + bone.Rotation.Multiply(fv);
                        }
                        else if (bid.Track == 25 && bone != null) // Face rotation (Euler)
                        {
                            var v4 = animData.EvaluateVector4(fp, bi, true);
                            float mult = -0.314159265f;
                            var q = Quaternion.RotationYawPitchRoll(v4.Z * mult, v4.Y * mult, v4.X * mult);
                            state.AnimRotation = bone.Rotation * q;
                        }
                        else if (bid.Track == 26 && bone != null) // Face rotation (Quaternion)
                        {
                            var q = animData.EvaluateQuaternion(fp, bi, true);
                            state.AnimRotation = bone.Rotation * q;
                        }
                    }
                    catch { }
                }
            }

            // Copy ThighRoll bones (replicates renderer hack)
            ushort SKEL_L_Thigh = 42773;
            ushort SKEL_R_Thigh = 51857;
            ushort RB_L_ThighRoll = 24617;
            ushort RB_R_ThighRoll = 30728;

            if (boneStates.TryGetValue(SKEL_L_Thigh, out var lThigh) && boneStates.TryGetValue(RB_L_ThighRoll, out var lThighRoll))
                lThighRoll.AnimRotation = lThigh.AnimRotation;
            if (boneStates.TryGetValue(SKEL_R_Thigh, out var rThigh) && boneStates.TryGetValue(RB_R_ThighRoll, out var rThighRoll))
                rThighRoll.AnimRotation = rThigh.AnimRotation;

            return boneStates.Values.ToList();
        }

        private class BoneAnimState
        {
            public ushort Tag;
            public string Name;
            public Vector3 AnimTranslation;
            public Quaternion AnimRotation;
            public Vector3 AnimScale;
        }

        /// <summary>
        /// Calculate animation playback time from cut offset, replicating
        /// GltfWriter.GetSubAnimPlaybackTime logic.
        /// </summary>
        private static float GetSubAnimPlaybackTimeDebug(float clipTime, float startTime, float endTime)
        {
            double duration = endTime - startTime;
            if (duration <= 0) return clipTime;
            double curpos = clipTime % duration;
            return startTime + (float)curpos;
        }

        /// <summary>
        /// Get a human-readable name for an animation track number.
        /// </summary>
        private static string GetTrackName(byte track)
        {
            switch (track)
            {
                case 0: return "Translation";
                case 1: return "Rotation";
                case 2: return "Scale";
                case 5: return "RootPosition";
                case 6: return "RootRotation";
                case 7: return "CameraPosition";
                case 8: return "CameraRotation";
                case 24: return "FaceTranslation";
                case 25: return "FaceRotationEuler";
                case 26: return "FaceRotationQuaternion";
                default: return $"Unknown_{track}";
            }
        }

    }


    [TypeConverter(typeof(ExpandableObjectConverter))] public class Cutscene
    {
        public CutFile CutFile { get; set; } = null;
        private GameFileCache GameFileCache = null;
        private WorldForm WorldForm = null;
        private AudioDatabase AudioDB = null;

        public float[] CameraCutList { get; set; } = null;
        public YcdFile[] Ycds { get; set; } = null;


        public float Duration { get; set; } = 0.0f;
        public float PlaybackTime { get; set; } = 0.0f;
        private bool Seeking = false;

        public bool EnableSubtitles { get; set; } = true;


        public Dictionary<int, CutObject> Objects { get; set; } = null;
        public Dictionary<int, CutsceneObject> SceneObjects { get; set; } = null;
        public CutEvent[] LoadEvents { get; set; } = null;
        public CutEvent[] PlayEvents { get; set; } = null;
        public CutConcatData[] ConcatDatas { get; set; } = null;

        public int NextLoadEvent { get; set; } = 0;
        public int NextPlayEvent { get; set; } = 0;
        public int NextCameraCut { get; set; } = 0;
        public int NextConcatData { get; set; } = 0;

        public Gxt2File Gxt2File { get; set; } = null;

        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }


        public CutsceneObject CameraObject = null;
        public float CameraNearClip { get; set; } = 0.5f;
        public float CameraFarClip { get; set; } = 12000.0f;
        public bool CameraClipUpdate = false;//signal to the form to update the camera clip planes
        public Quaternion CameraRotationOffset = Quaternion.RotationAxis(Vector3.UnitX, -1.57079632679f) * Quaternion.RotationAxis(Vector3.UnitZ, 3.141592653f);

        public AudioPlayer SoundPlayer { get; set; } = null;
        public float SoundStartOffset { get; set; }



        public void Init(CutFile cutFile, GameFileCache gfc, WorldForm wf, AudioDatabase adb)
        {
            CutFile = cutFile;
            GameFileCache = gfc;
            WorldForm = wf;
            AudioDB = adb;

            var csf = cutFile?.CutsceneFile2;
            if (csf == null) return;
            if (gfc == null) return;


            Duration = csf.fTotalDuration;
            CameraCutList = csf.cameraCutList;
            Position = csf.vOffset;
            Rotation = Quaternion.RotationAxis(Vector3.UnitZ, csf.fRotation);
            Objects = csf.ObjectsDict;
            LoadEvents = RecastArray<CutEvent>(csf.pCutsceneLoadEventList);
            PlayEvents = RecastArray<CutEvent>(csf.pCutsceneEventList);
            ConcatDatas = csf.concatDataList;


            LoadYcds();
            CreateSceneObjects();
            RaiseEvents(0.0f);
        }

        private void LoadYcds()
        {
            int cutListCount = (CameraCutList?.Length ?? 0) + 1;
            var shortName = CutFile.FileEntry?.GetShortNameLower() ?? "";
            Ycds = new YcdFile[cutListCount];
            if (!string.IsNullOrEmpty(shortName))
            {
                for (int i = 0; i < cutListCount; i++)
                {
                    var ycdname = shortName + "-" + i.ToString();
                    var ycdhash = JenkHash.GenHash(ycdname);
                    var ycd = GameFileCache.GetYcd(ycdhash);
                    while ((ycd != null) && (!ycd.Loaded))
                    {
                        Thread.Sleep(1);//bite me
                        ycd = GameFileCache.GetYcd(ycdhash);
                    }
                    if (ycd != null)
                    {
                        ycd.BuildCutsceneMap(i);
                    }
                    Ycds[i] = ycd;
                }
            }
        }


        public void Update(float newTime)
        {
            if (newTime > Duration)
            {
                newTime = 0.0f; //stop or loop?
            }

            if (newTime >= PlaybackTime)
            {
                RaiseEvents(newTime);
            }
            else
            {
                //reset playback to beginning, and seek to newTime
                Seeking = true;
                RaiseEvents(Duration);//raise all events up to the end first
                PlaybackTime = 0.0f;
                NextLoadEvent = 0;
                NextPlayEvent = 0;
                NextCameraCut = 0;
                NextConcatData = 0;
                RaiseEvents(newTime);
                Seeking = false;
            }

            PlaybackTime = newTime;

            int cutIndex = 0;
            float cutStart = 0.0f;
            for (cutIndex = 0; cutIndex < CameraCutList?.Length; cutIndex++)
            {
                var cutTime = CameraCutList[cutIndex];
                if (cutTime > newTime) break;
                cutStart = cutTime;
            }

            float cutOffset = newTime - cutStart;//offset into the current cut


            void updateObjectTransform(CutsceneObject obj, ClipMapEntry cme, ushort boneTag, byte posTrack, byte rotTrack)
            {
                if (cme != null)
                {
                    if (cme.Clip is ClipAnimation canim)
                    {
                        if (canim.Animation != null)
                        {
                            var t = canim.GetPlaybackTime(cutOffset);
                            var f = canim.Animation.GetFramePosition(t);
                            var p = canim.Animation.FindBoneIndex(boneTag, posTrack);
                            var r = canim.Animation.FindBoneIndex(boneTag, rotTrack);
                            if (p >= 0) obj.Position = canim.Animation.EvaluateVector4(f, p, true).XYZ();
                            if (r >= 0) obj.Rotation = canim.Animation.EvaluateQuaternion(f, r, true);
                        }
                    }
                    else if (cme.Clip is ClipAnimationList alist)
                    {
                        if (alist.Animations?.Data != null)
                        {
                            foreach (var anim in alist.Animations.Data)
                            {
                                var t = anim.GetPlaybackTime(cutOffset);
                                var f = anim.Animation.GetFramePosition(t);
                                var p = anim.Animation.FindBoneIndex(boneTag, posTrack);
                                var r = anim.Animation.FindBoneIndex(boneTag, rotTrack);
                                if (p >= 0) obj.Position = anim.Animation.EvaluateVector4(f, p, true).XYZ();
                                if (r >= 0) obj.Rotation = anim.Animation.EvaluateQuaternion(f, r, true);
                            }
                        }
                    }
                }
            }




            var ycd = (cutIndex < (Ycds?.Length ?? 0)) ? Ycds[cutIndex] : null;
            if (ycd?.CutsceneMap != null)
            {
                ClipMapEntry cme = null;

                if (CameraObject != null)
                {
                    ycd.CutsceneMap.TryGetValue(CameraObject.Name, out cme);

                    updateObjectTransform(CameraObject, cme, 0, 7, 8);


                    if (cme != null)
                    {
                        var pos = Position;
                        var rot = Rotation;
                        pos = pos + rot.Multiply(CameraObject.Position);
                        rot = rot * CameraObject.Rotation * CameraRotationOffset;
                        CameraObject.Position = pos;
                        CameraObject.Rotation = rot;
                    }

                }

                if (SceneObjects != null)
                {
                    foreach (var obj in SceneObjects.Values)
                    {
                        if (obj.Enabled == false) continue;

                        var pos = Position;
                        var rot = Rotation;
                        var animate = (obj.Ped != null) || (obj.Prop != null) || (obj.Vehicle != null) || (obj.Weapon != null);
                        if (animate)
                        {
                            ycd.CutsceneMap.TryGetValue(obj.AnimHash, out cme);
                            if (cme != null)
                            {
                                cme.OverridePlayTime = true;
                                cme.PlayTime = cutOffset;
                                updateObjectTransform(obj, cme, 0, 5, 6); //using root animation bone ids
                                pos = pos + rot.Multiply(obj.Position);
                                rot = rot * obj.Rotation;
                            }
                            obj.AnimClip = cme;
                        }
                        if (obj.Ped != null)
                        {
                            obj.Ped.Position = pos;
                            obj.Ped.Rotation = rot;
                            obj.Ped.UpdateEntity();
                            obj.Ped.AnimClip = cme;
                        }
                        if (obj.Prop != null)
                        {
                            obj.Prop.Position = pos;
                            obj.Prop.Orientation = rot;
                        }
                        if (obj.Vehicle != null)
                        {
                            obj.Vehicle.Position = pos;
                            obj.Vehicle.Rotation = rot;
                            obj.Vehicle.UpdateEntity();
                        }
                        if (obj.Weapon != null)
                        {
                            obj.Weapon.Position = pos;
                            obj.Weapon.Rotation = rot;
                            obj.Weapon.UpdateEntity();
                        }
                    }
                }




            }


        }


        public void Render(Renderer renderer)
        {

            if (SceneObjects != null)
            {
                foreach (var obj in SceneObjects.Values)
                {
                    if (obj.Enabled == false) continue;

                    if (obj.Ped != null)
                    {
                        renderer.RenderPed(obj.Ped);
                    }
                    if (obj.Prop != null)
                    {
                        renderer.RenderArchetype(obj.Prop.Archetype, obj.Prop, null, true, obj.AnimClip);
                    }
                    if (obj.Vehicle != null)
                    {
                        renderer.RenderVehicle(obj.Vehicle, obj.AnimClip);
                    }
                    if (obj.Weapon != null)
                    {
                        renderer.RenderWeapon(obj.Weapon, obj.AnimClip);
                    }
                }
                foreach (var obj in SceneObjects.Values)
                {
                    if (obj.Enabled == false) continue;

                    if (obj.HideEntity != null)
                    {
                        renderer.RenderHideEntity(obj.HideEntity);
                    }
                }
            }

        }





        private void RaiseEvents(float upToTime)
        {

            int i;
            for (i = NextLoadEvent; i < LoadEvents?.Length; i++)
            {
                var e = LoadEvents[i];
                if (e != null)
                {
                    if (e.fTime > upToTime) break;
                    RaiseEvent(e);
                }
            }
            NextLoadEvent = i;

            for (i = NextPlayEvent; i < PlayEvents?.Length; i++)
            {
                var e = PlayEvents[i];
                if (e != null)
                {
                    if (e.fTime > upToTime) break;
                    RaiseEvent(e);
                }
            }
            NextPlayEvent = i;

            for (i = NextCameraCut; i < CameraCutList?.Length; i++)
            {
                var c = CameraCutList[i];
                if (c > upToTime) break;
            }
            NextCameraCut = i;

            for (i = NextConcatData; i < ConcatDatas?.Length; i++)
            {
                var c = ConcatDatas[i];
                if (c.fStartTime > upToTime) break;
                if (c.cSceneName == 0) break;

                Position = c.vOffset;
                Rotation = Quaternion.RotationAxis(Vector3.UnitZ, c.fRotation * 0.0174532925f); //is this right?
            }
            NextConcatData = i;


        }
        private void RaiseEvent(CutEvent e)
        {

            switch (e.iEventId)
            {
                case CutEventType.LoadScene: LoadScene(e); break;
                case CutEventType.LoadAnimation: LoadAnimation(e); break;
                case CutEventType.LoadAudio: LoadAudio(e); break;
                case CutEventType.LoadModels: LoadModels(e); break;
                case CutEventType.LoadRayfireDes: LoadRayfireDes(e); break;
                case CutEventType.LoadParticles: LoadParticles(e); break;
                case CutEventType.LoadOverlays: LoadOverlays(e); break;
                case CutEventType.LoadGxt2: LoadGxt2(e); break;
                case CutEventType.UnloadModels: UnloadModels(e); break;
                case CutEventType.UnloadRayfireDes: UnloadRayfireDes(e); break;
                case CutEventType.EnableScreenFade: EnableScreenFade(e); break;
                case CutEventType.EnableHideObject: EnableHideObject(e); break;
                case CutEventType.EnableFixupModel: EnableFixupModel(e); break;
                case CutEventType.EnableBlockBounds: EnableBlockBounds(e); break;
                case CutEventType.EnableAnimation: EnableAnimation(e); break;
                case CutEventType.EnableParticleEffect: EnableParticleEffect(e); break;
                case CutEventType.EnableOverlay: EnableOverlay(e); break;
                case CutEventType.EnableAudio: EnableAudio(e); break;
                case CutEventType.EnableCamera: EnableCamera(e); break;
                case CutEventType.EnableLight: EnableLight(e); break;
                case CutEventType.DisableScreenFade: DisableScreenFade(e); break;
                case CutEventType.DisableHideObject: DisableHideObject(e); break;
                case CutEventType.DisableBlockBounds: DisableBlockBounds(e); break;
                case CutEventType.DisableAnimation: DisableAnimation(e); break;
                case CutEventType.DisableParticleEffect: DisableParticleEffect(e); break;
                case CutEventType.DisableOverlay: DisableOverlay(e); break;
                case CutEventType.DisableAudio: DisableAudio(e); break;
                case CutEventType.DisableCamera: DisableCamera(e); break;
                case CutEventType.DisableLight: DisableLight(e); break;
                case CutEventType.Subtitle: Subtitle(e); break;
                case CutEventType.PedVariation: PedVariation(e); break;
                case CutEventType.CameraCut: CameraCut(e); break;
                case CutEventType.CameraShadowCascade: CameraShadowCascade(e); break;
                case CutEventType.CameraUnk1: CameraUnk1(e); break;
                case CutEventType.CameraUnk2: CameraUnk2(e); break;
                case CutEventType.CameraUnk3: CameraUnk3(e); break;
                case CutEventType.CameraUnk4: CameraUnk4(e); break;
                case CutEventType.CameraUnk5: CameraUnk5(e); break;
                case CutEventType.CameraUnk6: CameraUnk6(e); break;
                case CutEventType.CameraUnk7: CameraUnk7(e); break;
                case CutEventType.CameraUnk8: CameraUnk8(e); break;
                case CutEventType.DecalUnk1: DecalUnk1(e); break;
                case CutEventType.DecalUnk2: DecalUnk2(e); break;
                case CutEventType.PropUnk1: PropUnk1(e); break;
                case CutEventType.Unk1: Unk1(e); break;
                case CutEventType.Unk2: Unk2(e); break;
                case CutEventType.VehicleUnk1: VehicleUnk1(e); break;
                case CutEventType.PedUnk1: PedUnk1(e); break;
                default: break;
            }
            
        }

        private void LoadScene(CutEvent e)
        {
            var args = e.EventArgs as CutLoadSceneEventArgs;
            if (args == null)
            { return; }


            Position = args.vOffset;
            Rotation = Quaternion.RotationAxis(Vector3.UnitZ, args.fRotation * 0.0174532925f);//is this right?

        }
        private void LoadAnimation(CutEvent e)
        {
            var args = e.EventArgs as CutNameEventArgs;
            if (args == null)
            { return; }

        }
        private void LoadAudio(CutEvent e)
        {
            var args = e.EventArgs as CutNameEventArgs;
            if (args == null)
            { return; }

            var obje = e as CutObjectIdEvent;
            if (obje == null)
            { return; }

            var obj = obje.Object as CutAudioObject;
            if (obj == null)
            { return; }

            if (Seeking) return;

            if (SceneObjects.TryGetValue(obje.iObjectId, out CutsceneObject audobj))
            {
                if (audobj.SoundPlayer != null)
                {
                    SoundStartOffset = obj.fOffset;
                    if (SoundPlayer != audobj.SoundPlayer)
                    {
                        if (SoundPlayer != null)
                        {
                            SoundPlayer.Stop();
                            SoundPlayer.DisposeAudio();
                            SoundPlayer = null;
                        }
                        SoundPlayer = audobj.SoundPlayer;
                    }
                }
                else
                { }
            }
            else
            { }
        }
        private void LoadModels(CutEvent e)
        {
            var args = e.EventArgs as CutObjectIdListEventArgs;
            if (args == null)
            { return; }

            if (args.iObjectIdList == null) return;

            foreach (var objid in args.iObjectIdList)
            {
                CutsceneObject obj = null;
                SceneObjects.TryGetValue(objid, out obj);
                if (obj != null)
                {
                    obj.Enabled = true;
                }
            }
        }
        private void LoadRayfireDes(CutEvent e)
        {
        }
        private void LoadParticles(CutEvent e)
        {
            var args = e.EventArgs as CutObjectIdListEventArgs;
            if (args == null)
            { return; }

        }
        private void LoadOverlays(CutEvent e)
        {
            var args = e.EventArgs as CutObjectIdListEventArgs;
            if (args == null)
            { return; }

        }
        private void LoadGxt2(CutEvent e)
        {
            if (GameFileCache == null)
            { return; }
            if (Gxt2File != null)
            { }

            var args = e.EventArgs as CutFinalNameEventArgs;
            if (args == null)
            { return; }

            var namel = args.cName?.ToLowerInvariant();
            var namehash = JenkHash.GenHash(namel);

            RpfFileEntry gxt2entry = null;
            GameFileCache.Gxt2Dict.TryGetValue(namehash, out gxt2entry);

            if (gxt2entry != null) //probably should do this load async
            {
                Gxt2File = GameFileCache.RpfMan.GetFile<Gxt2File>(gxt2entry);

                if (Gxt2File != null)
                {
                    for (int i = 0; i < Gxt2File.TextEntries.Length; i++)
                    {
                        var te = Gxt2File.TextEntries[i];
                        GlobalText.Ensure(te.Text, te.Hash);
                    }
                }
            }

        }
        private void UnloadModels(CutEvent e)
        {
            var args = e.EventArgs as CutObjectIdListEventArgs;
            if (args == null)
            { return; }

            if (args.iObjectIdList == null) return;

            foreach (var objid in args.iObjectIdList)
            {
                CutsceneObject obj = null;
                SceneObjects.TryGetValue(objid, out obj);
                if (obj != null)
                {
                    obj.Enabled = false;
                }
            }
        }
        private void UnloadRayfireDes(CutEvent e)
        {
        }
        private void EnableHideObject(CutEvent e)
        {
            var oe = e as CutObjectIdEvent;
            if (oe == null) return;

            CutsceneObject cso = null;
            SceneObjects.TryGetValue(oe.iObjectId, out cso);
            if (cso != null)
            {
                cso.Enabled = true;
            }
        }
        private void EnableFixupModel(CutEvent e)
        {
        }
        private void EnableBlockBounds(CutEvent e)
        {
            var oe = e as CutObjectIdEvent;
            if (oe == null) return;

        }
        private void EnableScreenFade(CutEvent e)
        {
        }
        private void EnableAnimation(CutEvent e)
        {
            var oe = e as CutObjectIdEvent;
            if (oe == null) return;

        }
        private void EnableParticleEffect(CutEvent e)
        {
        }
        private void EnableOverlay(CutEvent e)
        {
        }
        private void EnableAudio(CutEvent e)
        {
            var args = e.EventArgs as CutNameEventArgs;
            if (args == null)
            { return; }

        }
        private void EnableCamera(CutEvent e)
        {
            var oe = e as CutObjectIdEvent;
            if (oe == null) return;

        }
        private void EnableLight(CutEvent e)
        {
        }
        private void DisableHideObject(CutEvent e)
        {
            var oe = e as CutObjectIdEvent;
            if (oe == null) return;

            CutsceneObject cso = null;
            SceneObjects.TryGetValue(oe.iObjectId, out cso);
            if (cso != null)
            {
                cso.Enabled = false;
            }
        }
        private void DisableBlockBounds(CutEvent e)
        {
            var oe = e as CutObjectIdEvent;
            if (oe == null) return;

        }
        private void DisableScreenFade(CutEvent e)
        {
        }
        private void DisableAnimation(CutEvent e)
        {
            var oe = e as CutObjectIdEvent;
            if (oe == null) return;

        }
        private void DisableParticleEffect(CutEvent e)
        {
        }
        private void DisableOverlay(CutEvent e)
        {
        }
        private void DisableAudio(CutEvent e)
        {
            var args = e.EventArgs as CutNameEventArgs;
            if (args == null)
            { return; }

        }
        private void DisableCamera(CutEvent e)
        {
            var oe = e as CutObjectIdEvent;
            if (oe == null) return;

        }
        private void DisableLight(CutEvent e)
        {
        }
        private void Subtitle(CutEvent e)
        {
            var args = e.EventArgs as CutSubtitleEventArgs;
            if (args == null)
            { return; }

            if (!EnableSubtitles) return;
            if (Seeking) return; //don't raise subtitle events while seeking backwards...

            if (WorldForm != null)
            {
                var txt = args.cName.ToString();
                var dur = args.fSubtitleDuration;

                txt = txt.Replace("~z~", "");
                txt = txt.Replace("~c~~n~", "\n - ");
                txt = txt.Replace("~n~", "\n");
                txt = txt.Replace("~c~", " - ");
                txt = txt.Replace("~t~", " - ");

                WorldForm.ShowSubtitle(txt, dur);
            }
        }
        private void PedVariation(CutEvent e)
        {
            var args = e.EventArgs as CutObjectVariationEventArgs;
            if (args == null)
            { return; }

            var oe = e as CutObjectIdEvent;
            if (oe == null)
            { return; }

            if (Seeking) return; //this gets a bit messy when seeking backwards

            CutsceneObject cso = null;
            SceneObjects.TryGetValue(oe.iObjectId, out cso);

            if (cso?.Ped != null)
            {
                int comp = args.iComponent;
                int drbl = args.iDrawable;
                int texx = args.iTexture;

                Task.Run(() =>
                {
                    cso.Ped.SetComponentDrawable(comp, drbl, 0, texx, GameFileCache);
                });
            }


        }
        private void CameraCut(CutEvent e)
        {
            var args = e.EventArgs as CutCameraCutEventArgs;
            if (args == null)
            { return; }

            var oe = e as CutObjectIdEvent;
            if (oe == null)
            { return; }


            CutsceneObject obj = null;
            SceneObjects.TryGetValue(oe.iObjectId, out obj);
            if (obj == null)
            { return; }


            var pos = Position;
            var rot = Rotation * Quaternion.RotationAxis(Vector3.UnitX, 1.57079632679f);
            obj.Position = pos + rot.Multiply(args.vPosition);
            obj.Rotation = rot * args.vRotationQuaternion;

            CameraNearClip = (args.fNearDrawDistance > 0) ? Math.Min(args.fNearDrawDistance, 0.5f) : 0.5f;
            CameraFarClip = (args.fFarDrawDistance > 0) ? Math.Max(args.fFarDrawDistance, 1000.0f) : 12000.0f;
            CameraClipUpdate = true;
            CameraObject = obj;
        }
        private void CameraShadowCascade(CutEvent e)
        {
        }
        private void CameraUnk1(CutEvent e)
        {
        }
        private void CameraUnk2(CutEvent e)
        {
        }
        private void CameraUnk3(CutEvent e)
        {
        }
        private void CameraUnk4(CutEvent e)
        {
        }
        private void CameraUnk5(CutEvent e)
        {
        }
        private void CameraUnk6(CutEvent e)
        {
        }
        private void CameraUnk7(CutEvent e)
        {
        }
        private void CameraUnk8(CutEvent e)
        {
        }
        private void DecalUnk1(CutEvent e)
        {
        }
        private void DecalUnk2(CutEvent e)
        {
        }
        private void PropUnk1(CutEvent e)
        {
        }
        private void Unk1(CutEvent e)
        {
        }
        private void Unk2(CutEvent e)
        {
        }
        private void VehicleUnk1(CutEvent e)
        {
        }
        private void PedUnk1(CutEvent e)
        {
        }



        private T[] RecastArray<T>(object[] arr) where T : class
        {
            if (arr == null) return null;
            var r = new T[arr.Length];
            for (int i = 0; i < arr.Length; i++)
            {
                r[i] = arr[i] as T;
            }
            return r;
        }


        private void CreateSceneObjects()
        {
            SceneObjects = new Dictionary<int, CutsceneObject>();

            if (Objects == null) return;


            var refCounts = new Dictionary<MetaHash, int>();

            foreach (var obj in Objects.Values)
            {
                var sobj = new CutsceneObject();
                sobj.Init(obj, GameFileCache, AudioDB);
                SceneObjects[sobj.ObjectID] = sobj;

                if (sobj.AnimHash != 0)
                {
                    int refcount = 0;
                    var hash = sobj.AnimHash;
                    refCounts.TryGetValue(hash, out refcount);
                    if (refcount > 0)
                    {
                        var newstr = hash.ToString() + "^" + refcount.ToString();
                        sobj.AnimHash = JenkHash.GenHash(newstr);
                    }
                    refcount++;
                    refCounts[hash] = refcount;
                }

            }
        }
    }

    [TypeConverter(typeof(ExpandableObjectConverter))] public class CutsceneObject
    {
        public int ObjectID { get; set; }
        public CutObject CutObject { get; set; }
        public MetaHash Name { get; set; }

        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }

        public Ped Ped { get; set; }
        public YmapEntityDef Prop { get; set; }
        public Vehicle Vehicle { get; set; }
        public Weapon Weapon { get; set; }
        public YmapEntityDef HideEntity { get; set; }

        public MetaHash AnimHash { get; set; }
        public ClipMapEntry AnimClip { get; set; }

        public Dat54Sound SoundInfo { get; set; }
        public AwcStream[] SoundStreams { get; set; }
        public AudioPlayer SoundPlayer { get; set; }

        public bool Enabled { get; set; } = false;


        public void Init(CutObject obj, GameFileCache gfc, AudioDatabase adb)
        {
            CutObject = obj;
            ObjectID = obj?.iObjectId ?? -1;

            if (obj is CutNamedObject nobj)
            {
                Name = nobj.cName;
            }

            if (obj is CutAnimationManagerObject anim)
            {
            }
            else if (obj is CutAssetManagerObject ass)
            {
            }
            else if (obj is CutCameraObject cam)
            {
            }
            else if (obj is CutAudioObject aud)
            {
                InitAudio(aud, gfc, adb);
            }
            else if (obj is CutPedModelObject ped)
            {
                InitPed(ped, gfc);
            }
            else if (obj is CutPropModelObject prop)
            {
                InitProp(prop, gfc);
            }
            else if (obj is CutVehicleModelObject veh)
            {
                InitVehicle(veh, gfc);
            }
            else if (obj is CutWeaponModelObject weap)
            {
                InitWeapon(weap, gfc);
            }
            else if (obj is CutHiddenModelObject hid)
            {
                InitHiddenModel(hid, gfc);
            }
            else if (obj is CutFixupModelObject fix)
            {
            }
            else if (obj is CutRayfireObject rayf)
            {
            }
            else if (obj is CutParticleEffectObject eff)
            {
            }
            else if (obj is CutAnimatedParticleEffectObject aeff)
            {
            }
            else if (obj is CutLightObject light)
            {
            }
            else if (obj is CutAnimatedLightObject alight)
            {
            }
            else if (obj is CutDecalObject dec)
            {
            }
            else if (obj is CutOverlayObject ovr)
            {
            }
            else if (obj is CutSubtitleObject sub)
            {
            }
            else if (obj is CutBlockingBoundsObject blk)
            {
            }
            else if (obj is CutScreenFadeObject fad)
            {
            }
            else
            { }
        }

        private void InitAudio(CutAudioObject aud, GameFileCache gfc, AudioDatabase adb)
        {

            //how to know the correct name/hash to use?
            //sound name in the format: cutscenes_name_mastered_only
            var name = aud.cName.ToCleanString().ToLowerInvariant().Replace(".wav", "");
            var soundname = "cutscenes_" + name + "_mastered";
            var soundname2 = "cutscenes_" + name + "_mastered_only";
            uint soundhash = JenkHash.GenHash(soundname);
            uint soundhash2 = JenkHash.GenHash(soundname2);

            if (adb?.SoundsDB != null)
            {
                if (adb.SoundsDB.TryGetValue(soundhash, out Dat54Sound snd))
                {
                    SoundInfo = snd;
                }
                else if (adb.SoundsDB.TryGetValue(soundhash2, out Dat54Sound snd2))
                {
                    SoundInfo = snd2;
                }
            }


            if (SoundInfo is Dat54StreamingSound strsnd)
            {
                int dur = strsnd.Duration;
                MetaHash awchash = 0;
                AwcFile awc = null;

                var streaminfs = new List<Dat54SimpleSound>();
                var streamlist = new List<AwcStream>();

                foreach (var chan in strsnd.ChildSounds)
                {
                    if (chan is Dat54SimpleSound chansnd)
                    {
                        var chanawchash = chansnd.ContainerName;
                        if (chanawchash != awchash)
                        {
                            awchash = chanawchash;
                            if (adb.ContainerDB.TryGetValue(awchash, out RpfFileEntry awcentry))
                            {
                                awc = new AwcFile();
                                gfc.RpfMan.LoadFile(awc, awcentry);
                            }
                            else
                            { }
                        }

                        if (awc?.StreamDict != null)
                        {
                            var chanhash = chansnd.FileName & 0x1FFFFFFF;
                            if (awc.StreamDict.TryGetValue(chanhash, out AwcStream chanstream))
                            {
                                streaminfs.Add(chansnd);
                                streamlist.Add(chanstream);
                            }
                            else
                            { }
                        }
                        else
                        { }
                    }
                    else
                    { }
                }

                var streams = streamlist.ToArray();

                SoundPlayer = new AudioPlayer();
                SoundPlayer.LoadAudio(streams);
                for (int i = 0; i < streaminfs.Count; i++)
                {
                    var streaminf = streaminfs[i];
                    var left = 1.0f;
                    var right = 1.0f;
                    switch (streaminf.Header?.Pan ?? 0)
                    {
                        case 0://center/default
                            left = 1.0f;
                            right = 1.0f;
                            break;
                        case 0x133: // 307://left channel
                            left = 1.0f;
                            right = 0.0f;
                            break;
                        case 0x35: // 53://right channel
                            left = 0.0f;
                            right = 1.0f;
                            break;
                        default:
                            break;
                    }
                    SoundPlayer.SetOutputMatrix(i, left, right);
                }

            }
            else if (SoundInfo != null)
            { }
            if (SoundInfo == null)
            { }


        }

        private void InitPed(CutPedModelObject ped, GameFileCache gfc)
        {

            Ped = new Ped();
            Ped.Init(ped.StreamingName, gfc);
            Ped.LoadDefaultComponents(gfc);

            //if (ped.StreamingName == JenkHash.GenHash("player_zero"))
            //{
            //    //for michael, switch his outfit so it's not glitching everywhere (until it's fixed?)
            //    Ped.SetComponentDrawable(3, 27, 0, 0, gfc);
            //    Ped.SetComponentDrawable(4, 19, 0, 0, gfc);
            //    Ped.SetComponentDrawable(6, null, null, gfc);
            //}

            AnimHash = ped.StreamingName;
        }

        private void InitProp(CutPropModelObject prop, GameFileCache gfc)
        {

            Prop = new YmapEntityDef();
            Prop.SetArchetype(gfc.GetArchetype(prop.StreamingName));

            AnimHash = prop.StreamingName;
        }

        private void InitVehicle(CutVehicleModelObject veh, GameFileCache gfc)
        {
            var name = veh.StreamingName.ToString();

            Vehicle = new Vehicle();
            Vehicle.Init(name, gfc);

            AnimHash = veh.StreamingName;
        }

        private void InitWeapon(CutWeaponModelObject weap, GameFileCache gfc)
        {
            var name = weap.StreamingName.ToString();

            Weapon = new Weapon();
            Weapon.Init(name, gfc);

            AnimHash = weap.StreamingName;
        }

        private void InitHiddenModel(CutHiddenModelObject hid, GameFileCache gfc)
        {

            HideEntity = new YmapEntityDef();
            HideEntity._CEntityDef.archetypeName = hid.cName;
            HideEntity.SetPosition(hid.vPosition);

        }


        public override string ToString()
        {
            return CutObject?.ToString() ?? (ObjectID.ToString() + ": " + Name.ToString());
        }
    }




}
