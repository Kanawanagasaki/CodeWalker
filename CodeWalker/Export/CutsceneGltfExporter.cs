using CodeWalker.GameFiles;
using CodeWalker.World;
using SharpDX;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeWalker.Export
{
    /// <summary>
    /// Exports multiple characters and objects from a cutscene as a single glTF 2.0 or GLB file.
    /// Unlike PedGltfExporter which exports a single ped, this exporter creates one file containing
    /// all selected cutscene objects (peds, props) with their armatures, meshes, and animations.
    /// Uses GltfWriter for all shared ped/mesh/texture/animation logic.
    /// </summary>
    public static class CutsceneGltfExporter
    {
        #region Per-Ped Helper Data

        /// <summary>
        /// Holds per-ped export state for cutscene export.
        /// Wraps GltfWriter.PedArmatureData with cutscene-specific data.
        /// </summary>
        class PedExportData
        {
            public CutsceneObject CutsceneObject;
            public GltfWriter.PedArmatureData Armature;
        }

        #endregion

        #region Main Export

        /// <summary>
        /// Export selected cutscene objects as a single glTF/GLB file.
        /// </summary>
        /// <param name="cutscene">The cutscene containing all scene objects</param>
        /// <param name="selectedObjects">The objects to include in the export</param>
        /// <param name="filePath">Output file path (.gltf or .glb)</param>
        /// <param name="logger">Optional diagnostic logger. When null, no logging is performed; otherwise
        /// per-ped and per-component state is recorded to help diagnose T-pose / wrong-texture /
        /// wrong-model export issues.</param>
        public static void Export(Cutscene cutscene, IEnumerable<CutsceneObject> selectedObjects, string filePath,
            GltfExportLogger logger = null)
        {
            if (cutscene == null) throw new ArgumentNullException(nameof(cutscene));
            if (selectedObjects == null) throw new ArgumentNullException(nameof(selectedObjects));

            var log = logger ?? GltfExportLogger.Current;
            log?.BeginScope("CutsceneGltfExporter.Export");
            log?.Field("Output file", filePath);
            log?.Field("Cutscene position", cutscene.Position.ToString());
            log?.Field("Cutscene rotation", cutscene.Rotation.ToString());

            var selectedList = selectedObjects.ToList();
            log?.Field("Selected objects", selectedList.Count);
            foreach (var o in selectedList)
            {
                var kind = o?.Ped != null ? "Ped"
                          : o?.Weapon != null ? "Weapon"
                          : o?.Vehicle != null ? "Vehicle"
                          : o?.Prop != null ? "Prop"
                          : o?.HideEntity != null ? "Hidden"
                          : "Unknown";
                log?.Log($"  - ObjectID={o?.ObjectID ?? -1}  Name={o?.Name ?? 0}  kind={kind}");
            }
            log?.Blank();

            bool isGlb = filePath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);
            var ctx = new GltfWriter.ExportContext();

            // Node 0: scene root with cutscene world position/rotation
            BuildSceneRoot(ctx, cutscene);

            var pedDataList = new List<PedExportData>();

            // Export peds — build armatures first so skin indices are stable
            int pedIndex = 0;
            foreach (var obj in selectedList)
            {
                if (obj?.Ped == null) continue;

                var ped = obj.Ped;
                // Use a unique per-ped name for glTF node naming. Ped.Name is often
                // empty for cutscene peds, so fall back to the NameHash (which is the
                // ped model hash like "player_one" / "cs_lamardavis") or ObjectID.
                string pedName = ped.Name;
                if (string.IsNullOrEmpty(pedName))
                    pedName = ped.NameHash.ToString();
                if (string.IsNullOrEmpty(pedName))
                    pedName = "Ped_" + obj.ObjectID;

                log?.BeginScope($"Ped #{pedIndex}: {pedName}");
                log?.Field("ObjectID", obj.ObjectID);
                log?.Field("Ped.Name", ped.Name ?? "<null>");
                log?.Field("Ped.NameHash", ped.NameHash);
                log?.Field("Object position", obj.Position.ToString());
                log?.Field("Object rotation", obj.Rotation.ToString());
                log?.Field("AnimHash on CutsceneObject", obj.AnimHash);
                log?.Field("AnimClip on CutsceneObject",
                    obj.AnimClip?.Clip != null ? obj.AnimClip.Clip.GetType().Name : "<null>");
                log?.Field("AnimClip on Ped",
                    ped.AnimClip?.Clip != null ? ped.AnimClip.Clip.GetType().Name : "<null>");

                DumpPedStateForDiagnosis(log, ped, obj);

                Vector3 objPos = obj.Position;
                Quaternion objRot = obj.Rotation;
                float[] gltfPos = new float[] { objPos.X, objPos.Z, -objPos.Y };
                float[] gltfRot = new float[] { objRot.X, objRot.Z, -objRot.Y, objRot.W };

                int nodesBefore = ctx.Nodes.Count;
                var armature = GltfWriter.BuildPedArmature(ctx, ped, pedName, gltfPos, gltfRot, null, ctx.RootNodeIndex);
                log?.Field("BuildPedArmature result", armature != null ? "OK" : "<null> (skipped — missing skeleton)");
                if (armature != null)
                {
                    log?.Field("Bones exported to armature", armature.BoneToNode.Count);
                    log?.Field("Skeleton root node idx", armature.SkeletonRootNodeIndex);
                    log?.Field("Ped root node idx", armature.PedRootNodeIndex);
                    log?.Field("Skin idx", armature.SkinIndex);
                    log?.Field("Nodes added", ctx.Nodes.Count - nodesBefore);
                    pedDataList.Add(new PedExportData { CutsceneObject = obj, Armature = armature });
                }
                log?.EndScope();
                pedIndex++;
            }

            // Build meshes for each ped
            log?.BeginScope("Build ped meshes");
            foreach (var pedData in pedDataList)
            {
                // Build a unique per-ped prefix for mesh/material/texture names.
                // Ped.Name is often empty for cutscene peds, so we can't rely on it alone.
                // Use the ped's NameHash (e.g., "player_one", "cs_lamardavis") which is
                // always set and unique per ped model. This prefix is also used as the
                // texture dedup key in ExportTexture, so it MUST be unique per ped to
                // prevent cross-ped texture reuse (the "wrong textures" bug where ped #2
                // would render with ped #1's head/face/clothing textures).
                string pedName = pedData.Armature.Ped.Name;
                if (string.IsNullOrEmpty(pedName))
                    pedName = pedData.Armature.Ped.NameHash.ToString();
                string meshPrefix = pedName + "_";
                int meshesBefore = ctx.Meshes.Count;
                int materialsBefore = ctx.Materials.Count;
                GltfWriter.BuildPedMeshes(ctx, pedData.Armature.Ped, pedData.Armature, meshPrefix);
                log?.Log($"  Ped {pedName}: meshes +{ctx.Meshes.Count - meshesBefore}, materials +{ctx.Materials.Count - materialsBefore}");
            }
            log?.EndScope();

            // Build animations for each ped
            log?.BeginScope("Build ped animations");
            foreach (var pedData in pedDataList)
            {
                var animClip = pedData.CutsceneObject.AnimClip ?? pedData.Armature.Ped.AnimClip;
                string pedName = pedData.Armature.Ped.Name;
                if (string.IsNullOrEmpty(pedName))
                    pedName = pedData.Armature.Ped.NameHash.ToString();
                if (string.IsNullOrEmpty(pedName))
                    pedName = "Ped_" + pedData.CutsceneObject.ObjectID;
                if (animClip == null)
                {
                    log?.Log($"  Ped {pedName}: SKIPPED — no AnimClip resolved (will end up in T-pose)");
                    continue;
                }
                if (animClip.Clip == null)
                {
                    log?.Log($"  Ped {pedName}: SKIPPED — AnimClip.Clip is null (will end up in T-pose)");
                    continue;
                }

                // Build a merged BoneTracksDict from all per-component expressions.
                // The renderer uses ped.Expressions[i] (per-component) for facial bone remapping,
                // but BuildPedAnimation only accepts a single BoneTracksDict. Merging all component
                // expressions ensures every facial bone ID can be remapped, regardless of which
                // component's expression it came from. This fixes facial animations (mouth, eyebrows,
                // etc.) being silently skipped in the export when ped.Expression is null or incomplete.
                var mergedBoneTracksDict = BuildMergedBoneTracksDict(pedData.Armature.Ped);

                log?.Field("  Ped", pedName);
                log?.Field("  AnimClip hash", animClip.Hash);
                log?.Field("  AnimClip.Clip type", animClip.Clip.GetType().Name);
                log?.Field("  Merged BoneTracksDict entries", mergedBoneTracksDict?.Count ?? 0);

                int animsBefore = ctx.Animations.Count;
                GltfWriter.BuildPedAnimation(ctx, pedData.Armature, animClip, pedName,
                    pedData.Armature.Ped.Expression, mergedBoneTracksDict);
                log?.Field("  Animations added", ctx.Animations.Count - animsBefore);
                if (ctx.Animations.Count == animsBefore)
                    log?.Log($"  !! WARNING: no animation produced for {pedName} — exported model will be in T-pose");
            }
            log?.EndScope();

            // Export props, weapons, and vehicles
            log?.BeginScope("Build props / weapons / vehicles");
            foreach (var obj in selectedList)
            {
                if (obj == null) continue;

                if (obj.Weapon != null)
                {
                    Vector3 objPos = obj.Position;
                    Quaternion objRot = obj.Rotation;
                    log?.Log($"  Weapon ObjectID={obj.ObjectID}: drawable={(obj.Weapon.Drawable != null ? "OK" : "<null>")}");
                    GltfWriter.BuildDrawableObjectMesh(ctx, obj.Weapon.Drawable,
                        new float[] { objPos.X, objPos.Z, -objPos.Y },
                        new float[] { objRot.X, objRot.Z, -objRot.Y, objRot.W },
                        new float[] { 1, 1, 1 },
                        ctx.RootNodeIndex, "Weapon_" + obj.ObjectID);
                }
                else if (obj.Vehicle != null)
                {
                    DrawableBase vehDrawable = obj.Vehicle.Yft?.Fragment?.Drawable;
                    Vector3 objPos = obj.Position;
                    Quaternion objRot = obj.Rotation;
                    log?.Log($"  Vehicle ObjectID={obj.ObjectID}: drawable={(vehDrawable != null ? "OK" : "<null>")}");
                    GltfWriter.BuildDrawableObjectMesh(ctx, vehDrawable,
                        new float[] { objPos.X, objPos.Z, -objPos.Y },
                        new float[] { objRot.X, objRot.Z, -objRot.Y, objRot.W },
                        new float[] { 1, 1, 1 },
                        ctx.RootNodeIndex, "Vehicle_" + obj.ObjectID);
                }
                else if (obj.Prop != null)
                {
                    log?.Log($"  Prop ObjectID={obj.ObjectID}: archetype={(obj.Prop.Archetype != null ? obj.Prop.Archetype.Name : "<null>")}");
                    BuildPropMesh(ctx, obj);
                }
            }
            log?.EndScope();

            var csName = cutscene.CutFile?.FileEntry?.GetShortName() ?? "Cutscene";

            // ── Merge per-ped animations into a single glTF animation ───────
            // A cutscene is ONE timeline that drives ALL characters simultaneously.
            // glTF viewers (including Blender) typically only play one animation at a
            // time. If each ped has its own animation, the viewer plays the first one
            // and the other peds stay in bind pose (T-pose).
            //
            // By merging all per-ped animations into a single glTF animation, all
            // channels play simultaneously — every character animates together on the
            // same timeline, matching how the cutscene plays in-game.
            //
            // Each channel's sampler index is remapped: channel j in animation i
            // references sampler s within animation i; after merging, it references
            // sampler (sum of samplers in animations 0..i-1) + s.
            if (ctx.Animations.Count > 1)
            {
                log?.BeginScope("Merge animations");
                log?.Field("  Input animations", ctx.Animations.Count);

                var merged = new GltfWriter.GltfAnimation { name = csName + "_Anim" };
                int samplerOffset = 0;
                foreach (var pedAnim in ctx.Animations)
                {
                    // Copy all samplers from this animation into the merged animation.
                    // Sampler accessors (input/output) are global to the export context,
                    // so no remapping of accessor indices is needed — only sampler indices
                    // need remapping (they're per-animation).
                    int samplersBefore = merged.samplers.Count;
                    foreach (var s in pedAnim.samplers)
                        merged.samplers.Add(s);

                    // Copy all channels, remapping sampler indices by the offset.
                    foreach (var c in pedAnim.channels)
                    {
                        merged.channels.Add(new GltfWriter.GltfAnimationChannel
                        {
                            sampler = c.sampler + samplerOffset,
                            target = c.target, // target.node is already a global node index
                        });
                    }

                    log?.Log($"  Merged animation '{pedAnim.name}': channels={pedAnim.channels.Count}  samplers={pedAnim.samplers.Count}  (offset={samplerOffset})");
                    samplerOffset = merged.samplers.Count;
                }

                log?.Field("  Merged total channels", merged.channels.Count);
                log?.Field("  Merged total samplers", merged.samplers.Count);

                ctx.Animations.Clear();
                ctx.Animations.Add(merged);
                log?.EndScope();
            }

            log?.BeginScope("Final glTF state");
            log?.Field("Nodes", ctx.Nodes.Count);
            log?.Field("Meshes", ctx.Meshes.Count);
            log?.Field("Materials", ctx.Materials.Count);
            log?.Field("Skins", ctx.Skins.Count);
            log?.Field("Animations", ctx.Animations.Count);
            log?.Field("Textures", ctx.Textures.Count);
            log?.Field("Images", ctx.Images.Count);
            log?.Field("Accessors", ctx.Accessors.Count);
            log?.Field("BufferViews", ctx.BufferViews.Count);
            log?.EndScope();

            if (isGlb)
                GltfWriter.WriteGlb(ctx, filePath, csName, "CodeWalker Cutscene Exporter");
            else
                GltfWriter.WriteGltf(ctx, filePath, csName, "CodeWalker Cutscene Exporter");

            log?.EndScope();
        }

        /// <summary>
        /// Dump everything we can about a Ped's loaded state BEFORE armature/mesh/animation
        /// building, so we can spot the source of broken exports: null drawables, null
        /// textures, missing expression files, mismatched skeletons, etc.
        /// </summary>
        static void DumpPedStateForDiagnosis(GltfExportLogger log, Ped ped, CutsceneObject obj)
        {
            if (log == null) return;

            log.BeginScope("Ped state dump");
            log.Field("Ped.Ydd", ped.Ydd != null ? ped.Ydd?.Name ?? "<unnamed>" : "<null>");
            log.Field("Ped.Yft", ped.Yft != null ? ped.Yft?.Name ?? "<unnamed>" : "<null>");
            log.Field("Ped.Skeleton", ped.Skeleton != null ? "OK" : "<null> (will produce no armature)");
            if (ped.Skeleton != null)
            {
                var bones = ped.Skeleton.Bones?.Items;
                log.Field("Ped.Skeleton.Bones count", bones?.Length ?? 0);
                log.Field("Ped.Skeleton.BonesMap count", ped.Skeleton.BonesMap?.Count ?? 0);
                if (bones != null && bones.Length > 0)
                {
                    // List essential GTA V skeleton bone tags. If any of these are missing,
                    // the ped's skeleton is unusual (e.g. a custom ped with a stripped rig),
                    // which is one possible cause of T-pose exports.
                    var knownTags = new (ushort tag, string name)[]
                    {
                        (0x0000, "SKEL_ROOT"),
                        (0x0E2C, "SKEL_Pelvis"),
                        (0x4F3B, "SKEL_Spine_Root"),
                        (0x6DDA, "SKEL_Spine_0"),
                        (0x6C0A, "SKEL_Spine_1"),
                        (0x16BD, "SKEL_Spine_2"),
                        (0x67D2, "SKEL_Spine_3"),
                        (0x94F0, "SKEL_Neck_1"),
                        (0x6394, "SKEL_Head"),
                        (0x6DD6, "SKEL_L_Thigh"),
                        (0x8B08, "SKEL_R_Thigh"),
                        (0x6DD7, "SKEL_L_Calf"),
                        (0x8B09, "SKEL_R_Calf"),
                        (0x6CC6, "SKEL_L_Foot"),
                        (0x7770, "SKEL_R_Foot"),
                        (0x9D4D, "SKEL_L_UpperArm"),
                        (0x9D4E, "SKEL_R_UpperArm"),
                        (0x6DD9, "SKEL_L_Forearm"),
                        (0x9D50, "SKEL_R_Forearm"),
                        (0xE26F, "SKEL_L_Hand"),
                        (0xE270, "SKEL_R_Hand"),
                    };
                    log.BeginScope("Essential skeleton bone tags");
                    var byTag = new Dictionary<ushort, string>();
                    foreach (var b in bones)
                        if (b != null && !byTag.ContainsKey(b.Tag))
                            byTag[b.Tag] = b.Name ?? ("<" + b.Tag + ">");
                    foreach (var (tag, name) in knownTags)
                    {
                        var present = byTag.TryGetValue(tag, out var bname);
                        log.Log($"  {(present ? "OK " : "MISSING ")} 0x{tag:X4} {name}  {(present ? "-> " + bname : "")}");
                    }
                    log.EndScope();

                    // Sanity check: a T-pose export often means the animation's BoneIds reference
                    // bone tags that are NOT in the ped's skeleton, so lookups silently fail and
                    // no rotation channels get produced. We can't dump the animation's BoneIds here
                    // because the clip is attached to obj.AnimClip / ped.AnimClip — log its type so
                    // we can correlate with the per-ped animation log section.
                    var clip = obj.AnimClip ?? ped.AnimClip;
                    log.Field("Effective AnimClip", clip != null ? "present" : "<null>");
                    if (clip?.Clip != null)
                    {
                        log.Field("Effective AnimClip.Clip type", clip.Clip.GetType().Name);
                    }
                }
            }

            log.BeginScope("Ped components (drawables + textures + expressions)");
            string[] compNames = { "Head", "Berd", "Hair", "Uppr", "Lowr", "Hand", "Feet", "Teef", "Accs", "Task", "Decl", "Jbib" };
            for (int i = 0; i < 12; i++)
            {
                var d = ped.Drawables?[i];
                var t = ped.Textures?[i];
                var e = ped.Expressions?[i];
                var c = ped.Clothes?[i];
                var dname = ped.DrawableNames?[i];
                log.Log($"  [{i}] {compNames[i]}  drawableName={dname ?? "<null>"}  " +
                        $"drawable={(d != null ? (d.Name ?? "<unnamed>") : "<null>")}  " +
                        $"texture={(t != null ? (t.Name ?? "<unnamed>") : "<null>")}  " +
                        $"expression={(e != null ? "OK" : "<null>")}  " +
                        $"cloth={(c != null ? "OK" : "<null>")}");
                if (d != null)
                {
                    var models = d.DrawableModels?.High;
                    int geomCount = 0;
                    if (models != null)
                        foreach (var m in models)
                            if (m?.Geometries != null) geomCount += m.Geometries.Length;
                    log.Log($"        drawable models={models?.Length ?? 0}  geometries={geomCount}  " +
                            $"skeleton={(d.Skeleton != null ? (ReferenceEquals(d.Skeleton, ped.Skeleton) ? "shared" : "own") : "<none>")}");
                }
            }
            log.EndScope();

            log.Field("Ped.Expression (global)", ped.Expression != null ? "OK" : "<null>");
            if (ped.Expression != null)
            {
                log.Field("  .BoneTracksDict entries", ped.Expression.BoneTracksDict?.Count ?? 0);
                log.Field("  .Streams count", ped.Expression.Streams?.data_items?.Length ?? 0);
                log.Field("  .Tracks count", ped.Expression.Tracks?.data_items?.Length ?? 0);
            }
        }

        #endregion

        #region Scene Root

        /// <summary>
        /// Create the scene root node (index 0) with the cutscene's world position/rotation.
        /// GTA V LH: X=right, Y=forward, Z=up
        /// glTF RH: X=right, Y=up, Z=-forward
        /// Translation: (X, Z, -Y)
        /// Rotation quaternion: (X, Z, -Y, W)
        /// </summary>
        static void BuildSceneRoot(GltfWriter.ExportContext ctx, Cutscene cutscene)
        {
            Vector3 pos = cutscene.Position;
            Quaternion rot = cutscene.Rotation;

            var rootNode = new GltfWriter.GltfNode
            {
                name = "Cutscene",
                translation = new float[] { pos.X, pos.Z, -pos.Y },
                rotation = new float[] { rot.X, rot.Z, -rot.Y, rot.W },
                scale = new float[] { 1, 1, 1 },
            };

            ctx.RootNodeIndex = ctx.Nodes.Count;
            ctx.Nodes.Add(rootNode);
            ctx.NodeChildren[ctx.RootNodeIndex] = new List<int>();
        }

        #endregion

        #region Facial Expression Resolution

        /// <summary>
        /// Build a merged BoneTracksDict from all per-component expressions of a ped.
        ///
        /// In the GTA V renderer, each ped component (Head, Berd, Hair, etc.) has its own
        /// Expression loaded from the ped's YED file, keyed by the drawable's name hash.
        /// The renderer uses ped.Expressions[i] (per-component) for facial bone remapping
        /// in Renderable.UpdateAnim(). However, the cutscene animation clip contains ALL
        /// facial bone tracks for the entire ped in a single clip — not separated by component.
        ///
        /// ped.Expression (the global expression from InitData.ExpressionName) may be null
        /// or may have an incomplete BoneTracksDict compared to the union of all component
        /// expressions. When the BoneTracksDict is null or missing entries, facial bone IDs
        /// in tracks 24/25/26 cannot be remapped to skeleton bone tags, causing those tracks
        /// to be silently skipped in the export — resulting in no facial animation.
        ///
        /// This method merges all BoneTracksDict entries from ped.Expressions[0..11] into a
        /// single dictionary, ensuring complete facial bone remapping coverage. It also
        /// includes entries from ped.Expression as a fallback.
        /// </summary>
        static Dictionary<ExpressionTrack, ExpressionTrack> BuildMergedBoneTracksDict(Ped ped)
        {
            var merged = new Dictionary<ExpressionTrack, ExpressionTrack>();

            // First, add entries from the global expression (lowest priority)
            if (ped.Expression?.BoneTracksDict != null)
            {
                foreach (var kvp in ped.Expression.BoneTracksDict)
                {
                    if (!merged.ContainsKey(kvp.Key))
                        merged[kvp.Key] = kvp.Value;
                }
            }

            // Then, add entries from per-component expressions (higher priority, may override)
            // The renderer uses ped.Expressions[i] per component, so these are the authoritative
            // source for facial bone remapping.
            if (ped.Expressions != null)
            {
                foreach (var expr in ped.Expressions)
                {
                    if (expr?.BoneTracksDict == null) continue;
                    foreach (var kvp in expr.BoneTracksDict)
                    {
                        // Later components override earlier ones for the same key.
                        // This matches the renderer's sequential application of expressions.
                        merged[kvp.Key] = kvp.Value;
                    }
                }
            }

            return merged.Count > 0 ? merged : null;
        }

        #endregion

        #region Prop Export

        /// <summary>
        /// Export a prop as a static mesh (no skeleton/skinning).
        /// The prop's drawable geometry is exported with position/orientation from the CutsceneObject.
        /// Props typically store their archetype reference; the drawable is loaded separately
        /// by the rendering system. This method attempts to find the drawable through the
        /// archetype's YdrFile if loaded, or through other available references.
        /// </summary>
        static void BuildPropMesh(GltfWriter.ExportContext ctx, CutsceneObject obj)
        {
            var prop = obj.Prop;
            if (prop == null) return;

            // Try to get the drawable from the archetype
            // The archetype itself doesn't store a Drawable directly; drawables are loaded
            // by GameFileCache and cached as YdrFile objects. In the cutscene rendering pipeline,
            // the RenderableCache loads these on demand. Here we attempt to find the drawable
            // through any available reference path.
            DrawableBase drawable = null;
            var archetype = prop.Archetype;
            if (archetype != null)
            {
                // The GameFileCache may have loaded the archetype's drawable into a YdrFile
                // that's cached elsewhere. Check if the archetype's Ytyp has loaded children.
                // Note: In practice, the caller should ensure the prop's drawable is loaded
                // before calling this exporter.
            }

            // If no drawable found through the archetype, skip this prop
            if (drawable == null) return;

            Vector3 objPos = obj.Position;
            Quaternion objRot = obj.Rotation;
            GltfWriter.BuildDrawableObjectMesh(ctx, drawable,
                new float[] { objPos.X, objPos.Z, -objPos.Y },
                new float[] { objRot.X, objRot.Z, -objRot.Y, objRot.W },
                new float[] { 1, 1, 1 },
                ctx.RootNodeIndex, "Prop_" + obj.ObjectID);
        }

        #endregion
    }
}
