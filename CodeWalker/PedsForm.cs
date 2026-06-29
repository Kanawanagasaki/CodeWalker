using CodeWalker.Export;
using CodeWalker.GameFiles;
using CodeWalker.Properties;
using CodeWalker.Rendering;
using CodeWalker.World;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.XInput;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Color = SharpDX.Color;

namespace CodeWalker
{
    public partial class PedsForm : Form, DXForm
    {
        public Form Form { get { return this; } } //for DXForm/DXManager use

        public Renderer Renderer = null;
        public object RenderSyncRoot { get { return Renderer.RenderSyncRoot; } }

        volatile bool formopen = false;
        volatile bool running = false;
        volatile bool pauserendering = false;
        //volatile bool initialised = false;

        Stopwatch frametimer = new Stopwatch();
        Camera camera;
        Timecycle timecycle;
        Weather weather;
        Clouds clouds;

        Entity camEntity = new Entity();


        bool MouseLButtonDown = false;
        bool MouseRButtonDown = false;
        int MouseX;
        int MouseY;
        System.Drawing.Point MouseDownPoint;
        System.Drawing.Point MouseLastPoint;


        public GameFileCache GameFileCache { get; } = GameFileCacheFactory.Create();


        InputManager Input = new InputManager();


        bool initedOk = false;



        bool toolsPanelResizing = false;
        int toolsPanelResizeStartX = 0;
        int toolsPanelResizeStartLeft = 0;
        int toolsPanelResizeStartRight = 0;


        bool enableGrid = false;
        float gridSize = 1.0f;
        int gridCount = 40;
        List<VertexTypePC> gridVerts = new List<VertexTypePC>();
        object gridSyncRoot = new object();





        Ped SelectedPed = new Ped();

        // Cache of all clip-dict (YCD) short names, sorted alphabetically.
        // Populated once in UpdateGlobalPedsUI() and reused by UpdateClipDictComboBox()
        // to apply the "all anim dicts" vs "selected-ped-only" filter without re-scanning
        // GameFileCache.YcdDict on every checkbox toggle.
        List<string> _allYcdNames = new List<string>();


        ComboBox[] ComponentComboBoxes = null;
        public class ComponentComboItem
        {
            public MCPVDrawblData DrawableData { get; set; }
            public int AlternativeIndex { get; set; }
            public int TextureIndex { get; set; }
            public ComponentComboItem(MCPVDrawblData drawableData, int altIndex = 0, int textureIndex = -1)
            {
                DrawableData = drawableData;
                AlternativeIndex = altIndex;
                TextureIndex = textureIndex;
            }
            public override string ToString()
            {
                if (DrawableData == null) return TextureIndex.ToString();
                var itemname = DrawableData.GetDrawableName(AlternativeIndex);
                if (DrawableData.TexData?.Length > 0) return itemname + " + " + DrawableData.GetTextureSuffix(TextureIndex);
                return itemname;
            }
            public string DrawableName
            {
                get
                {
                    return DrawableData?.GetDrawableName(AlternativeIndex) ?? "error";
                }
            }
            public string TextureName
            {
                get
                {
                    return DrawableData?.GetTextureName(TextureIndex);
                }
            }
        }



        public PedsForm()
        {
            InitializeComponent();

            ComponentComboBoxes = new[]
            {
                CompHeadComboBox,
                CompBerdComboBox,
                CompHairComboBox,
                CompUpprComboBox,
                CompLowrComboBox,
                CompHandComboBox,
                CompFeetComboBox,
                CompTeefComboBox,
                CompAccsComboBox,
                CompTaskComboBox,
                CompDeclComboBox,
                CompJbibComboBox
            };


            Renderer = new Renderer(this, GameFileCache);
            camera = Renderer.camera;
            timecycle = Renderer.timecycle;
            weather = Renderer.weather;
            clouds = Renderer.clouds;

            initedOk = Renderer.Init();

            Renderer.controllightdir = !Settings.Default.Skydome;
            Renderer.rendercollisionmeshes = false;
            Renderer.renderclouds = false;
            //Renderer.renderclouds = true;
            //Renderer.individualcloudfrag = "Contrails";
            Renderer.rendermoon = false;
            Renderer.renderskeletons = false;
            Renderer.SelectionFlagsTestAll = true;
            Renderer.swaphemisphere = true;

            GTAFolder.UpdateEnhancedFormTitle(this);
        }

        public void InitScene(Device device)
        {
            int width = ClientSize.Width;
            int height = ClientSize.Height;

            try
            {
                Renderer.DeviceCreated(device, width, height);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error loading shaders!\n" + ex.ToString());
                return;
            }


            camera.FollowEntity = camEntity;
            camera.FollowEntity.Position = Vector3.Zero;// prevworldpos;
            camera.FollowEntity.Orientation = Quaternion.LookAtLH(Vector3.Zero, Vector3.Up, Vector3.ForwardLH);
            camera.TargetDistance = 2.0f;
            camera.CurrentDistance = 2.0f;
            camera.TargetRotation.Y = 0.2f;
            camera.CurrentRotation.Y = 0.2f;
            camera.TargetRotation.X = 1.0f * (float)Math.PI;
            camera.CurrentRotation.X = 1.0f * (float)Math.PI;

            Renderer.shaders.deferred = false; //no point using this here yet


            LoadSettings();


            formopen = true;
            new Thread(new ThreadStart(ContentThread)).Start();

            frametimer.Start();

        }
        public void CleanupScene()
        {
            formopen = false;

            Renderer.DeviceDestroyed();

            int count = 0;
            while (running && (count < 5000)) //wait for the content thread to exit gracefully
            {
                Thread.Sleep(1);
                count++;
            }
        }
        public void RenderScene(DeviceContext context)
        {
            float elapsed = (float)frametimer.Elapsed.TotalSeconds;
            frametimer.Restart();

            if (pauserendering) return;

            GameFileCache.BeginFrame();

            if (!Monitor.TryEnter(Renderer.RenderSyncRoot, 50))
            { return; } //couldn't get a lock, try again next time

            UpdateControlInputs(elapsed);
            //space.Update(elapsed);

            Renderer.Update(elapsed, MouseLastPoint.X, MouseLastPoint.Y);



            //UpdateWidgets();
            //BeginMouseHitTest();




            Renderer.BeginRender(context);

            Renderer.RenderSkyAndClouds();

            Renderer.SelectedDrawable = null;// SelectedItem.Drawable;


            Renderer.RenderPed(SelectedPed);

            //UpdateMouseHitsFromRenderer();
            //RenderSelection();


            RenderGrid(context);


            Renderer.RenderQueued();

            //Renderer.RenderBounds(MapSelectionMode.Entity);

            //Renderer.RenderSelectionGeometry(MapSelectionMode.Entity);

            //RenderMoused();

            Renderer.RenderFinalPass();

            //RenderMarkers();
            //RenderWidgets();

            Renderer.EndRender();

            Monitor.Exit(Renderer.RenderSyncRoot);

            //UpdateMarkerSelectionPanelInvoke();
        }
        public void BuffersResized(int w, int h)
        {
            Renderer.BuffersResized(w, h);
        }
        public bool ConfirmQuit()
        {
            return true;
        }





