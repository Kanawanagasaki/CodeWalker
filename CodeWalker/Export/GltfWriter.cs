using CodeWalker.GameFiles;
using CodeWalker.Utils;
using CodeWalker.World;
using SharpDX;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace CodeWalker.Export
{
    /// <summary>
    /// Shared glTF 2.0 infrastructure: data structures, export context, file writing,
    /// coordinate conversion, and utility methods used by all glTF exporters.
    /// Also contains the shared ped export logic (armature, meshes, animation, textures)
    /// used by both PedGltfExporter and CutsceneGltfExporter.
    /// </summary>
    public static class GltfWriter
    {
        // ── Expression VM tunable constants (must match Renderable.cs) ──
        // These scale factors compensate for the difference between how the
        // animation loop and the VM produce face bone transforms.
        const float VmRotScaleFace = 0.3f;
        const float VmRotScaleEye = 0.15f;
        const float VmPosScale = 0.3f;
        const float VmPosMaxOffset = 0.5f;

        #region glTF Data Structures

        public class GltfScene
        {
            public string name = "";
            public List<int> nodes = new List<int>();
        }

        public class GltfNode
        {
            public string name;
            public int? mesh;
            public int? skin;
            public float[] translation;
            public float[] rotation;
            public float[] scale;
        }

        public class GltfMesh
        {
            public string name;
            public List<GltfMeshPrimitive> primitives = new List<GltfMeshPrimitive>();
        }

        public class GltfMeshPrimitive
        {
            public Dictionary<string, int> attributes = new Dictionary<string, int>();
            public int? indices = null;
            public int? material = null;
        }

        public class GltfSkin
        {
            public string name;
            public int inverseBindMatrices;
            public int skeleton;
            public List<int> joints = new List<int>();
        }

        public class GltfAccessor
        {
            public int bufferView;
            public int byteOffset;
            public int componentType;
            public int count;
            public string type;
            public float[] min;
            public float[] max;
        }

        public class GltfBufferView
        {
            public int buffer;
            public int byteOffset;
            public int byteLength;
            public int? byteStride;
            public int? target;
        }

        public class GltfMaterial
        {
            public string name;
            public GltfPbrMetallicRoughness pbrMetallicRoughness;
            public string alphaMode; // "OPAQUE" (default), "MASK", "BLEND"
            public float? alphaCutoff; // used with MASK mode, default 0.5
            public bool doubleSided;
        }

        public class GltfPbrMetallicRoughness
        {
            public float[] baseColorFactor = new float[] { 1f, 1f, 1f, 1f };
            public GltfTextureInfo baseColorTexture;
            public float metallicFactor = 0f;
            public float roughnessFactor = 1f;
        }

        public class GltfTextureInfo
        {
            public int index;
            public int texCoord = 0;
        }

        public class GltfTexture
        {
            public int source;
            public int? sampler;
        }

        public class GltfImage
        {
            public string name;
            public string mimeType = "image/png";
            public int? bufferView;
        }

        public class GltfSampler
        {
            public int magFilter = 9729;
            public int minFilter = 9987;
            public int wrapS = 10497;
            public int wrapT = 10497;
        }

        public class GltfAnimation
        {
            public string name;
            public List<GltfAnimationChannel> channels = new List<GltfAnimationChannel>();
            public List<GltfAnimationSampler> samplers = new List<GltfAnimationSampler>();
        }

        public class GltfAnimationChannel
        {
            public int sampler;
            public GltfAnimationChannelTarget target;
        }

        public class GltfAnimationChannelTarget
        {
            public int node;
            public string path;
        }

        public class GltfAnimationSampler
        {
            public int input;
            public int output;
            public string interpolation = "LINEAR";
        }

        #endregion

        #region Export Context

        public class ExportContext
        {
            public List<byte> BufferData = new List<byte>();
            public List<GltfBufferView> BufferViews = new List<GltfBufferView>();
            public List<GltfAccessor> Accessors = new List<GltfAccessor>();
            public List<GltfMesh> Meshes = new List<GltfMesh>();
            public List<GltfNode> Nodes = new List<GltfNode>();
            public List<GltfSkin> Skins = new List<GltfSkin>();
            public List<GltfMaterial> Materials = new List<GltfMaterial>();
            public List<GltfTexture> Textures = new List<GltfTexture>();
            public List<GltfImage> Images = new List<GltfImage>();
            public List<GltfSampler> Samplers = new List<GltfSampler>();
            public List<GltfAnimation> Animations = new List<GltfAnimation>();
            public List<GltfScene> Scenes = new List<GltfScene>();

            public Dictionary<int, int> BoneToNode = new Dictionary<int, int>();
            public Dictionary<ushort, int> BoneTagToNode = new Dictionary<ushort, int>();
            public int RootNodeIndex = -1;
            public int PedRootNodeIndex = -1;
            public Dictionary<long, int> TextureToGltfIndex = new Dictionary<long, int>();
            public Dictionary<int, List<int>> NodeChildren = new Dictionary<int, List<int>>();
            public List<int> MeshNodeIndices = new List<int>();

            public int AddBufferView(byte[] data, int? byteStride = null, int? target = null)
            {
                int offset = BufferData.Count;
                int pad = (4 - (offset % 4)) % 4;
                for (int i = 0; i < pad; i++) BufferData.Add(0);
                offset += pad;
                BufferData.AddRange(data);

                var bv = new GltfBufferView
                {
                    buffer = 0,
                    byteOffset = offset,
                    byteLength = data.Length,
                };
                if (byteStride.HasValue) bv.byteStride = byteStride.Value;
                if (target.HasValue) bv.target = target.Value;

                int idx = BufferViews.Count;
                BufferViews.Add(bv);
                return idx;
            }

            public int AddAccessor(int bufferView, int componentType, string type, int count, float[] min = null, float[] max = null)
            {
                var a = new GltfAccessor
                {
                    bufferView = bufferView,
                    byteOffset = 0,
                    componentType = componentType,
                    type = type,
                    count = count,
                };
                if (min != null) a.min = min;
                if (max != null) a.max = max;
                int idx = Accessors.Count;
                Accessors.Add(a);
                return idx;
            }
        }

        #endregion

        #region Constants

        // Shader hash for ped_hair_spiked.sps — used to detect hair control mesh geometries
        public const uint HairShaderHash = 100720695;
        // ShaderParamNames.orderNumber hash
        public const uint OrderNumberHash = 1617153586;

        // Bone tags for ThighRoll bones that need rotation copied from main Thigh bones.
        // This replicates the hardcoded hack in Renderable.cs (lines 517-518):
        //   RB_L_ThighRoll (23639) -> SKEL_L_Thigh (58271)
        //   RB_R_ThighRoll (6442)  -> SKEL_R_Thigh (51826)
        // Without this, ThighRoll bones stay at bind pose while the thigh animates,
        // causing the leg to "arch" or appear noodly in Blender.
        public const ushort BoneTag_RB_L_ThighRoll = 23639;
        public const ushort BoneTag_RB_R_ThighRoll = 6442;
        public const ushort BoneTag_SKEL_L_Thigh = 58271;
        public const ushort BoneTag_SKEL_R_Thigh = 51826;

        #endregion

        #region Cloth Vertex Data

        /// <summary>
        /// Helper data for cloth vertex bone weight computation.
        /// Passed to BuildPrimitive for vertices where blendIndices[2] == 255.
        /// </summary>
        public class ClothVertexData
        {
            public CharClothBoneWeightsInds[] BoneWeightsInds;  // from Controller.BoneWeightsInds.data_items
            public int[] ClothBoneToArrayIndex;                  // maps ClothInstance.Bones[i] -> skeleton bone array index
            public int DefaultClothBoneArrayIndex;               // fallback cloth bone
        }

        #endregion

        #region Ped Armature Data

        /// <summary>
        /// Holds per-ped export state: bone-to-node mappings, skin index, root node index.
        /// Needed because a cutscene can contain multiple peds, each with their own
        /// armature and skin within the same glTF file.
        /// </summary>
        public class PedArmatureData
        {
            public Ped Ped;
            public int PedRootNodeIndex = -1;
            public int SkeletonRootNodeIndex = -1;
            public int SkinIndex = -1;
            public Dictionary<int, int> BoneToNode = new Dictionary<int, int>();
            public Dictionary<ushort, int> BoneTagToNode = new Dictionary<ushort, int>();
        }

        #endregion

        #region Ped Armature Builder

        /// <summary>
        /// Build armature for a ped: creates ped root node, bone nodes, parent-child
        /// relationships, inverse bind matrices, and a skin.
        /// </summary>
        /// <param name="ctx">The export context</param>
        /// <param name="ped">The ped whose skeleton to export</param>
        /// <param name="pedName">Name for the ped root node</param>
        /// <param name="pedRootTranslation">glTF translation for ped root (null = [0,0,0])</param>
        /// <param name="pedRootRotation">glTF rotation for ped root (null = [0,0,0,1])</param>
        /// <param name="pedRootScale">glTF scale for ped root (null = [1,1,1])</param>
        /// <param name="parentNodeIndex">-1 = ped root IS the scene root (single ped export);
        /// >=0 = attach ped root as child of that node (cutscene multi-ped)</param>
        /// <returns>PedArmatureData with per-ped mappings, or null if skeleton is missing</returns>
        public static PedArmatureData BuildPedArmature(ExportContext ctx, Ped ped, string pedName,
            float[] pedRootTranslation, float[] pedRootRotation, float[] pedRootScale,
            int parentNodeIndex)
        {
            var skeleton = ped.Skeleton;
            if (skeleton == null) return null;
            var bones = skeleton.Bones?.Items;
            if (bones == null || bones.Length == 0) return null;

            var pedData = new PedArmatureData
            {
                Ped = ped,
            };

            // Ped root node
            var pedRoot = new GltfNode
            {
                name = pedName,
                translation = pedRootTranslation ?? new float[] { 0, 0, 0 },
                rotation = pedRootRotation ?? new float[] { 0, 0, 0, 1 },
                scale = pedRootScale ?? new float[] { 1, 1, 1 },
            };
            pedData.PedRootNodeIndex = ctx.Nodes.Count;
            ctx.Nodes.Add(pedRoot);
            ctx.NodeChildren[pedData.PedRootNodeIndex] = new List<int>();

            // When parentNodeIndex == -1, the ped root IS the scene root.
            // Set ctx fields for compatibility.
            if (parentNodeIndex == -1)
            {
                ctx.RootNodeIndex = pedData.PedRootNodeIndex;
                ctx.PedRootNodeIndex = pedData.PedRootNodeIndex;
            }
            else
            {
                // Attach ped root as child of the specified parent node
                ctx.NodeChildren[parentNodeIndex].Add(pedData.PedRootNodeIndex);
            }

            // GTA V is left-handed: X=right, Y=forward, Z=up
            // glTF is right-handed Y-up: X=right, Y=up, Z=backward(-forward)
            // Conversion: glTF_X = GTA_X, glTF_Y = GTA_Z, glTF_Z = -GTA_Y
            // The coordinate change matrix P is a proper rotation (det=+1),
            // so scale just swaps Y/Z components without negation: (sx, sz, sy)

            // Bone nodes
            for (int i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                var node = new GltfNode
                {
                    name = bone.Name ?? ("Bone_" + bone.Tag),
                };
                Vector3 t = bone.Translation;
                Quaternion r = bone.Rotation;
                Vector3 s = bone.Scale;
                node.translation = new float[] { t.X, t.Z, -t.Y };
                // Quaternion conversion for axis swap (X,Z,-Y): verified via similarity transform
                node.rotation = new float[] { r.X, r.Z, -r.Y, r.W };
                // Scale: axis swap only, no negation (P is a rotation, P*S*P^T = diag(sx,sz,sy))
                node.scale = new float[] { s.X, s.Z, s.Y };

                int nodeIdx = ctx.Nodes.Count;
                ctx.Nodes.Add(node);
                pedData.BoneToNode[i] = nodeIdx;
                pedData.BoneTagToNode[bone.Tag] = nodeIdx;
                ctx.NodeChildren[nodeIdx] = new List<int>();
            }

            // Parent-child relationships
            for (int i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                int nodeIdx = pedData.BoneToNode[i];
                if (bone.ParentIndex >= 0 && bone.ParentIndex < bones.Length && bone.ParentIndex != i)
                {
                    int parentNodeIdx = pedData.BoneToNode[bone.ParentIndex];
                    ctx.NodeChildren[parentNodeIdx].Add(nodeIdx);
                }
                else
                {
                    pedData.SkeletonRootNodeIndex = nodeIdx;
                    ctx.NodeChildren[pedData.PedRootNodeIndex].Add(nodeIdx);
                }
            }

            // Inverse bind matrices: computed from the glTF TRS hierarchy.
            // By building the IBM from the same TRS values that glTF uses for node transforms,
            // we guarantee that IBM * GlobalTransform = Identity at bind pose.
            // This avoids subtle mismatches between CodeWalker's ScaleVector*= diagonal-only
            // scaling and glTF's standard T*R*S column scaling.

            // Pre-compute local transforms for all bones first.
            var localTransforms = new Matrix[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                Vector3 t_gltf = new Vector3(bone.Translation.X, bone.Translation.Z, -bone.Translation.Y);
                Quaternion r_gltf = new Quaternion(bone.Rotation.X, bone.Rotation.Z, -bone.Rotation.Y, bone.Rotation.W);
                Vector3 s_gltf = new Vector3(bone.Scale.X, bone.Scale.Z, bone.Scale.Y);
                localTransforms[i] = Matrix.Scaling(s_gltf) * Matrix.RotationQuaternion(r_gltf) * Matrix.Translation(t_gltf);
            }

            // Compute global transforms with proper parent-before-child ordering.
            // GTA V ped bones are often stored in non-hierarchical order (children before parents),
            // so we must recursively ensure the parent's global transform is computed first.
            var globalTransforms = new Matrix[bones.Length];
            var globalComputed = new bool[bones.Length];
            for (int i = 0; i < bones.Length; i++)
                ComputeGlobalTransform(i, bones, localTransforms, globalTransforms, globalComputed);

            var ibmFloats = new List<float>();
            for (int i = 0; i < bones.Length; i++)
            {
                Matrix ibm = Matrix.Invert(globalTransforms[i]);
                // SharpDX uses row-vector convention (v*M), glTF uses column-vector (M*v).
                // To convert: transpose the matrix, then store column-major.
                // Transpose + column-major = row-major of the original = row-by-row reading.
                ibmFloats.Add(ibm.M11); ibmFloats.Add(ibm.M12); ibmFloats.Add(ibm.M13); ibmFloats.Add(ibm.M14);
                ibmFloats.Add(ibm.M21); ibmFloats.Add(ibm.M22); ibmFloats.Add(ibm.M23); ibmFloats.Add(ibm.M24);
                ibmFloats.Add(ibm.M31); ibmFloats.Add(ibm.M32); ibmFloats.Add(ibm.M33); ibmFloats.Add(ibm.M34);
                ibmFloats.Add(ibm.M41); ibmFloats.Add(ibm.M42); ibmFloats.Add(ibm.M43); ibmFloats.Add(ibm.M44);
            }
            byte[] ibmBytes = new byte[ibmFloats.Count * 4];
            Buffer.BlockCopy(ibmFloats.ToArray(), 0, ibmBytes, 0, ibmBytes.Length);
            int ibmBv = ctx.AddBufferView(ibmBytes);
            int ibmAcc = ctx.AddAccessor(ibmBv, 5126, "MAT4", bones.Length);

            var skin = new GltfSkin
            {
                name = pedName + "_Skin",
                inverseBindMatrices = ibmAcc,
                skeleton = pedData.SkeletonRootNodeIndex,
            };
            for (int i = 0; i < bones.Length; i++)
                skin.joints.Add(pedData.BoneToNode[i]);

            pedData.SkinIndex = ctx.Skins.Count;
            ctx.Skins.Add(skin);

            return pedData;
        }

        #endregion

        #region Ped Meshes Builder

        /// <summary>
        /// Build mesh nodes for all 12 ped component drawables.
        /// Uses pedData.SkinIndex and pedData.PedRootNodeIndex for proper skinning attachment.
        /// </summary>
        /// <param name="ctx">The export context</param>
        /// <param name="ped">The ped whose meshes to export</param>
        /// <param name="pedData">Per-ped armature data from BuildPedArmature</param>
        /// <param name="namePrefix">Prefix for mesh/material names ("" for single ped, "PedName_" for cutscene)</param>
        public static void BuildPedMeshes(ExportContext ctx, Ped ped, PedArmatureData pedData, string namePrefix)
        {
            string[] compNames = { "Head", "Berd", "Hair", "Uppr", "Lowr", "Hand", "Feet", "Teef", "Accs", "Task", "Decl", "Jbib" };
            var skeleton = ped.Skeleton;
            var bones = skeleton?.Bones?.Items;

            // ── Mirror Renderer.RenderPedComponent's skeleton transplant ──
            // The GTA V renderer (Renderer.cs:3536-3558) does a "skeleton transplant"
            // for every ped component before rendering:
            //   1. If drawable.Skeleton == null → assign ped.Skeleton (null-fallback)
            //   2. If drawable.Skeleton != ped.Skeleton → replace the drawable's bones
            //      with the ped's bones (matched by Tag, placed at the drawable's array
            //      positions). This makes the geometry's BoneIds — which index into the
            //      drawable's skeleton — resolve to the correct ped bones.
            //
            // Without this transplant, the exporter's compBoneToPedBone remap (built
            // from the drawable's ORIGINAL skeleton) maps drawable indices to ped
            // indices correctly ONLY when the drawable's skeleton hasn't been mutated
            // yet. But if the renderer has already run (e.g., the user viewed the
            // cutscene before exporting), the drawable's skeleton bones have already
            // been replaced with ped bones, and the Tag-based remap may produce
            // incorrect results because the bone array positions no longer match the
            // original drawable skeleton ordering.
            //
            // By doing the transplant here (idempotent — safe to call even if the
            // renderer already did it), we guarantee the drawable's skeleton is in
            // the exact state the renderer expects, and the compBoneToPedBone remap
            // (built AFTER the transplant) correctly maps drawable indices → ped indices.
            if (skeleton?.Bones?.Items != null)
            {
                for (int compIdx = 0; compIdx < 12; compIdx++)
                {
                    var drawable = ped.Drawables[compIdx];
                    if (drawable == null) continue;
                    PrepareComponentSkeleton(drawable, skeleton);
                }
            }

            // Pre-build Bone->array-index lookup for cloth bone remapping
            Dictionary<Bone, int> boneToArrayIndex = null;
            if (bones != null)
            {
                boneToArrayIndex = new Dictionary<Bone, int>(bones.Length);
                for (int i = 0; i < bones.Length; i++)
                    boneToArrayIndex[bones[i]] = i;
            }

            // Pre-build bone Tag->array-position lookup for the ped's main skeleton.
            // This is needed because geom.BoneIds[] references the component drawable's
            // own skeleton, which may have bones at different array positions than the
            // ped's main skeleton (especially for player models like player_zero/one/two).
            // We need to convert from component skeleton indices to ped skeleton indices
            // so that JOINTS_0 values correctly reference the glTF armature built from
            // the ped's skeleton.
            Dictionary<ushort, int> pedTagToArrayIndex = null;
            if (bones != null)
            {
                pedTagToArrayIndex = new Dictionary<ushort, int>(bones.Length);
                for (int i = 0; i < bones.Length; i++)
                    pedTagToArrayIndex[bones[i].Tag] = i;
            }

            string[] compNamesLog = { "Head", "Berd", "Hair", "Uppr", "Lowr", "Hand", "Feet", "Teef", "Accs", "Task", "Decl", "Jbib" };
            var log = GltfExportLogger.Current;
            log?.BeginScope($"BuildPedMeshes: {ped.Name ?? "<unnamed>"}");

            for (int compIdx = 0; compIdx < 12; compIdx++)
            {
                var drawable = ped.Drawables[compIdx];
                if (drawable == null)
                {
                    log?.Log($"  [{compIdx}] {compNamesLog[compIdx]}: drawable=NULL (skipped)");
                    continue;
                }
                var texture = ped.Textures[compIdx];
                var models = drawable.DrawableModels?.High;
                if (models == null)
                {
                    log?.Log($"  [{compIdx}] {compNamesLog[compIdx]}: drawable='{drawable.Name}' but DrawableModels.High=NULL (skipped)");
                    continue;
                }
                log?.Log($"  [{compIdx}] {compNamesLog[compIdx]}: drawable='{drawable.Name}'  texture='{(texture?.Name ?? "<null>")}'  models={models.Length}  " +
                         $"compSkeleton={(drawable.Skeleton != null ? (ReferenceEquals(drawable.Skeleton, skeleton) ? "shared (null-fallback — BoneIds used as ped skeleton indices, no remap)" : "own (will Tag-remap bone indices)") : "<none>")}");

                // Detect cloth components and build cloth vertex data.
                // Regular cloth drawable vertices use their original skeletal bone weights,
                // which is the same approach as the in-app renderer — they deform properly
                // with standard bone skinning during animation.
                //
                // Previously, a clothBoneRemap was used to remap all skeletal bone references
                // to the nearest cloth bone ancestor. This collapsed multi-bone influences to
                // a single bone, making cloth rigid (following one bone instead of deforming).
                // That approach has been removed — regular vertices now keep their original
                // skeletal bone weights for proper multi-bone deformation.
                ClothVertexData clothVertexData = null;
                var clothInst = (ped.Clothes != null && compIdx < ped.Clothes.Length) ? ped.Clothes[compIdx] : null;
                if (clothInst?.CharCloth?.Controller != null && bones != null)
                {
                    var controller = clothInst.CharCloth.Controller;
                    var clothBoneTags = controller.BoneIds?.data_items;
                    if (clothBoneTags != null && clothBoneTags.Length > 0)
                    {
                        // Build cloth vertex data for vertices where blendIndices[2] == 255.
                        // These "cloth vertices" use a different rendering path in the GTA V shader:
                        // their blend indices reference ClothInstance.Vertices[] instead of bone matrices,
                        // and their blend weights are barycentric interpolation weights.
                        // We use CharacterClothController.BoneWeightsInds to compute proper bone weights.
                        var clothBones = clothInst.Bones;
                        var cbw = controller.BoneWeightsInds?.data_items;
                        if (clothBones != null && cbw != null)
                        {
                            clothVertexData = new ClothVertexData();
                            clothVertexData.BoneWeightsInds = cbw;
                            clothVertexData.ClothBoneToArrayIndex = new int[clothBones.Length];
                            for (int i = 0; i < clothBones.Length; i++)
                            {
                                if (clothBones[i] != null && boneToArrayIndex.TryGetValue(clothBones[i], out int aidx))
                                    clothVertexData.ClothBoneToArrayIndex[i] = aidx;
                                else
                                    clothVertexData.ClothBoneToArrayIndex[i] = 0;
                            }
                            clothVertexData.DefaultClothBoneArrayIndex =
                                clothVertexData.ClothBoneToArrayIndex.Length > 0 ? clothVertexData.ClothBoneToArrayIndex[0] : 0;
                        }
                    }
                }

                // Build component skeleton -> ped skeleton bone index mapping.
                // geom.BoneIds[] values are indices into the component drawable's skeleton,
                // but the glTF armature (skin joints, IBMs) is built from the ped's main
                // skeleton. For most NPC peds, both skeletons have identical bone ordering,
                // so BoneIds values work directly. But for player models (player_zero,
                // player_one, player_two), the component drawable's skeleton and the ped's
                // .yft skeleton have bones at different array positions. Without this mapping,
                // JOINTS_0 values reference wrong bones, causing distorted animation:
                // moving hand bones stretches face geometry, moving leg bones stretches
                // random body parts, etc.
                //
                // The renderer handles this via a "skeleton transplant" (Renderer.cs line 3532+),
                // which copies animated bone data from the ped's skeleton into the component's
                // skeleton at the correct positions using Tag matching. We replicate that
                // Tag-based matching here to build the index mapping.
                Dictionary<int, int> compBoneToPedBone = null;
                var compSkeleton = drawable.Skeleton;
                if (compSkeleton != null && compSkeleton != skeleton &&
                    compSkeleton.Bones?.Items != null && pedTagToArrayIndex != null)
                {
                    var compBones = compSkeleton.Bones.Items;
                    compBoneToPedBone = new Dictionary<int, int>(compBones.Length);
                    for (int ci = 0; ci < compBones.Length; ci++)
                    {
                        ushort tag = compBones[ci].Tag;
                        if (pedTagToArrayIndex.TryGetValue(tag, out int pedIdx))
                            compBoneToPedBone[ci] = pedIdx;
                        else
                            compBoneToPedBone[ci] = 0; // fallback to root bone
                    }
                }

                var diffuseTex = FindDiffuseTexture(drawable, texture);
                int? materialIdx = null;
                if (diffuseTex != null)
                {
                    materialIdx = ExportTexture(ctx, diffuseTex, namePrefix + compNames[compIdx], drawable);
                    log?.Log($"           diffuse texture: '{diffuseTex.Name ?? "<unnamed>"}' (embedded in material #{materialIdx})");
                }
                else
                {
                    log?.Log($"           !! diffuse texture NOT FOUND (componentDrawable='{drawable.Name}', pedTexture='{texture?.Name ?? "<null>"}') — using blank material (THIS IS A COMMON SOURCE OF WRONG-TEXTURE EXPORTS)");
                    var mat = new GltfMaterial
                    {
                        name = namePrefix + compNames[compIdx] + "_Material",
                        pbrMetallicRoughness = new GltfPbrMetallicRoughness { metallicFactor = 0f, roughnessFactor = 1f },
                    };
                    materialIdx = ctx.Materials.Count;
                    ctx.Materials.Add(mat);
                }

                var mesh = new GltfMesh { name = namePrefix + compNames[compIdx] };
                int geomTotal = 0, geomSkipped = 0, primBuilt = 0;
                for (int mi = 0; mi < models.Length; mi++)
                {
                    var model = models[mi];
                    if (model?.Geometries == null) continue;
                    for (int gi = 0; gi < model.Geometries.Length; gi++)
                    {
                        var geom = model.Geometries[gi];
                        if (geom == null) continue;
                        geomTotal++;

                        // Skip hair control mesh geometries (orderNumber > 0 on hair shaders).
                        // In GTA V, these geometries drive GPU tessellation and should not be
                        // rendered directly. The renderer skips them via disableRendering flag.
                        if (ShouldSkipGeometry(geom))
                        {
                            geomSkipped++;
                            log?.Log($"           skipping geometry #{gi} (ShouldSkipGeometry=true — typically hair control mesh)");
                            continue;
                        }

                        var prim = BuildPrimitive(ctx, geom, model, ped.Skeleton, clothVertexData, compBoneToPedBone);
                        if (prim != null)
                        {
                            if (materialIdx.HasValue) prim.material = materialIdx.Value;
                            mesh.primitives.Add(prim);
                            primBuilt++;
                        }
                        else
                        {
                            log?.Log($"           geometry #{gi} produced NULL primitive (BuildPrimitive returned null)");
                        }
                    }
                }
                log?.Log($"           geometries: total={geomTotal}  skipped={geomSkipped}  primitivesBuilt={primBuilt}");

                if (mesh.primitives.Count > 0)
                {
                    int meshIdx = ctx.Meshes.Count;
                    ctx.Meshes.Add(mesh);

                    var meshNode = new GltfNode
                    {
                        name = namePrefix + compNames[compIdx] + "_Mesh",
                        mesh = meshIdx,
                        skin = pedData.SkinIndex,
                    };

                    int meshNodeIdx = ctx.Nodes.Count;
                    ctx.Nodes.Add(meshNode);
                    ctx.MeshNodeIndices.Add(meshNodeIdx);
                    ctx.NodeChildren[meshNodeIdx] = new List<int>();
                    ctx.NodeChildren[pedData.PedRootNodeIndex].Add(meshNodeIdx);
                    log?.Log($"           -> mesh added: '{mesh.name}' primitives={mesh.primitives.Count} materialIdx={materialIdx}");
                }
                else
                {
                    log?.Log($"           -> NO mesh added (all primitives were skipped — geometry produced nothing exportable)");
                }
            }

            log?.EndScope();
        }

        /// <summary>
        /// Mirror Renderer.RenderPedComponent's skeleton transplant logic.
        ///
        /// This is the EXACT same logic the GTA V renderer runs on every frame for
        /// each ped component (Renderer.cs:3536-3558). We run it once before building
        /// meshes so the drawable's skeleton is in the state the renderer expects:
        ///
        /// 1. If drawable.Skeleton == null:
        ///    Assign ped.Skeleton. The drawable was authored without an embedded
        ///    skeleton, so its geometry's BoneIds are intended to index directly
        ///    into the host ped's skeleton. (Renderer null-fallback.)
        ///
        /// 2. If drawable.Skeleton != ped.Skeleton:
        ///    Replace the drawable's bones with the ped's bones, matched by Tag,
        ///    placed at the DRAWABLE's array positions. After this transplant,
        ///    drawable.Skeleton.Bones.Items[drawableIdx] is the ped bone whose Tag
        ///    matches the original drawable bone at that index. The geometry's
        ///    BoneIds (which index into the drawable's skeleton) now resolve to
        ///    the correct ped bones. (Renderer Tag-based transplant.)
        ///
        /// 3. If drawable.Skeleton == ped.Skeleton (already transplanted or
        ///    null-fallback already applied): no-op.
        ///
        /// This method is IDEMPOTENT — safe to call even if the renderer has
        /// already done the transplant. The "if (srcbone == dstbone) break" guard
        /// from the renderer is replicated: once a ped bone reference is found at
        /// the expected position, we know the transplant is already done and stop.
        /// </summary>
        static void PrepareComponentSkeleton(DrawableBase drawable, Skeleton pedSkeleton)
        {
            if (drawable == null || pedSkeleton?.Bones?.Items == null) return;

            if (drawable.Skeleton == null)
            {
                // Case 1: null-fallback — drawable has no skeleton, use ped's.
                drawable.Skeleton = pedSkeleton;
                return;
            }

            if (ReferenceEquals(drawable.Skeleton, pedSkeleton))
            {
                // Case 3: already assigned (null-fallback applied previously).
                return;
            }

            // Case 2: Tag-based transplant — replace drawable's bones with ped's bones.
            var dskel = drawable.Skeleton;
            var dskelBones = dskel.Bones?.Items;
            var dskelBonesMap = dskel.BonesMap;
            if (dskelBones == null || dskelBonesMap == null) return;

            var pedBones = pedSkeleton.Bones.Items;
            for (int b = 0; b < pedBones.Length; b++)
            {
                var srcbone = pedBones[b];
                if (srcbone == null) continue;

                // Find the bone in the drawable's skeleton with the same Tag
                if (!dskelBonesMap.TryGetValue(srcbone.Tag, out var dstbone) || dstbone == null)
                    continue;

                // If the drawable's bone is already the ped's bone (by reference),
                // the transplant was already done — stop early.
                if (ReferenceEquals(srcbone, dstbone)) break;

                // Replace the drawable's bone at its array position with the ped's bone.
                // dstbone.Index is the array position in the drawable's skeleton.
                if (dstbone.Index >= 0 && dstbone.Index < dskelBones.Length)
                    dskelBones[dstbone.Index] = srcbone;
                // Update the BonesMap so the Tag now points to the ped's bone.
                dskelBonesMap[srcbone.Tag] = srcbone;
            }

            // Also copy the sorted bones array (the renderer does this; it's used for
            // hierarchical bone transform updates).
            dskel.BonesSorted = pedSkeleton.BonesSorted;
        }

        #endregion

        #region Ped Animation Builder

        /// <summary>
        /// Build animation for a single ped from its ClipMapEntry.
        /// Handles both ClipAnimation and ClipAnimationList, sampling the full animation
        /// time range for proper playback in glTF viewers.
        /// The ClipMapEntry's OverridePlayTime is ignored during export since we want
        /// the complete animation curve, not a single frozen frame.
        /// </summary>
        /// <param name="ctx">The export context</param>
        /// <param name="pedData">Per-ped armature data from BuildPedArmature</param>
        /// <param name="animClip">The animation clip to export</param>
        /// <param name="animName">Name for the animation</param>
        /// <param name="expression">Optional Expression for facial bone remapping (tracks 24/25/26). Can be null.</param>
        /// <param name="boneTracksDictOverride">Optional merged BoneTracksDict for facial bone remapping.
        /// When provided, takes precedence over expression?.BoneTracksDict. Used by the cutscene exporter
        /// which merges per-component expressions to ensure all facial bones are remapped correctly,
        /// matching the renderer's behavior of using ped.Expressions[i] per component.</param>
        public static void BuildPedAnimation(ExportContext ctx, PedArmatureData pedData, ClipMapEntry animClip, string animName, Expression expression = null, Dictionary<ExpressionTrack, ExpressionTrack> boneTracksDictOverride = null)
        {
            var log = GltfExportLogger.Current;
            log?.BeginScope($"BuildPedAnimation: {animName}");

            if (animClip?.Clip == null)
            {
                log?.Log("  !! animClip or animClip.Clip is NULL — animation will be skipped (T-pose)");
                log?.EndScope();
                return;
            }
            var skeleton = pedData.Ped.Skeleton;
            if (skeleton == null)
            {
                log?.Log("  !! ped.Skeleton is NULL — animation will be skipped (T-pose)");
                log?.EndScope();
                return;
            }

            log?.Field("  Clip type", animClip.Clip.GetType().Name);
            log?.Field("  Clip hash", animClip.Hash);
            log?.Field("  Ped.Name", pedData.Ped.Name ?? "<null>");
            log?.Field("  Skeleton bone count", skeleton.Bones?.Items?.Length ?? 0);
            log?.Field("  BoneTagToNode entries", pedData.BoneTagToNode.Count);
            log?.Field("  Expression (global)", expression != null ? "OK" : "<null>");
            log?.Field("  BoneTracksDictOverride entries", boneTracksDictOverride?.Count ?? 0);

            var anim = new GltfAnimation { name = animName + "_Anim" };

            // Build the list of sub-animations to process.
            // The GTA V renderer (Renderable.UpdateAnim) iterates over ALL sub-animations
            // in a ClipAnimationList, applying each one's bone tracks on top of the previous.
            var subAnimations = new List<(Animation Animation, float StartTime, float EndTime)>();
            if (animClip.Clip is ClipAnimation clipAnim)
            {
                if (clipAnim.Animation != null)
                    subAnimations.Add((clipAnim.Animation, clipAnim.StartTime, clipAnim.EndTime));
            }
            else if (animClip.Clip is ClipAnimationList clipList && clipList.Animations != null)
            {
                foreach (var canim in clipList.Animations)
                {
                    if (canim?.Animation != null)
                        subAnimations.Add((canim.Animation, canim.StartTime, canim.EndTime));
                }
            }

            log?.Field("  Sub-animations resolved", subAnimations.Count);

            if (subAnimations.Count == 0)
            {
                log?.Log("  !! no sub-animations resolved — animation will be skipped (T-pose)");
                log?.EndScope();
                return;
            }

            // Use the maximum duration across all sub-animations and the clip's own duration.
            float duration = 0f;
            foreach (var sa in subAnimations)
            {
                float saDur = sa.EndTime - sa.StartTime;
                if (saDur > duration) duration = saDur;
                if (sa.Animation?.Duration > 0 && sa.Animation.Duration > duration)
                    duration = sa.Animation.Duration;
            }
            log?.Field("  Computed duration (s)", duration);
            if (duration <= 0)
            {
                log?.Log("  !! duration <= 0 — animation will be skipped (T-pose)");
                log?.EndScope();
                return;
            }

            // Use the first sub-animation's frame info for frame count calculation.
            var firstAnim = subAnimations[0].Animation;
            int frameCount = Math.Min(firstAnim.Frames > 0 ? firstAnim.Frames : (int)(duration * 30f), 300);
            float frameDelta = duration / (frameCount - 1);
            log?.Field("  firstAnim.Frames", firstAnim.Frames);
            log?.Field("  frameCount (export)", frameCount);
            log?.Field("  frameDelta", frameDelta);

            // Time accessor
            var timeData = new float[frameCount];
            for (int f = 0; f < frameCount; f++) timeData[f] = f * frameDelta;
            byte[] timeBytes = new byte[frameCount * 4];
            Buffer.BlockCopy(timeData, 0, timeBytes, 0, timeBytes.Length);
            int timeBv = ctx.AddBufferView(timeBytes);
            int timeAcc = ctx.AddAccessor(timeBv, 5126, "SCALAR", frameCount, new float[] { 0f }, new float[] { duration });

            // Collect per-frame animation data per (nodeIdx, path) pair.
            // The GTA V renderer applies tracks sequentially within a single animation —
            // if both Track 1 (body rotation) and Track 25/26 (facial rotation) target the
            // same bone, the last one wins. glTF does NOT support multiple channels for the
            // same node+path (undefined behavior), so we must collect data first, let later
            // tracks override earlier ones for the same node+path, then create one channel
            // per unique (nodeIdx, path) pair.
            // Key: (nodeIdx, glTF path string)  Value: per-frame float data (3 for VEC3, 4 for VEC4)
            var channelData = new Dictionary<(int nodeIdx, string path), List<float>>();
            // Track which bone tags have rotation channels for ThighRoll copy
            var rotationBoneTags = new HashSet<ushort>();

            // Capture raw face track values (T=24/25/26) per (BoneId, Track) per frame for VM seeding.
            // The VM's Blend instructions need these raw animation values as INPUT to compute
            // face bone transforms. Without seeding, the VM produces zero/identity output.
            // Key: (animBoneId, track) → array of per-frame Vector4 values
            var faceTrackAnimValues = new Dictionary<(ushort BoneId, byte Track), Vector4[]>();

            // Per-track counters for diagnostics — how many tracks were processed,
            // how many were remapped successfully, how many fell through because the
            // bone wasn't in the skeleton. A T-pose export typically shows
            // tracksProcessed=many but tracksResolvedToNode=0 for the body rotation track (T=1).
            int tracksProcessed = 0, tracksResolvedToNode = 0;
            var tracksByType = new Dictionary<byte, int>();
            var tracksUnresolvedByType = new Dictionary<byte, int>();
            var unresolvedBodyBoneIds = new List<ushort>(); // body bones (T<=2) whose lookup failed

            // Process each sub-animation, just like the renderer does.
            int subIdx = 0;
            foreach (var subAnim in subAnimations)
            {
                var animData = subAnim.Animation;
                var boneIds = animData.BoneIds?.data_items;
                log?.Field($"  Sub-anim[{subIdx}].BoneIds count", boneIds?.Length ?? 0);
                log?.Field($"  Sub-anim[{subIdx}].StartTime", subAnim.StartTime);
                log?.Field($"  Sub-anim[{subIdx}].EndTime", subAnim.EndTime);
                log?.Field($"  Sub-anim[{subIdx}].Animation.Duration", subAnim.Animation?.Duration ?? 0);
                if (boneIds == null) { subIdx++; continue; }

                for (int bi = 0; bi < boneIds.Length; bi++)
                {
                    var boneId = boneIds[bi];
                    tracksProcessed++;
                    tracksByType[boneId.Track] = tracksByType.TryGetValue(boneId.Track, out var c) ? c + 1 : 1;

                    // For facial tracks (24/25/26), remap bone ID through the BoneTracksDict.
                    // This replicates the renderer's behavior in Renderable.cs UpdateAnim()
                    // where facial bone IDs are remapped before lookup.
                    // The boneTracksDictOverride (merged from per-component expressions) takes
                    // precedence over expression?.BoneTracksDict, ensuring all facial bones from
                    // all components are correctly remapped — matching the renderer which uses
                    // ped.Expressions[i] per component.
                    ushort effectiveBoneId = boneId.BoneId;
                    if (boneId.Track == 24 || boneId.Track == 25 || boneId.Track == 26)
                    {
                        var btDict = boneTracksDictOverride ?? expression?.BoneTracksDict;
                        if (btDict != null)
                        {
                            var exprbt = new ExpressionTrack() { BoneId = boneId.BoneId, Track = boneId.Track, Flags = boneId.Unk0 };
                            if (btDict.TryGetValue(exprbt, out var exprbtmap))
                                effectiveBoneId = exprbtmap.BoneId;
                            else
                            {
                                // Try with 0x80 bit set (animation data doesn't include UnkFlag)
                                // This matches the renderer's fallback logic in Renderable.cs
                                var altKey = new ExpressionTrack() { BoneId = boneId.BoneId, Track = boneId.Track, Flags = (byte)(boneId.Unk0 | 0x80) };
                                if (btDict.TryGetValue(altKey, out var altRemap))
                                    effectiveBoneId = altRemap.BoneId;
                            }
                        }
                    }

                    // For facial tracks, use the remapped bone ID for node lookup
                    ushort lookupBoneId = (boneId.Track == 24 || boneId.Track == 25 || boneId.Track == 26) ? effectiveBoneId : boneId.BoneId;
                    if (!pedData.BoneTagToNode.TryGetValue(lookupBoneId, out int nodeIdx))
                    {
                        // Bone lookup failed — this track will be silently dropped, contributing to T-pose
                        // if it's a body rotation track (T=1) for a major bone like spine/thigh/upperarm.
                        tracksUnresolvedByType[boneId.Track] = tracksUnresolvedByType.TryGetValue(boneId.Track, out var uc) ? uc + 1 : 1;
                        if (boneId.Track <= 2)
                        {
                            unresolvedBodyBoneIds.Add(boneId.BoneId);
                            log?.Log($"           !! BONE LOOKUP FAILED: track={boneId.Track} boneId=0x{boneId.BoneId:X4}({boneId.BoneId}) — not in ped skeleton (will be dropped, contributing to T-pose)");
                        }
                        continue;
                    }
                    tracksResolvedToNode++;

                    // Look up the bone for facial animation calculations (need bind-pose TRS)
                    Bone bone = null;
                    if (boneId.Track == 24 || boneId.Track == 25 || boneId.Track == 26)
                        pedData.Ped.Skeleton?.BonesMap?.TryGetValue(effectiveBoneId, out bone);

                    if (boneId.Track == 0) // Translation
                    {
                        var vals = new List<float>(frameCount * 3);
                        for (int f = 0; f < frameCount; f++)
                        {
                            Vector3 val = Vector3.Zero;
                            try
                            {
                                float t = GetSubAnimPlaybackTime(f * frameDelta, subAnim.StartTime, subAnim.EndTime);
                                var fp = animData.GetFramePosition(t);
                                var v4 = animData.EvaluateVector4(fp, bi, true);
                                val = new Vector3(v4.X, v4.Y, v4.Z);
                            }
                            catch { }
                            // Y-up LH to Y-up RH: glTF_X = GTA_X, glTF_Y = GTA_Z, glTF_Z = -GTA_Y
                            vals.Add(val.X); vals.Add(val.Z); vals.Add(-val.Y);
                        }
                        channelData[(nodeIdx, "translation")] = vals;
                    }
                    else if (boneId.Track == 1) // Rotation
                    {
                        var vals = new List<float>(frameCount * 4);
                        for (int f = 0; f < frameCount; f++)
                        {
                            Quaternion val = Quaternion.Identity;
                            try
                            {
                                float t = GetSubAnimPlaybackTime(f * frameDelta, subAnim.StartTime, subAnim.EndTime);
                                var fp = animData.GetFramePosition(t);
                                val = animData.EvaluateQuaternion(fp, bi, true);
                            }
                            catch { }
                            // Y-up LH to Y-up RH quaternion conversion
                            vals.Add(val.X); vals.Add(val.Z); vals.Add(-val.Y); vals.Add(val.W);
                        }
                        channelData[(nodeIdx, "rotation")] = vals;
                        rotationBoneTags.Add(lookupBoneId);
                    }
                    else if (boneId.Track == 2) // Scale
                    {
                        var vals = new List<float>(frameCount * 3);
                        for (int f = 0; f < frameCount; f++)
                        {
                            Vector3 val = Vector3.One;
                            try
                            {
                                float t = GetSubAnimPlaybackTime(f * frameDelta, subAnim.StartTime, subAnim.EndTime);
                                var fp = animData.GetFramePosition(t);
                                var v4 = animData.EvaluateVector4(fp, bi, true);
                                val = new Vector3(v4.X, v4.Y, v4.Z);
                            }
                            catch { }
                            // Scale: axis swap (X,Z,Y) same as bone scale - no negation
                            vals.Add(val.X); vals.Add(val.Z); vals.Add(val.Y);
                        }
                        channelData[(nodeIdx, "scale")] = vals;
                    }
                    else if (boneId.Track == 24) // Face translation
                    {
                        // Renderable.cs: bone.AnimTranslation = bone.Translation + bone.AnimRotation.Multiply(new Vector3(0, v.X * 0.005f, 0))
                        // Since glTF animation replaces the node transform, we compute the final translation
                        // as bind-pose translation + rotated offset, then convert to glTF coordinates.
                        if (bone == null) continue;
                        var vals = new List<float>(frameCount * 3);
                        var faceVals = new Vector4[frameCount];
                        for (int f = 0; f < frameCount; f++)
                        {
                            try
                            {
                                float t = GetSubAnimPlaybackTime(f * frameDelta, subAnim.StartTime, subAnim.EndTime);
                                var fp = animData.GetFramePosition(t);
                                var v4 = animData.EvaluateVector4(fp, bi, true);
                                faceVals[f] = v4; // capture raw for VM seeding
                                var fv = new Vector3(0, v4.X * 0.005f, 0);
                                var animTrans = bone.Translation + bone.Rotation.Multiply(fv);
                                vals.Add(animTrans.X); vals.Add(animTrans.Z); vals.Add(-animTrans.Y);
                            }
                            catch
                            {
                                faceVals[f] = Vector4.Zero;
                                vals.Add(bone.Translation.X); vals.Add(bone.Translation.Z); vals.Add(-bone.Translation.Y);
                            }
                        }
                        faceTrackAnimValues[(boneId.BoneId, (byte)boneId.Track)] = faceVals;
                        // Later track for same node+path overrides earlier (matches renderer: last writer wins)
                        channelData[(nodeIdx, "translation")] = vals;
                    }
                    else if (boneId.Track == 25) // Face rotation (Euler angles)
                    {
                        // Renderable.cs: q = Quaternion.RotationYawPitchRoll(v.Z*mult, v.Y*mult, v.X*mult)
                        // where mult = -0.314159265f; bone.AnimRotation = bone.Rotation * q
                        // Since glTF animation replaces the node transform, we output bone.Rotation * q
                        // (the final animated rotation), converted to glTF coordinates.
                        if (bone == null) continue;
                        var vals = new List<float>(frameCount * 4);
                        float mult = -0.314159265f;
                        var faceVals = new Vector4[frameCount];
                        for (int f = 0; f < frameCount; f++)
                        {
                            try
                            {
                                float t = GetSubAnimPlaybackTime(f * frameDelta, subAnim.StartTime, subAnim.EndTime);
                                var fp = animData.GetFramePosition(t);
                                var v4 = animData.EvaluateVector4(fp, bi, true);
                                faceVals[f] = v4; // capture raw for VM seeding
                                var q = Quaternion.RotationYawPitchRoll(v4.Z * mult, v4.Y * mult, v4.X * mult);
                                // Ensure consistent quaternion hemisphere to prevent face bone flipping
                                if (q.W < 0) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
                                var animRot = bone.Rotation * q;
                                vals.Add(animRot.X); vals.Add(animRot.Z); vals.Add(-animRot.Y); vals.Add(animRot.W);
                            }
                            catch
                            {
                                faceVals[f] = Vector4.Zero;
                                vals.Add(bone.Rotation.X); vals.Add(bone.Rotation.Z); vals.Add(-bone.Rotation.Y); vals.Add(bone.Rotation.W);
                            }
                        }
                        faceTrackAnimValues[(boneId.BoneId, (byte)boneId.Track)] = faceVals;
                        // Facial rotation overrides body rotation for the same bone (matches renderer)
                        channelData[(nodeIdx, "rotation")] = vals;
                        rotationBoneTags.Add(lookupBoneId);
                    }
                    else if (boneId.Track == 26) // Face rotation (Quaternion)
                    {
                        // Renderable.cs: bone.AnimRotation = bone.Rotation * q
                        if (bone == null) continue;
                        var vals = new List<float>(frameCount * 4);
                        var faceVals = new Vector4[frameCount];
                        for (int f = 0; f < frameCount; f++)
                        {
                            try
                            {
                                float t = GetSubAnimPlaybackTime(f * frameDelta, subAnim.StartTime, subAnim.EndTime);
                                var fp = animData.GetFramePosition(t);
                                var q = animData.EvaluateQuaternion(fp, bi, true);
                                faceVals[f] = new Vector4(q.X, q.Y, q.Z, q.W); // capture raw for VM seeding
                                // Ensure consistent quaternion hemisphere to prevent face bone flipping
                                if (q.W < 0) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
                                var animRot = bone.Rotation * q;
                                vals.Add(animRot.X); vals.Add(animRot.Z); vals.Add(-animRot.Y); vals.Add(animRot.W);
                            }
                            catch
                            {
                                faceVals[f] = new Vector4(0, 0, 0, 1);
                                vals.Add(bone.Rotation.X); vals.Add(bone.Rotation.Z); vals.Add(-bone.Rotation.Y); vals.Add(bone.Rotation.W);
                            }
                        }
                        faceTrackAnimValues[(boneId.BoneId, (byte)boneId.Track)] = faceVals;
                        channelData[(nodeIdx, "rotation")] = vals;
                        rotationBoneTags.Add(lookupBoneId);
                    }
                }
                subIdx++;
            }

            // ── Expression VM execution (AFTER animation track loop) ────────
            // The animation loop above has set up all bone channel data from raw tracks.
            // Now the expression VM computes facial expression deltas and overrides face
            // bone channels — matching the renderer's UpdateAnim() behavior exactly.
            //
            // Without the VM, only direct face animation tracks (T=24/25/26) contribute
            // to facial bone channels. Many facial bones (eyes, jaw, eyebrows, etc.) are
            // animated exclusively by the VM's expression bytecode, not by direct tracks.
            // The VM must run per-frame with proper seeding to produce correct output.
            ApplyExpressionVm(pedData, skeleton, subAnimations, frameCount, frameDelta,
                channelData, rotationBoneTags, expression, boneTracksDictOverride, faceTrackAnimValues);

            // Now create one glTF channel per unique (nodeIdx, path) pair from the collected data.
            // This ensures no duplicate channels — glTF undefined behavior when multiple channels
            // target the same node+path was causing "upside-down" animation in some viewers.
            foreach (var kvp in channelData)
            {
                var key = kvp.Key;
                var vals = kvp.Value;
                bool isRotation = key.path == "rotation";
                string accessorType = isRotation ? "VEC4" : "VEC3";

                byte[] vb = new byte[vals.Count * 4];
                Buffer.BlockCopy(vals.ToArray(), 0, vb, 0, vb.Length);
                int valBv = ctx.AddBufferView(vb);
                int valAcc = ctx.AddAccessor(valBv, 5126, accessorType, frameCount);
                int sidx = anim.samplers.Count;
                anim.samplers.Add(new GltfAnimationSampler { input = timeAcc, output = valAcc });
                anim.channels.Add(new GltfAnimationChannel { sampler = sidx, target = new GltfAnimationChannelTarget { node = key.nodeIdx, path = key.path } });
            }

            // ThighRoll bone rotation copy — replicates the hardcoded hack from
            // Renderable.cs (lines 517-518). In GTA V, RB_L_ThighRoll and RB_R_ThighRoll
            // are helper bones that should copy the rotation of their corresponding main
            // Thigh bone (SKEL_L_Thigh / SKEL_R_Thigh).
            CopyThighRollRotation(ctx, pedData, anim, subAnimations, timeAcc, frameCount, frameDelta, rotationBoneTags);

            // ── Diagnostic summary ───────────────────────────────────────────
            log?.Field("  Tracks processed", tracksProcessed);
            log?.Field("  Tracks resolved to a node", tracksResolvedToNode);
            log?.Field("  Tracks dropped (bone not in skeleton)", tracksProcessed - tracksResolvedToNode);
            {
                var sb = new System.Text.StringBuilder();
                foreach (var kv in tracksByType.OrderBy(k => k.Key))
                    sb.Append($"T{kv.Key}={kv.Value} ");
                log?.Field("  Tracks by type", sb.ToString().Trim());

                var usb = new System.Text.StringBuilder();
                foreach (var kv in tracksUnresolvedByType.OrderBy(k => k.Key))
                    usb.Append($"T{kv.Key}={kv.Value} ");
                log?.Field("  Unresolved tracks by type", usb.Length == 0 ? "<none>" : usb.ToString().Trim());
            }
            if (unresolvedBodyBoneIds.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var bid in unresolvedBodyBoneIds.Distinct().OrderBy(x => x))
                    sb.Append($"0x{bid:X4} ");
                log?.Field("  Unresolved BODY bone IDs", sb.ToString().Trim());
            }
            log?.Field("  Unique (nodeIdx,path) channels produced", channelData.Count);
            log?.Field("  Face tracks captured for VM seeding", faceTrackAnimValues.Count);
            log?.Field("  Final anim.channels", anim.channels.Count);
            log?.Field("  Final anim.samplers", anim.samplers.Count);
            if (anim.channels.Count == 0)
                log?.Log("  !! FINAL: animation has 0 channels — exported model will be in T-pose");
            else if (anim.channels.Count < 5)
                log?.Log($"  !! WARNING: only {anim.channels.Count} channels produced — very few; body bones may be missing animation (likely T-pose)");
            log?.Field("  Animation added to ctx.Animations", anim.channels.Count > 0 ? "YES" : "NO");

            if (anim.channels.Count > 0)
                ctx.Animations.Add(anim);
            log?.EndScope();
        }

        /// <summary>
        /// Execute the Expression VM for each animation frame and apply VM output to
        /// channel data, overriding animation-applied values for face bones.
        ///
        /// This replicates the Expression VM execution from Renderable.UpdateAnim() so
        /// that glTF exports include the interpretive facial expressions (mouth, eyes,
        /// jaw, eyebrows) that the VM computes from expression bytecode (.yed files).
        /// Without this, only direct face animation tracks (T=24/25/26) contribute to
        /// facial bone channels, and many facial bones remain at bind pose.
        /// </summary>
        static void ApplyExpressionVm(
            PedArmatureData pedData,
            Skeleton skeleton,
            List<(Animation Animation, float StartTime, float EndTime)> subAnimations,
            int frameCount,
            float frameDelta,
            Dictionary<(int nodeIdx, string path), List<float>> channelData,
            HashSet<ushort> rotationBoneTags,
            Expression expression,
            Dictionary<ExpressionTrack, ExpressionTrack> boneTracksDictOverride,
            Dictionary<(ushort BoneId, byte Track), Vector4[]> faceTrackAnimValues)
        {
            // The renderer runs the VM per-component (ped.Expressions[i]), but since
            // all components share the same skeleton and the animation clip contains
            // all facial tracks in one clip, we need to find the best Expression to
            // run the VM with. Try per-component expressions first (they have the
            // actual facial expression data), then fall back to the global expression.
            Expression vmExpression = null;
            if (pedData.Ped.Expressions != null)
            {
                foreach (var expr in pedData.Ped.Expressions)
                {
                    if (expr?.Streams?.data_items != null && expr.Streams.data_items.Length > 0)
                    {
                        vmExpression = expr;
                        break;
                    }
                }
            }
            if (vmExpression == null && expression?.Streams?.data_items != null && expression.Streams.data_items.Length > 0)
                vmExpression = expression;

            if (vmExpression == null) return;

            var bonesMap = skeleton?.BonesMap;
            if (bonesMap == null) return;

            var btDict = boneTracksDictOverride ?? vmExpression.BoneTracksDict;

            // ── Build _faceBoneIds set ──
            // Identifies which skeleton bone IDs are face bones, so we only apply
            // VM output to face bones (not body bones). Matches Renderable.cs logic.
            var faceBoneIds = new HashSet<ushort>();

            // 1) Add skeleton bone IDs from animation face tracks (already remapped)
            foreach (var ftkv in faceTrackAnimValues.Keys)
            {
                // Remap through BoneTracksDict if possible
                if (btDict != null)
                {
                    var lookupKey = new ExpressionTrack() { BoneId = ftkv.BoneId, Track = ftkv.Track, Flags = 0 };
                    if (btDict.TryGetValue(lookupKey, out var mapped))
                        faceBoneIds.Add(mapped.BoneId);
                    else
                    {
                        var altKey = new ExpressionTrack() { BoneId = ftkv.BoneId, Track = ftkv.Track, Flags = 0x80 };
                        if (btDict.TryGetValue(altKey, out var altMapped))
                            faceBoneIds.Add(altMapped.BoneId);
                    }
                }
            }

            // 2) Add skeleton bone IDs from BoneTracksDict targets with T>=24 source
            if (btDict != null)
            {
                foreach (var kvp in btDict)
                {
                    var source = kvp.Key;
                    var target = kvp.Value;
                    if (source.Track >= 24 && source.Track <= 26)
                        faceBoneIds.Add(target.BoneId);
                }
            }

            // 3) Add skeleton bone IDs from expression's UnkFlag=False track definitions (T<=2)
            // These are skeleton bones the expression modifies directly.
            if (vmExpression.Tracks?.data_items != null)
            {
                foreach (var et in vmExpression.Tracks.data_items)
                {
                    if (!et.UnkFlag && et.Track <= 2 && bonesMap.ContainsKey(et.BoneId))
                        faceBoneIds.Add(et.BoneId);
                }
            }

            // ── Build exprTrackFormats from Expression.Tracks ──
            // Store FULL Flags (including 0x80 bit) for correct BoneTracksDict lookups.
            var exprTrackFormats = new Dictionary<(ushort, byte), byte>();
            if (vmExpression.Tracks?.data_items != null)
            {
                foreach (var et in vmExpression.Tracks.data_items)
                {
                    var fk = (et.BoneId, et.Track);
                    if (!exprTrackFormats.ContainsKey(fk))
                        exprTrackFormats[fk] = et.Flags;
                }
            }

            // ── Initialize VM ──
            var vm = new ExpressionVm();
            vm.Weight = 1.0f;
            vm.Init(vmExpression, 0, 0, 1f / 30f);

            // ── Pre-compute body bone animation values for seeding ──
            // The VM needs body bone transforms as INPUT (for lookAt etc.).
            // We compute these per-frame from the existing channelData.
            // For body bones NOT in faceBoneIds, seed their animated values.
            // For face bones, leave at zero/identity defaults — the VM computes
            // face deltas from expression parameters, not from current face state.

            // ── Per-frame VM execution ──
            for (int f = 0; f < frameCount; f++)
            {
                float t = f * frameDelta;
                float deltaTime = (f == 0) ? 1f / 30f : frameDelta;

                // Reset VM for this frame (keeps spring state)
                vm.ResetForFrame(t, deltaTime);

                // ── Seed VM with body bone transforms ──
                var seedData = new Dictionary<(ushort BoneId, byte Track), Vector4>();

                if (vmExpression.Tracks?.data_items != null)
                {
                    foreach (var exprTrack in vmExpression.Tracks.data_items)
                    {
                        // Only seed position/rotation/scale tracks (T=0/1/2)
                        if (exprTrack.Track > 2 && exprTrack.Track < 24) continue;
                        // Face animation channels are seeded separately below
                        if (exprTrack.Track >= 24) continue;

                        // Resolve the skeleton bone for this expression track
                        ushort lookupBoneId = exprTrack.BoneId;
                        var lookupKey = new ExpressionTrack() { BoneId = exprTrack.BoneId, Track = exprTrack.Track, Flags = exprTrack.Format };
                        if (exprTrack.UnkFlag && vmExpression.BoneTracksDict != null && vmExpression.BoneTracksDict.TryGetValue(lookupKey, out var mapped))
                            lookupBoneId = mapped.BoneId;

                        // Skip face bones — they get zero/identity defaults
                        if (faceBoneIds.Contains(lookupBoneId))
                            continue;

                        Bone skelBone = null;
                        bonesMap.TryGetValue(lookupBoneId, out skelBone);
                        if (skelBone == null) continue;

                        switch (exprTrack.Track)
                        {
                            case 0: // position
                                var trans = skelBone.Translation;
                                seedData[(exprTrack.BoneId, exprTrack.Track)] = new Vector4(trans.X, trans.Y, trans.Z, 0);
                                break;
                            case 1: // rotation
                                var rot = skelBone.Rotation;
                                seedData[(exprTrack.BoneId, exprTrack.Track)] = new Vector4(rot.X, rot.Y, rot.Z, rot.W);
                                break;
                            case 2: // scale
                                var scale = skelBone.Scale;
                                seedData[(exprTrack.BoneId, exprTrack.Track)] = new Vector4(scale.X, scale.Y, scale.Z, 0);
                                break;
                        }
                    }
                }

                // ── Seed VM with face animation track values (T=24/25/26) ──
                foreach (var ftkv in faceTrackAnimValues)
                {
                    if (ftkv.Value != null && f < ftkv.Value.Length)
                        seedData[ftkv.Key] = ftkv.Value[f];
                }

                // Also seed face tracks referenced in expression's Tracks list but not in animation
                if (vmExpression.Tracks?.data_items != null)
                {
                    foreach (var exprTrack in vmExpression.Tracks.data_items)
                    {
                        if (exprTrack.Track < 24) continue;
                        var faceKey = (exprTrack.BoneId, (byte)exprTrack.Track);
                        if (!seedData.ContainsKey(faceKey))
                        {
                            seedData[faceKey] = (exprTrack.Format == 1)
                                ? new Vector4(0, 0, 0, 1)
                                : Vector4.Zero;
                        }
                    }
                }

                vm.SeedTracks(seedData);

                // Execute the VM
                vm.RunAllStreams(t, deltaTime);

                // ── Apply VM output to channel data ──
                if (vm.Tracks.Count == 0 || vm.OutputTracks.Count == 0) continue;

                // Pre-scan: build set of bones with VALID VM rotation output
                var bonesWithValidRotation = new HashSet<ushort>();
                foreach (var preKey in vm.OutputTracks)
                {
                    var (preBoneId, preTrack, preComp) = preKey;
                    if (preComp != 0 || preTrack != 1) continue;

                    Vector4 preVal;
                    if (!vm.Tracks.TryGetValue(preKey, out preVal)) continue;

                    var preLenSq = preVal.X * preVal.X + preVal.Y * preVal.Y + preVal.Z * preVal.Z + preVal.W * preVal.W;
                    if (preLenSq >= 0.0001f && Math.Abs(preLenSq - 1.0f) <= 0.5f)
                    {
                        ushort preSkelId = preBoneId;
                        byte preFormat;
                        if (exprTrackFormats.TryGetValue((preBoneId, (byte)preTrack), out preFormat))
                        {
                            var preLookup = new ExpressionTrack() { BoneId = preBoneId, Track = (byte)preTrack, Flags = (byte)(preFormat & 0x7F) };
                            if (vmExpression.BoneTracksDict != null && vmExpression.BoneTracksDict.TryGetValue(preLookup, out var preMapped))
                                preSkelId = preMapped.BoneId;
                        }
                        bonesWithValidRotation.Add(preSkelId);
                    }
                }

                // TWO-PASS APPLICATION: Rotations first, then Positions
                // This matches the renderer: rotations must be applied first so that
                // position offsets are rotated by the correct animated rotation.

                // PASS 1: Apply rotations (T=1)
                foreach (var outKey in vm.OutputTracks)
                {
                    var (exprBoneId, trackType, compIdx) = outKey;
                    if (compIdx != 0 || trackType != 1) continue;

                    Vector4 val;
                    if (!vm.Tracks.TryGetValue(outKey, out val)) continue;

                    ushort skelBoneId = exprBoneId;
                    bool didRemap = false;
                    byte format = 0;

                    if (exprTrackFormats.TryGetValue((exprBoneId, trackType), out format))
                    {
                        var lookup = new ExpressionTrack() { BoneId = exprBoneId, Track = trackType, Flags = (byte)(format & 0x7F) };
                        if (vmExpression.BoneTracksDict != null && vmExpression.BoneTracksDict.TryGetValue(lookup, out var mapped))
                        {
                            skelBoneId = mapped.BoneId;
                            didRemap = true;
                        }
                    }
                    else { continue; }

                    if (!didRemap && !faceBoneIds.Contains(skelBoneId))
                        continue;

                    if (!pedData.BoneTagToNode.TryGetValue(skelBoneId, out int nodeIdx)) continue;

                    Bone targetBone = null;
                    bonesMap.TryGetValue(skelBoneId, out targetBone);
                    if (targetBone == null) continue;

                    var vmQuat = new Quaternion(val.X, val.Y, val.Z, val.W);
                    var lenSq = vmQuat.LengthSquared();
                    if (lenSq < 0.0001f || Math.Abs(lenSq - 1.0f) > 0.5f) continue;

                    vmQuat = Quaternion.Normalize(vmQuat);
                    if (vmQuat.W < 0) vmQuat = new Quaternion(-vmQuat.X, -vmQuat.Y, -vmQuat.Z, -vmQuat.W);

                    // Scale VM rotation to match animation loop magnitude
                    bool isEyeBone = !string.IsNullOrEmpty(targetBone.Name) &&
                        targetBone.Name.IndexOf("Eye", StringComparison.OrdinalIgnoreCase) >= 0;
                    float rotScale = isEyeBone ? VmRotScaleEye : VmRotScaleFace;
                    float vmAngle = 2.0f * (float)Math.Acos(Math.Min(1.0f, Math.Abs(vmQuat.W)));
                    if (vmAngle > 0.001f)
                    {
                        float scaledAngle = vmAngle * rotScale;
                        float sinHalf = (float)Math.Sin(scaledAngle / 2.0f);
                        float cosHalf = (float)Math.Cos(scaledAngle / 2.0f);
                        float axisScale = (vmAngle > 0.0001f) ? sinHalf / (float)Math.Sin(vmAngle / 2.0f) : 1.0f;
                        vmQuat = new Quaternion(
                            vmQuat.X * axisScale,
                            vmQuat.Y * axisScale,
                            vmQuat.Z * axisScale,
                            cosHalf * Math.Sign(vmQuat.W));
                        vmQuat = Quaternion.Normalize(vmQuat);
                    }

                    // Apply as delta from bind pose: bind rotation * VM delta quaternion
                    var animRot = Quaternion.Normalize(targetBone.Rotation * vmQuat);
                    // Convert to glTF coordinates and write into channel data
                    if (!channelData.ContainsKey((nodeIdx, "rotation")))
                        channelData[(nodeIdx, "rotation")] = new List<float>(frameCount * 4);

                    var rotList = channelData[(nodeIdx, "rotation")];
                    // Ensure the list has enough space (may have been created by another bone track)
                    int targetOffset = f * 4;
                    if (rotList.Count < (f + 1) * 4)
                    {
                        // Pad with bind-pose rotation
                        while (rotList.Count < targetOffset)
                        {
                            rotList.Add(targetBone.Rotation.X);
                            rotList.Add(targetBone.Rotation.Z);
                            rotList.Add(-targetBone.Rotation.Y);
                            rotList.Add(targetBone.Rotation.W);
                        }
                        rotList.Add(animRot.X); rotList.Add(animRot.Z); rotList.Add(-animRot.Y); rotList.Add(animRot.W);
                    }
                    else
                    {
                        // Override existing frame data
                        rotList[targetOffset] = animRot.X;
                        rotList[targetOffset + 1] = animRot.Z;
                        rotList[targetOffset + 2] = -animRot.Y;
                        rotList[targetOffset + 3] = animRot.W;
                    }
                    rotationBoneTags.Add(skelBoneId);
                }

                // PASS 2: Apply positions (T=0) using updated AnimRotation
                foreach (var outKey in vm.OutputTracks)
                {
                    var (exprBoneId, trackType, compIdx) = outKey;
                    if (compIdx != 0 || trackType != 0) continue;

                    Vector4 val;
                    if (!vm.Tracks.TryGetValue(outKey, out val)) continue;

                    ushort skelBoneId = exprBoneId;
                    bool didRemap = false;
                    byte format = 0;

                    if (exprTrackFormats.TryGetValue((exprBoneId, trackType), out format))
                    {
                        var lookup = new ExpressionTrack() { BoneId = exprBoneId, Track = trackType, Flags = (byte)(format & 0x7F) };
                        if (vmExpression.BoneTracksDict != null && vmExpression.BoneTracksDict.TryGetValue(lookup, out var mapped))
                        {
                            skelBoneId = mapped.BoneId;
                            didRemap = true;
                        }
                    }
                    else { continue; }

                    if (!didRemap && !faceBoneIds.Contains(skelBoneId)) continue;

                    // Skip position for bones with invalid rotation (parameter channels)
                    if (!bonesWithValidRotation.Contains(skelBoneId)) continue;

                    if (!pedData.BoneTagToNode.TryGetValue(skelBoneId, out int nodeIdx)) continue;

                    Bone targetBone = null;
                    bonesMap.TryGetValue(skelBoneId, out targetBone);
                    if (targetBone == null) continue;

                    var posOffset = new Vector3(val.X, val.Y, val.Z);
                    posOffset *= VmPosScale;
                    if (posOffset.Length() > VmPosMaxOffset) continue;

                    // Use the VM's computed rotation for this bone at this frame.
                    // If the VM wrote a rotation for this bone, we already applied it in Pass 1,
                    // so compute the final rotation now. Otherwise use bind rotation.
                    Quaternion animRotForPos = targetBone.Rotation;
                    var rotChannelKey = (nodeIdx, "rotation");
                    if (channelData.ContainsKey(rotChannelKey))
                    {
                        var rotList = channelData[rotChannelKey];
                        int rotOffset = f * 4;
                        if (rotList.Count >= rotOffset + 4)
                        {
                            // Use the VM-computed rotation (already in glTF coordinates)
                            // Convert back to GTA LH for position calculation: glTF(RH) → GTA(LH)
                            var rx = rotList[rotOffset];
                            var ry = rotList[rotOffset + 2]; // -GTA_Y → glTF_Z, so GTA_Y = -glTF_Z
                            var rz = rotList[rotOffset + 1]; // GTA_Z → glTF_Y
                            var rw = rotList[rotOffset + 3];
                            animRotForPos = new Quaternion(rx, -rz, -ry, rw);
                        }
                    }

                    var animTrans = targetBone.Translation + animRotForPos.Multiply(posOffset);

                    if (!channelData.ContainsKey((nodeIdx, "translation")))
                        channelData[(nodeIdx, "translation")] = new List<float>(frameCount * 3);

                    var transList = channelData[(nodeIdx, "translation")];
                    int transOffset = f * 3;
                    if (transList.Count < (f + 1) * 3)
                    {
                        while (transList.Count < transOffset)
                        {
                            transList.Add(targetBone.Translation.X);
                            transList.Add(targetBone.Translation.Z);
                            transList.Add(-targetBone.Translation.Y);
                        }
                        transList.Add(animTrans.X); transList.Add(animTrans.Z); transList.Add(-animTrans.Y);
                    }
                    else
                    {
                        transList[transOffset] = animTrans.X;
                        transList[transOffset + 1] = animTrans.Z;
                        transList[transOffset + 2] = -animTrans.Y;
                    }
                }
            }

            // ── Post-loop: ensure all VM-created channels have frameCount entries ──
            // When the VM creates a new channel for a bone that had no animation tracks,
            // and the VM doesn't produce output for every frame, the channel list will be
            // shorter than frameCount * 4 (rotation) or frameCount * 3 (translation).
            // Fill remaining frames with bind-pose values so the glTF accessor has the
            // correct number of elements.
            var channelsToFill = new List<(int nodeIdx, string path)>();
            foreach (var kvp in channelData)
            {
                int expectedCount = kvp.Key.path == "rotation" ? frameCount * 4 : frameCount * 3;
                if (kvp.Value.Count < expectedCount)
                    channelsToFill.Add(kvp.Key);
            }
            foreach (var key in channelsToFill)
            {
                var list = channelData[key];
                int expectedCount = key.path == "rotation" ? frameCount * 4 : frameCount * 3;
                // Find the bone for this node to get bind-pose values
                ushort boneTag = 0;
                foreach (var btk in pedData.BoneTagToNode)
                {
                    if (btk.Value == key.nodeIdx) { boneTag = btk.Key; break; }
                }
                Bone fillBone = null;
                bonesMap.TryGetValue(boneTag, out fillBone);

                if (key.path == "rotation")
                {
                    float bx = fillBone?.Rotation.X ?? 0f;
                    float by = fillBone?.Rotation.Z ?? 0f;  // glTF Y = GTA Z
                    float bz = -(fillBone?.Rotation.Y ?? 0f); // glTF Z = -GTA Y
                    float bw = fillBone?.Rotation.W ?? 1f;
                    while (list.Count < expectedCount)
                    {
                        list.Add(bx); list.Add(by); list.Add(bz); list.Add(bw);
                    }
                }
                else if (key.path == "translation")
                {
                    float bx = fillBone?.Translation.X ?? 0f;
                    float by = fillBone?.Translation.Z ?? 0f;
                    float bz = -(fillBone?.Translation.Y ?? 0f);
                    while (list.Count < expectedCount)
                    {
                        list.Add(bx); list.Add(by); list.Add(bz);
                    }
                }
            }
        }

        #endregion

        #region Drawable Object Mesh (Static)

        /// <summary>
        /// Export a drawable object (weapon, vehicle, prop) as a static mesh.
        /// Creates a root node with the given position/orientation and exports
        /// all geometry from the drawable as non-skinned mesh primitives.
        /// </summary>
        /// <param name="ctx">The export context</param>
        /// <param name="drawable">The drawable to export</param>
        /// <param name="gltfPosition">Already-converted glTF translation [x, y, z]</param>
        /// <param name="gltfRotation">Already-converted glTF rotation [x, y, z, w]</param>
        /// <param name="gltfScale">Already-converted glTF scale [x, y, z]</param>
        /// <param name="parentNodeIndex">Parent node index to attach under</param>
        /// <param name="objectName">Name for the object node and mesh</param>
        public static void BuildDrawableObjectMesh(ExportContext ctx, DrawableBase drawable,
            float[] gltfPosition, float[] gltfRotation, float[] gltfScale,
            int parentNodeIndex, string objectName)
        {
            if (drawable == null) return;
            var models = drawable.DrawableModels?.High;
            if (models == null) return;

            // Create a root node under the parent
            var objRoot = new GltfNode
            {
                name = objectName,
                translation = gltfPosition,
                rotation = gltfRotation,
                scale = gltfScale,
            };
            int objRootIdx = ctx.Nodes.Count;
            ctx.Nodes.Add(objRoot);
            ctx.NodeChildren[objRootIdx] = new List<int>();
            ctx.NodeChildren[parentNodeIndex].Add(objRootIdx);

            // Export textures/materials
            Texture fallbackTex = null;
            var diffuseTex = FindDiffuseTexture(drawable, fallbackTex);
            int? materialIdx = null;
            if (diffuseTex != null)
                materialIdx = ExportTexture(ctx, diffuseTex, objectName, drawable);
            else
            {
                var mat = new GltfMaterial
                {
                    name = objectName + "_Material",
                    pbrMetallicRoughness = new GltfPbrMetallicRoughness { metallicFactor = 0f, roughnessFactor = 1f },
                };
                materialIdx = ctx.Materials.Count;
                ctx.Materials.Add(mat);
            }

            var mesh = new GltfMesh { name = objectName };

            for (int mi = 0; mi < models.Length; mi++)
            {
                var model = models[mi];
                if (model?.Geometries == null) continue;
                for (int gi = 0; gi < model.Geometries.Length; gi++)
                {
                    var geom = model.Geometries[gi];
                    if (geom == null) continue;

                    // Static meshes — no skin/cloth data
                    var prim = BuildPrimitive(ctx, geom, model, null, null, null);
                    if (prim != null)
                    {
                        if (materialIdx.HasValue) prim.material = materialIdx.Value;
                        mesh.primitives.Add(prim);
                    }
                }
            }

            if (mesh.primitives.Count > 0)
            {
                int meshIdx = ctx.Meshes.Count;
                ctx.Meshes.Add(mesh);

                var meshNode = new GltfNode
                {
                    name = objectName + "_Mesh",
                    mesh = meshIdx,
                    // No skin for static meshes
                };

                int meshNodeIdx = ctx.Nodes.Count;
                ctx.Nodes.Add(meshNode);
                ctx.MeshNodeIndices.Add(meshNodeIdx);
                ctx.NodeChildren[meshNodeIdx] = new List<int>();
                ctx.NodeChildren[objRootIdx].Add(meshNodeIdx);
            }
        }

        #endregion

        #region Build Primitive

        public static GltfMeshPrimitive BuildPrimitive(ExportContext ctx, DrawableGeometry geom, DrawableModel model, Skeleton skeleton, ClothVertexData clothVertexData = null, Dictionary<int, int> compBoneToPedBone = null)
        {
            // Use geom.VertexData which resolves Data1 ?? Data2 automatically
            var vdata = geom.VertexData;
            if (vdata == null) return null;
            var decl = vdata.Info;
            if (decl == null) return null;

            // Use VertexData.VertexCount as the authoritative vertex count
            // (geom.VerticesCount is a ushort that can overflow or be stale)
            int vertCount = vdata.VertexCount;
            if (vertCount <= 0) return null;

            // Create the primitive object at the start so it can be used throughout
            var prim = new GltfMeshPrimitive();

            bool hasSkin = model.HasSkin > 0;
            ushort[] geomBoneIds = geom.BoneIds;

            uint flags = decl.Flags;
            bool hasNormals = ((flags >> 3) & 1) == 1;
            bool hasTexCoords = ((flags >> 6) & 1) == 1;
            bool hasBlendWeights = ((flags >> 1) & 1) == 1;
            bool hasBlendIndices = ((flags >> 2) & 1) == 1;
            bool hasBlendData = hasBlendWeights && hasBlendIndices;

            // Precompute component offsets for direct byte access
            int stride = decl.Stride;
            int posOffset = decl.GetComponentOffset(0);
            int nrmOffset = hasNormals ? decl.GetComponentOffset(3) : -1;
            int uvOffset = hasTexCoords ? decl.GetComponentOffset(6) : -1;
            int bwOffset = hasBlendWeights ? decl.GetComponentOffset(1) : -1;
            int biOffset = hasBlendIndices ? decl.GetComponentOffset(2) : -1;

            // Get the blend weight component type to handle different encodings
            VertexComponentType bwType = hasBlendWeights ? decl.GetComponentType(1) : VertexComponentType.Nothing;

            var positions = new List<float>();
            var normals = new List<float>();
            var texcoords = new List<float>();
            var blendWeights = new List<float>();
            var blendJoints = new List<ushort>();
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

            var vbytes = vdata.VertexBytes;

            for (int v = 0; v < vertCount; v++)
            {
                int baseOff = v * stride;

                // Position (always Float3 at semantic 0)
                Vector3 pos = Vector3.Zero;
                int po = baseOff + posOffset;
                if (po + 12 <= vbytes.Length)
                {
                    pos = new Vector3(
                        BitConverter.ToSingle(vbytes, po),
                        BitConverter.ToSingle(vbytes, po + 4),
                        BitConverter.ToSingle(vbytes, po + 8));
                }
                // GTA V is left-handed (X=right, Y=forward, Z=up), glTF is right-handed Y-up
                // Conversion: glTF_X = GTA_X, glTF_Y = GTA_Z, glTF_Z = -GTA_Y
                float px = pos.X, py = pos.Z, pz = -pos.Y;
                positions.Add(px); positions.Add(py); positions.Add(pz);
                if (px < minX) minX = px; if (py < minY) minY = py; if (pz < minZ) minZ = pz;
                if (px > maxX) maxX = px; if (py > maxY) maxY = py; if (pz > maxZ) maxZ = pz;

                // Normal (Float3 at semantic 3)
                if (hasNormals)
                {
                    Vector3 nrm = Vector3.UnitZ;
                    int no = baseOff + nrmOffset;
                    if (no + 12 <= vbytes.Length)
                    {
                        nrm = new Vector3(
                            BitConverter.ToSingle(vbytes, no),
                            BitConverter.ToSingle(vbytes, no + 4),
                            BitConverter.ToSingle(vbytes, no + 8));
                    }
                    normals.Add(nrm.X); normals.Add(nrm.Z); normals.Add(-nrm.Y);
                }

                // TexCoord0 (Float2 at semantic 6)
                if (hasTexCoords)
                {
                    Vector2 uv = Vector2.Zero;
                    int uo = baseOff + uvOffset;
                    if (uo + 8 <= vbytes.Length)
                    {
                        uv = new Vector2(
                            BitConverter.ToSingle(vbytes, uo),
                            BitConverter.ToSingle(vbytes, uo + 4));
                    }
                    // glTF 2.0 spec: UV (0,0) = upper-left corner of texture image.
                    // This matches DirectX/GTA V convention (V=0 at top), so no flip needed.
                    texcoords.Add(uv.X); texcoords.Add(uv.Y);
                }

                // Blend weights and indices (skinning data)
                if (hasBlendData && hasSkin)
                {
                    // Read blend weights - handle different component types
                    Vector4 bw = Vector4.Zero;
                    int bwo = baseOff + bwOffset;
                    switch (bwType)
                    {
                        case VertexComponentType.Float4:
                            if (bwo + 16 <= vbytes.Length)
                                bw = new Vector4(
                                    BitConverter.ToSingle(vbytes, bwo),
                                    BitConverter.ToSingle(vbytes, bwo + 4),
                                    BitConverter.ToSingle(vbytes, bwo + 8),
                                    BitConverter.ToSingle(vbytes, bwo + 12));
                            break;
                        case VertexComponentType.Half4:
                            if (bwo + 8 <= vbytes.Length)
                                bw = new Vector4(
                                    HalfHelper.HalfToSingle(vbytes[bwo], vbytes[bwo + 1]),
                                    HalfHelper.HalfToSingle(vbytes[bwo + 2], vbytes[bwo + 3]),
                                    HalfHelper.HalfToSingle(vbytes[bwo + 4], vbytes[bwo + 5]),
                                    HalfHelper.HalfToSingle(vbytes[bwo + 6], vbytes[bwo + 7]));
                            break;
                        case VertexComponentType.UByte4:
                        case VertexComponentType.Colour:
                            // Both UByte4 and Colour are 4 bytes (R8G8B8A8_UNORM).
                            // Each byte is a normalized weight [0-255] -> [0.0-1.0].
                            // Read raw bytes directly to avoid Color struct byte shuffling.
                            if (bwo + 4 <= vbytes.Length)
                                bw = new Vector4(
                                    vbytes[bwo] / 255.0f,
                                    vbytes[bwo + 1] / 255.0f,
                                    vbytes[bwo + 2] / 255.0f,
                                    vbytes[bwo + 3] / 255.0f);
                            break;
                        default:
                            // Fallback: try reading 4 raw bytes for unknown types
                            if (bwo + 4 <= vbytes.Length)
                                bw = new Vector4(
                                    vbytes[bwo] / 255.0f,
                                    vbytes[bwo + 1] / 255.0f,
                                    vbytes[bwo + 2] / 255.0f,
                                    vbytes[bwo + 3] / 255.0f);
                            break;
                    }

                    // Read blend indices directly from raw bytes
                    byte bi0 = 0, bi1 = 0, bi2 = 0, bi3 = 0;
                    int bio = baseOff + biOffset;
                    if (bio + 4 <= vbytes.Length)
                    {
                        bi0 = vbytes[bio];
                        bi1 = vbytes[bio + 1];
                        bi2 = vbytes[bio + 2];
                        bi3 = vbytes[bio + 3];
                    }

                    // GTA V cloth/hair shader: when blendIndices[2] == 255, the vertex is a
                    // "cloth vertex" that uses a different rendering path. The blend indices are
                    // NOT bone indices — they index into the ClothInstance.Vertices[] buffer,
                    // and the blend weights are barycentric interpolation weights (not bone weights).
                    // In the shader: binds.w->cv0, binds.x->cv1, binds.y->cv2;
                    // weights.z->cv0, weights.y->cv1, weights.x->cv2; weights.w = thickness.
                    // We compute proper bone weights using CharacterClothController.BoneWeightsInds.
                    if (bi2 == 255 && clothVertexData != null)
                    {
                        // CLOTH VERTEX (blendIndices[2] == 255)
                        // Compute interpolation weights for the 3 cloth sim vertices
                        float wz = bw.Z, wy = bw.Y, wx = bw.X;
                        float tw = wz + wy + wx;
                        if (tw > 0.001f) { wz /= tw; wy /= tw; wx /= tw; }
                        else { wz = wy = wx = 0.333f; }

                        // Collect bone weights from the 3 cloth sim vertices
                        // bi3->cv0 (weight wz), bi0->cv1 (weight wy), bi1->cv2 (weight wx)
                        var combined = new Dictionary<int, float>();
                        AddClothBoneWeights(combined, bi3, wz, clothVertexData);
                        AddClothBoneWeights(combined, bi0, wy, clothVertexData);
                        AddClothBoneWeights(combined, bi1, wx, clothVertexData);

                        // Take top 4 bone influences, normalize
                        var sorted = combined.OrderByDescending(kv => kv.Value).Take(4).ToList();
                        float totalW = sorted.Sum(kv => kv.Value);
                        if (totalW < 0.001f) totalW = 1f;

                        for (int k = 0; k < 4; k++)
                        {
                            if (k < sorted.Count)
                            {
                                blendJoints.Add((ushort)sorted[k].Key);
                                blendWeights.Add(sorted[k].Value / totalW);
                            }
                            else
                            {
                                blendJoints.Add((ushort)clothVertexData.DefaultClothBoneArrayIndex);
                                blendWeights.Add(0f);
                            }
                        }
                    }
                    else
                    {
                        // REGULAR VERTEX
                        float tw = bw.X + bw.Y + bw.Z + bw.W;
                        if (tw > 0.001f) { bw.X /= tw; bw.Y /= tw; bw.Z /= tw; bw.W /= tw; }
                        else { bw.X = 1f; bw.Y = 0f; bw.Z = 0f; bw.W = 0f; }
                        blendWeights.Add(bw.X); blendWeights.Add(bw.Y); blendWeights.Add(bw.Z); blendWeights.Add(bw.W);

                        // Remap local bone indices -> component skeleton indices -> ped skeleton indices
                        blendJoints.Add(RemapBoneIndex(bi0, geomBoneIds, skeleton, compBoneToPedBone));
                        blendJoints.Add(RemapBoneIndex(bi1, geomBoneIds, skeleton, compBoneToPedBone));
                        blendJoints.Add(RemapBoneIndex(bi2, geomBoneIds, skeleton, compBoneToPedBone));
                        blendJoints.Add(RemapBoneIndex(bi3, geomBoneIds, skeleton, compBoneToPedBone));
                    }
                }
            }

            // Position accessor
            byte[] posBytes = new byte[positions.Count * 4];
            Buffer.BlockCopy(positions.ToArray(), 0, posBytes, 0, posBytes.Length);
            int posBv = ctx.AddBufferView(posBytes, 12, 34962);
            int posAcc = ctx.AddAccessor(posBv, 5126, "VEC3", vertCount, new float[] { minX, minY, minZ }, new float[] { maxX, maxY, maxZ });
            prim.attributes["POSITION"] = posAcc;

            if (hasNormals && normals.Count > 0)
            {
                byte[] nrmBytes = new byte[normals.Count * 4];
                Buffer.BlockCopy(normals.ToArray(), 0, nrmBytes, 0, nrmBytes.Length);
                int nrmBv = ctx.AddBufferView(nrmBytes, 12, 34962);
                prim.attributes["NORMAL"] = ctx.AddAccessor(nrmBv, 5126, "VEC3", vertCount);
            }

            if (hasTexCoords && texcoords.Count > 0)
            {
                byte[] uvBytes = new byte[texcoords.Count * 4];
                Buffer.BlockCopy(texcoords.ToArray(), 0, uvBytes, 0, uvBytes.Length);
                int uvBv = ctx.AddBufferView(uvBytes, 8, 34962);
                prim.attributes["TEXCOORD_0"] = ctx.AddAccessor(uvBv, 5126, "VEC2", vertCount);
            }

            if (hasBlendData && hasSkin && blendWeights.Count > 0)
            {
                // NOTE: The previous clothBoneRemap logic that remapped all skeletal bone
                // references to the nearest cloth bone ancestor has been removed. That approach
                // collapsed multi-bone influences to a single cloth bone, making the cloth rigid
                // — it would just follow one bone and rotate instead of deforming naturally.
                // Regular cloth drawable vertices now keep their original skeletal bone weights,
                // matching the in-app renderer's behavior for proper multi-bone deformation.
                // Cloth vertices (bi2==255) already have correct bone weights computed from
                // CharClothBoneWeightsInds.

                byte[] wBytes = new byte[blendWeights.Count * 4];
                Buffer.BlockCopy(blendWeights.ToArray(), 0, wBytes, 0, wBytes.Length);
                int wBv = ctx.AddBufferView(wBytes, 16, 34962);
                prim.attributes["WEIGHTS_0"] = ctx.AddAccessor(wBv, 5126, "VEC4", vertCount);

                byte[] jBytes = new byte[blendJoints.Count * 2];
                Buffer.BlockCopy(blendJoints.ToArray(), 0, jBytes, 0, jBytes.Length);
                int jBv = ctx.AddBufferView(jBytes, 8, 34962);
                prim.attributes["JOINTS_0"] = ctx.AddAccessor(jBv, 5123, "VEC4", vertCount);
            }

            // Indices
            var indices = geom.IndexBuffer?.Indices;
            if (indices != null && indices.Length > 0)
            {
                int idxCount = Math.Min((int)geom.IndicesCount, indices.Length);
                if (idxCount <= 0) idxCount = indices.Length;
                ushort[] idxData = new ushort[idxCount];
                Array.Copy(indices, idxData, idxCount);
                byte[] idxBytes = new byte[idxCount * 2];
                Buffer.BlockCopy(idxData, 0, idxBytes, 0, idxBytes.Length);
                int idxBv = ctx.AddBufferView(idxBytes, null, 34963);
                prim.indices = ctx.AddAccessor(idxBv, 5123, "SCALAR", idxCount);
            }

            return prim;
        }

        #endregion

        #region Bone Remapping Helpers

        public static ushort RemapBoneIndex(byte localIdx, ushort[] geomBoneIds, Skeleton skeleton, Dictionary<int, int> compBoneToPedBone = null)
        {
            // geom.BoneIds[] maps vertex local bone indices to the component drawable's
            // skeleton bone array indices (NOT the ped's main skeleton indices).
            // The renderer confirms this: Renderable.UpdateBoneTransforms does
            //   var id = boneids[b]; geom.BoneTransforms[b] = bonetransforms[id];
            // where bonetransforms[] is indexed by the component's skeleton array.
            //
            // For NPC peds, the component drawable's skeleton typically has identical
            // bone ordering to the ped's .yft skeleton, so BoneIds values work directly.
            //
            // For player models (player_zero, player_one, player_two), the component
            // drawable's skeleton and the ped's .yft skeleton have bones at different
            // array positions. We must convert from component skeleton indices to
            // ped skeleton indices using the compBoneToPedBone mapping (built via
            // bone Tag matching, same as the renderer's "skeleton transplant").
            if (geomBoneIds == null || localIdx >= geomBoneIds.Length) return 0;
            int compIdx = geomBoneIds[localIdx];

            // If we have a component->ped mapping, use it to convert the index
            if (compBoneToPedBone != null && compBoneToPedBone.TryGetValue(compIdx, out int pedIdx))
                return (ushort)pedIdx;

            // No mapping available — the component and ped skeletons share the same
            // bone ordering (or the component has no skeleton of its own), so the
            // BoneIds value can be used directly as a ped skeleton array index.
            return (ushort)compIdx;
        }

        /// <summary>
        /// Add bone weights from a cloth simulation vertex into the combined weight dictionary.
        /// The cloth simulation vertex has 4 bone weights and 4 bone indices referencing
        /// ClothInstance.Bones[], which are then mapped to skeleton bone array indices.
        /// </summary>
        public static void AddClothBoneWeights(Dictionary<int, float> combined, int cvIdx, float interpWeight, ClothVertexData clothData)
        {
            if (clothData.BoneWeightsInds == null || cvIdx >= clothData.BoneWeightsInds.Length) return;
            var bw = clothData.BoneWeightsInds[cvIdx];
            float[] ws = { bw.Weights.X, bw.Weights.Y, bw.Weights.Z, bw.Weights.W };
            uint[] idxs = { bw.Index0, bw.Index1, bw.Index2, bw.Index3 };
            for (int i = 0; i < 4; i++)
            {
                if (ws[i] < 0.0001f) continue;
                if (idxs[i] >= clothData.ClothBoneToArrayIndex.Length) continue;
                int skelIdx = clothData.ClothBoneToArrayIndex[idxs[i]];
                if (!combined.ContainsKey(skelIdx))
                    combined[skelIdx] = 0f;
                combined[skelIdx] += ws[i] * interpWeight;
            }
        }

        /// <summary>
        /// Check if a geometry should be skipped during export.
        /// This replicates the renderer's logic: hair control mesh geometries (using
        /// ped_hair_spiked.sps with orderNumber > 0) drive GPU tessellation and should
        /// not be rendered directly.
        /// </summary>
        public static bool ShouldSkipGeometry(DrawableGeometry geom)
        {
            var shader = geom.Shader;
            if (shader == null) return false;

            // Check if this is a hair shader (ped_hair_spiked.sps, hash 100720695)
            if (shader.FileName.Hash != HairShaderHash) return false;

            // Read orderNumber from shader parameters
            var sparams = shader.ParametersList?.Parameters;
            var hashes = shader.ParametersList?.Hashes;
            if (sparams == null || hashes == null) return false;

            for (int pi = 0; pi < sparams.Length && pi < hashes.Length; pi++)
            {
                if ((uint)hashes[pi] == OrderNumberHash)
                {
                    var param = sparams[pi];
                    if (param?.Data is SharpDX.Vector4 v && v.X > 0.0f)
                        return true;
                    break;
                }
            }
            return false;
        }

        #endregion

        #region Texture Helpers

        public static Texture FindDiffuseTexture(DrawableBase drawable, Texture fallbackTex)
        {
            var sg = drawable.ShaderGroup;
            if (sg?.Shaders?.data_items == null) return fallbackTex;

            // Try to find from embedded texture dictionary first
            Texture embeddedMatch = null;
            foreach (var shader in sg.Shaders.data_items)
            {
                if (shader?.ParametersList?.Parameters == null) continue;
                var hashes = shader.ParametersList.Hashes;
                for (int pi = 0; pi < shader.ParametersList.Parameters.Length; pi++)
                {
                    var param = shader.ParametersList.Parameters[pi];
                    if (param.DataType == 0 && param.Data is Texture tex)
                    {
                        if (hashes != null && pi < hashes.Length)
                        {
                            uint h = (uint)hashes[pi];
                            if (h == JenkHash.GenHash("DiffuseSampler"))
                                return tex;
                        }
                        if (embeddedMatch == null) embeddedMatch = tex;
                    }
                }
            }
            return embeddedMatch ?? fallbackTex;
        }

        public static int ExportTexture(ExportContext ctx, Texture tex, string compName, DrawableBase drawable = null)
        {
            if (tex == null) return -1;

            // Per-ped texture isolation: the dedup key must include the compName prefix
            // (which contains the ped name for cutscene exports, e.g., "player_one_Head").
            // Without this, two peds that both have a texture named "head_diff_000_a_bla"
            // would share the same glTF material — causing ped #2 to render with ped #1's
            // textures. The renderer doesn't have this issue because it renders each ped
            // independently with its own texture bindings.
            //
            // The compName prefix already encodes which ped this texture belongs to
            // (passed as namePrefix + compNames[compIdx] from BuildPedMeshes), so we
            // use it directly as part of the dedup key.
            //
            // IMPORTANT: the key MUST be a long (64-bit) to hold both the compName hash
            // (upper 32 bits) and the texture NameHash (lower 32 bits). Casting to
            // MetaHash (uint) would truncate the compName hash, re-introducing the bug.
            long dedupKey = ((long)(uint)compName.GetHashCode() << 32) | (uint)tex.NameHash;
            if (ctx.TextureToGltfIndex.TryGetValue(dedupKey, out int existingIdx))
                return existingIdx;

            int imageIdx;
            byte[] rgbaPixels = null;

            // Try DDSIO pixel extraction first (handles DXT1/3/5, BC4/5, uncompressed)
            try { rgbaPixels = DDSIO.GetPixels(tex, 0); } catch { }

            // Detect whether the texture has meaningful alpha data.
            // We check two things:
            //   1. The texture format — DXT3, DXT5, BC7, A8R8G8B8 etc. all support alpha
            //   2. The shader used — hair shaders (ped_hair_spiked.sps) and other alpha shaders
            //      need alphaMode set so the glTF material uses the alpha channel
            bool textureHasAlpha = TextureFormatHasAlpha(tex.Format);
            bool shaderUsesAlpha = DrawableUsesAlphaShader(drawable);

            // Also scan the actual pixel data for non-opaque alpha values,
            // but only if the format supports alpha (avoid false positives from DXT1 1-bit alpha)
            bool hasActualAlphaPixels = false;
            if (textureHasAlpha && rgbaPixels != null && rgbaPixels.Length > 0)
            {
                hasActualAlphaPixels = ScanForAlphaPixels(rgbaPixels, (int)tex.Width, (int)tex.Height);
            }

            bool needsAlphaMode = textureHasAlpha && (shaderUsesAlpha || hasActualAlphaPixels);

            if (rgbaPixels != null && rgbaPixels.Length > 0)
            {
                byte[] pngData = EncodePng(rgbaPixels, (int)tex.Width, (int)tex.Height);
                if (pngData != null && pngData.Length > 0)
                {
                    int imgBv = ctx.AddBufferView(pngData);
                    var img = new GltfImage { name = compName + "_Diffuse", bufferView = imgBv, mimeType = "image/png" };
                    imageIdx = ctx.Images.Count;
                    ctx.Images.Add(img);
                }
                else
                {
                    // PNG encoding failed, embed as DDS
                    imageIdx = EmbedDdsTexture(ctx, tex, compName);
                }
            }
            else
            {
                // DDSIO failed (e.g. BC7 format) - try embedding raw DDS
                imageIdx = EmbedDdsTexture(ctx, tex, compName);
            }

            var sampler = new GltfSampler();
            int samplerIdx = ctx.Samplers.Count;
            ctx.Samplers.Add(sampler);

            var gltfTex = new GltfTexture { source = imageIdx, sampler = samplerIdx };
            int texIdx = ctx.Textures.Count;
            ctx.Textures.Add(gltfTex);
            ctx.TextureToGltfIndex[dedupKey] = texIdx;

            var mat = new GltfMaterial
            {
                name = compName + "_Material",
                pbrMetallicRoughness = new GltfPbrMetallicRoughness
                {
                    baseColorTexture = new GltfTextureInfo { index = texIdx },
                    metallicFactor = 0f,
                    roughnessFactor = 1f,
                }
            };

            // Set alphaMode for materials that need transparency.
            // "MASK" = alpha test/cutout — pixels with alpha >= cutoff are fully opaque,
            // pixels with alpha < cutoff are fully discarded. This matches GTA V's hair
            // rendering which uses alpha test (HardAlphaBlend / cutout).
            // "BLEND" = alpha blending — would be needed for glass, etc.
            if (needsAlphaMode)
            {
                mat.alphaMode = "MASK";
                mat.alphaCutoff = 0.5f;
                mat.doubleSided = true; // hair/alpha geometry is often double-sided
            }

            int matIdx = ctx.Materials.Count;
            ctx.Materials.Add(mat);
            return matIdx;
        }

        /// <summary>
        /// Check if a texture format supports an alpha channel.
        /// DXT1 does NOT have proper alpha (only 1-bit), DXT3/5, BC7, and ARGB formats do.
        /// </summary>
        public static bool TextureFormatHasAlpha(TextureFormat format)
        {
            switch (format)
            {
                // Formats WITH alpha
                case TextureFormat.D3DFMT_A8R8G8B8:
                case TextureFormat.D3DFMT_A8B8G8R8:
                case TextureFormat.D3DFMT_A1R5G5B5:
                case TextureFormat.D3DFMT_A8:
                case TextureFormat.D3DFMT_DXT3:
                case TextureFormat.D3DFMT_DXT5:
                case TextureFormat.D3DFMT_BC7:
                    return true;
                // Formats WITHOUT alpha (or only 1-bit alpha like DXT1)
                case TextureFormat.D3DFMT_X8R8G8B8:
                case TextureFormat.D3DFMT_L8:
                case TextureFormat.D3DFMT_DXT1:
                case TextureFormat.D3DFMT_ATI1:
                case TextureFormat.D3DFMT_ATI2:
                default:
                    return false;
            }
        }

        /// <summary>
        /// Check if a drawable uses a shader that requires alpha transparency.
        /// This includes hair shaders (ped_hair_spiked.sps) and other alpha/cutout shaders.
        /// </summary>
        public static bool DrawableUsesAlphaShader(DrawableBase drawable)
        {
            if (drawable == null) return false;
            var sg = drawable.ShaderGroup;
            if (sg?.Shaders?.data_items == null) return false;

            foreach (var shader in sg.Shaders.data_items)
            {
                if (shader == null) continue;
                uint hash = shader.FileName.Hash;

                // Hair shader — always uses alpha
                if (hash == HairShaderHash) return true;

                // Other known alpha/cutout shaders used by ped components.
                // These shader names contain "alpha" or "cutout" in GTA V's shader files.
                // Hash values computed from JenkHash of the .sps filename.
                if (hash == 2865780613u   // ped_default_alpha.sps
                    || hash == 3176173383u  // ped_default_cutout.sps
                    || hash == 3190732435u  // cutout_um.sps
                    || hash == 748520668u   // normal_cutout_um.sps
                    || hash == 1436689415u  // normal_spec_reflect_emissivenight_alpha.sps
                    || hash == 179247185u   // emissive_alpha.sps
                    || hash == 1314864030u  // emissive_alpha_tnt.sps
                    || hash == 1478174766u  // emissive_additive_alpha.sps
                    || hash == 3733846327u  // emissivenight_alpha.sps
                    || hash == 3174327089u  // emissivestrong_alpha.sps
                    || hash == 3924045432u  // glass_emissive.sps
                    || hash == 485710087u   // glass_emissivenight_alpha.sps
                    || hash == 2055615352u  // glass_emissive_alpha.sps
                    ) return true;

                // Also check HardAlphaBlend shader parameter — if set, the shader uses alpha
                if (shader.ParametersList?.Parameters != null && shader.ParametersList.Hashes != null)
                {
                    var pl = shader.ParametersList.Parameters;
                    var hl = shader.ParametersList.Hashes;
                    for (int pi = 0; pi < pl.Length && pi < hl.Length; pi++)
                    {
                        if ((uint)hl[pi] == (uint)ShaderParamNames.HardAlphaBlend)
                        {
                            if (pl[pi]?.Data is SharpDX.Vector4 v && v.X > 0.0f)
                                return true;
                            break;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Scan RGBA pixel data for any non-opaque pixels (alpha &lt; 255).
        /// Returns true if any pixel has alpha &lt; 250 (with small tolerance for
        /// compression artifacts in DXT formats).
        /// Input format: BGRA bytes (4 bytes per pixel, as returned by DDSIO.GetPixels).
        /// </summary>
        public static bool ScanForAlphaPixels(byte[] rgbaPixels, int width, int height)
        {
            int pixelCount = width * height;
            if (rgbaPixels.Length < pixelCount * 4) return false;

            // Sample pixels (don't check every single one for performance on large textures)
            int step = Math.Max(1, pixelCount / 10000); // check ~10000 pixels max
            for (int i = 0; i < pixelCount; i += step)
            {
                int offset = i * 4;
                // BGRA format: offset+3 is alpha
                byte alpha = rgbaPixels[offset + 3];
                if (alpha < 250) // tolerance for DXT compression artifacts
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Fallback: embed the texture as a raw DDS file when pixel extraction fails (e.g. BC7).
        /// </summary>
        public static int EmbedDdsTexture(ExportContext ctx, Texture tex, string compName)
        {
            byte[] ddsData = null;
            try { ddsData = DDSIO.GetDDSFile(tex); } catch { }

            if (ddsData != null && ddsData.Length > 0)
            {
                int imgBv = ctx.AddBufferView(ddsData);
                var img = new GltfImage { name = compName + "_Diffuse_DDS", bufferView = imgBv, mimeType = "image/vnd-ms.dds" };
                int imageIdx = ctx.Images.Count;
                ctx.Images.Add(img);
                return imageIdx;
            }

            // No texture data at all - create a placeholder
            var placeholder = new GltfImage { name = compName + "_Diffuse_Missing" };
            int phIdx = ctx.Images.Count;
            ctx.Images.Add(placeholder);
            return phIdx;
        }

        #endregion

        #region Animation Helpers

        /// <summary>
        /// Convert clip time to sub-animation local time, replicating the
        /// ClipAnimationsEntry.GetPlaybackTime logic from the renderer.
        /// Maps the global clip time into the sub-animation's [StartTime, EndTime] range.
        /// </summary>
        public static float GetSubAnimPlaybackTime(float clipTime, float startTime, float endTime)
        {
            double duration = endTime - startTime;
            if (duration <= 0) return clipTime;
            double curpos = clipTime % duration;
            return startTime + (float)curpos;
        }

        /// <summary>
        /// Copy rotation animation from main Thigh bones to ThighRoll bones.
        /// This replicates the hardcoded hack in Renderable.cs (lines 517-528):
        ///   RB_L_ThighRoll copies rotation from SKEL_L_Thigh
        ///   RB_R_ThighRoll copies rotation from SKEL_R_Thigh
        /// The copy only happens if:
        ///   - The ThighRoll bone exists in the skeleton
        ///   - The ThighRoll bone does NOT already have its own rotation channel
        ///   - The Thigh bone HAS rotation data in the clip
        ///   - The ThighRoll bone's parent is NOT already the Thigh bone
        ///     (if it is, it inherits the rotation naturally via the hierarchy)
        /// Uses per-ped BoneTagToNode mapping from pedData.
        /// </summary>
        public static void CopyThighRollRotation(ExportContext ctx, PedArmatureData pedData,
            GltfAnimation anim,
            List<(Animation Animation, float StartTime, float EndTime)> subAnimations,
            int timeAcc, int frameCount, float frameDelta,
            HashSet<ushort> rotationBoneTags)
        {
            var bones = pedData.BoneTagToNode; // per-ped mapping
            var rollMappings = new[]
            {
                (rollTag: BoneTag_RB_L_ThighRoll, thighTag: BoneTag_SKEL_L_Thigh),
                (rollTag: BoneTag_RB_R_ThighRoll, thighTag: BoneTag_SKEL_R_Thigh),
            };

            foreach (var mapping in rollMappings)
            {
                // Both ThighRoll and Thigh must exist in the skeleton
                if (!bones.TryGetValue(mapping.rollTag, out int rollNodeIdx)) continue;
                if (!bones.TryGetValue(mapping.thighTag, out int thighNodeIdx)) continue;

                // If ThighRoll already has rotation data from the clip, skip
                if (rotationBoneTags.Contains(mapping.rollTag)) continue;

                // If Thigh has no rotation data, nothing to copy
                if (!rotationBoneTags.Contains(mapping.thighTag)) continue;

                // Check if ThighRoll's parent is the Thigh bone — if so, the rotation
                // is already inherited naturally via the glTF node hierarchy, and we
                // should NOT copy it (same logic as the renderer's tag != bone.Parent?.Tag check).
                // We check if the ThighRoll node is a direct child of the Thigh node.
                bool parentIsThigh = false;
                if (ctx.NodeChildren.TryGetValue(thighNodeIdx, out var children))
                {
                    parentIsThigh = children.Contains(rollNodeIdx);
                }
                if (parentIsThigh) continue;

                // Search across all sub-animations for the Thigh bone rotation track.
                // The Thigh rotation might be in any of the sub-animations within
                // a ClipAnimationList.
                Animation thighAnimData = null;
                int thighBoneIdx = -1;
                float thighStartTime = 0f;
                float thighEndTime = 0f;
                foreach (var subAnim in subAnimations)
                {
                    var boneIds = subAnim.Animation.BoneIds?.data_items;
                    if (boneIds == null) continue;
                    for (int bi = 0; bi < boneIds.Length; bi++)
                    {
                        if (boneIds[bi].BoneId == mapping.thighTag && boneIds[bi].Track == 1)
                        {
                            thighAnimData = subAnim.Animation;
                            thighBoneIdx = bi;
                            thighStartTime = subAnim.StartTime;
                            thighEndTime = subAnim.EndTime;
                            break;
                        }
                    }
                    if (thighBoneIdx >= 0) break;
                }
                if (thighBoneIdx < 0 || thighAnimData == null) continue;

                // Sample the Thigh rotation at each frame and create a rotation channel for ThighRoll
                var vals = new List<float>();
                for (int f = 0; f < frameCount; f++)
                {
                    Quaternion val = Quaternion.Identity;
                    try
                    {
                        float t = GetSubAnimPlaybackTime(f * frameDelta, thighStartTime, thighEndTime);
                        var fp = thighAnimData.GetFramePosition(t);
                        val = thighAnimData.EvaluateQuaternion(fp, thighBoneIdx, true);
                    }
                    catch { }
                    // Y-up LH to Y-up RH quaternion conversion
                    vals.Add(val.X); vals.Add(val.Z); vals.Add(-val.Y); vals.Add(val.W);
                }
                byte[] vb = new byte[vals.Count * 4];
                Buffer.BlockCopy(vals.ToArray(), 0, vb, 0, vb.Length);
                int valBv = ctx.AddBufferView(vb);
                int valAcc = ctx.AddAccessor(valBv, 5126, "VEC4", frameCount);
                int sidx = anim.samplers.Count;
                anim.samplers.Add(new GltfAnimationSampler { input = timeAcc, output = valAcc });
                anim.channels.Add(new GltfAnimationChannel
                {
                    sampler = sidx,
                    target = new GltfAnimationChannelTarget { node = rollNodeIdx, path = "rotation" }
                });
            }
        }

        #endregion

        #region File Writing

        public static void WriteGltf(ExportContext ctx, string filePath, string sceneName = "Scene", string generatorName = "CodeWalker Exporter")
        {
            string base64 = Convert.ToBase64String(ctx.BufferData.ToArray());
            string json = BuildJson(ctx, "data:application/octet-stream;base64," + base64, true, sceneName, generatorName);
            File.WriteAllText(filePath, json);
        }

        public static void WriteGlb(ExportContext ctx, string filePath, string sceneName = "Scene", string generatorName = "CodeWalker Exporter")
        {
            string json = BuildJson(ctx, null, false, sceneName, generatorName);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            int jsonPad = (4 - (jsonBytes.Length % 4)) % 4;
            byte[] jsonPadded = new byte[jsonBytes.Length + jsonPad];
            Array.Copy(jsonBytes, jsonPadded, jsonBytes.Length);
            for (int i = jsonBytes.Length; i < jsonPadded.Length; i++) jsonPadded[i] = 0x20;

            byte[] binData = ctx.BufferData.ToArray();
            int binPad = (4 - (binData.Length % 4)) % 4;
            if (binPad > 0) Array.Resize(ref binData, binData.Length + binPad);

            int totalLength = 12 + 8 + jsonPadded.Length + 8 + binData.Length;
            using (var fs = new FileStream(filePath, FileMode.Create))
            using (var w = new BinaryWriter(fs))
            {
                w.Write(0x46546C67); w.Write(2); w.Write(totalLength);
                w.Write(jsonPadded.Length); w.Write(0x4E4F534A); w.Write(jsonPadded);
                w.Write(binData.Length); w.Write(0x004E4942); w.Write(binData);
            }
        }

        public static string BuildJson(ExportContext ctx, string bufferUri, bool indent, string sceneName = "Scene", string generatorName = "CodeWalker Exporter")
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"asset\":{\"generator\":"); sb.Append(JsonStr(generatorName)); sb.Append(",\"version\":\"2.0\"},");
            sb.Append("\"scene\":0,");

            // Scenes
            sb.Append("\"scenes\":[{\"name\":"); sb.Append(JsonStr(sceneName)); sb.Append(",\"nodes\":[0]}],");

            // Nodes
            sb.Append("\"nodes\":[");
            for (int i = 0; i < ctx.Nodes.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var n = ctx.Nodes[i];
                sb.Append("{");
                bool first = true;
                if (!string.IsNullOrEmpty(n.name)) { sb.Append("\"name\":"); sb.Append(JsonStr(n.name)); first = false; }
                if (n.mesh.HasValue) { if (!first) sb.Append(","); sb.Append("\"mesh\":"); sb.Append(n.mesh.Value); first = false; }
                if (n.skin.HasValue) { if (!first) sb.Append(","); sb.Append("\"skin\":"); sb.Append(n.skin.Value); first = false; }
                if (n.translation != null) { if (!first) sb.Append(","); sb.Append("\"translation\":"); sb.Append(JsonArr(n.translation)); first = false; }
                if (n.rotation != null) { if (!first) sb.Append(","); sb.Append("\"rotation\":"); sb.Append(JsonArr(n.rotation)); first = false; }
                if (n.scale != null) { if (!first) sb.Append(","); sb.Append("\"scale\":"); sb.Append(JsonArr(n.scale)); first = false; }
                if (ctx.NodeChildren.ContainsKey(i) && ctx.NodeChildren[i].Count > 0)
                { if (!first) sb.Append(","); sb.Append("\"children\":"); sb.Append(JsonArr(ctx.NodeChildren[i])); }
                sb.Append("}");
            }
            sb.Append("],");

            // Meshes
            sb.Append("\"meshes\":[");
            for (int i = 0; i < ctx.Meshes.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var m = ctx.Meshes[i];
                sb.Append("{\"name\":"); sb.Append(JsonStr(m.name)); sb.Append(",\"primitives\":[");
                for (int j = 0; j < m.primitives.Count; j++)
                {
                    if (j > 0) sb.Append(",");
                    var p = m.primitives[j];
                    sb.Append("{\"attributes\":{");
                    int ai = 0;
                    foreach (var kv in p.attributes)
                    { if (ai++ > 0) sb.Append(","); sb.Append(JsonStr(kv.Key)); sb.Append(":"); sb.Append(kv.Value); }
                    sb.Append("}");
                    if (p.indices.HasValue) { sb.Append(",\"indices\":"); sb.Append(p.indices.Value); }
                    if (p.material.HasValue) { sb.Append(",\"material\":"); sb.Append(p.material.Value); }
                    sb.Append("}");
                }
                sb.Append("]}");
            }
            sb.Append("],");

            // Skins
            if (ctx.Skins.Count > 0)
            {
                sb.Append("\"skins\":[");
                for (int i = 0; i < ctx.Skins.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var s = ctx.Skins[i];
                    sb.Append("{\"name\":"); sb.Append(JsonStr(s.name));
                    sb.Append(",\"inverseBindMatrices\":"); sb.Append(s.inverseBindMatrices);
                    sb.Append(",\"skeleton\":"); sb.Append(s.skeleton);
                    sb.Append(",\"joints\":"); sb.Append(JsonArr(s.joints));
                    sb.Append("}");
                }
                sb.Append("],");
            }

            // Materials
            sb.Append("\"materials\":[");
            for (int i = 0; i < ctx.Materials.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var m = ctx.Materials[i];
                sb.Append("{\"name\":"); sb.Append(JsonStr(m.name));
                sb.Append(",\"pbrMetallicRoughness\":{");
                sb.Append("\"baseColorFactor\":"); sb.Append(JsonArr(m.pbrMetallicRoughness.baseColorFactor));
                if (m.pbrMetallicRoughness.baseColorTexture != null)
                { sb.Append(",\"baseColorTexture\":{\"index\":"); sb.Append(m.pbrMetallicRoughness.baseColorTexture.index); sb.Append(",\"texCoord\":"); sb.Append(m.pbrMetallicRoughness.baseColorTexture.texCoord); sb.Append("}"); }
                sb.Append(",\"metallicFactor\":"); sb.Append(FloatStr(m.pbrMetallicRoughness.metallicFactor));
                sb.Append(",\"roughnessFactor\":"); sb.Append(FloatStr(m.pbrMetallicRoughness.roughnessFactor));
                sb.Append("}");
                // Alpha mode — required for hair and other transparent materials
                if (!string.IsNullOrEmpty(m.alphaMode))
                {
                    sb.Append(",\"alphaMode\":"); sb.Append(JsonStr(m.alphaMode));
                }
                if (m.alphaCutoff.HasValue)
                {
                    sb.Append(",\"alphaCutoff\":"); sb.Append(FloatStr(m.alphaCutoff.Value));
                }
                if (m.doubleSided)
                {
                    sb.Append(",\"doubleSided\":true");
                }
                sb.Append("}");
            }
            sb.Append("],");

            // Textures
            if (ctx.Textures.Count > 0)
            {
                sb.Append("\"textures\":[");
                for (int i = 0; i < ctx.Textures.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var t = ctx.Textures[i];
                    sb.Append("{\"source\":"); sb.Append(t.source);
                    if (t.sampler.HasValue) { sb.Append(",\"sampler\":"); sb.Append(t.sampler.Value); }
                    sb.Append("}");
                }
                sb.Append("],");
            }

            // Images
            if (ctx.Images.Count > 0)
            {
                sb.Append("\"images\":[");
                for (int i = 0; i < ctx.Images.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var img = ctx.Images[i];
                    sb.Append("{\"mimeType\":"); sb.Append(JsonStr(img.mimeType));
                    if (!string.IsNullOrEmpty(img.name)) { sb.Append(",\"name\":"); sb.Append(JsonStr(img.name)); }
                    if (img.bufferView.HasValue) { sb.Append(",\"bufferView\":"); sb.Append(img.bufferView.Value); }
                    sb.Append("}");
                }
                sb.Append("],");
            }

            // Samplers
            if (ctx.Samplers.Count > 0)
            {
                sb.Append("\"samplers\":[");
                for (int i = 0; i < ctx.Samplers.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var s = ctx.Samplers[i];
                    sb.Append("{\"magFilter\":"); sb.Append(s.magFilter);
                    sb.Append(",\"minFilter\":"); sb.Append(s.minFilter);
                    sb.Append(",\"wrapS\":"); sb.Append(s.wrapS);
                    sb.Append(",\"wrapT\":"); sb.Append(s.wrapT);
                    sb.Append("}");
                }
                sb.Append("],");
            }

            // Accessors
            sb.Append("\"accessors\":[");
            for (int i = 0; i < ctx.Accessors.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var a = ctx.Accessors[i];
                sb.Append("{\"bufferView\":"); sb.Append(a.bufferView);
                sb.Append(",\"byteOffset\":"); sb.Append(a.byteOffset);
                sb.Append(",\"componentType\":"); sb.Append(a.componentType);
                sb.Append(",\"count\":"); sb.Append(a.count);
                sb.Append(",\"type\":"); sb.Append(JsonStr(a.type));
                if (a.min != null) { sb.Append(",\"min\":"); sb.Append(JsonArr(a.min)); }
                if (a.max != null) { sb.Append(",\"max\":"); sb.Append(JsonArr(a.max)); }
                sb.Append("}");
            }
            sb.Append("],");

            // BufferViews
            sb.Append("\"bufferViews\":[");
            for (int i = 0; i < ctx.BufferViews.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var bv = ctx.BufferViews[i];
                sb.Append("{\"buffer\":"); sb.Append(bv.buffer);
                sb.Append(",\"byteOffset\":"); sb.Append(bv.byteOffset);
                sb.Append(",\"byteLength\":"); sb.Append(bv.byteLength);
                if (bv.byteStride.HasValue) { sb.Append(",\"byteStride\":"); sb.Append(bv.byteStride.Value); }
                if (bv.target.HasValue) { sb.Append(",\"target\":"); sb.Append(bv.target.Value); }
                sb.Append("}");
            }
            sb.Append("],");

            // Buffers
            sb.Append("\"buffers\":[{\"byteLength\":"); sb.Append((uint)ctx.BufferData.Count);
            if (bufferUri != null) { sb.Append(",\"uri\":"); sb.Append(JsonStr(bufferUri)); }
            sb.Append("}]");

            // Animations
            if (ctx.Animations.Count > 0)
            {
                sb.Append(",\"animations\":[");
                for (int i = 0; i < ctx.Animations.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var a = ctx.Animations[i];
                    sb.Append("{\"name\":"); sb.Append(JsonStr(a.name));
                    sb.Append(",\"channels\":[");
                    for (int j = 0; j < a.channels.Count; j++)
                    {
                        if (j > 0) sb.Append(",");
                        var c = a.channels[j];
                        sb.Append("{\"sampler\":"); sb.Append(c.sampler);
                        sb.Append(",\"target\":{\"node\":"); sb.Append(c.target.node);
                        sb.Append(",\"path\":"); sb.Append(JsonStr(c.target.path)); sb.Append("}}");
                    }
                    sb.Append("],\"samplers\":[");
                    for (int j = 0; j < a.samplers.Count; j++)
                    {
                        if (j > 0) sb.Append(",");
                        var s = a.samplers[j];
                        sb.Append("{\"input\":"); sb.Append(s.input);
                        sb.Append(",\"output\":"); sb.Append(s.output);
                        sb.Append(",\"interpolation\":"); sb.Append(JsonStr(s.interpolation)); sb.Append("}");
                    }
                    sb.Append("]}");
                }
                sb.Append("]");
            }

            sb.Append("}");
            return sb.ToString();
        }

        #endregion

        #region JSON Helpers

        public static string JsonStr(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 32) sb.Append("\\u" + ((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string FloatStr(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) return "0";
            return f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        public static string JsonArr(float[] arr)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(FloatStr(arr[i]));
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static string JsonArr(int[] arr)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(arr[i]);
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static string JsonArr(List<int> list)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(list[i]);
            }
            sb.Append(']');
            return sb.ToString();
        }

        #endregion

        #region Half-Float Conversion

        public static class HalfHelper
        {
            public static float HalfToSingle(byte b0, byte b1)
            {
                uint half = (uint)(b0 | (b1 << 8));
                return HalfToSingle(half);
            }

            public static float HalfToSingle(uint half)
            {
                uint sign = (half >> 15) & 0x1;
                uint exponent = (half >> 10) & 0x1F;
                uint mantissa = half & 0x3FF;

                uint result;
                if (exponent == 0)
                {
                    if (mantissa == 0)
                    {
                        // Zero
                        result = sign << 31;
                    }
                    else
                    {
                        // Denormalized - normalize it
                        exponent = 1;
                        while ((mantissa & 0x400) == 0) { mantissa <<= 1; exponent++; }
                        mantissa &= 0x3FF;
                        result = (sign << 31) | ((exponent - 15 + 127) << 23) | (mantissa << 13);
                    }
                }
                else if (exponent == 31)
                {
                    // Inf or NaN
                    result = (sign << 31) | (0xFF << 23) | (mantissa << 13);
                }
                else
                {
                    // Normalized
                    result = (sign << 31) | ((exponent - 15 + 127) << 23) | (mantissa << 13);
                }

                byte[] bytes = BitConverter.GetBytes(result);
                return BitConverter.ToSingle(bytes, 0);
            }
        }

        #endregion

        #region Coordinate Conversion

        /// <summary>
        /// Convert a GTA LH space matrix (SharpDX row-major, row-vector convention)
        /// to glTF RH Y-up space (column-major, column-vector convention).
        /// Formula: M_gltf_col = P * M_gta_row^T * P^T where P maps (X,Y,Z) -> (X,Z,-Y).
        /// Result is 16 floats in column-major order ready for glTF buffer.
        /// </summary>
        public static float[] ConvertMatrixToGltf(Matrix m)
        {
            return new float[]
            {
                m.M11,  m.M13, -m.M12,  m.M14,
                m.M31,  m.M33, -m.M32,  m.M34,
               -m.M21, -m.M23,  m.M22, -m.M24,
                m.M41,  m.M43, -m.M42,  m.M44,
            };
        }

        /// <summary>
        /// Recursively compute the global transform for bone at index i,
        /// ensuring the parent's global transform is computed first.
        /// This handles GTA V ped skeletons where bones are not stored in
        /// parent-before-child order.
        /// </summary>
        public static void ComputeGlobalTransform(int i, Bone[] bones, Matrix[] localTransforms, Matrix[] globalTransforms, bool[] globalComputed)
        {
            if (globalComputed[i]) return;
            var bone = bones[i];
            if (bone.ParentIndex >= 0 && bone.ParentIndex < bones.Length && bone.ParentIndex != i)
            {
                ComputeGlobalTransform(bone.ParentIndex, bones, localTransforms, globalTransforms, globalComputed);
                globalTransforms[i] = localTransforms[i] * globalTransforms[bone.ParentIndex];
            }
            else
            {
                globalTransforms[i] = localTransforms[i];
            }
            globalComputed[i] = true;
        }

        #endregion

        #region PNG Encoding

        /// <summary>
        /// Encode RGBA pixel data to PNG bytes using System.Drawing.
        /// DDSIO.GetPixels returns BGRA byte order, and Format32bppArgb expects BGRA,
        /// so direct copy works without channel swapping.
        /// </summary>
        public static byte[] EncodePng(byte[] rgbaPixels, int width, int height)
        {
            // Use System.Drawing.Bitmap for reliable PNG encoding from DDSIO pixel data.
            // DDSIO.GetPixels always returns BGRA byte order:
            //   - Compressed formats (DXT1/3/5, BC4/5): decompressor outputs RGBA, then
            //     swaprb swaps R<->B producing BGRA
            //   - Uncompressed B8G8R8A8: already BGRA, swaprb=false
            // Format32bppArgb in System.Drawing expects BGRA in memory, so direct copy works.
            // UV coordinates are NOT flipped since glTF and DirectX both use V=0 at top.
            try
            {
                if (rgbaPixels == null || rgbaPixels.Length < width * height * 4) return null;

                using (var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
                        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    // Direct copy: DDSIO returns BGRA, Format32bppArgb expects BGRA.
                    Marshal.Copy(rgbaPixels, 0, bd.Scan0, rgbaPixels.Length);
                    bmp.UnlockBits(bd);
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        return ms.ToArray();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        #endregion
    }
}
