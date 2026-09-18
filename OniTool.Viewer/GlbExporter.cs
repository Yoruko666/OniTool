using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace OniTool.Core
{
    public static class GlbExporter
    {
        private const int UBYTE = 5121, UINT = 5125, FLOAT = 5126;

        private sealed class BinBuilder
        {
            public MemoryStream Ms = new();
            public List<Dictionary<string, object>> BufferViews = new();
            public List<Dictionary<string, object>> Accessors = new();

            private void Align4()
            {
                while (Ms.Length % 4 != 0) Ms.WriteByte(0);
            }

            private int AddView(byte[] data, int? target)
            {
                Align4();
                long off = Ms.Length;
                Ms.Write(data, 0, data.Length);
                var v = new Dictionary<string, object>
                {
                    ["buffer"] = 0,
                    ["byteOffset"] = (int)off,
                    ["byteLength"] = data.Length,
                };
                if (target.HasValue) v["target"] = target.Value;
                BufferViews.Add(v);
                return BufferViews.Count - 1;
            }

            public int AddFloat(float[] data, int comps, string type, int count, float[]? min, float[]? max, int? target = 34962)
            {
                var bytes = new byte[data.Length * 4];
                Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
                int bv = AddView(bytes, target);
                var acc = new Dictionary<string, object>
                {
                    ["bufferView"] = bv,
                    ["componentType"] = FLOAT,
                    ["count"] = count,
                    ["type"] = type,
                };
                if (min != null) acc["min"] = min;
                if (max != null) acc["max"] = max;
                Accessors.Add(acc);
                return Accessors.Count - 1;
            }

            public int AddUInt(uint[] data, int count, int? target = 34963)
            {
                var bytes = new byte[data.Length * 4];
                Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
                int bv = AddView(bytes, target);
                Accessors.Add(new Dictionary<string, object>
                {
                    ["bufferView"] = bv,
                    ["componentType"] = UINT,
                    ["count"] = count,
                    ["type"] = "SCALAR",
                });
                return Accessors.Count - 1;
            }

            public int AddUByteVec4(byte[] data, int count)
            {
                int bv = AddView(data, 34962);
                Accessors.Add(new Dictionary<string, object>
                {
                    ["bufferView"] = bv,
                    ["componentType"] = UBYTE,
                    ["count"] = count,
                    ["type"] = "VEC4",
                    ["normalized"] = false,
                });
                return Accessors.Count - 1;
            }

            public int AddMat4(float[] flat, int count)
            {
                var bytes = new byte[flat.Length * 4];
                Buffer.BlockCopy(flat, 0, bytes, 0, bytes.Length);
                int bv = AddView(bytes, null);
                Accessors.Add(new Dictionary<string, object>
                {
                    ["bufferView"] = bv,
                    ["componentType"] = FLOAT,
                    ["count"] = count,
                    ["type"] = "MAT4",
                });
                return Accessors.Count - 1;
            }
        }

        public static void ExportGlb(ParsedMesh mesh, string outPath, int lod = 0, IReadOnlyList<MotClip>? anims = null)
        {
            var b = new BinBuilder();
            var nodes = new List<Dictionary<string, object>>();
            var skel = mesh.Skeleton;

            // bone nodes
            var nameToNode = new Dictionary<string, int>();
            if (skel != null)
            {
                for (int i = 0; i < skel.Bones.Count; i++)
                {
                    var bone = skel.Bones[i];
                    var n = new Dictionary<string, object>
                    {
                        ["name"] = bone.Name,
                        ["matrix"] = bone.Local,
                    };
                    nodes.Add(n);
                    if (!nameToNode.ContainsKey(bone.Name)) nameToNode[bone.Name] = i;
                }
                // children
                for (int i = 0; i < skel.Bones.Count; i++)
                {
                    var children = new List<int>();
                    for (int j = 0; j < skel.Bones.Count; j++)
                        if (skel.Bones[j].Parent == i) children.Add(j);
                    if (children.Count > 0) nodes[i]["children"] = children;
                }
            }

            // skin
            int? skinIndex = null;
            var jointNodeIndices = new List<int>();
            if (skel != null && skel.WeightedBones.Count > 0)
            {
                var ibm = new List<float>();
                foreach (var wb in skel.WeightedBones)
                {
                    int nodeIdx = nameToNode.TryGetValue(wb, out var ni) ? ni : 0;
                    jointNodeIndices.Add(nodeIdx);
                    ibm.AddRange(skel.Bones[nodeIdx].Inverse);
                }
                int ibmAcc = b.AddMat4(ibm.ToArray(), skel.WeightedBones.Count);
                // skin created after we know node count; store placeholder
                skinIndex = 0;
                _pendingSkin = new Dictionary<string, object>
                {
                    ["inverseBindMatrices"] = ibmAcc,
                    ["joints"] = jointNodeIndices,
                };
            }

            // materials
            var materials = new List<Dictionary<string, object>>();
            foreach (var mn in mesh.MaterialNames)
                materials.Add(new Dictionary<string, object>
                {
                    ["name"] = mn,
                    ["doubleSided"] = true,
                    ["pbrMetallicRoughness"] = new Dictionary<string, object>
                    {
                        ["baseColorFactor"] = new float[] { 0.8f, 0.8f, 0.8f, 1f },
                        ["metallicFactor"] = 0f,
                        ["roughnessFactor"] = 0.8f,
                    },
                });
            if (materials.Count == 0)
                materials.Add(new Dictionary<string, object> { ["name"] = "default" });

            // primitives
            var primitives = new List<Dictionary<string, object>>();
            var subs = (lod < mesh.Lods.Count) ? mesh.Lods[lod] : new List<ParsedSubMesh>();
            int jointCount = jointNodeIndices.Count;
            foreach (var s in subs)
            {
                if (s.VertexCount == 0 || s.Faces.Length == 0) continue;
                var attrs = new Dictionary<string, object>();

                // POSITION with min/max
                var min = new float[] { float.MaxValue, float.MaxValue, float.MaxValue };
                var max = new float[] { float.MinValue, float.MinValue, float.MinValue };
                for (int v = 0; v < s.VertexCount; v++)
                    for (int c = 0; c < 3; c++)
                    {
                        float val = s.Positions[v * 3 + c];
                        if (val < min[c]) min[c] = val;
                        if (val > max[c]) max[c] = val;
                    }
                attrs["POSITION"] = b.AddFloat(s.Positions, 3, "VEC3", s.VertexCount, min, max);

                if (s.Normals.Length != 0)
                    attrs["NORMAL"] = b.AddFloat(s.Normals, 3, "VEC3", s.VertexCount, null, null);
                if (s.UV.Length != 0)
                    attrs["TEXCOORD_0"] = b.AddFloat(s.UV, 2, "VEC2", s.VertexCount, null, null);

                if (s.Weights.Length != 0 && skinIndex.HasValue)
                {
                    var joints = new byte[s.VertexCount * 4];
                    var weights = new float[s.VertexCount * 4];
                    Span<int> idx = stackalloc int[8];
                    Span<float> w = stackalloc float[8];
                    for (int v = 0; v < s.VertexCount; v++)
                    {
                        // top-4 of 8
                        for (int k = 0; k < 8; k++)
                        {
                            idx[k] = s.BoneIndices[v * 8 + k];
                            w[k] = s.Weights[v * 8 + k];
                        }
                        // selection sort top 4 by weight
                        for (int a = 0; a < 4; a++)
                        {
                            int best = a;
                            for (int c = a + 1; c < 8; c++) if (w[c] > w[best]) best = c;
                            (w[a], w[best]) = (w[best], w[a]);
                            (idx[a], idx[best]) = (idx[best], idx[a]);
                        }
                        float sum = w[0] + w[1] + w[2] + w[3];
                        if (sum <= 0f) { w[0] = 1f; sum = 1f; }
                        for (int k = 0; k < 4; k++)
                        {
                            int ji = idx[k];
                            if (ji >= jointCount) ji = 0;
                            joints[v * 4 + k] = (byte)ji;
                            weights[v * 4 + k] = w[k] / sum;
                        }
                    }
                    attrs["JOINTS_0"] = b.AddUByteVec4(joints, s.VertexCount);
                    attrs["WEIGHTS_0"] = b.AddFloat(weights, 4, "VEC4", s.VertexCount, null, null);
                }

                var faces = new uint[s.Faces.Length];
                for (int f = 0; f < s.Faces.Length; f++) faces[f] = (uint)s.Faces[f];
                int idxAcc = b.AddUInt(faces, faces.Length);

                var prim = new Dictionary<string, object>
                {
                    ["attributes"] = attrs,
                    ["indices"] = idxAcc,
                    ["mode"] = 4,
                };
                if (s.MaterialIndex < materials.Count) prim["material"] = s.MaterialIndex;
                primitives.Add(prim);
            }

            var meshDict = new Dictionary<string, object>
            {
                ["name"] = "mesh",
                ["primitives"] = primitives,
            };

            // mesh node
            var meshNode = new Dictionary<string, object> { ["name"] = "meshNode", ["mesh"] = 0 };
            if (skinIndex.HasValue) meshNode["skin"] = 0;
            int meshNodeIndex = nodes.Count;
            nodes.Add(meshNode);

            // scene roots: root bones + mesh node
            var sceneNodes = new List<int>();
            if (skel != null)
                for (int i = 0; i < skel.Bones.Count; i++)
                    if (skel.Bones[i].Parent < 0) sceneNodes.Add(i);
            sceneNodes.Add(meshNodeIndex);

            var gltf = new Dictionary<string, object>
            {
                ["asset"] = new Dictionary<string, object> { ["version"] = "2.0", ["generator"] = "OniTool" },
                ["scene"] = 0,
                ["scenes"] = new List<object> { new Dictionary<string, object> { ["nodes"] = sceneNodes } },
                ["nodes"] = nodes,
                ["meshes"] = new List<object> { meshDict },
                ["materials"] = materials,
                ["bufferViews"] = b.BufferViews,
                ["accessors"] = b.Accessors,
                ["buffers"] = new List<object> { new Dictionary<string, object> { ["byteLength"] = (int)b.Ms.Length } },
            };
            if (skinIndex.HasValue && _pendingSkin != null)
                gltf["skins"] = new List<object> { _pendingSkin };

            // ---- animations ----
            if (anims != null && anims.Count > 0 && skel != null)
            {
                var animList = new List<object>();
                foreach (var clip in anims)
                {
                    var samplers = new List<object>();
                    var channels = new List<object>();

                    void AddChannel(int node, string pathName, List<float> times, float[] outData, int comps, string type)
                    {
                        if (times.Count == 0) return;
                        var tarr = times.ToArray();
                        float tmin = float.MaxValue, tmax = float.MinValue;
                        foreach (var t in tarr) { if (t < tmin) tmin = t; if (t > tmax) tmax = t; }
                        int inAcc = b.AddFloat(tarr, 1, "SCALAR", tarr.Length, new[] { tmin }, new[] { tmax }, null);
                        int outAcc = b.AddFloat(outData, comps, type, times.Count, null, null, null);
                        int sIdx = samplers.Count;
                        samplers.Add(new Dictionary<string, object>
                        {
                            ["input"] = inAcc,
                            ["output"] = outAcc,
                            ["interpolation"] = "LINEAR",
                        });
                        channels.Add(new Dictionary<string, object>
                        {
                            ["sampler"] = sIdx,
                            ["target"] = new Dictionary<string, object> { ["node"] = node, ["path"] = pathName },
                        });
                    }

                    foreach (var tr in clip.Tracks)
                    {
                        if (!nameToNode.TryGetValue(tr.BoneName, out int node)) continue;
                        if (tr.Trans.Count > 0)
                        {
                            var o = new float[tr.Trans.Count * 3];
                            for (int i = 0; i < tr.Trans.Count; i++) { o[i * 3] = tr.Trans[i].X; o[i * 3 + 1] = tr.Trans[i].Y; o[i * 3 + 2] = tr.Trans[i].Z; }
                            AddChannel(node, "translation", tr.TransTimes, o, 3, "VEC3");
                        }
                        if (tr.Rot.Count > 0)
                        {
                            var o = new float[tr.Rot.Count * 4];
                            for (int i = 0; i < tr.Rot.Count; i++) { o[i * 4] = tr.Rot[i].X; o[i * 4 + 1] = tr.Rot[i].Y; o[i * 4 + 2] = tr.Rot[i].Z; o[i * 4 + 3] = tr.Rot[i].W; }
                            AddChannel(node, "rotation", tr.RotTimes, o, 4, "VEC4");
                        }
                        if (tr.Scale.Count > 0)
                        {
                            var o = new float[tr.Scale.Count * 3];
                            for (int i = 0; i < tr.Scale.Count; i++) { o[i * 3] = tr.Scale[i].X; o[i * 3 + 1] = tr.Scale[i].Y; o[i * 3 + 2] = tr.Scale[i].Z; }
                            AddChannel(node, "scale", tr.ScaleTimes, o, 3, "VEC3");
                        }
                    }
                    if (channels.Count > 0)
                        animList.Add(new Dictionary<string, object>
                        {
                            ["name"] = string.IsNullOrEmpty(clip.Name) ? $"clip{animList.Count}" : clip.Name,
                            ["samplers"] = samplers,
                            ["channels"] = channels,
                        });
                }
                if (animList.Count > 0) gltf["animations"] = animList;
            }

            WriteGlb(gltf, b.Ms.ToArray(), outPath);
            _pendingSkin = null;
        }

        private static Dictionary<string, object>? _pendingSkin;

        private static void WriteGlb(Dictionary<string, object> gltf, byte[] bin, string outPath)
        {
            var opts = new JsonSerializerOptions { WriteIndented = false };
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(gltf, opts);
            // pad json to 4 with spaces
            int jsonPad = (4 - json.Length % 4) % 4;
            if (jsonPad > 0)
            {
                var jp = new byte[json.Length + jsonPad];
                Array.Copy(json, jp, json.Length);
                for (int i = 0; i < jsonPad; i++) jp[json.Length + i] = 0x20;
                json = jp;
            }
            int binPad = (4 - bin.Length % 4) % 4;
            int binLen = bin.Length + binPad;

            using var fs = File.Create(outPath);
            using var w = new BinaryWriter(fs);
            int total = 12 + 8 + json.Length + 8 + binLen;
            w.Write(0x46546C67u);            // glTF
            w.Write(2u);                     // version
            w.Write((uint)total);
            w.Write((uint)json.Length);
            w.Write(0x4E4F534Au);            // JSON
            w.Write(json);
            w.Write((uint)binLen);
            w.Write(0x004E4942u);            // BIN\0
            w.Write(bin);
            for (int i = 0; i < binPad; i++) w.Write((byte)0);
        }
    }
}