        private void Init()
        {
            //called from PedForm_Load

            if (!initedOk)
            {
                Close();
                return;
            }


            MouseWheel += PedsForm_MouseWheel;

            if (!GTAFolder.UpdateGTAFolder(true))
            {
                Close();
                return;
            }



            ShaderParamNames[] texsamplers = RenderableGeometry.GetTextureSamplerList();
            foreach (var texsampler in texsamplers)
            {
                TextureSamplerComboBox.Items.Add(texsampler);
            }
            //TextureSamplerComboBox.SelectedIndex = 0;//LoadSettings will do this..


            UpdateGridVerts();
            GridSizeComboBox.SelectedIndex = 1;
            GridCountComboBox.SelectedIndex = 1;



            Input.Init();


            Renderer.Start();
        }


        private void ContentThread()
        {
            //main content loading thread.
            running = true;

            UpdateStatus("Scanning...");

            try
            {
                GTA5Keys.LoadFromPath(GTAFolder.CurrentGTAFolder, GTAFolder.IsGen9, Settings.Default.Key);
            }
            catch
            {
                MessageBox.Show("Keys not found! This shouldn't happen.");
                Close();
                return;
            }

            GameFileCache.EnableDlc = true;
            GameFileCache.EnableMods = true;
            GameFileCache.LoadPeds = true;
            GameFileCache.LoadVehicles = false;
            GameFileCache.LoadArchetypes = false;//to speed things up a little
            GameFileCache.BuildExtendedJenkIndex = false;//to speed things up a little
            GameFileCache.DoFullStringIndex = true;//to get all global text from DLC...
            GameFileCache.Init(UpdateStatus, LogError);

            //UpdateDlcListComboBox(gameFileCache.DlcNameList);

            //EnableCacheDependentUI();

            UpdateGlobalPedsUI();


            LoadWorld();



            //initialised = true;

            //EnableDLCModsUI();

            //UpdateStatus("Ready");


            Task.Run(() => {
                while (formopen && !IsDisposed) //renderer content loop
                {
                    bool rcItemsPending = Renderer.ContentThreadProc();

                    if (!rcItemsPending)
                    {
                        Thread.Sleep(1); //sleep if there's nothing to do
                    }
                }
            });

            while (formopen && !IsDisposed) //main asset loop
            {
                bool fcItemsPending = GameFileCache.ContentThreadProc();

                if (!fcItemsPending)
                {
                    Thread.Sleep(1); //sleep if there's nothing to do
                }
            }

            GameFileCache.Clear();

            running = false;
        }




        private void LoadSettings()
        {
            var s = Settings.Default;
            //WindowState = s.WindowMaximized ? FormWindowState.Maximized : WindowState;
            //FullScreenCheckBox.Checked = s.FullScreen;
            WireframeCheckBox.Checked = s.Wireframe;
            HDRRenderingCheckBox.Checked = s.HDR;
            ShadowsCheckBox.Checked = s.Shadows;
            SkydomeCheckBox.Checked = s.Skydome;
            RenderModeComboBox.SelectedIndex = Math.Max(RenderModeComboBox.FindString(s.RenderMode), 0);
            TextureSamplerComboBox.SelectedIndex = Math.Max(TextureSamplerComboBox.FindString(s.RenderTextureSampler), 0);
            TextureCoordsComboBox.SelectedIndex = Math.Max(TextureCoordsComboBox.FindString(s.RenderTextureSamplerCoord), 0);
            AnisotropicFilteringCheckBox.Checked = s.AnisotropicFiltering;
            //ErrorConsoleCheckBox.Checked = s.ShowErrorConsole;
            //StatusBarCheckBox.Checked = s.ShowStatusBar;
        }



        private void LoadWorld()
        {
            UpdateStatus("Loading timecycles...");
            timecycle.Init(GameFileCache, UpdateStatus);
            timecycle.SetTime(Renderer.timeofday);

            UpdateStatus("Loading materials...");
            BoundsMaterialTypes.Init(GameFileCache);

            UpdateStatus("Loading weather...");
            weather.Init(GameFileCache, UpdateStatus, timecycle);
            //UpdateWeatherTypesComboBox(weather);

            UpdateStatus("Loading clouds...");
            clouds.Init(GameFileCache, UpdateStatus, weather);
            //UpdateCloudTypesComboBox(clouds);

        }






