using CodeWalker.GameFiles;
using CodeWalker.World;
using System;

namespace CodeWalker.Export
{
    /// <summary>
    /// Exports a Ped with mesh, textures, armature and animation to glTF 2.0 or GLB format.
    /// Thin wrapper around GltfWriter shared logic.
    /// </summary>
    public static class PedGltfExporter
    {
        public static void Export(Ped ped, string filePath)
        {
            bool isGlb = filePath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);
            var ctx = new GltfWriter.ExportContext();

            var pedData = GltfWriter.BuildPedArmature(ctx, ped, ped.Name ?? "Ped", null, null, null, -1);
            GltfWriter.BuildPedMeshes(ctx, ped, pedData, "");
            if (ped.AnimClip != null)
            {
                // Build a merged BoneTracksDict from all per-component expressions, matching
                // the cutscene exporter's logic. Without this, facial animation tracks
                // (mouth, eyebrows, etc.) from per-component expressions are silently skipped
                // when ped.Expression is null or has an incomplete BoneTracksDict.
                var mergedBoneTracksDict = GltfWriter.BuildMergedBoneTracksDict(ped);
                GltfWriter.BuildPedAnimation(ctx, pedData, ped.AnimClip, ped.Name ?? "Ped",
                    ped.Expression, mergedBoneTracksDict);
            }

            if (isGlb) GltfWriter.WriteGlb(ctx, filePath);
            else GltfWriter.WriteGltf(ctx, filePath);
        }

        /// <summary>
        /// Exports a Ped with mesh, textures, and armature only — no animation.
        /// Intended for batch export where animations are not needed.
        /// </summary>
        public static void ExportWithoutAnimation(Ped ped, string filePath)
        {
            bool isGlb = filePath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);
            var ctx = new GltfWriter.ExportContext();

            var pedData = GltfWriter.BuildPedArmature(ctx, ped, ped.Name ?? "Ped", null, null, null, -1);
            GltfWriter.BuildPedMeshes(ctx, ped, pedData, "");
            // Intentionally skip animation — batch export is geometry + textures + skeleton only

            if (isGlb) GltfWriter.WriteGlb(ctx, filePath);
            else GltfWriter.WriteGltf(ctx, filePath);
        }
    }
}
