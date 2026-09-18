using System;
using System.Collections.Generic;
using System.IO;

namespace REE.Unpacker
{
    class PakChunkEntry
    {
        public UInt64 dwChunkOffset { get; set; }
        public UInt32 dwChunkSize { get; set; }
    }

    class PakChunks
    {
        public static List<PakChunkEntry> lpMapTable = new List<PakChunkEntry>();
        public static Int64 dwBlockSize = 0;

        //Based on https://github.com/eigeen/ree-pak-rs
        public static void iReadMapTable(Stream TPakStream)
        {
            Int32 dwMaxBlockSize = TPakStream.ReadInt32();
            Int32 dwChunksCount = TPakStream.ReadInt32();

            dwBlockSize = (UInt32)dwMaxBlockSize;

            var dwOffsets = new UInt32[dwChunksCount];
            var dwSizes = new UInt32[dwChunksCount];

            for (int i = 0; i < dwChunksCount; i++)
            {
                dwOffsets[i] = TPakStream.ReadUInt32();
                dwSizes[i] = TPakStream.ReadUInt32();
            }

            lpMapTable.Clear();
            for (Int32 i = 0; i < dwChunksCount; i++)
            {
                var m_ChunkEntry = new PakChunkEntry();

                // meta = (compressed length << 10) | (offset high 10 bits)
                m_ChunkEntry.dwChunkOffset = ((UInt64)(dwSizes[i] & 0x3FF) << 32) | dwOffsets[i];
                m_ChunkEntry.dwChunkSize = dwSizes[i] >> 10;

                lpMapTable.Add(m_ChunkEntry);
            }
        }

        public static Byte[] iUnwrapChunks(FileStream TPakStream, PakEntry m_Entry)
        {
            Int64 dwBlock = dwBlockSize;
            Int64 dwTotal = m_Entry.dwDecompressedSize != 0 ? m_Entry.dwDecompressedSize : m_Entry.dwCompressedSize;
            Int32 dwChunkId = (Int32)m_Entry.dwOffset;

            using (var TMemoryStream = new MemoryStream())
            {
                Int64 dwRemainSize = dwTotal;
                while (dwRemainSize > 0)
                {
                    PakChunkEntry m_Chunk = lpMapTable[dwChunkId];

                    Int64 dwChunkOffset = (Int64)m_Chunk.dwChunkOffset;
                    Int32 dwCompressedLen = (Int32)m_Chunk.dwChunkSize;

                    TPakStream.Seek(dwChunkOffset, SeekOrigin.Begin);
                    var lpSrcBuffer = TPakStream.ReadBytes(dwCompressedLen);

                    if (dwCompressedLen == dwBlock)
                    {
                        TMemoryStream.Write(lpSrcBuffer, 0, lpSrcBuffer.Length);
                    }
                    else
                    {
                        var lpDstBuffer = ZSTD.iDecompress(lpSrcBuffer);
                        TMemoryStream.Write(lpDstBuffer, 0, lpDstBuffer.Length);
                    }

                    dwChunkId++;
                    dwRemainSize -= dwBlock;
                }

                TMemoryStream.SetLength(dwTotal);

                return TMemoryStream.ToArray();
            }
        }
    }
}