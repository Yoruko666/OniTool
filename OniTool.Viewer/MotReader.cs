using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OniTool.Core
{
    public struct Vec3 { public float X, Y, Z; public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; } }
    public struct Quat { public float X, Y, Z, W; public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; } }

    public sealed class BoneTrack
    {
        public string BoneName = "";
        public int BoneIndex;
        public List<float> TransTimes = new();
        public List<Vec3> Trans = new();
        public List<float> RotTimes = new();
        public List<Quat> Rot = new();
        public List<float> ScaleTimes = new();
        public List<Vec3> Scale = new();
    }

    public sealed class MotClip
    {
        public string Name = "";
        public float FrameCount;
        public float FrameRate;
        public List<BoneTrack> Tracks = new();
    }

    public sealed class MotList
    {
        public List<MotClip> Clips = new();
        public List<string> UnknownComps = new();
    }

    public static class MotReader
    {
        private const int TRANSLATION = 1, ROTATION = 2, SCALE = 4;

        public static MotList Read(string path)
        {
            var d = File.ReadAllBytes(path);
            var ml = new MotList();
            _sharedBoneNames = null;

            uint mlVer = U32(d, 0);
            if (Enc(d, 4, 4) != "mlst")
                throw new Exception("Not a motlist file (bad magic).");
            ulong pointersOffs = U64(d, 16);

            var motOffsets = new List<long>();
            var seen = new HashSet<long>();
            long o = (long)pointersOffs;
            while (o + 8 <= d.Length)
            {
                long a = (long)U64(d, (int)o);
                if (a == 0) break;
                if (a > 0 && a + 8 <= d.Length && Enc(d, (int)a + 4, 4) == "mot " && seen.Add(a))
                    motOffsets.Add(a);
                o += 8;
            }

            foreach (var mo in motOffsets)
                ml.Clips.Add(ReadMot(d, mo, ml));

            return ml;
        }

        private static Dictionary<int, string>? _sharedBoneNames;

        private static MotClip ReadMot(byte[] d, long mo, MotList ml)
        {
            var clip = new MotClip();
            ulong offsToBoneHdrOffs = U64(d, (int)mo + 16);
            ulong boneClipHdrOffs = U64(d, (int)mo + 24);
            ulong namesOffs = U64(d, (int)mo + 88);
            clip.FrameCount = F32(d, (int)mo + 96);
            ushort boneClipCount = U16(d, (int)mo + 114);
            ushort frameRate = U16(d, (int)mo + 118);
            clip.FrameRate = frameRate > 0 ? frameRate : 60f;
            clip.Name = namesOffs != 0 ? WStr(d, (int)(mo + (long)namesOffs)) : "";

            // bone headers (for name lookup). Some mots share the motlist skeleton
            // and carry no valid table of their own; reuse the last valid map.
            var boneNames = new Dictionary<int, string>();
            if (offsToBoneHdrOffs != 0)
            {
                long bh = mo + (long)offsToBoneHdrOffs;
                ulong boneHdrOffs = U64(d, (int)bh);
                ulong boneHdrCount = U64(d, (int)bh + 8);
                long baseOff = mo + (long)boneHdrOffs;
                bool valid = boneHdrCount > 0 && boneHdrCount < 100000
                             && baseOff >= 0 && baseOff + (long)boneHdrCount * 80 <= d.Length;
                if (valid)
                {
                    for (int i = 0; i < (int)boneHdrCount; i++)
                    {
                        long bo = baseOff + i * 80;
                        ulong nameOff = U64(d, (int)bo);
                        uint idx = U32(d, (int)bo + 64);
                        long np = mo + (long)nameOff;
                        if (np >= 0 && np < d.Length) boneNames[(int)idx] = WStr(d, (int)np);
                    }
                }
            }
            if (boneNames.Count == 0 && _sharedBoneNames != null) boneNames = _sharedBoneNames;
            else if (boneNames.Count > 0) _sharedBoneNames = boneNames;

            long bc = mo + (long)boneClipHdrOffs;
            for (int i = 0; i < boneClipCount; i++)
            {
                long co = bc + i * 12;
                ushort boneIndex = U16(d, (int)co);
                ushort trackFlags = U16(d, (int)co + 2);
                uint trackHdrOffs = U32(d, (int)co + 8);

                var tr = new BoneTrack { BoneIndex = boneIndex };
                tr.BoneName = boneNames.TryGetValue(boneIndex, out var nm) ? nm : $"bone_{boneIndex}";

                long toff = mo + trackHdrOffs;
                if ((trackFlags & TRANSLATION) != 0)
                {
                    ReadTrack(d, mo, toff, false, tr.TransTimes, tr.Trans, null, clip, ml);
                    toff += 20;
                }
                if ((trackFlags & ROTATION) != 0)
                {
                    ReadTrack(d, mo, toff, true, tr.RotTimes, null, tr.Rot, clip, ml);
                    toff += 20;
                }
                if ((trackFlags & SCALE) != 0)
                {
                    ReadTrack(d, mo, toff, false, tr.ScaleTimes, tr.Scale, null, clip, ml);
                    toff += 20;
                }
                clip.Tracks.Add(tr);
            }
            return clip;
        }

        private static void ReadTrack(byte[] d, long mo, long toff, bool isRot,
            List<float> times, List<Vec3>? vecs, List<Quat>? quats, MotClip clip, MotList ml)
        {
            uint flags = U32(d, (int)toff);
            uint keyCount = U32(d, (int)toff + 4);
            uint frameIndOffs = U32(d, (int)toff + 8);
            uint frameDataOffs = U32(d, (int)toff + 12);
            uint unpackDataOffs = U32(d, (int)toff + 16);
            uint comp = flags & 0xFF000;
            uint idxType = flags >> 20;

            // frame index (times, in frames)
            var frames = new float[keyCount];
            if (frameIndOffs > 0)
            {
                long fo = mo + frameIndOffs;
                for (int k = 0; k < keyCount; k++)
                    frames[k] = idxType switch
                    {
                        2 => d[fo + k],
                        4 => (float)BitConverter.ToInt16(d, (int)fo + k * 2),
                        5 => (float)BitConverter.ToInt32(d, (int)fo + k * 4),
                        _ => k,
                    };
            }
            else for (int k = 0; k < keyCount; k++) frames[k] = k;

            // unpack data
            float[] u = new float[8];
            if (unpackDataOffs > 0)
                for (int t = 0; t < 8; t++) u[t] = F32(d, (int)(mo + unpackDataOffs) + t * 4);

            int elemSize = ElemSize(comp, isRot);
            if (elemSize < 0)
            {
                string tag = $"{(isRot ? "R" : "T")} comp={comp:X}";
                if (!ml.UnknownComps.Contains(tag)) ml.UnknownComps.Add(tag);
                return; // fallback: leave bone at bind pose
            }

            long fd = mo + frameDataOffs;
            for (int k = 0; k < keyCount; k++)
            {
                long p = fd + (long)k * elemSize;
                times.Add(frames[k] / clip.FrameRate);
                if (isRot) quats!.Add(DecodeRot(d, (int)p, comp, u));
                else vecs!.Add(DecodeVec(d, (int)p, comp, u));
            }
        }

        private static int ElemSize(uint comp, bool isRot)
        {
            switch (comp)
            {
                case 0x00000: return isRot ? 16 : 12;      // Full
                case 0xB0000:
                case 0xC0000: return isRot ? 12 : -1;      // 3Component (rot only)
                case 0x20000:
                case 0x30000: return 2;                    // 5Bit(T) / 10Bit-fallthrough(R uses 0x40000)->but 0x30000 R falls to 10bit(4). handle below
                case 0x40000: return 4;                    // 10Bit
                case 0x47000: return 4;                    // modern T 10Bit 3-comp
                case 0x57000: return 5;                    // modern T 13Bit 3-comp
                case 0x50000: return 6;                    // R: falls to 16Bit(6)
                case 0x60000: return 6;                    // 16Bit (R 3xu16 / T 3-comp)
                case 0x70000:
                case 0x80000: return 8;                    // 21Bit
                case 0x21000:
                case 0x22000:
                case 0x23000:
                case 0x24000: return 2;                    // single/xyz axis 16bit
                case 0x31000:
                case 0x32000:
                case 0x33000:
                case 0x41000:
                case 0x42000:
                case 0x43000:
                case 0x44000: return 4;                    // single axis float
                default: return -1;
            }
        }

        private static Vec3 DecodeVec(byte[] d, int p, uint comp, float[] u)
        {
            switch (comp)
            {
                case 0x00000:
                    return new Vec3(F32(d, p), F32(d, p + 4), F32(d, p + 8));
                case 0x20000:
                case 0x30000:
                {
                    ushort v = U16(d, p);
                    return new Vec3(
                        u[0] * (((v >> 0) & 0x1F) / 31f) + u[3],
                        u[1] * (((v >> 5) & 0x1F) / 31f) + u[4],
                        u[2] * (((v >> 10) & 0x1F) / 31f) + u[5]);
                }
                case 0x40000:
                {
                    uint v = U32(d, p);
                    return new Vec3(
                        u[0] * (((v >> 0) & 0x3FF) / 1023f) + u[3],
                        u[1] * (((v >> 10) & 0x3FF) / 1023f) + u[4],
                        u[2] * (((v >> 20) & 0x3FF) / 1023f) + u[5]);
                }
                case 0x80000:
                {
                    ulong v = U64(d, p);
                    return new Vec3(
                        u[0] * (((v >> 0) & 0x1FFFFF) / 2097151f) + u[3],
                        u[1] * (((v >> 21) & 0x1FFFFF) / 2097151f) + u[4],
                        u[2] * (((v >> 42) & 0x1FFFFF) / 2097151f) + u[5]);
                }
                case 0x47000:
                {
                    uint v = U32(d, p);
                    return new Vec3(
                        u[0] * (((v >> 0) & 0x3FF) / 1023f) + u[2],
                        u[1] * (((v >> 10) & 0x3FF) / 1023f) + u[3],
                        u[5] * (((v >> 20) & 0x3FF) / 1023f) + u[4]);
                }
                case 0x57000:
                {
                    ulong v = 0;
                    for (int t = 0; t < 5; t++) v |= (ulong)d[p + t] << (8 * t);
                    return new Vec3(
                        u[0] * (((v >> 0) & 0x1FFF) / 8191f) + u[2],
                        u[1] * (((v >> 13) & 0x1FFF) / 8191f) + u[3],
                        u[5] * (((v >> 26) & 0x1FFF) / 8191f) + u[4]);
                }
                case 0x60000:
                {
                    ushort dx = U16(d, p), dy = U16(d, p + 2), dz = U16(d, p + 4);
                    return new Vec3(
                        u[0] * (dx / 65535f) + u[2],
                        u[1] * (dy / 65535f) + u[3],
                        u[5] * (dz / 65535f) + u[4]);
                }
                case 0x21000: { ushort v = U16(d, p); return new Vec3(u[0] * (v / 65535f) + u[1], u[2], u[3]); }
                case 0x22000: { ushort v = U16(d, p); return new Vec3(u[1], u[0] * (v / 65535f) + u[2], u[3]); }
                case 0x23000: { ushort v = U16(d, p); return new Vec3(u[1], u[2], u[0] * (v / 65535f) + u[3]); }
                case 0x24000: { ushort v = U16(d, p); float s = u[0] * (v / 65535f) + u[3]; return new Vec3(s, s, s); }
                case 0x31000:
                case 0x41000: return new Vec3(F32(d, p), u[1], u[2]);
                case 0x32000:
                case 0x42000: return new Vec3(u[0], F32(d, p), u[2]);
                case 0x33000:
                case 0x43000: return new Vec3(u[0], u[1], F32(d, p));
                case 0x44000: { float s = F32(d, p); return new Vec3(s, s, s); }
                default: return new Vec3(0, 0, 0);
            }
        }

        private static Quat DecodeRot(byte[] d, int p, uint comp, float[] u)
        {
            float x = 0, y = 0, z = 0, w;
            switch (comp)
            {
                case 0x00000:
                    return new Quat(F32(d, p), F32(d, p + 4), F32(d, p + 8), F32(d, p + 12));
                case 0xB0000:
                case 0xC0000:
                    x = F32(d, p); y = F32(d, p + 4); z = F32(d, p + 8); break;
                case 0x20000:
                {
                    ushort v = U16(d, p);
                    x = u[0] * (((v >> 0) & 0x1F) / 31f) + u[4];
                    y = u[1] * (((v >> 5) & 0x1F) / 31f) + u[5];
                    z = u[2] * (((v >> 10) & 0x1F) / 31f) + u[6];
                    break;
                }
                case 0x30000:
                case 0x40000:
                {
                    uint v = U32(d, p);
                    x = u[0] * (((v >> 0) & 0x3FF) / 1023f) + u[4];
                    y = u[1] * (((v >> 10) & 0x3FF) / 1023f) + u[5];
                    z = u[2] * (((v >> 20) & 0x3FF) / 1023f) + u[6];
                    break;
                }
                case 0x50000:
                case 0x60000:
                {
                    ushort dx = U16(d, p), dy = U16(d, p + 2), dz = U16(d, p + 4);
                    x = u[0] * (dx / 65535f) + u[4];
                    y = u[1] * (dy / 65535f) + u[5];
                    z = u[2] * (dz / 65535f) + u[6];
                    break;
                }
                case 0x70000:
                case 0x80000:
                {
                    ulong v = U64(d, p);
                    x = u[0] * (((v >> 0) & 0x1FFFFF) / 2097151f) + u[4];
                    y = u[1] * (((v >> 21) & 0x1FFFFF) / 2097151f) + u[5];
                    z = u[2] * (((v >> 42) & 0x1FFFFF) / 2097151f) + u[6];
                    break;
                }
                case 0x21000: { ushort v = U16(d, p); x = u[1] * (v / 65535f) + u[0]; break; }
                case 0x22000: { ushort v = U16(d, p); y = u[1] * (v / 65535f) + u[0]; break; }
                case 0x23000: { ushort v = U16(d, p); z = u[1] * (v / 65535f) + u[0]; break; }
                case 0x31000:
                case 0x41000: x = F32(d, p); break;
                case 0x32000:
                case 0x42000: y = F32(d, p); break;
                case 0x33000:
                case 0x43000: z = F32(d, p); break;
                default: return new Quat(0, 0, 0, 1);
            }
            float ww = 1f - (x * x + y * y + z * z);
            w = ww > 0f ? (float)Math.Sqrt(ww) : 0f;
            return new Quat(x, y, z, w);
        }

        private static ushort U16(byte[] d, int o) => BitConverter.ToUInt16(d, o);
        private static uint U32(byte[] d, int o) => BitConverter.ToUInt32(d, o);
        private static ulong U64(byte[] d, int o) => BitConverter.ToUInt64(d, o);
        private static float F32(byte[] d, int o) => BitConverter.ToSingle(d, o);
        private static string Enc(byte[] d, int o, int n) => Encoding.ASCII.GetString(d, o, n);
        private static string WStr(byte[] d, int o)
        {
            var sb = new StringBuilder();
            while (o + 1 < d.Length)
            {
                ushort c = BitConverter.ToUInt16(d, o); o += 2;
                if (c == 0) break;
                sb.Append((char)c);
            }
            return sb.ToString();
        }
    }
}
