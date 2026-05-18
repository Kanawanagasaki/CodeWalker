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
    /// Exports a Ped with mesh, textures, armature and animation to glTF 2.0 or GLB format.
    /// </summary>
    public static class PedGltfExporter
    {
        #region glTF Data Structures

        class GltfScene
        {
            public string name = "";
            public List<int> nodes = new List<int>();
        }

        class GltfNode
        {
            public string name;
            public int? mesh;
            public int? skin;
            public float[] translation;
            public float[] rotation;
            public float[] scale;
        }

        class GltfMesh
        {
            public string name;
            public List<GltfMeshPrimitive> primitives = new List<GltfMeshPrimitive>();
        }

        class GltfMeshPrimitive
        {
            public Dictionary<string, int> attributes = new Dictionary<string, int>();
            public int? indices = null;
            public int? material = null;
        }

        class GltfSkin
        {
            public string name;
            public int inverseBindMatrices;
            public int skeleton;
            public List<int> joints = new List<int>();
        }

        class GltfAccessor
        {
            public int bufferView;
            public int byteOffset;
            public int componentType;
            public int count;
            public string type;
            public float[] min;
            public float[] max;
        }

        class GltfBufferView
        {
            public int buffer;
            public int byteOffset;
            public int byteLength;
            public int? byteStride;
            public int? target;
        }

        class GltfMaterial
        {
            public string name;
            public GltfPbrMetallicRoughness pbrMetallicRoughness;
            public string alphaMode; // "OPAQUE" (default), "MASK", "BLEND"
            public float? alphaCutoff; // used with MASK mode, default 0.5
            public bool doubleSided;
        }

        class GltfPbrMetallicRoughness
        {
            public float[] baseColorFactor = new float[] { 1f, 1f, 1f, 1f };
            public GltfTextureInfo baseColorTexture;
            public float metallicFactor = 0f;
            public float roughnessFactor = 1f;
        }

        class GltfTextureInfo
        {
            public int index;
            public int texCoord = 0;
        }

        class GltfTexture
        {
            public int source;
            public int? sampler;
        }

        class GltfImage
        {
            public string name;
            public string mimeType = "image/png";
            public int? bufferView;
        }

        class GltfSampler
        {
            public int magFilter = 9729;
            public int minFilter = 9987;
            public int wrapS = 10497;
            public int wrapT = 10497;
        }

        class GltfAnimation
        {
            public string name;
            public List<GltfAnimationChannel> channels = new List<GltfAnimationChannel>();
            public List<GltfAnimationSampler> samplers = new List<GltfAnimationSampler>();
        }

        class GltfAnimationChannel
        {
            public int sampler;
            public GltfAnimationChannelTarget target;
        }

        class GltfAnimationChannelTarget
        {
            public int node;
            public string path;
        }

        class GltfAnimationSampler
        {
            public int input;
            public int output;
            public string interpolation = "LINEAR";
        }

        #endregion

        #region Export Context

        class ExportContext
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
            public Dictionary<MetaHash, int> TextureToGltfIndex = new Dictionary<MetaHash, int>();
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

        public static void Export(Ped ped, string filePath)
        {
            bool isGlb = filePath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);
            var ctx = new ExportContext();

            BuildArmature(ctx, ped);
            BuildMeshes(ctx, ped);
            if (ped.AnimClip != null)
                BuildAnimation(ctx, ped);

            if (isGlb)
                WriteGlb(ctx, filePath);
            else
                WriteGltf(ctx, filePath);
        }

        #region Armature

        static void BuildArmature(ExportContext ctx, Ped ped)
        {
            var skeleton = ped.Skeleton;
            if (skeleton == null) return;
            var bones = skeleton.Bones?.Items;
            if (bones == null || bones.Length == 0) return;

            // Ped root node
            var pedRoot = new GltfNode
            {
                name = ped.Name ?? "Ped",
                translation = new float[] { 0, 0, 0 },
                rotation = new float[] { 0, 0, 0, 1 },
                scale = new float[] { 1, 1, 1 },
            };
            ctx.PedRootNodeIndex = ctx.Nodes.Count;
            ctx.Nodes.Add(pedRoot);
            ctx.NodeChildren[ctx.PedRootNodeIndex] = new List<int>();

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
                ctx.BoneToNode[i] = nodeIdx;
                ctx.BoneTagToNode[bone.Tag] = nodeIdx;
                ctx.NodeChildren[nodeIdx] = new List<int>();
            }

            // Parent-child
            for (int i = 0; i < bones.Length; i++)
            {
                var bone = bones[i];
                int nodeIdx = ctx.BoneToNode[i];
                if (bone.ParentIndex >= 0 && bone.ParentIndex < bones.Length && bone.ParentIndex != i)
                {
                    int parentNodeIdx = ctx.BoneToNode[bone.ParentIndex];
                    ctx.NodeChildren[parentNodeIdx].Add(nodeIdx);
                }
                else
                {
                    ctx.RootNodeIndex = nodeIdx;
                    ctx.NodeChildren[ctx.PedRootNodeIndex].Add(nodeIdx);
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
                name = "PedSkin",
                inverseBindMatrices = ibmAcc,
                skeleton = ctx.RootNodeIndex,
            };
            for (int i = 0; i < bones.Length; i++)
                skin.joints.Add(ctx.BoneToNode[i]);
            ctx.Skins.Add(skin);
        }

        /// <summary>
        /// Recursively compute the global transform for bone at index i,
        /// ensuring the parent's global transform is computed first.
        /// This handles GTA V ped skeletons where bones are not stored in
        /// parent-before-child order.
        /// </summary>
        static void ComputeGlobalTransform(int i, Bone[] bones, Matrix[] localTransforms, Matrix[] globalTransforms, bool[] globalComputed)
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

        /// <summary>
        /// Convert a GTA LH space matrix (SharpDX row-major, row-vector convention)
        /// to glTF RH Y-up space (column-major, column-vector convention).
        /// Formula: M_gltf_col = P * M_gta_row^T * P^T where P maps (X,Y,Z) → (X,Z,-Y).
        /// Result is 16 floats in column-major order ready for glTF buffer.
        /// </summary>
        static float[] ConvertMatrixToGltf(Matrix m)
        {
            return new float[]
            {
                m.M11,  m.M13, -m.M12,  m.M14,
                m.M31,  m.M33, -m.M32,  m.M34,
               -m.M21, -m.M23,  m.M22, -m.M24,
                m.M41,  m.M43, -m.M42,  m.M44,
            };
        }

        #endregion

        #region Meshes

        /// <summary>
        /// Helper data for cloth vertex bone weight computation.
        /// Passed to BuildPrimitive for vertices where blendIndices[2] == 255.
        /// </summary>
        class ClothVertexData
        {
            public CharClothBoneWeightsInds[] BoneWeightsInds;  // from Controller.BoneWeightsInds.data_items
            public int[] ClothBoneToArrayIndex;                  // maps ClothInstance.Bones[i] → skeleton bone array index
            public int DefaultClothBoneArrayIndex;               // fallback cloth bone
        }

        // Shader hash for ped_hair_spiked.sps — used to detect hair control mesh geometries
        const uint HairShaderHash = 100720695;
        // ShaderParamNames.orderNumber hash
        const uint OrderNumberHash = 1617153586;

        // Bone tags for ThighRoll bones that need rotation copied from main Thigh bones.
        // This replicates the hardcoded hack in Renderable.cs (lines 517-518):
        //   RB_L_ThighRoll (23639) → SKEL_L_Thigh (58271)
        //   RB_R_ThighRoll (6442)  → SKEL_R_Thigh (51826)
        // Without this, ThighRoll bones stay at bind pose while the thigh animates,
        // causing the leg to "arch" or appear noodly in Blender.
        const ushort BoneTag_RB_L_ThighRoll = 23639;
        const ushort BoneTag_RB_R_ThighRoll = 6442;
        const ushort BoneTag_SKEL_L_Thigh = 58271;
        const ushort BoneTag_SKEL_R_Thigh = 51826;

        static void BuildMeshes(ExportContext ctx, Ped ped)
        {
            string[] compNames = { "Head", "Berd", "Hair", "Uppr", "Lowr", "Hand", "Feet", "Teef", "Accs", "Task", "Decl", "Jbib" };
            var skeleton = ped.Skeleton;
            var bones = skeleton?.Bones?.Items;

            // Pre-build Bone→array-index lookup for cloth bone remapping
            Dictionary<Bone, int> boneToArrayIndex = null;
            if (bones != null)
            {
                boneToArrayIndex = new Dictionary<Bone, int>(bones.Length);
                for (int i = 0; i < bones.Length; i++)
                    boneToArrayIndex[bones[i]] = i;
            }

            // Pre-build bone Tag→array-position lookup for the ped's main skeleton.
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

            for (int compIdx = 0; compIdx < 12; compIdx++)
            {
                var drawable = ped.Drawables[compIdx];
                if (drawable == null) continue;
                var texture = ped.Textures[compIdx];
                var models = drawable.DrawableModels?.High;
                if (models == null) continue;

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

                // Build component skeleton → ped skeleton bone index mapping.
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
                    materialIdx = ExportTexture(ctx, diffuseTex, compNames[compIdx], drawable);
                else
                {
                    var mat = new GltfMaterial
                    {
                        name = compNames[compIdx] + "_Material",
                        pbrMetallicRoughness = new GltfPbrMetallicRoughness { metallicFactor = 0f, roughnessFactor = 1f },
                    };
                    materialIdx = ctx.Materials.Count;
                    ctx.Materials.Add(mat);
                }

                var mesh = new GltfMesh { name = compNames[compIdx] };
                for (int mi = 0; mi < models.Length; mi++)
                {
                    var model = models[mi];
                    if (model?.Geometries == null) continue;
                    for (int gi = 0; gi < model.Geometries.Length; gi++)
                    {
                        var geom = model.Geometries[gi];
                        if (geom == null) continue;

                        // Skip hair control mesh geometries (orderNumber > 0 on hair shaders).
                        // In GTA V, these geometries drive GPU tessellation and should not be
                        // rendered directly. The renderer skips them via disableRendering flag.
                        if (ShouldSkipGeometry(geom)) continue;

                        var prim = BuildPrimitive(ctx, geom, model, ped.Skeleton, clothVertexData, compBoneToPedBone);
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
                        name = compNames[compIdx] + "_Mesh",
                        mesh = meshIdx,
                    };
                    if (ctx.Skins.Count > 0) meshNode.skin = 0;

                    int meshNodeIdx = ctx.Nodes.Count;
                    ctx.Nodes.Add(meshNode);
                    ctx.MeshNodeIndices.Add(meshNodeIdx);
                    ctx.NodeChildren[meshNodeIdx] = new List<int>();
                    ctx.NodeChildren[ctx.PedRootNodeIndex].Add(meshNodeIdx);
                }
            }
        }

        static GltfMeshPrimitive BuildPrimitive(ExportContext ctx, DrawableGeometry geom, DrawableModel model, Skeleton skeleton, ClothVertexData clothVertexData = null, Dictionary<int, int> compBoneToPedBone = null)
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
            VertexComponentType biType = hasBlendIndices ? decl.GetComponentType(2) : VertexComponentType.Nothing;

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
                            // Each byte is a normalized weight [0-255] → [0.0-1.0].
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
                    // In the shader: binds.w→cv0, binds.x→cv1, binds.y→cv2;
                    // weights.z→cv0, weights.y→cv1, weights.x→cv2; weights.w = thickness.
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
                        // bi3→cv0 (weight wz), bi0→cv1 (weight wy), bi1→cv2 (weight wx)
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

                        // Remap local bone indices → component skeleton indices → ped skeleton indices
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

        static ushort RemapBoneIndex(byte localIdx, ushort[] geomBoneIds, Skeleton skeleton, Dictionary<int, int> compBoneToPedBone = null)
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

            // If we have a component→ped mapping, use it to convert the index
            if (compBoneToPedBone != null && compBoneToPedBone.TryGetValue(compIdx, out int pedIdx))
                return (ushort)pedIdx;

            // No mapping available — the component and ped skeletons share the same
            // bone ordering (or the component has no skeleton of its own), so the
            // BoneIds value can be used directly as a ped skeleton array index.
            return (ushort)compIdx;
        }

        /// <summary>
        /// Find the nearest cloth bone ancestor for a given bone by walking up the hierarchy.
        /// If the bone itself is a cloth bone, returns its own index.
        /// If no ancestor is a cloth bone, returns defaultClothBone.
        /// NOTE: This method is no longer used — the clothBoneRemap approach that called it
        /// has been removed because it made cloth rigid by collapsing multi-bone influences.
        /// Kept for reference in case a better remapping strategy is needed in the future.
        /// </summary>
        static int FindNearestClothAncestor(int boneIdx, Bone[] bones, HashSet<int> clothBoneIndices, int defaultClothBone)
        {
            int current = boneIdx;
            int limit = bones.Length + 1; // prevent infinite loops from bad data
            while (current >= 0 && current < bones.Length && limit-- > 0)
            {
                if (clothBoneIndices.Contains(current))
                    return current;
                var bone = bones[current];
                if (bone.ParentIndex >= 0 && bone.ParentIndex < bones.Length && bone.ParentIndex != current)
                    current = bone.ParentIndex;
                else
                    break; // reached root
            }
            // No cloth bone ancestor found, use the default (first cloth bone)
            return defaultClothBone >= 0 ? defaultClothBone : 0;
        }

        /// <summary>
        /// Add bone weights from a cloth simulation vertex into the combined weight dictionary.
        /// The cloth simulation vertex has 4 bone weights and 4 bone indices referencing
        /// ClothInstance.Bones[], which are then mapped to skeleton bone array indices.
        /// </summary>
        static void AddClothBoneWeights(Dictionary<int, float> combined, int cvIdx, float interpWeight, ClothVertexData clothData)
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
        static bool ShouldSkipGeometry(DrawableGeometry geom)
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

        /// <summary>
        /// Helper to convert IEEE 754 half-precision (2 bytes) to float.
        /// </summary>
        static class HalfHelper
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

        static Texture FindDiffuseTexture(DrawableBase drawable, Texture fallbackTex)
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

        static int ExportTexture(ExportContext ctx, Texture tex, string compName, DrawableBase drawable = null)
        {
            if (tex == null) return -1;
            if (ctx.TextureToGltfIndex.TryGetValue(tex.NameHash, out int existingIdx))
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
            ctx.TextureToGltfIndex[tex.NameHash] = texIdx;

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
        static bool TextureFormatHasAlpha(TextureFormat format)
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
        static bool DrawableUsesAlphaShader(DrawableBase drawable)
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
        static bool ScanForAlphaPixels(byte[] rgbaPixels, int width, int height)
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
        static int EmbedDdsTexture(ExportContext ctx, Texture tex, string compName)
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

        #region Animation

        static void BuildAnimation(ExportContext ctx, Ped ped)
        {
            var cme = ped.AnimClip;
            if (cme?.Clip == null) return;
            var skeleton = ped.Skeleton;
            if (skeleton == null) return;

            var anim = new GltfAnimation { name = cme.Clip.ShortName ?? cme.Clip.Name ?? "Animation" };

            // Build the list of sub-animations to process.
            // The GTA V renderer (Renderable.UpdateAnim) iterates over ALL sub-animations
            // in a ClipAnimationList, applying each one's bone tracks on top of the previous.
            // Previously this exporter only used the first sub-animation, which missed bone
            // tracks from other sub-animations. This caused player model distortion because
            // their clips (e.g. in move_m@generic) use ClipAnimationList with separate
            // sub-animations for upper body, lower body, etc.
            var subAnimations = new List<(Animation Animation, float StartTime, float EndTime)>();
            if (cme.Clip is ClipAnimation clipAnim)
            {
                if (clipAnim.Animation != null)
                    subAnimations.Add((clipAnim.Animation, clipAnim.StartTime, clipAnim.EndTime));
            }
            else if (cme.Clip is ClipAnimationList clipList && clipList.Animations != null)
            {
                foreach (var canim in clipList.Animations)
                {
                    if (canim?.Animation != null)
                        subAnimations.Add((canim.Animation, canim.StartTime, canim.EndTime));
                }
            }

            if (subAnimations.Count == 0) return;

            // Use the maximum duration across all sub-animations and the clip's own duration.
            float duration = 0f;
            foreach (var sa in subAnimations)
            {
                float saDur = sa.EndTime - sa.StartTime;
                if (saDur > duration) duration = saDur;
                if (sa.Animation?.Duration > 0 && sa.Animation.Duration > duration)
                    duration = sa.Animation.Duration;
            }
            if (duration <= 0) return;

            // Use the first sub-animation's frame info for frame count calculation.
            var firstAnim = subAnimations[0].Animation;
            int frameCount = Math.Min(firstAnim.Frames > 0 ? firstAnim.Frames : (int)(duration * 30f), 300);
            float frameDelta = duration / (frameCount - 1);

            // Time accessor
            var timeData = new float[frameCount];
            for (int f = 0; f < frameCount; f++) timeData[f] = f * frameDelta;
            byte[] timeBytes = new byte[frameCount * 4];
            Buffer.BlockCopy(timeData, 0, timeBytes, 0, timeBytes.Length);
            int timeBv = ctx.AddBufferView(timeBytes);
            int timeAcc = ctx.AddAccessor(timeBv, 5126, "SCALAR", frameCount, new float[] { 0f }, new float[] { duration });

            // Track which bone tags have rotation channels across all sub-animations,
            // so we can copy ThighRoll rotations from Thigh bones after the main loop.
            var rotationBoneTags = new HashSet<ushort>();

            // Process each sub-animation, just like the renderer does.
            // Each sub-animation provides bone tracks for a different set of bones,
            // and they are applied cumulatively (later sub-animations can override
            // earlier ones for the same bone+track combination).
            foreach (var subAnim in subAnimations)
            {
                var animData = subAnim.Animation;
                var boneIds = animData.BoneIds?.data_items;
                if (boneIds == null) continue;

                for (int bi = 0; bi < boneIds.Length; bi++)
                {
                    var boneId = boneIds[bi];
                    // Look up the glTF node by bone tag (BoneId = bone.Tag).
                    // Do NOT use bone.Index since BoneToNode is keyed by array position,
                    // and bone.Index can differ from array position in GTA V ped skeletons.
                    if (!ctx.BoneTagToNode.TryGetValue(boneId.BoneId, out int nodeIdx)) continue;

                    if (boneId.Track == 1) rotationBoneTags.Add(boneId.BoneId);

                    if (boneId.Track == 0) // Translation
                    {
                        var vals = new List<float>();
                        for (int f = 0; f < frameCount; f++)
                        {
                            Vector3 val = Vector3.Zero;
                            try
                            {
                                // Convert clip time to this sub-animation's local time,
                                // same as the renderer's canim.GetPlaybackTime(CurrentAnimTime).
                                float t = GetSubAnimPlaybackTime(f * frameDelta, subAnim.StartTime, subAnim.EndTime);
                                var fp = animData.GetFramePosition(t);
                                var v4 = animData.EvaluateVector4(fp, bi, true);
                                val = new Vector3(v4.X, v4.Y, v4.Z); // explicit conversion, drop W
                            }
                            catch { }
                            // Y-up LH to Y-up RH: glTF_X = GTA_X, glTF_Y = GTA_Z, glTF_Z = -GTA_Y
                            vals.Add(val.X); vals.Add(val.Z); vals.Add(-val.Y);
                        }
                        byte[] vb = new byte[vals.Count * 4];
                        Buffer.BlockCopy(vals.ToArray(), 0, vb, 0, vb.Length);
                        int valBv = ctx.AddBufferView(vb);
                        int valAcc = ctx.AddAccessor(valBv, 5126, "VEC3", frameCount);
                        int sidx = anim.samplers.Count;
                        anim.samplers.Add(new GltfAnimationSampler { input = timeAcc, output = valAcc });
                        anim.channels.Add(new GltfAnimationChannel { sampler = sidx, target = new GltfAnimationChannelTarget { node = nodeIdx, path = "translation" } });
                    }
                    else if (boneId.Track == 1) // Rotation
                    {
                        var vals = new List<float>();
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
                        byte[] vb = new byte[vals.Count * 4];
                        Buffer.BlockCopy(vals.ToArray(), 0, vb, 0, vb.Length);
                        int valBv = ctx.AddBufferView(vb);
                        int valAcc = ctx.AddAccessor(valBv, 5126, "VEC4", frameCount);
                        int sidx = anim.samplers.Count;
                        anim.samplers.Add(new GltfAnimationSampler { input = timeAcc, output = valAcc });
                        anim.channels.Add(new GltfAnimationChannel { sampler = sidx, target = new GltfAnimationChannelTarget { node = nodeIdx, path = "rotation" } });
                    }
                    else if (boneId.Track == 2) // Scale
                    {
                        var vals = new List<float>();
                        for (int f = 0; f < frameCount; f++)
                        {
                            Vector3 val = Vector3.One;
                            try
                            {
                                float t = GetSubAnimPlaybackTime(f * frameDelta, subAnim.StartTime, subAnim.EndTime);
                                var fp = animData.GetFramePosition(t);
                                var v4 = animData.EvaluateVector4(fp, bi, true);
                                val = new Vector3(v4.X, v4.Y, v4.Z); // explicit conversion, drop W
                            }
                            catch { }
                            // Scale: axis swap (X,Z,Y) same as bone scale - no negation
                            vals.Add(val.X); vals.Add(val.Z); vals.Add(val.Y);
                        }
                        byte[] vb = new byte[vals.Count * 4];
                        Buffer.BlockCopy(vals.ToArray(), 0, vb, 0, vb.Length);
                        int valBv = ctx.AddBufferView(vb);
                        int valAcc = ctx.AddAccessor(valBv, 5126, "VEC3", frameCount);
                        int sidx = anim.samplers.Count;
                        anim.samplers.Add(new GltfAnimationSampler { input = timeAcc, output = valAcc });
                        anim.channels.Add(new GltfAnimationChannel { sampler = sidx, target = new GltfAnimationChannelTarget { node = nodeIdx, path = "scale" } });
                    }
                }
            }

            // ThighRoll bone rotation copy — replicates the hardcoded hack from
            // Renderable.cs (lines 517-518). In GTA V, RB_L_ThighRoll and RB_R_ThighRoll
            // are helper bones that should copy the rotation of their corresponding main
            // Thigh bone (SKEL_L_Thigh / SKEL_R_Thigh). The animation clips typically
            // only contain rotation data for the main Thigh bones, not the ThighRoll bones.
            // Without this, ThighRoll bones stay at bind pose while the thigh animates,
            // causing the leg to "arch" or appear noodly in Blender.
            CopyThighRollRotation(ctx, anim, subAnimations, timeAcc, frameCount, frameDelta, rotationBoneTags);

            if (anim.channels.Count > 0)
                ctx.Animations.Add(anim);
        }

        /// <summary>
        /// Convert clip time to sub-animation local time, replicating the
        /// ClipAnimationsEntry.GetPlaybackTime logic from the renderer.
        /// Maps the global clip time into the sub-animation's [StartTime, EndTime] range.
        /// </summary>
        static float GetSubAnimPlaybackTime(float clipTime, float startTime, float endTime)
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
        /// </summary>
        static void CopyThighRollRotation(ExportContext ctx, GltfAnimation anim,
            List<(Animation Animation, float StartTime, float EndTime)> subAnimations,
            int timeAcc, int frameCount, float frameDelta,
            HashSet<ushort> rotationBoneTags)
        {
            var bones = ctx.BoneTagToNode; // shorthand
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

        #region File Writers

        static void WriteGltf(ExportContext ctx, string filePath)
        {
            string base64 = Convert.ToBase64String(ctx.BufferData.ToArray());
            string json = BuildJson(ctx, "data:application/octet-stream;base64," + base64, true);
            File.WriteAllText(filePath, json);
        }

        static void WriteGlb(ExportContext ctx, string filePath)
        {
            string json = BuildJson(ctx, null, false);
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

        static string BuildJson(ExportContext ctx, string bufferUri, bool indent)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append("\"asset\":{\"generator\":\"CodeWalker Ped Exporter\",\"version\":\"2.0\"},");
            sb.Append("\"scene\":0,");

            // Scenes
            sb.Append("\"scenes\":[{\"name\":\"PedScene\",\"nodes\":[0]}],");

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

        static string JsonStr(string s)
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

        static string FloatStr(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) return "0";
            return f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        static string JsonArr(float[] arr)
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

        static string JsonArr(int[] arr)
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

        static string JsonArr(List<int> list)
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

        #region PNG Encoding

        static byte[] EncodePng(byte[] rgbaPixels, int width, int height)
        {
            // Use System.Drawing.Bitmap for reliable PNG encoding from DDSIO pixel data.
            // DDSIO.GetPixels always returns BGRA byte order:
            //   - Compressed formats (DXT1/3/5, BC4/5): decompressor outputs RGBA, then
            //     swaprb swaps R↔B producing BGRA
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
