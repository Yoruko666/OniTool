using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OniTool.Core
{
    // Parsed output model -------------------------------------------------
    public sealed class ParsedBone
    {
        public string Name = "bone";
        public int Index;
        public int Parent = -1;
        public float[] Local = new float[16];
        public float[] World = new float[16];
        public float[] Inverse = new float[16];
    }

    public sealed class ParsedSkeleton
    {
        public List<ParsedBone> Bones = new();
        public List<string> WeightedBones = new();
    }

    public sealed class ParsedSubMesh
    {
        public int MaterialIndex;
        public int VertexCount;
        public float[] Positions = Array.Empty<float>();   // n*3
        public float[] Normals = Array.Empty<float>();     // n*3
        public float[] UV = Array.Empty<float>();          // n*2
        public byte[] BoneIndices = Array.Empty<byte>();   // n*8
        public float[] Weights = Array.Empty<float>();     // n*8
        public int[] Faces = Array.Empty<int>();           // local indices
    }

    public sealed class ParsedMesh
    {
        public long MeshVersion;
        public List<string> MaterialNames = new();
        public ParsedSkeleton? Skeleton;
        public List<List<ParsedSubMesh>> Lods = new(); // per-LOD list of submeshes
    }

    // Binary reader (BinaryReader is little-endian on .NET) --------------
    internal sealed class Reader : IDisposable
    {
        private readonly BinaryReader _r;
        public Reader(Stream s) { _r = new BinaryReader(s); }
        public long Pos { get => _r.BaseStream.Position; set => _r.BaseStream.Position = value; }
        public long Length => _r.BaseStream.Length;
        public void Seek(long p) => _r.BaseStream.Position = p;
        public byte U8() => _r.ReadByte();
        public sbyte S8() => _r.ReadSByte();
        public ushort U16() => _r.ReadUInt16();
        public short S16() => _r.ReadInt16();
        public uint U32() => _r.ReadUInt32();
        public int S32() => _r.ReadInt32();
        public ulong U64() => _r.ReadUInt64();
        public long S64() => _r.ReadInt64();
        public float F32() => _r.ReadSingle();
        public byte[] Bytes(int n) => _r.ReadBytes(n);
        public float[] Mat4()
        {
            var m = new float[16];
            for (int i = 0; i < 16; i++) m[i] = _r.ReadSingle();
            return m;
        }
        public string CString()
        {
            var sb = new List<byte>();
            byte b;
            while ((b = _r.ReadByte()) != 0) sb.Add(b);
            return Encoding.UTF8.GetString(sb.ToArray());
        }
        public void Dispose() => _r.Dispose();
    }

    // Reader for the newest RE Engine mesh path (SF6/ONI2/RE9 layout) ----
    public sealed class MeshReader
    {
        private static long PadTo(long pos, int align)
        {
            long rem = pos % align;
            return rem == 0 ? pos : pos + (align - rem);
        }

        private struct MatSub
        {
            public byte MaterialIndex, VertexBufferIndex;
            public uint FaceCount, FaceStartIndex, VertexStartIndex;
        }
        private struct MeshGroupData
        {
            public uint VertexCount, FaceCount;
            public List<MatSub> Subs;
        }
        private struct VElem { public ushort Typing; public ushort Stride; public uint PosStart; }

        public static ParsedMesh Read(string path)
        {
            long meshVersion = 0;
            var ext = Path.GetExtension(path).TrimStart('.');
            long.TryParse(ext, out meshVersion);

            using var fs = File.OpenRead(path);
            var r = new Reader(fs);

            uint magic = r.U32();
            if (magic != 1213416781)
                throw new Exception("Not an RE mesh file (bad magic).");
            uint internalVersion = r.U32();
            r.U32(); // fileSize
            r.U32(); // lodGroupNameHash

            // ---- FileHeader (ONI2/RE9 branch) ----
            r.U32();               // wilds_unkn1
            short nameCount = r.S16();
            ushort contentFlag = r.U16();
            r.S16();               // sf6UnknCount
            r.U32(); r.U32(); r.U32(); // wilds_unkn2..4
            r.S16();               // wilds_unkn5
            ulong verticesOffset = r.U64();
            ulong meshGroupOffset = r.U64();
            ulong shadowMeshGroupOffset = r.U64();
            ulong occlusionMeshGroupOffset = r.U64();
            ulong noet = r.U64();
            ulong blendShapesOffset = r.U64();
            ulong meshOffset = r.U64();
            r.U64();               // sf6unkn1
            ulong floatsOffset = r.U64();
            ulong aabbOffset = r.U64();
            ulong skeletonOffset = r.U64();
            ulong materialNameRemapOffset = r.U64();
            ulong boneNameRemapOffset = r.U64();
            ulong blendShapeNameOffset = r.U64();
            ulong nameOffsetsOffset = r.U64();
            ulong streamingInfoOffset = r.U64();
            r.U64();               // sf6unkn4

            // ---- Main mesh header + LODs ----
            int materialCount = 0;
            bool has32Bit = false;
            var lodGroups = new List<List<MeshGroupData>>(); // per LOD -> mesh groups
            if (meshGroupOffset != 0)
            {
                r.Seek((long)meshGroupOffset);
                int lodGroupCount = r.U8();
                materialCount = r.U8();
                r.U8();                    // uvCount
                r.U8();                    // skinWeightCount
                r.U16();                   // totalMeshCount
                has32Bit = r.U8() != 0;
                r.U8();                    // sharedLodBits
                // sphere (4f) + bbox (8f)
                for (int i = 0; i < 12; i++) r.F32();
                r.U64();                   // offsetOffset
                var lodOffsets = new ulong[lodGroupCount];
                for (int i = 0; i < lodGroupCount; i++) lodOffsets[i] = r.U64();

                foreach (var off in lodOffsets)
                {
                    r.Seek((long)off);
                    lodGroups.Add(ReadLodGroup(r));
                }
            }

            // ---- Skeleton ----
            ParsedSkeletonRaw? skelRaw = null;
            if (skeletonOffset != 0)
            {
                r.Seek((long)skeletonOffset);
                skelRaw = ReadSkeleton(r);
            }

            // ---- Bone bounding boxes (count only, for weighted-bone fixup) ----
            long boneBBoxCount = -1;
            if (aabbOffset != 0)
            {
                r.Seek((long)aabbOffset);
                boneBBoxCount = (long)r.U64();
            }

            // ---- Streaming info ----
            long streamEntryCount = 0;
            if (streamingInfoOffset != 0)
            {
                r.Seek((long)streamingInfoOffset);
                streamEntryCount = r.U32();
            }
            if (streamEntryCount != 0)
                throw new Exception("This mesh uses a separate streaming buffer (character mesh). Not supported yet — try item/weapon meshes.");

            // ---- Mesh buffer ----
            byte[] vertexBuffer = Array.Empty<byte>();
            byte[] faceBuffer = Array.Empty<byte>();
            var vElems = new List<VElem>();
            if (meshOffset != 0)
            {
                r.Seek((long)meshOffset);
                ReadMeshBuffer(r, internalVersion, out vertexBuffer, out faceBuffer, out vElems);
            }

            // ---- Name table ----
            var rawNames = new List<string>();
            if (nameOffsetsOffset != 0)
            {
                r.Seek((long)nameOffsetsOffset);
                var nameOffsets = new ulong[nameCount];
                for (int i = 0; i < nameCount; i++) nameOffsets[i] = r.U64();
                foreach (var o in nameOffsets) { r.Seek((long)o); rawNames.Add(r.CString()); }
            }

            // ---- Material name remap ----
            var materialRemap = new List<int>();
            if (materialNameRemapOffset != 0 && meshGroupOffset != 0)
            {
                r.Seek((long)materialNameRemapOffset);
                for (int i = 0; i < materialCount; i++) materialRemap.Add(r.U16());
            }

            // ---- Bone name remap ----
            var boneNameRemap = new List<int>();
            if (boneNameRemapOffset != 0 && skelRaw != null)
            {
                r.Seek((long)boneNameRemapOffset);
                for (int i = 0; i < skelRaw.BoneCount; i++) boneNameRemap.Add(r.U16());
            }

            // ================= Build ParsedMesh =================
            var pm = new ParsedMesh { MeshVersion = meshVersion };
            foreach (var ri in materialRemap)
                pm.MaterialNames.Add(ri < rawNames.Count ? rawNames[ri] : $"mat_{ri}");

            if (skelRaw != null)
            {
                var skel = new ParsedSkeleton();
                foreach (var remapIndex in skelRaw.BoneRemapList)
                {
                    int nameIdx = boneNameRemap[remapIndex];
                    skel.WeightedBones.Add(rawNames[nameIdx]);
                }
                if (boneBBoxCount >= 0 && skelRaw.RemapCount != boneBBoxCount && boneNameRemap.Count > 0)
                    skel.WeightedBones.Add(rawNames[boneNameRemap[0]]);

                for (int i = 0; i < skelRaw.BoneCount; i++)
                {
                    var b = new ParsedBone
                    {
                        Index = i,
                        Name = rawNames[boneNameRemap[i]],
                        Parent = skelRaw.Parents[i],
                        Local = skelRaw.Local[i],
                        World = skelRaw.World[i],
                        Inverse = skelRaw.Inverse[i],
                    };
                    skel.Bones.Add(b);
                }
                pm.Skeleton = skel;
            }

            // Decode vertex element buffers
            var decoded = DecodeVertexBuffers(vElems, vertexBuffer);

            // Slice submeshes per LOD
            foreach (var meshGroups in lodGroups)
            {
                var lodSubs = new List<ParsedSubMesh>();
                foreach (var mg in meshGroups)
                {
                    int last = mg.Subs.Count - 1;
                    for (int idx = 0; idx < mg.Subs.Count; idx++)
                    {
                        var sub = mg.Subs[idx];
                        long start = sub.VertexStartIndex;
                        long end = (idx == last)
                            ? mg.Subs[0].VertexStartIndex + mg.VertexCount
                            : mg.Subs[idx + 1].VertexStartIndex;
                        int vcount = (int)(end - start);

                        var psm = new ParsedSubMesh
                        {
                            MaterialIndex = sub.MaterialIndex,
                            VertexCount = vcount,
                        };
                        psm.Positions = Slice3(decoded.Positions, (int)start, vcount);
                        if (decoded.Normals.Length != 0) psm.Normals = Slice3(decoded.Normals, (int)start, vcount);
                        if (decoded.UV.Length != 0) psm.UV = Slice2(decoded.UV, (int)start, vcount);
                        if (decoded.BoneIndices.Length != 0)
                        {
                            psm.BoneIndices = SliceN(decoded.BoneIndices, (int)start, vcount, 8);
                            psm.Weights = SliceNf(decoded.Weights, (int)start, vcount, 8);
                        }
                        // faces (uint16 local, or uint32)
                        int fcount = (int)sub.FaceCount;
                        var faces = new int[fcount];
                        if (has32Bit)
                        {
                            int baseB = (int)sub.FaceStartIndex * 4;
                            for (int f = 0; f < fcount; f++)
                                faces[f] = BitConverter.ToInt32(faceBuffer, baseB + f * 4);
                        }
                        else
                        {
                            int baseB = (int)sub.FaceStartIndex * 2;
                            for (int f = 0; f < fcount; f++)
                                faces[f] = BitConverter.ToUInt16(faceBuffer, baseB + f * 2);
                        }
                        psm.Faces = faces;
                        lodSubs.Add(psm);
                    }
                }
                pm.Lods.Add(lodSubs);
            }

            return pm;
        }

        private static List<MeshGroupData> ReadLodGroup(Reader r)
        {
            int count = r.U8();
            r.U8();                 // vertexFormat
            r.U16();                // reserved
            r.F32();                // distance
            r.U64();                // offsetOffset
            var mgOffsets = new ulong[count];
            for (int i = 0; i < count; i++) mgOffsets[i] = r.U64();
            r.Seek(PadTo(r.Pos, 16));
            var groups = new List<MeshGroupData>();
            for (int i = 0; i < count; i++)
                groups.Add(ReadMeshGroup(r));
            return groups;
        }

        private static MeshGroupData ReadMeshGroup(Reader r)
        {
            r.U8();                 // visconGroupID
            int meshCount = r.U8();
            r.U16(); r.U16(); r.U16(); // null0..2
            uint vertexCount = r.U32();
            uint faceCount = r.U32();
            var subs = new List<MatSub>(meshCount);
            for (int i = 0; i < meshCount; i++)
            {
                var m = new MatSub();
                m.MaterialIndex = r.U8();
                r.U8();             // isQuad
                m.VertexBufferIndex = r.U8();
                r.U8();             // padding
                r.U32();            // dr_unkn0 (>=DR)
                m.FaceCount = r.U32();
                m.FaceStartIndex = r.U32();
                m.VertexStartIndex = r.U32();
                r.U32();            // streamingOffsetBytes (>=RE8)
                r.U32();            // streamingPlatformSpecificOffsetBytes (>=RE8)
                r.U32();            // dr_unkn1 (>=DD2NEW)
                subs.Add(m);
            }
            return new MeshGroupData { VertexCount = vertexCount, FaceCount = faceCount, Subs = subs };
        }

        private sealed class ParsedSkeletonRaw
        {
            public int BoneCount, RemapCount;
            public List<int> BoneRemapList = new();
            public int[] Parents = Array.Empty<int>();
            public float[][] Local = Array.Empty<float[]>();
            public float[][] World = Array.Empty<float[]>();
            public float[][] Inverse = Array.Empty<float[]>();
        }

        private static ParsedSkeletonRaw ReadSkeleton(Reader r)
        {
            var s = new ParsedSkeletonRaw();
            s.BoneCount = (int)r.U32();
            s.RemapCount = (int)r.U32();
            r.U64();                // NULL
            r.U64(); r.U64(); r.U64(); r.U64(); // 4 offsets
            for (int i = 0; i < s.RemapCount; i++) s.BoneRemapList.Add(r.U16());
            r.Seek(PadTo(r.Pos, 16));
            s.Parents = new int[s.BoneCount];
            for (int i = 0; i < s.BoneCount; i++)
            {
                r.U16();            // boneIndex
                short parent = r.S16();
                r.S16(); r.S16(); r.S16(); r.S16(); r.S16(); r.S16(); // sibling,child,sym,useSecondary,pad,pad
                s.Parents[i] = parent;
            }
            r.Seek(PadTo(r.Pos, 16));
            s.Local = new float[s.BoneCount][];
            for (int i = 0; i < s.BoneCount; i++) s.Local[i] = r.Mat4();
            s.World = new float[s.BoneCount][];
            for (int i = 0; i < s.BoneCount; i++) s.World[i] = r.Mat4();
            s.Inverse = new float[s.BoneCount][];
            for (int i = 0; i < s.BoneCount; i++) s.Inverse[i] = r.Mat4();
            return s;
        }

        private const long VERSION_PRAGDEMO_INTERNAL = 250707828;

        private static void ReadMeshBuffer(Reader r, uint internalVersion,
            out byte[] vertexBuffer, out byte[] faceBuffer, out List<VElem> vElems)
        {
            ulong vertexElementOffset = r.U64();
            ulong vertexBufferOffset = r.U64();
            r.U64();                            // sunbreakOffset
            r.U32();                            // totalBufferSize
            uint vertexBufferSize = r.U32();
            ulong faceBufferOffset = vertexBufferOffset + vertexBufferSize;
            r.U16();                            // mainVertexElementCount
            ushort vertexElementCount = r.U16();
            // prag branch: RE9/ONI2 internal >= pragmata demo internal
            r.U64(); r.U64();                   // prag_unknOffset0/1 (>= PRAGDEMO)
            uint block2FaceBufferOffset = r.U32();
            uint faceBufferSize = block2FaceBufferOffset - vertexBufferSize;
            r.U32();                            // NULL
            r.S16();                            // vertexElementSize
            r.S16();                            // unkn1
            r.U64(); r.U64(); r.U64(); r.U64(); // sunbreakSecondUnknown, sf6unkn0, streamingVertexElementOffset, sf6unkn2

            vElems = new List<VElem>();
            r.Seek((long)vertexElementOffset);
            for (int i = 0; i < vertexElementCount; i++)
            {
                vElems.Add(new VElem { Typing = r.U16(), Stride = r.U16(), PosStart = r.U32() });
            }
            r.Seek((long)vertexBufferOffset);
            vertexBuffer = r.Bytes((int)vertexBufferSize);
            r.Seek((long)faceBufferOffset);
            faceBuffer = r.Bytes((int)faceBufferSize);
        }

        private sealed class DecodedBuffers
        {
            public float[] Positions = Array.Empty<float>();
            public float[] Normals = Array.Empty<float>();
            public float[] UV = Array.Empty<float>();
            public byte[] BoneIndices = Array.Empty<byte>();
            public float[] Weights = Array.Empty<float>();
        }

        private static DecodedBuffers DecodeVertexBuffers(List<VElem> elems, byte[] buf)
        {
            var d = new DecodedBuffers();
            // vertex count from Position element (typing 0)
            int posElem = elems.FindIndex(e => e.Typing == 0);
            if (posElem < 0) return d;
            long posEnd = (posElem < elems.Count - 1) ? elems[posElem + 1].PosStart : buf.Length;
            int vcount = (int)((posEnd - elems[posElem].PosStart) / 12);

            for (int i = 0; i < elems.Count; i++)
            {
                var e = elems[i];
                long end = (i < elems.Count - 1)
                    ? elems[i + 1].PosStart
                    : e.PosStart + (long)e.Stride * vcount;
                int start = (int)e.PosStart;
                int len = (int)(end - start);
                switch (e.Typing)
                {
                    case 0: // Position vec3f
                        d.Positions = new float[vcount * 3];
                        Buffer.BlockCopy(buf, start, d.Positions, 0, vcount * 12);
                        break;
                    case 1: // NorTan: 4 sbytes/127, interleaved normal,tangent (stride 8)
                        d.Normals = new float[vcount * 3];
                        for (int v = 0; v < vcount; v++)
                        {
                            int o = start + v * 8; // normal is first of the pair
                            d.Normals[v * 3 + 0] = (sbyte)buf[o + 0] / 127f;
                            d.Normals[v * 3 + 1] = (sbyte)buf[o + 1] / 127f;
                            d.Normals[v * 3 + 2] = (sbyte)buf[o + 2] / 127f;
                        }
                        break;
                    case 2: // UV half-float, v = 1 - v
                        d.UV = new float[vcount * 2];
                        for (int v = 0; v < vcount; v++)
                        {
                            int o = start + v * 4;
                            float u = (float)BitConverter.ToHalf(buf, o);
                            float vv = (float)BitConverter.ToHalf(buf, o + 2);
                            d.UV[v * 2 + 0] = u;
                            d.UV[v * 2 + 1] = 1f - vv;
                        }
                        break;
                    case 4: // Weight: 16 bytes = 8 indices + 8 weights/255
                        d.BoneIndices = new byte[vcount * 8];
                        d.Weights = new float[vcount * 8];
                        for (int v = 0; v < vcount; v++)
                        {
                            int o = start + v * 16;
                            for (int k = 0; k < 8; k++)
                            {
                                d.BoneIndices[v * 8 + k] = buf[o + k];
                                d.Weights[v * 8 + k] = buf[o + 8 + k] / 255f;
                            }
                        }
                        break;
                    default:
                        break; // UV2/Color/etc ignored for preview
                }
            }
            return d;
        }

        private static float[] Slice3(float[] src, int startVert, int count)
        {
            var o = new float[count * 3];
            Array.Copy(src, startVert * 3, o, 0, count * 3);
            return o;
        }
        private static float[] Slice2(float[] src, int startVert, int count)
        {
            var o = new float[count * 2];
            Array.Copy(src, startVert * 2, o, 0, count * 2);
            return o;
        }
        private static byte[] SliceN(byte[] src, int startVert, int count, int n)
        {
            var o = new byte[count * n];
            Array.Copy(src, startVert * n, o, 0, count * n);
            return o;
        }
        private static float[] SliceNf(float[] src, int startVert, int count, int n)
        {
            var o = new float[count * n];
            Array.Copy(src, startVert * n, o, 0, count * n);
            return o;
        }
    }
}
