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
                node.rotation = new float[] { r.X, r.Z, -r.Y, r.W };
                node.scale = new float[] { s.X, s.Z, s.Y };

                int nodeIdx = ctx.Nodes.Count;
                ctx.Nodes.Add(node);
                ctx.BoneToNode[i] = nodeIdx;
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

            // Inverse bind matrices
            var ibmFloats = new List<float>();
            for (int i = 0; i < bones.Length; i++)
            {
                var m = ConvertMatrixYUp(bones[i].BindTransformInv);
                ibmFloats.AddRange(m);
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

        static float[] ConvertMatrixYUp(Matrix m)
        {
            return new float[]
            {
                m.M11, -m.M13, m.M12, m.M14,
                -m.M31, m.M33, -m.M32, m.M34,
                m.M21, -m.M23, m.M22, m.M24,
                m.M41, -m.M43, m.M42, m.M44,
            };
        }

        #endregion

        #region Meshes

        static void BuildMeshes(ExportContext ctx, Ped ped)
        {
            string[] compNames = { "Head", "Berd", "Hair", "Uppr", "Lowr", "Hand", "Feet", "Teef", "Accs", "Task", "Decl", "Jbib" };

            for (int compIdx = 0; compIdx < 12; compIdx++)
            {
                var drawable = ped.Drawables[compIdx];
                if (drawable == null) continue;
                var texture = ped.Textures[compIdx];
                var models = drawable.DrawableModels?.High;
                if (models == null) continue;

                var diffuseTex = FindDiffuseTexture(drawable, texture);
                int? materialIdx = null;
                if (diffuseTex != null)
                    materialIdx = ExportTexture(ctx, diffuseTex, compNames[compIdx]);
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
                        var prim = BuildPrimitive(ctx, geom, model, ped.Skeleton);
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

        static GltfMeshPrimitive BuildPrimitive(ExportContext ctx, DrawableGeometry geom, DrawableModel model, Skeleton skeleton)
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
                // GTA V is left-handed Y-up, glTF is right-handed Y-up
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
                    // Flip V for OpenGL/glTF convention (V=0 at bottom)
                    texcoords.Add(uv.X); texcoords.Add(1.0f - uv.Y);
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
                            if (bwo + 4 <= vbytes.Length)
                                bw = new Vector4(
                                    vbytes[bwo] / 255.0f,
                                    vbytes[bwo + 1] / 255.0f,
                                    vbytes[bwo + 2] / 255.0f,
                                    vbytes[bwo + 3] / 255.0f);
                            break;
                        default:
                            // Fallback: try GetVector4
                            try { bw = vdata.GetVector4(v, 1); } catch { }
                            break;
                    }

                    // Normalize blend weights so they sum to 1.0
                    float tw = bw.X + bw.Y + bw.Z + bw.W;
                    if (tw > 0.001f) { bw.X /= tw; bw.Y /= tw; bw.Z /= tw; bw.W /= tw; }
                    else { bw.X = 1f; bw.Y = 0f; bw.Z = 0f; bw.W = 0f; }
                    blendWeights.Add(bw.X); blendWeights.Add(bw.Y); blendWeights.Add(bw.Z); blendWeights.Add(bw.W);

                    // Read blend indices directly from raw bytes to avoid Color struct byte shuffling
                    // These 4 bytes are local bone indices into geom.BoneIds[]
                    byte bi0 = 0, bi1 = 0, bi2 = 0, bi3 = 0;
                    int bio = baseOff + biOffset;
                    if (bio + 4 <= vbytes.Length)
                    {
                        bi0 = vbytes[bio];
                        bi1 = vbytes[bio + 1];
                        bi2 = vbytes[bio + 2];
                        bi3 = vbytes[bio + 3];
                    }
                    // Remap local bone indices to global skeleton bone indices
                    blendJoints.Add(RemapBoneIndex(bi0, geomBoneIds, skeleton));
                    blendJoints.Add(RemapBoneIndex(bi1, geomBoneIds, skeleton));
                    blendJoints.Add(RemapBoneIndex(bi2, geomBoneIds, skeleton));
                    blendJoints.Add(RemapBoneIndex(bi3, geomBoneIds, skeleton));
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

        static ushort RemapBoneIndex(byte localIdx, ushort[] geomBoneIds, Skeleton skeleton)
        {
            // geom.BoneIds[] maps vertex local bone indices to skeleton bone array indices.
            // The values ARE direct skeleton bone indices (not bone tags/hashes).
            // This is confirmed by Renderable.UpdateBoneTransforms which does:
            //   var id = boneids[b]; geom.BoneTransforms[b] = bonetransforms[id];
            if (geomBoneIds == null || localIdx >= geomBoneIds.Length) return 0;
            return geomBoneIds[localIdx];
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

        static int ExportTexture(ExportContext ctx, Texture tex, string compName)
        {
            if (tex == null) return -1;
            if (ctx.TextureToGltfIndex.TryGetValue(tex.NameHash, out int existingIdx))
                return existingIdx;

            int imageIdx;
            byte[] rgbaPixels = null;

            // Try DDSIO pixel extraction first (handles DXT1/3/5, BC4/5, uncompressed)
            try { rgbaPixels = DDSIO.GetPixels(tex, 0); } catch { }

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
            int matIdx = ctx.Materials.Count;
            ctx.Materials.Add(mat);
            return matIdx;
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
            Animation animData = null;
            float duration = 0f;

            if (cme.Clip is ClipAnimation clipAnim)
            {
                animData = clipAnim.Animation;
                duration = clipAnim.EndTime - clipAnim.StartTime;
            }
            else if (cme.Clip is ClipAnimationList clipList && clipList.Animations != null && clipList.Animations.Count > 0)
            {
                animData = clipList.Animations[0].Animation;
                duration = clipList.Animations[0].EndTime - clipList.Animations[0].StartTime;
            }
            if (animData == null || duration <= 0) return;

            if (animData.Duration > 0) duration = animData.Duration;
            int frameCount = Math.Min(animData.Frames > 0 ? animData.Frames : (int)(duration * 30f), 300);
            float frameDelta = duration / (frameCount - 1);

            // Time accessor
            var timeData = new float[frameCount];
            for (int f = 0; f < frameCount; f++) timeData[f] = f * frameDelta;
            byte[] timeBytes = new byte[frameCount * 4];
            Buffer.BlockCopy(timeData, 0, timeBytes, 0, timeBytes.Length);
            int timeBv = ctx.AddBufferView(timeBytes);
            int timeAcc = ctx.AddAccessor(timeBv, 5126, "SCALAR", frameCount, new float[] { 0f }, new float[] { duration });

            var boneIds = animData.BoneIds?.data_items;
            if (boneIds == null) return;

            for (int bi = 0; bi < boneIds.Length; bi++)
            {
                var boneId = boneIds[bi];
                if (!skeleton.BonesMap.TryGetValue(boneId.BoneId, out var bone)) continue;
                if (!ctx.BoneToNode.TryGetValue(bone.Index, out int nodeIdx)) continue;

                if (boneId.Track == 0) // Translation
                {
                    var vals = new List<float>();
                    for (int f = 0; f < frameCount; f++)
                    {
                        Vector3 val = Vector3.Zero;
                        try
                        {
                            var fp = animData.GetFramePosition(f * frameDelta);
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
                            var fp = animData.GetFramePosition(f * frameDelta);
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
                            var fp = animData.GetFramePosition(f * frameDelta);
                            var v4 = animData.EvaluateVector4(fp, bi, true);
                            val = new Vector3(v4.X, v4.Y, v4.Z); // explicit conversion, drop W
                        }
                        catch { }
                        // Scale uses same axis swap as translation
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

            if (anim.channels.Count > 0)
                ctx.Animations.Add(anim);
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
                sb.Append("}}");
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
                    sb.Append(",\"wrapS\":"); s.wrapS.ToString(); sb.Append(s.wrapS);
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
            // Use System.Drawing.Bitmap for reliable PNG encoding from RGBA pixel data.
            // The previous custom PNG encoder produced broken output (pink textures).
            try
            {
                if (rgbaPixels == null || rgbaPixels.Length < width * height * 4) return null;

                using (var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                {
                    var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, width, height),
                        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    // DDSIO.GetPixels returns RGBA bytes, but Format32bppArgb expects BGRA in memory.
                    // We need to swap R and B channels.
                    int pixelCount = width * height;
                    var bgraPixels = new byte[pixelCount * 4];
                    for (int i = 0; i < pixelCount; i++)
                    {
                        bgraPixels[i * 4 + 0] = rgbaPixels[i * 4 + 2]; // B = R
                        bgraPixels[i * 4 + 1] = rgbaPixels[i * 4 + 1]; // G = G
                        bgraPixels[i * 4 + 2] = rgbaPixels[i * 4 + 0]; // R = B
                        bgraPixels[i * 4 + 3] = rgbaPixels[i * 4 + 3]; // A = A
                    }
                    Marshal.Copy(bgraPixels, 0, bd.Scan0, bgraPixels.Length);
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
