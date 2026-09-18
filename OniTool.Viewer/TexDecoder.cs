using System;
using System.IO;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace OniTool.Core
{
    public sealed class DecodedTex
    {
        public int Width;
        public int Height;
        public byte[] Rgba = Array.Empty<byte>(); // width*height*4, RGBA order
    }

    // Decodes RE Engine .tex containers to RGBA. Header layout based on
    // alphazolam/RE-Engine-010-Templates (RE_Engine_TEX.bt).
    public static class TexDecoder
    {
        public static DecodedTex Decode(string path)
        {
            var data = File.ReadAllBytes(path);
            return Decode(data);
        }

        public static DecodedTex Decode(byte[] data)
        {
            if (data.Length < 32 || data[0] != (byte)'T' || data[1] != (byte)'E' || data[2] != (byte)'X' || data[3] != 0)
                throw new InvalidDataException("Not a TEX file");

            uint version = BitConverter.ToUInt32(data, 4);
            bool isNew = version != 10 && version != 11 && version != 6 && version != 190820018;

            int width = BitConverter.ToUInt16(data, 8);
            int height = BitConverter.ToUInt16(data, 10);
            // data[12] = slices, data[13] = ukn

            int numImages, numMips;
            if (isNew)
            {
                numImages = data[14];
                int mipHeaderSize = data[15];
                numMips = mipHeaderSize / 16;
            }
            else
            {
                numMips = data[14];
                numImages = data[15];
            }
            if (numMips < 1) numMips = 1;
            if (numImages < 1) numImages = 1;

            uint format = BitConverter.ToUInt32(data, 16);
            // data[28] = streamingTexture flag

            int mipTableOffset = isNew ? 40 : 32;

            var fmt = MapFormat(format, out bool hdr);

            // Walk image-0 mip headers; pick the largest mip whose data is
            // actually present in this file (streaming textures keep top mips
            // in a separate .streaming file, so their offsets fall past EOF).
            for (int mip = 0; mip < numMips; mip++)
            {
                int he = mipTableOffset + mip * 16;
                if (he + 16 > data.Length) break;
                ulong mipOffs = BitConverter.ToUInt64(data, he);
                // uint pitch = BitConverter.ToUInt32(data, he + 8);
                uint size = BitConverter.ToUInt32(data, he + 12);

                int mw = Math.Max(1, width >> mip);
                int mh = Math.Max(1, height >> mip);
                int expected = ExpectedSize(fmt, mw, mh);

                if (mipOffs == 0 || mipOffs >= (ulong)data.Length) continue;
                if (mipOffs + (ulong)expected > (ulong)data.Length) continue;

                var block = new byte[expected];
                Array.Copy(data, (long)mipOffs, block, 0, expected);
                return DecodeBlock(block, mw, mh, fmt, hdr);
            }

            throw new InvalidDataException("No decodable mip present (texture may be fully streamed)");
        }

        public static void SavePng(DecodedTex tex, string outPath)
        {
            using var img = Image.LoadPixelData<Rgba32>(tex.Rgba, tex.Width, tex.Height);
            img.SaveAsPng(outPath);
        }

        public static byte[] EncodePng(DecodedTex tex)
        {
            using var img = Image.LoadPixelData<Rgba32>(tex.Rgba, tex.Width, tex.Height);
            using var ms = new MemoryStream();
            img.SaveAsPng(ms);
            return ms.ToArray();
        }

        static DecodedTex DecodeBlock(byte[] block, int w, int h, CompressionFormat fmt, bool hdr)
        {
            var dec = new BcDecoder();
            var rgba = new byte[w * h * 4];

            if (hdr)
            {
                ColorRgbFloat[] px = dec.DecodeRawHdr(block, w, h, fmt);
                for (int i = 0; i < px.Length && i < w * h; i++)
                {
                    rgba[i * 4 + 0] = ToByte(px[i].r);
                    rgba[i * 4 + 1] = ToByte(px[i].g);
                    rgba[i * 4 + 2] = ToByte(px[i].b);
                    rgba[i * 4 + 3] = 255;
                }
            }
            else
            {
                ColorRgba32[] px = dec.DecodeRaw(block, w, h, fmt);
                for (int i = 0; i < px.Length && i < w * h; i++)
                {
                    rgba[i * 4 + 0] = px[i].r;
                    rgba[i * 4 + 1] = px[i].g;
                    rgba[i * 4 + 2] = px[i].b;
                    rgba[i * 4 + 3] = px[i].a;
                }
            }

            return new DecodedTex { Width = w, Height = h, Rgba = rgba };
        }

        static byte ToByte(float f)
        {
            f = f / (f + 1f); // simple Reinhard tonemap for HDR
            int v = (int)(MathF.Sqrt(Math.Clamp(f, 0f, 1f)) * 255f + 0.5f);
            return (byte)Math.Clamp(v, 0, 255);
        }

        static int ExpectedSize(CompressionFormat fmt, int w, int h)
        {
            switch (fmt)
            {
                case CompressionFormat.R: return w * h;
                case CompressionFormat.Rg: return w * h * 2;
                case CompressionFormat.Rgb: return w * h * 3;
                case CompressionFormat.Rgba:
                case CompressionFormat.Bgra: return w * h * 4;
                default:
                    int bx = (w + 3) / 4;
                    int by = (h + 3) / 4;
                    int blockBytes = fmt == CompressionFormat.Bc1 || fmt == CompressionFormat.Bc1WithAlpha || fmt == CompressionFormat.Bc4 ? 8 : 16;
                    return bx * by * blockBytes;
            }
        }

        static CompressionFormat MapFormat(uint dxgi, out bool hdr)
        {
            hdr = false;
            switch (dxgi)
            {
                case 28: case 29: case 27: return CompressionFormat.Rgba;   // R8G8B8A8
                case 87: case 90: case 91: return CompressionFormat.Bgra;   // B8G8R8A8
                case 61: return CompressionFormat.R;                        // R8_UNORM
                case 49: return CompressionFormat.Rg;                       // R8G8_UNORM
                case 70: case 71: case 72: return CompressionFormat.Bc1;    // BC1
                case 73: case 74: case 75: return CompressionFormat.Bc2;    // BC2
                case 76: case 77: case 78: return CompressionFormat.Bc3;    // BC3
                case 79: case 80: case 81: return CompressionFormat.Bc4;    // BC4
                case 82: case 83: case 84: return CompressionFormat.Bc5;    // BC5
                case 94: case 95: hdr = true; return CompressionFormat.Bc6U; // BC6H UF16
                case 96: hdr = true; return CompressionFormat.Bc6S;          // BC6H SF16
                case 97: case 98: case 99: return CompressionFormat.Bc7;    // BC7
                default:
                    throw new NotSupportedException($"Unsupported DXGI format {dxgi}");
            }
        }
    }
}
