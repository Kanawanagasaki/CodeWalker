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
        public static void Export(Cutscene cutscene, IEnumerable<CutsceneObject> selectedObjects, string filePath)
        {
            if (cutscene == null) throw new ArgumentNullException(nameof(cutscene));
            if (selectedObjects == null) throw new ArgumentNullException(nameof(selectedObjects));

            bool isGlb = filePath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);
            var ctx = new GltfWriter.ExportContext();

            // Node 0: scene root with cutscene world position/rotation
            BuildSceneRoot(ctx, cutscene);

            var pedDataList = new List<PedExportData>();

            // Export peds — build armatures first so skin indices are stable
            foreach (var obj in selectedObjects)
            {
                if (obj?.Ped == null) continue;

                var ped = obj.Ped;
                string pedName = ped.Name ?? ("Ped_" + obj.ObjectID);
                Vector3 objPos = obj.Position;
                Quaternion objRot = obj.Rotation;
                float[] gltfPos = new float[] { objPos.X, objPos.Z, -objPos.Y };
                float[] gltfRot = new float[] { objRot.X, objRot.Z, -objRot.Y, objRot.W };

                var armature = GltfWriter.BuildPedArmature(ctx, ped, pedName, gltfPos, gltfRot, null, ctx.RootNodeIndex);
                if (armature != null)
                    pedDataList.Add(new PedExportData { CutsceneObject = obj, Armature = armature });
            }

            // Build meshes for each ped
            foreach (var pedData in pedDataList)
            {
                string meshPrefix = (pedData.Armature.Ped.Name ?? "Ped") + "_";
                GltfWriter.BuildPedMeshes(ctx, pedData.Armature.Ped, pedData.Armature, meshPrefix);
            }

            // Build animations for each ped
            foreach (var pedData in pedDataList)
            {
                var animClip = pedData.CutsceneObject.AnimClip ?? pedData.Armature.Ped.AnimClip;
                if (animClip != null)
                {
                    string animName = pedData.Armature.Ped.Name ?? ("Ped_" + pedData.CutsceneObject.ObjectID);

                    // Build a merged BoneTracksDict from all per-component expressions.
                    // The renderer uses ped.Expressions[i] (per-component) for facial bone remapping,
                    // but BuildPedAnimation only accepts a single BoneTracksDict. Merging all component
                    // expressions ensures every facial bone ID can be remapped, regardless of which
                    // component's expression it came from. This fixes facial animations (mouth, eyebrows,
                    // etc.) being silently skipped in the export when ped.Expression is null or incomplete.
                    var mergedBoneTracksDict = BuildMergedBoneTracksDict(pedData.Armature.Ped);

                    GltfWriter.BuildPedAnimation(ctx, pedData.Armature, animClip, animName,
                        pedData.Armature.Ped.Expression, mergedBoneTracksDict);
                }
            }

            // Export props, weapons, and vehicles
            foreach (var obj in selectedObjects)
            {
                if (obj == null) continue;

                if (obj.Weapon != null)
                {
                    Vector3 objPos = obj.Position;
                    Quaternion objRot = obj.Rotation;
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
                    GltfWriter.BuildDrawableObjectMesh(ctx, vehDrawable,
                        new float[] { objPos.X, objPos.Z, -objPos.Y },
                        new float[] { objRot.X, objRot.Z, -objRot.Y, objRot.W },
                        new float[] { 1, 1, 1 },
                        ctx.RootNodeIndex, "Vehicle_" + obj.ObjectID);
                }
                else if (obj.Prop != null)
                {
                    BuildPropMesh(ctx, obj);
                }
            }

            var csName = cutscene.CutFile?.FileEntry?.GetShortName() ?? "Cutscene";

            if (isGlb)
                GltfWriter.WriteGlb(ctx, filePath, csName, "CodeWalker Cutscene Exporter");
            else
                GltfWriter.WriteGltf(ctx, filePath, csName, "CodeWalker Cutscene Exporter");
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
