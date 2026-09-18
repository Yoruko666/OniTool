using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OniTool.Core
{
    public sealed class MdfTexture
    {
        public string Type = "";
        public string Path = "";
    }

    public sealed class MdfMaterial
    {
        public string Name = "";
        public List<MdfTexture> Textures = new();
    }

    // Parses RE Engine .mdf2 material files. Layout based on
    // alphazolam/RE-Engine-010-Templates (RE_Engine_MDF.bt), modern (v2) branch.
    public static class MdfReader
    {
        public static List<MdfMaterial> Read(string path)
        {
            var data = File.ReadAllBytes(path);
            var result = new List<MdfMaterial>();

            if (data.Length < 16 || data[0] != (byte)'M' || data[1] != (byte)'D' || data[2] != (byte)'F' || data[3] != 0)
                return result;

            int version = BitConverter.ToUInt16(data, 4);
            int mtrlCount = BitConverter.ToUInt16(data, 6);
            bool isV2 = version >= 30 && version < 100;
            bool hasGpbf = version >= 19 && version < 100000;

            int p = 16; // after id(4)+version(2)+count(2)+reserved(8)
            for (int m = 0; m < mtrlCount; m++)
            {
                if (p + 8 > data.Length) break;
                var mat = new MdfMaterial();

                ulong mtrlNameOffs = BitConverter.ToUInt64(data, p); p += 8;
                p += 4; // name hash
                p += 4; // sizeOfFloatStr
                p += 4; // propCount
                int texCount = (int)BitConverter.ToUInt32(data, p); p += 4;
                if (hasGpbf) p += 8; // gpbf[0], gpbf[1]
                p += 4; // shadingType
                if (isV2) p += 4; // unkInt_0x24
                p += 4; // alpha flags (ALPHAFLAG = 4 bytes)
                if (isV2) p += 8; // unkInt_0x2C, unkInt_0x30
                p += 8; // propHdrsOffs
                ulong texHdrOffs = BitConverter.ToUInt64(data, p); p += 8;
                if (hasGpbf) p += 8; // gpbfOffs
                p += 8; // propsOffs
                p += 8; // mmtrPathOffs
                if (isV2) p += 8; // uknStructsOffs

                mat.Name = ReadWStr(data, (long)mtrlNameOffs);

                long tp = (long)texHdrOffs;
                for (int t = 0; t < texCount; t++)
                {
                    if (tp + 32 > data.Length) break;
                    ulong typeOffs = BitConverter.ToUInt64(data, (int)tp);
                    ulong pathOffs = BitConverter.ToUInt64(data, (int)tp + 16);
                    tp += 32; // typeOffs(8)+utf16hash(4)+asciihash(4)+pathOffs(8)+ukn(8)

                    mat.Textures.Add(new MdfTexture
                    {
                        Type = ReadWStr(data, (long)typeOffs),
                        Path = ReadWStr(data, (long)pathOffs),
                    });
                }

                result.Add(mat);
            }

            return result;
        }

        static string ReadWStr(byte[] data, long offset)
        {
            if (offset <= 0 || offset >= data.Length) return "";
            var sb = new StringBuilder();
            long i = offset;
            while (i + 1 < data.Length)
            {
                ushort c = BitConverter.ToUInt16(data, (int)i);
                if (c == 0) break;
                sb.Append((char)c);
                i += 2;
            }
            return sb.ToString();
        }
    }
}