        private void UpdateStatus(string text)
        {
            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() => { UpdateStatus(text); }));
                }
                else
                {
                    StatusLabel.Text = text;
                }
            }
            catch { }
        }
        private void LogError(string text)
        {
            try
            {
                if (InvokeRequired)
                {
                    Invoke(new Action(() => { LogError(text); }));
                }
                else
                {
                    //TODO: error logging..
                    ConsoleTextBox.AppendText(text + "\r\n");
                    //StatusLabel.Text = text;
                    //MessageBox.Show(text);
                }
            }
            catch { }
        }




        private void UpdateMousePosition(MouseEventArgs e)
        {
            MouseX = e.X;
            MouseY = e.Y;
            MouseLastPoint = e.Location;
        }

        private void RotateCam(int dx, int dy)
        {
            camera.MouseRotate(dx, dy);
        }

        private void MoveCameraToView(Vector3 pos, float rad)
        {
            //move the camera to a default place where the given sphere is fully visible.

            rad = Math.Max(0.01f, rad*0.1f);

            camera.FollowEntity.Position = pos;
            camera.TargetDistance = rad * 1.2f;
            camera.CurrentDistance = rad * 1.2f;

            camera.UpdateProj = true;

        }







        private void AddDrawableTreeNode(DrawableBase drawable, string name, bool check)
        {
            var tnode = TexturesTreeView.Nodes.Add(name);
            var dnode = ModelsTreeView.Nodes.Add(name);
            dnode.Tag = drawable;
            dnode.Checked = check;

            AddDrawableModelsTreeNodes(drawable.DrawableModels?.High, "High Detail", true, dnode, tnode);
            AddDrawableModelsTreeNodes(drawable.DrawableModels?.Med, "Medium Detail", false, dnode, tnode);
            AddDrawableModelsTreeNodes(drawable.DrawableModels?.Low, "Low Detail", false, dnode, tnode);
            AddDrawableModelsTreeNodes(drawable.DrawableModels?.VLow, "Very Low Detail", false, dnode, tnode);
            //AddDrawableModelsTreeNodes(drawable.DrawableModels?.Extra, "X Detail", false, dnode, tnode);

        }
        private void AddDrawableModelsTreeNodes(DrawableModel[] models, string prefix, bool check, TreeNode parentDrawableNode = null, TreeNode parentTextureNode = null)
        {
            if (models == null) return;

            for (int mi = 0; mi < models.Length; mi++)
            {
                var tnc = (parentDrawableNode != null) ? parentDrawableNode.Nodes : ModelsTreeView.Nodes;

                var model = models[mi];
                string mprefix = prefix + " " + (mi + 1).ToString();
                var mnode = tnc.Add(mprefix + " " + model.ToString());
                mnode.Tag = model;
                mnode.Checked = check;

                var ttnc = (parentTextureNode != null) ? parentTextureNode.Nodes : TexturesTreeView.Nodes;
                var tmnode = ttnc.Add(mprefix + " " + model.ToString());
                tmnode.Tag = model;

                if (!check)
                {
                    Renderer.SelectionModelDrawFlags[model] = false;
                }

                if (model.Geometries == null) continue;

                foreach (var geom in model.Geometries)
                {
                    var gname = geom.ToString();
                    var gnode = mnode.Nodes.Add(gname);
                    gnode.Tag = geom;
                    gnode.Checked = true;// check;

                    var tgnode = tmnode.Nodes.Add(gname);
                    tgnode.Tag = geom;

                    if ((geom.Shader != null) && (geom.Shader.ParametersList != null) && (geom.Shader.ParametersList.Hashes != null))
                    {
                        var pl = geom.Shader.ParametersList;
                        var h = pl.Hashes;
                        var p = pl.Parameters;
                        for (int ip = 0; ip < h.Length; ip++)
                        {
                            var hash = pl.Hashes[ip];
                            var parm = pl.Parameters[ip];
                            var tex = parm.Data as TextureBase;
                            if (tex != null)
                            {
                                var t = tex as Texture;
                                var tstr = tex.Name.Trim();
                                if (t != null)
                                {
                                    tstr = string.Format("{0} ({1}x{2}, embedded)", tex.Name, t.Width, t.Height);
                                }
                                var tnode = tgnode.Nodes.Add(hash.ToString().Trim() + ": " + tstr);
                                tnode.Tag = tex;
                            }
                        }
                        tgnode.Expand();
                    }

                }

                mnode.Expand();
                tmnode.Expand();
            }
        }
        private void UpdateSelectionDrawFlags(TreeNode node)
        {
            //update the selection draw flags depending on tag and checked/unchecked
            var drwbl = node.Tag as DrawableBase;
            var model = node.Tag as DrawableModel;
            var geom = node.Tag as DrawableGeometry;
            bool rem = node.Checked;
            lock (Renderer.RenderSyncRoot)
            {
                if (drwbl != null)
                {
                    if (rem)
                    {
                        if (Renderer.SelectionDrawableDrawFlags.ContainsKey(drwbl))
                        {
                            Renderer.SelectionDrawableDrawFlags.Remove(drwbl);
                        }
                    }
                    else
                    {
                        Renderer.SelectionDrawableDrawFlags[drwbl] = false;
                    }
                }
                if (model != null)
                {
                    if (rem)
                    {
                        if (Renderer.SelectionModelDrawFlags.ContainsKey(model))
                        {
                            Renderer.SelectionModelDrawFlags.Remove(model);
                        }
                    }
                    else
                    {
                        Renderer.SelectionModelDrawFlags[model] = false;
                    }
                }
                if (geom != null)
                {
                    if (rem)
                    {
                        if (Renderer.SelectionGeometryDrawFlags.ContainsKey(geom))
                        {
                            Renderer.SelectionGeometryDrawFlags.Remove(geom);
                        }
                    }
                    else
                    {
                        Renderer.SelectionGeometryDrawFlags[geom] = false;
                    }
                }
                //updateArchetypeStatus = true;
            }
        }


        private void UpdateGlobalPedsUI()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => { UpdateGlobalPedsUI(); }));
            }
            else
            {

                ClipComboBox.Items.Clear();
                var ycds = GameFileCache.YcdDict.Values.ToList();
                ycds.Sort((a, b) => { return a.Name.CompareTo(b.Name); });
                _allYcdNames = new List<string>(ycds.Count);
                foreach (var ycde in ycds)
                {
                    _allYcdNames.Add(ycde.GetShortName());
                }

                // Populate the dropdown items themselves (not just the autocomplete source)
                // using the current filter ("all" vs "selected-ped-only"). The previous
                // implementation only filled AutoCompleteCustomSource, which meant the
                // dropdown list was empty and the only visible entry was the text set in
                // LoadPed() (e.g. "move_m@generic" / "move_f@generic").
                UpdateClipDictComboBox();



                PedNameComboBox.Items.Clear();
                var peds = GameFileCache.PedsInitDict.Values.ToList();
                peds.Sort((a, b) => { return a.Name.CompareTo(b.Name); });
                foreach (var ped in peds)
                {
                    PedNameComboBox.Items.Add(ped.Name);
                }
                if (peds.Count > 0)
                {
                    var ind = PedNameComboBox.FindString("A_F_Y_Beach_01"); // //A_C_Pug
                    PedNameComboBox.SelectedIndex = Math.Max(ind, 0);
                    //PedNameComboBox.SelectedIndex = 0;
                }

            }

        }


        /// <summary>
        /// Rebuild ClipDictComboBox.Items and AutoCompleteCustomSource based on the current
        /// state of ShowAllClipDictsCheckBox and the selected ped.
        ///
        /// GTA V ped metadata (CPedModelInfo__InitData) only references ONE clip dictionary
        /// per ped via ClipDictionaryName (typically "move_m@generic" or "move_f@generic").
        /// There is no static list of "all clip dicts usable by ped X" — at runtime the game
        /// dynamically loads scenario/script animation dicts that are not tied to a specific
        /// ped model. So "anim dicts that belong to the selected ped" can only be approximated
        /// by combining the ped's ClipDictionaryName with a heuristic substring search for
        /// YCDs whose name contains the ped model name (e.g. for story peds like
        /// "ig_lamardavis" there may be matching "anim@ig_lamardavis" / "facials@ig_lamardavis"
        /// dicts; for generic peds like "a_f_y_beach_01" there usually aren't).
        ///
        /// When the checkbox is unchecked (default), only this filtered set is shown so the
        /// user can quickly pick a relevant anim dict for the current ped. When checked, the
        /// full list of every YCD in the game cache is shown.
        /// </summary>
        private void UpdateClipDictComboBox()
        {
            // Preserve the current text so toggling the checkbox or loading a new ped doesn't
            // wipe the user's selection. ComboBox.Items.Clear() / AddRange() do not change
            // Text, but AutoCompleteCustomSource changes can sometimes reset the edit box
            // visually on certain Windows versions; capture+restore is the safe path.
            string preservedText = ClipDictComboBox.Text;

            List<string> listToShow;
            if (ShowAllClipDictsCheckBox.Checked)
            {
                listToShow = _allYcdNames;
            }
            else
            {
                listToShow = BuildClipDictsForSelectedPed();
                if (listToShow.Count == 0)
                {
                    // No ped loaded yet (or no matches at all) — fall back to the full list
                    // so the dropdown is still usable on first launch and for peds with no
                    // identifiable anim dicts.
                    listToShow = _allYcdNames;
                }
            }

            ClipDictComboBox.BeginUpdate();
            try
            {
                ClipDictComboBox.Items.Clear();
                ClipDictComboBox.Items.AddRange(listToShow.ToArray());
            }
            finally
            {
                ClipDictComboBox.EndUpdate();
            }

            ClipDictComboBox.AutoCompleteCustomSource.Clear();
            ClipDictComboBox.AutoCompleteCustomSource.AddRange(listToShow.ToArray());

            // Restore the text. Only re-assign if it actually changed, to avoid raising
            // an extra TextChanged event that would re-trigger LoadClipDict.
            if (!string.Equals(ClipDictComboBox.Text, preservedText, StringComparison.Ordinal))
            {
                ClipDictComboBox.Text = preservedText;
            }
        }


        /// <summary>
        /// Build the filtered list of clip-dict names that "belong" to the currently
        /// selected ped model. Returns an empty list if no ped is loaded.
        ///
        /// "Belong to" is approximated as:
        ///   1. SelectedPed.InitData.ClipDictionaryName — the ped's primary movement anim
        ///      dict (always included when non-empty).
        ///   2. Any YCD in _allYcdNames whose short name contains the ped model name as a
        ///      case-insensitive substring — catches per-ped animation packs shipped for
        ///      story characters (e.g. "anim@ig_lamardavis").
        ///
        /// Note: we use InitData.Name (loaded from ped meta XML) rather than Ped.Name,
        /// because Ped.Name is not set by Ped.Init() and remains empty for PedsForm peds.
        /// </summary>
        private List<string> BuildClipDictsForSelectedPed()
        {
            var result = new List<string>();
            if (SelectedPed?.InitData == null) return result;

            // InitData.Name comes from the ped.meta XML and uses mixed case like
            // "A_F_Y_Beach_01" or "ig_lamardavis". Compare case-insensitively.
            string pedName = SelectedPed.InitData.Name;
            string pedNameLower = pedName?.ToLowerInvariant();

            // 1. Always include the ped's primary ClipDictionaryName.
            string primaryClipDict = SelectedPed.InitData.ClipDictionaryName;
            bool hasPrimary = !string.IsNullOrEmpty(primaryClipDict);

            // 2. Substring match for per-ped animation packs.
            if (!string.IsNullOrEmpty(pedNameLower))
            {
                foreach (var ycdName in _allYcdNames)
                {
                    if (ycdName == null) continue;
                    if (ycdName.ToLowerInvariant().Contains(pedNameLower))
                    {
                        result.Add(ycdName);
                    }
                }
            }

            // Make sure the primary clip dict is present even if the substring search
            // didn't catch it (it usually won't, since "move_m@generic" doesn't contain
            // the ped name).
            if (hasPrimary && !result.Contains(primaryClipDict))
            {
                result.Add(primaryClipDict);
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }


        private void ShowAllClipDictsCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            UpdateClipDictComboBox();
        }



        private void UpdateModelsUI()
        {
            //TODO: change to go through each component and add/update/remove treeview item accordingly?

            Renderer.SelectionDrawableDrawFlags.Clear();
            Renderer.SelectionModelDrawFlags.Clear();
            Renderer.SelectionGeometryDrawFlags.Clear();
            ModelsTreeView.Nodes.Clear();
            ModelsTreeView.ShowRootLines = true;
            TexturesTreeView.Nodes.Clear();
            TexturesTreeView.ShowRootLines = true;

            if (SelectedPed == null) return;


            for (int i = 0; i < 12; i++)
            {
                var drawable = SelectedPed.Drawables[i];
                var drawablename = SelectedPed.DrawableNames[i];

                if (drawable != null)
                {
                    AddDrawableTreeNode(drawable, drawablename, true);
                }
            }

        }




        public void LoadPed()
        {
            var pedname = PedNameComboBox.Text;
            var pedhash = JenkHash.GenHash(pedname.ToLowerInvariant());
            var pedchange = SelectedPed.NameHash != pedhash;

            for (int i = 0; i < 12; i++)
            {
                ClearCombo(ComponentComboBoxes[i]);
            }

            DetailsPropertyGrid.SelectedObject = null;

            SelectedPed.Init(pedname, GameFileCache);

            LoadModel(SelectedPed.Yft, pedchange);


            var vi = SelectedPed.Ymt?.VariationInfo;
            if (vi != null)
            {
                for (int i = 0; i < 12; i++)
                {
                    PopulateCompCombo(ComponentComboBoxes[i], vi.GetComponentData(i));
                }
            }

            // Refresh the Clip Dict dropdown filter for the newly-selected ped BEFORE setting
            // the text. When "Show all anim dicts" is unchecked, this narrows the dropdown to
            // just the ped's relevant anim dicts (its ClipDictionaryName + any per-ped packs
            // whose name contains the ped model name). When checked, this is a no-op (full list).
            UpdateClipDictComboBox();

            ClipDictComboBox.Text = SelectedPed.InitData?.ClipDictionaryName ?? "";
            ClipComboBox.Text = "idle";


            DetailsPropertyGrid.SelectedObject = SelectedPed;

            UpdateModelsUI();

        }

        public void LoadModel(YftFile yft, bool movecamera = true)
        {
            if (yft == null) return;

            //FileName = yft.Name;
            //Yft = yft;

            var dr = yft.Fragment?.Drawable;
            if (movecamera && (dr != null))
            {
                MoveCameraToView(dr.BoundingCenter, dr.BoundingSphereRadius);
            }

            //UpdateModelsUI(yft.Fragment.Drawable);
        }



        private void ClearCombo(ComboBox c)
        {
            c.Items.Clear();
            c.Items.Add("");
            c.Text = string.Empty;
        }
        private void PopulateCompCombo(ComboBox c, MCPVComponentData compData)
        {
            if (compData?.DrawblData3 == null) return;
            foreach (var item in compData.DrawblData3)
            {
                for (int alt = 0; alt <= item.NumAlternatives; alt++)
                {
                    if (item.TexData?.Length > 0)
                    {
                        for (int tex = 0; tex < item.TexData.Length; tex++)
                        {
                            c.Items.Add(new ComponentComboItem(item, alt, tex));
                        }
                    }
                    else
                    {
                        c.Items.Add(new ComponentComboItem(item));
                    }
                }
            }
            if (compData.DrawblData3.Length > 0)
            {
                c.SelectedIndex = 1;
            }
        }

        private void SetComponentDrawable(int index, object comboObj)
        {

            var comboItem = comboObj as ComponentComboItem;
            var name = comboItem?.DrawableName;
            var tex = comboItem?.TextureName;

            SelectedPed.SetComponentDrawable(index, name, tex, GameFileCache);

            UpdateModelsUI();
        }






        private void LoadClipDict(string name)
        {
            var ycdhash = JenkHash.GenHash(name.ToLowerInvariant());
            var ycd = GameFileCache.GetYcd(ycdhash);
            while ((ycd != null) && (!ycd.Loaded))
            {
                Thread.Sleep(1);//kinda hacky
                ycd = GameFileCache.GetYcd(ycdhash);
            }



            //if (ycd != null)
            //{
            //    ////// TESTING XML CONVERSIONS
            //    //var data = ycd.Save();
            //    var xml = YcdXml.GetXml(ycd);
            //    var ycd2 = XmlYcd.GetYcd(xml);
            //    var data = ycd2.Save();
            //    var ycd3 = new YcdFile();
            //    RpfFile.LoadResourceFile(ycd3, data, 46);
            //    //var xml2 = YcdXml.GetXml(ycd3);
            //    //if (xml != xml2)
            //    //{ }
            //    ycd = ycd3;
            //}



            SelectedPed.Ycd = ycd;

            ClipComboBox.Items.Clear();
            ClipComboBox.Items.Add("");

            if (ycd?.ClipMapEntries == null)
            {
                ClipComboBox.SelectedIndex = 0;
                SelectedPed.AnimClip = null;
                return;
            }

            List<string> items = new List<string>();
            foreach (var cme in ycd.ClipMapEntries)
            {
                if (cme.Clip != null)
                {
                    items.Add(cme.Clip.ShortName);
                }
            }

            items.Sort();
            foreach (var item in items)
            {
                ClipComboBox.Items.Add(item);
            }
        }

        private void SelectClip(string name)
        {
            MetaHash cliphash = JenkHash.GenHash(name);
            ClipMapEntry cme = null;
            SelectedPed.Ycd?.ClipMap?.TryGetValue(cliphash, out cme);
            SelectedPed.AnimClip = cme;
        }





        private void UpdateTimeOfDayLabel()
        {
            int v = TimeOfDayTrackBar.Value;
            float fh = v / 60.0f;
            int ih = (int)fh;
            int im = v - (ih * 60);
            if (ih == 24) ih = 0;
            TimeOfDayLabel.Text = string.Format("{0:00}:{1:00}", ih, im);
        }


        private void UpdateControlInputs(float elapsed)
        {
            if (elapsed > 0.1f) elapsed = 0.1f;

            var s = Settings.Default;

            float moveSpeed = 2.0f;


            Input.Update();

            if (Input.xbenable)
            {
                //if (ControllerButtonJustPressed(GamepadButtonFlags.Start))
                //{
                //    SetControlMode(ControlMode == WorldControlMode.Free ? WorldControlMode.Ped : WorldControlMode.Free);
                //}
            }



            if (Input.ShiftPressed)
            {
                moveSpeed *= 5.0f;
            }
            if (Input.CtrlPressed)
            {
                moveSpeed *= 0.2f;
            }

            Vector3 movevec = Input.KeyboardMoveVec(false);

            if (Input.xbenable)
            {
                movevec.X += Input.xblx;
                movevec.Z -= Input.xbly;
                moveSpeed *= (1.0f + (Math.Min(Math.Max(Input.xblt, 0.0f), 1.0f) * 15.0f)); //boost with left trigger
                if (Input.ControllerButtonPressed(GamepadButtonFlags.A | GamepadButtonFlags.RightShoulder | GamepadButtonFlags.LeftShoulder))
                {
                    moveSpeed *= 5.0f;
                }
            }


            //if (MapViewEnabled == true)
            //{
            //    movevec *= elapsed * 100.0f * Math.Min(camera.OrthographicTargetSize * 0.01f, 30.0f);
            //    float mapviewscale = 1.0f / camera.Height;
            //    float fdx = MapViewDragX * mapviewscale;
            //    float fdy = MapViewDragY * mapviewscale;
            //    movevec.X -= fdx * camera.OrthographicSize;
            //    movevec.Y += fdy * camera.OrthographicSize;
            //}
            //else
            {
                //normal movement
                movevec *= elapsed * moveSpeed * Math.Min(camera.TargetDistance, 50.0f);
            }


            Vector3 movewvec = camera.ViewInvQuaternion.Multiply(movevec);
            camEntity.Position += movewvec;

            //MapViewDragX = 0;
            //MapViewDragY = 0;




            if (Input.xbenable)
            {
                camera.ControllerRotate(Input.xbrx, Input.xbry, elapsed);

                float zoom = 0.0f;
                float zoomspd = s.XInputZoomSpeed;
                float zoomamt = zoomspd * elapsed;
                if (Input.ControllerButtonPressed(GamepadButtonFlags.DPadUp)) zoom += zoomamt;
                if (Input.ControllerButtonPressed(GamepadButtonFlags.DPadDown)) zoom -= zoomamt;

                camera.ControllerZoom(zoom);

            }



        }



        private void UpdateGridVerts()
        {
            lock (gridSyncRoot)
            {
                gridVerts.Clear();

                float s = gridSize * gridCount * 0.5f;
                uint cblack = (uint)Color.Black.ToRgba();
                uint cgray = (uint)Color.DimGray.ToRgba();
                uint cred = (uint)Color.DarkRed.ToRgba();
                uint cgrn = (uint)Color.DarkGreen.ToRgba();
                int interval = 10;

                for (int i = 0; i <= gridCount; i++)
                {
                    float o = (gridSize * i) - s;
                    if ((i % interval) != 0)
                    {
                        gridVerts.Add(new VertexTypePC() { Position = new Vector3(o, -s, 0), Colour = cgray });
                        gridVerts.Add(new VertexTypePC() { Position = new Vector3(o, s, 0), Colour = cgray });
                        gridVerts.Add(new VertexTypePC() { Position = new Vector3(-s, o, 0), Colour = cgray });
                        gridVerts.Add(new VertexTypePC() { Position = new Vector3(s, o, 0), Colour = cgray });
                    }
                }
                for (int i = 0; i <= gridCount; i++) //draw main lines last, so they are on top
                {
                    float o = (gridSize * i) - s;
                    if ((i % interval) == 0)
                    {
                        var cx = (o == 0) ? cred : cblack;
                        var cy = (o == 0) ? cgrn : cblack;
                        gridVerts.Add(new VertexTypePC() { Position = new Vector3(o, -s, 0), Colour = cy });
                        gridVerts.Add(new VertexTypePC() { Position = new Vector3(o, s, 0), Colour = cy });
                        gridVerts.Add(new VertexTypePC() { Position = new Vector3(-s, o, 0), Colour = cx });
                        gridVerts.Add(new VertexTypePC() { Position = new Vector3(s, o, 0), Colour = cx });
                    }
                }

            }
        }

        private void RenderGrid(DeviceContext context)
        {
            if (!enableGrid) return;

            lock (gridSyncRoot)
            {
                if (gridVerts.Count > 0)
                {
                    Renderer.RenderLines(gridVerts);
                }
            }
        }










        private void PedsForm_Load(object sender, EventArgs e)
        {
            Init();
        }

        private void PedsForm_MouseDown(object sender, MouseEventArgs e)
        {
            switch (e.Button)
            {
                case MouseButtons.Left: MouseLButtonDown = true; break;
                case MouseButtons.Right: MouseRButtonDown = true; break;
            }

            if (!ToolsPanelShowButton.Focused)
            {
                ToolsPanelShowButton.Focus(); //make sure no textboxes etc are focused!
            }

            MouseDownPoint = e.Location;
            MouseLastPoint = MouseDownPoint;

            if (MouseLButtonDown)
            {
            }

            if (MouseRButtonDown)
            {
                //SelectMousedItem();
            }

            MouseX = e.X; //to stop jumps happening on mousedown, sometimes the last MouseMove event was somewhere else... (eg after clicked a menu)
            MouseY = e.Y;
        }

        private void PedsForm_MouseUp(object sender, MouseEventArgs e)
        {
            switch (e.Button)
            {
                case MouseButtons.Left: MouseLButtonDown = false; break;
                case MouseButtons.Right: MouseRButtonDown = false; break;
            }



            if (e.Button == MouseButtons.Left)
            {
            }
        }

        private void PedsForm_MouseMove(object sender, MouseEventArgs e)
        {
            int dx = e.X - MouseX;
            int dy = e.Y - MouseY;

            //if (MouseInvert)
            //{
            //    dy = -dy;
            //}

            //if (ControlMode == WorldControlMode.Free && !ControlBrushEnabled)
            {
                if (MouseLButtonDown)
                {
                    RotateCam(dx, dy);
                }
                if (MouseRButtonDown)
                {
                    if (Renderer.controllightdir)
                    {
                        Renderer.lightdirx += (dx * camera.Sensitivity);
                        Renderer.lightdiry += (dy * camera.Sensitivity);
                    }
                    else if (Renderer.controltimeofday)
                    {
                        float tod = Renderer.timeofday;
                        tod += (dx - dy) / 30.0f;
                        while (tod >= 24.0f) tod -= 24.0f;
                        while (tod < 0.0f) tod += 24.0f;
                        timecycle.SetTime(tod);
                        Renderer.timeofday = tod;

                        float fv = tod * 60.0f;
                        TimeOfDayTrackBar.Value = (int)fv;
                        UpdateTimeOfDayLabel();
                    }
                }

                UpdateMousePosition(e);

            }



        }

        private void PedsForm_MouseWheel(object sender, MouseEventArgs e)
        {
            if (e.Delta != 0)
            {
                camera.MouseZoom(e.Delta);
            }
        }

        private void PedsForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (ActiveControl is TextBox)
            {
                var tb = ActiveControl as TextBox;
                if (!tb.ReadOnly) return; //don't move the camera when typing!
            }
            if (ActiveControl is ComboBox)
            {
                var cb = ActiveControl as ComboBox;
                if (cb.DropDownStyle != ComboBoxStyle.DropDownList) return; //nontypable combobox
            }

            bool enablemove = true;// (!iseditmode) || (MouseLButtonDown && (GrabbedMarker == null) && (GrabbedWidget == null));

            Input.KeyDown(e, enablemove);

            var k = e.KeyCode;
            var kb = Input.keyBindings;
            bool ctrl = Input.CtrlPressed;
            bool shift = Input.ShiftPressed;


            if (!ctrl)
            {
                if (k == kb.MoveSlowerZoomIn)
                {
                    camera.MouseZoom(1);
                }
                if (k == kb.MoveFasterZoomOut)
                {
                    camera.MouseZoom(-1);
                }
            }


            if (!Input.kbmoving) //don't trigger further actions if moving.
            {
                if (!ctrl)
                {

                }
                else
                {
                    //switch (k)
                    //{
                    //    //case Keys.N:
                    //    //    New();
                    //    //    break;
                    //    //case Keys.O:
                    //    //    Open();
                    //    //    break;
                    //    //case Keys.S:
                    //    //    if (shift) SaveAll();
                    //    //    else Save();
                    //    //    break;
                    //    //case Keys.Z:
                    //    //    Undo();
                    //    //    break;
                    //    //case Keys.Y:
                    //    //    Redo();
                    //    //    break;
                    //    //case Keys.C:
                    //    //    CopyItem();
                    //    //    break;
                    //    //case Keys.V:
                    //    //    PasteItem();
                    //    //    break;
                    //    //case Keys.U:
                    //    //    ToolsPanelShowButton.Visible = !ToolsPanelShowButton.Visible;
                    //    //    break;
                    //}
                }
            }

            //if (ControlMode != WorldControlMode.Free || ControlBrushEnabled)
            //{
            //    e.Handled = true;
            //}
        }

        private void PedsForm_KeyUp(object sender, KeyEventArgs e)
        {
            Input.KeyUp(e);

            if (ActiveControl is TextBox)
            {
                var tb = ActiveControl as TextBox;
                if (!tb.ReadOnly) return; //don't move the camera when typing!
            }
            if (ActiveControl is ComboBox)
            {
                var cb = ActiveControl as ComboBox;
                if (cb.DropDownStyle != ComboBoxStyle.DropDownList) return; //non-typable combobox
            }

            //if (ControlMode != WorldControlMode.Free)
            //{
            //    e.Handled = true;
            //}
        }

        private void PedsForm_Deactivate(object sender, EventArgs e)
        {
            //try not to lock keyboard movement if the form loses focus.
            Input.KeyboardStop();
        }

        private void StatsUpdateTimer_Tick(object sender, EventArgs e)
        {
            StatsLabel.Text = Renderer.GetStatusText();

            if (Renderer.timerunning)
            {
                float fv = Renderer.timeofday * 60.0f;
                //TimeOfDayTrackBar.Value = (int)fv;
                UpdateTimeOfDayLabel();
            }

            //CameraPositionTextBox.Text = FloatUtil.GetVector3String(camera.Position, "0.##");
        }

        private void ToolsPanelShowButton_Click(object sender, EventArgs e)
        {
            ToolsPanel.Visible = true;
        }

        private void ToolsPanelHideButton_Click(object sender, EventArgs e)
        {
            ToolsPanel.Visible = false;
        }

        private void ToolsDragPanel_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                toolsPanelResizing = true;
                toolsPanelResizeStartX = e.X + ToolsPanel.Left + ToolsDragPanel.Left;
                toolsPanelResizeStartLeft = ToolsPanel.Left;
                toolsPanelResizeStartRight = ToolsPanel.Right;
            }
        }

        private void ToolsDragPanel_MouseUp(object sender, MouseEventArgs e)
        {
            toolsPanelResizing = false;
        }

        private void ToolsDragPanel_MouseMove(object sender, MouseEventArgs e)
        {
            if (toolsPanelResizing)
            {
                int rx = e.X + ToolsPanel.Left + ToolsDragPanel.Left;
                int dx = rx - toolsPanelResizeStartX;
                ToolsPanel.Width = toolsPanelResizeStartRight - toolsPanelResizeStartLeft + dx;
            }
        }

        private void ModelsTreeView_AfterCheck(object sender, TreeViewEventArgs e)
        {
            if (e.Node != null)
            {
                UpdateSelectionDrawFlags(e.Node);
            }
        }

        private void ModelsTreeView_NodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node != null)
            {
                e.Node.Checked = !e.Node.Checked;
                //UpdateSelectionDrawFlags(e.Node);
            }
        }

        private void ModelsTreeView_KeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = true; //stops annoying ding sound...
        }

        private void HDRRenderingCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            lock (Renderer.RenderSyncRoot)
            {
                Renderer.shaders.hdr = HDRRenderingCheckBox.Checked;
            }
        }

        private void ShadowsCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            lock (Renderer.RenderSyncRoot)
            {
                Renderer.shaders.shadows = ShadowsCheckBox.Checked;
            }
        }

        private void SkydomeCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            Renderer.renderskydome = SkydomeCheckBox.Checked;
            //Renderer.controllightdir = !Renderer.renderskydome;
        }

        private void ControlLightDirCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            Renderer.controllightdir = ControlLightDirCheckBox.Checked;
        }

        private void TimeOfDayTrackBar_Scroll(object sender, EventArgs e)
        {
            int v = TimeOfDayTrackBar.Value;
            float fh = v / 60.0f;
            UpdateTimeOfDayLabel();
            lock (Renderer.RenderSyncRoot)
            {
                Renderer.timeofday = fh;
                timecycle.SetTime(Renderer.timeofday);
            }
        }

        private void ShowCollisionMeshesCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            Renderer.rendercollisionmeshes = ShowCollisionMeshesCheckBox.Checked;
            Renderer.rendercollisionmeshlayerdrawable = ShowCollisionMeshesCheckBox.Checked;
        }

        private void WireframeCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            Renderer.shaders.wireframe = WireframeCheckBox.Checked;
        }

        private void AnisotropicFilteringCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            Renderer.shaders.AnisotropicFiltering = AnisotropicFilteringCheckBox.Checked;
        }

        private void HDTexturesCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            Renderer.renderhdtextures = HDTexturesCheckBox.Checked;
        }

        private void RenderModeComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            TextureSamplerComboBox.Enabled = false;
            TextureCoordsComboBox.Enabled = false;
            switch (RenderModeComboBox.Text)
            {
                default:
                case "Default":
                    Renderer.shaders.RenderMode = WorldRenderMode.Default;
                    break;
                case "Single texture":
                    Renderer.shaders.RenderMode = WorldRenderMode.SingleTexture;
                    TextureSamplerComboBox.Enabled = true;
                    TextureCoordsComboBox.Enabled = true;
                    break;
                case "Vertex normals":
                    Renderer.shaders.RenderMode = WorldRenderMode.VertexNormals;
                    break;
                case "Vertex tangents":
                    Renderer.shaders.RenderMode = WorldRenderMode.VertexTangents;
                    break;
                case "Vertex colour 1":
                    Renderer.shaders.RenderMode = WorldRenderMode.VertexColour;
                    Renderer.shaders.RenderVertexColourIndex = 1;
                    break;
                case "Vertex colour 2":
                    Renderer.shaders.RenderMode = WorldRenderMode.VertexColour;
                    Renderer.shaders.RenderVertexColourIndex = 2;
                    break;
                case "Vertex colour 3":
                    Renderer.shaders.RenderMode = WorldRenderMode.VertexColour;
                    Renderer.shaders.RenderVertexColourIndex = 3;
                    break;
                case "Texture coord 1":
                    Renderer.shaders.RenderMode = WorldRenderMode.TextureCoord;
                    Renderer.shaders.RenderTextureCoordIndex = 1;
                    break;
                case "Texture coord 2":
                    Renderer.shaders.RenderMode = WorldRenderMode.TextureCoord;
                    Renderer.shaders.RenderTextureCoordIndex = 2;
                    break;
                case "Texture coord 3":
                    Renderer.shaders.RenderMode = WorldRenderMode.TextureCoord;
                    Renderer.shaders.RenderTextureCoordIndex = 3;
                    break;
            }
        }

        private void TextureSamplerComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (TextureSamplerComboBox.SelectedItem is ShaderParamNames)
            {
                Renderer.shaders.RenderTextureSampler = (ShaderParamNames)TextureSamplerComboBox.SelectedItem;
            }
        }

        private void TextureCoordsComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            switch (TextureCoordsComboBox.Text)
            {
                default:
                case "Texture coord 1":
                    Renderer.shaders.RenderTextureSamplerCoord = 1;
                    break;
                case "Texture coord 2":
                    Renderer.shaders.RenderTextureSamplerCoord = 2;
                    break;
                case "Texture coord 3":
                    Renderer.shaders.RenderTextureSamplerCoord = 3;
                    break;
            }
        }

        private void GridCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            enableGrid = GridCheckBox.Checked;
        }

        private void GridSizeComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            float newgs;
            float.TryParse(GridSizeComboBox.Text, out newgs);
            if (newgs != gridSize)
            {
                gridSize = newgs;
                UpdateGridVerts();
            }
        }

        private void GridCountComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            int newgc;
            int.TryParse(GridCountComboBox.Text, out newgc);
            if (newgc != gridCount)
            {
                gridCount = newgc;
                UpdateGridVerts();
            }
        }

        private void SkeletonsCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            Renderer.renderskeletons = SkeletonsCheckBox.Checked;
        }

        private void StatusBarCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            StatusStrip.Visible = StatusBarCheckBox.Checked;
        }

        private void ErrorConsoleCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            ConsolePanel.Visible = ErrorConsoleCheckBox.Checked;
        }

        private void TextureViewerButton_Click(object sender, EventArgs e)
        {
            //TextureDictionary td = null;

            //if ((Ydr != null) && (Ydr.Loaded))
            //{
            //    td = Ydr.Drawable?.ShaderGroup?.TextureDictionary;
            //}
            //else if ((Yft != null) && (Yft.Loaded))
            //{
            //    td = Yft.Fragment?.Drawable?.ShaderGroup?.TextureDictionary;
            //}

            //if (td != null)
            //{
            //    YtdForm f = new YtdForm();
            //    f.Show();
            //    f.LoadTexDict(td, fileName);
            //    //f.LoadYtd(ytd);
            //}
            //else
            //{
            //    MessageBox.Show("Couldn't find embedded texture dict.");
            //}
        }





        private void PedNameComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!GameFileCache.IsInited) return;

            LoadPed();
        }

        private void CompHeadComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            SetComponentDrawable(0, CompHeadComboBox.SelectedItem);
        }

        private void CompBerdComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            SetComponentDrawable(1, CompBerdComboBox.SelectedItem);
        }

        private void CompHairComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            SetComponentDrawable(2, CompHairComboBox.SelectedItem);
        }

        private void CompUpprComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            SetComponentDrawable(3, CompUpprComboBox.SelectedItem);
        }

        private void CompLowrComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            SetComponentDrawable(4, CompLowrComboBox.SelectedItem);
        }

        private void CompHandComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            SetComponentDrawable(5, CompHandComboBox.SelectedItem);
        }

        private void CompFeetComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            SetComponentDrawable(6, CompFeetComboBox.SelectedItem);
        }

        private void CompTeefComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            SetComponentDrawable(7, CompTeefComboBox.SelectedItem);
        }

        private void CompAccsComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            SetComponentDrawable(8, CompAccsComboBox.SelectedItem);
        }

        private void CompTaskComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            SetComponentDrawable(9, CompTaskComboBox.SelectedItem);
        }

        private void CompDeclComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            SetComponentDrawable(10, CompDeclComboBox.SelectedItem);
        }

        private void CompJbibComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            SetComponentDrawable(11, CompJbibComboBox.SelectedItem);
        }

        private void ClipDictComboBox_TextChanged(object sender, EventArgs e)
        {
            LoadClipDict(ClipDictComboBox.Text);
        }

        private void ClipComboBox_TextChanged(object sender, EventArgs e)
        {
            SelectClip(ClipComboBox.Text);
        }

        private void EnableRootMotionCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            SelectedPed.EnableRootMotion = EnableRootMotionCheckBox.Checked;
        }

        private void ExportGltfButton_Click(object sender, EventArgs e)
        {
            if (SelectedPed.Yft == null)
            {
                MessageBox.Show("No ped loaded. Please select a ped first.", "Export Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var sfd = new SaveFileDialog())
            {
                sfd.Title = "Export Ped as glTF/GLB";

                // Build a descriptive filename: pedModel_clipDict_clip.glb
                // Empty/unknown segments are skipped so we don't end up with "ped___idle.glb".
                // Each segment is sanitized to replace characters that are illegal in Windows
                // filenames (< > : " / \ | ? *) and the clip-dict "@" separator.
                var nameParts = new List<string>(3);
                string pedModel = SelectedPed.InitData?.Name;
                if (!string.IsNullOrWhiteSpace(pedModel)) nameParts.Add(SanitizeFileNameSegment(pedModel));
                if (!string.IsNullOrWhiteSpace(ClipDictComboBox.Text)) nameParts.Add(SanitizeFileNameSegment(ClipDictComboBox.Text));
                if (!string.IsNullOrWhiteSpace(ClipComboBox.Text)) nameParts.Add(SanitizeFileNameSegment(ClipComboBox.Text));
                if (nameParts.Count == 0) nameParts.Add(SelectedPed.Name ?? "ped");
                sfd.FileName = string.Join("_", nameParts) + ".glb";

                sfd.Filter = "GLB Binary glTF|*.glb|glTF (embedded)|*.gltf|All files|*.*";
                sfd.DefaultExt = "glb";
                sfd.AddExtension = true;

                if (sfd.ShowDialog() != DialogResult.OK) return;

                try
                {
                    PedGltfExporter.Export(SelectedPed, sfd.FileName);
                    MessageBox.Show("Export completed successfully!\n\nFile: " + sfd.FileName,
                        "Export Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Export failed:\n\n" + ex.Message + "\n\n" + ex.StackTrace,
                        "Export Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void BatchExportGltfButton_Click(object sender, EventArgs e)
        {
            if (GameFileCache.PedsInitDict == null || GameFileCache.PedsInitDict.Count == 0)
            {
                MessageBox.Show("No peds available. Please wait for the game files to finish loading.",
                    "Batch Export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var selDlg = new BatchPedExportForm(GameFileCache))
            {
                if (selDlg.ShowDialog(this) != DialogResult.OK) return;

                var selectedPeds = selDlg.GetSelectedPeds()?.ToList();
                if (selectedPeds == null || selectedPeds.Count == 0)
                {
                    MessageBox.Show("No peds selected for export.", "Batch Export",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Choose output folder
                using (var fbd = new FolderBrowserDialog())
                {
                    fbd.Description = "Select output folder for batch glTF export";
                    fbd.ShowNewFolderButton = true;

                    if (fbd.ShowDialog(this) != DialogResult.OK) return;

                    var outputFolder = fbd.SelectedPath;
                    var successCount = 0;
                    var failCount = 0;
                    var errors = new List<string>();

                    for (int i = 0; i < selectedPeds.Count; i++)
                    {
                        var pedInit = selectedPeds[i];
                        var pedName = pedInit.Name;
                        var filePath = System.IO.Path.Combine(outputFolder, pedName + ".glb");

                        UpdateStatus($"Exporting {i + 1}/{selectedPeds.Count}: {pedName}...");

                        try
                        {
                            // Create a temporary Ped object, init with default components, export without animation
                            var ped = new Ped();
                            ped.Init(pedName, GameFileCache);
                            ped.LoadDefaultComponents(GameFileCache);

                            if (ped.Yft == null)
                            {
                                errors.Add($"{pedName}: No skeleton YFT loaded (skipped)");
                                failCount++;
                                continue;
                            }

                            PedGltfExporter.ExportWithoutAnimation(ped, filePath);
                            successCount++;
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"{pedName}: {ex.Message}");
                            failCount++;
                        }
                    }

                    UpdateStatus("Batch export complete.");

                    var msg = $"Batch export complete!\n\n" +
                              $"Successful: {successCount}\n" +
                              $"Failed: {failCount}\n" +
                              $"Output folder: {outputFolder}";

                    if (errors.Count > 0)
                    {
                        msg += "\n\nErrors:\n" + string.Join("\n", errors.Take(20));
                        if (errors.Count > 20)
                            msg += $"\n...and {errors.Count - 20} more";
                    }

                    MessageBox.Show(msg, "Batch Export Complete", MessageBoxButtons.OK,
                        failCount > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                }
            }
        }


        /// <summary>
        /// Replace characters that are illegal in Windows filenames
        /// (&lt; &gt; : " / \ | ? *) and the GTA clip-dict "@" separator
        /// with underscores, so the segment can be safely embedded in an export filename.
        /// </summary>
        private static string SanitizeFileNameSegment(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '<' || c == '>' || c == ':' || c == '"' || c == '/' ||
                    c == '\\' || c == '|' || c == '?' || c == '*' || c == '@')
                {
                    sb.Append('_');
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
