﻿﻿﻿﻿﻿﻿﻿using System;
using System.IO;
using System.Collections.Generic;

namespace REE.Unpacker
{
    class PakUnpack
    {
        private static Int32 dwEntrySize = 0;
        private static List<PakEntry> m_EntryTable = new List<PakEntry>();

        // current, total, currentFileName -> reported on percent change
        public static Action<Int32, Int32, String> Progress = null;

        public static void iDoIt(String m_PakFile, String m_DstFolder)
        {
            using (FileStream TPakStream = File.OpenRead(m_PakFile))
            {
                if (TPakStream.Length <= 16)
                {
                    Utils.iSetError("[ERROR]: Empty PAK archive file");
                    return;
                }

                var m_Header = new PakHeader();

                m_Header.dwMagic = TPakStream.ReadUInt32();
                m_Header.bMajorVersion = TPakStream.ReadByte();
                m_Header.bMinorVersion = TPakStream.ReadByte();
                m_Header.wFeature = (Features)TPakStream.ReadInt16();
                m_Header.dwTotalFiles = TPakStream.ReadInt32();
                m_Header.dwFingerprint = TPakStream.ReadUInt32();

                if (m_Header.dwMagic != 0x414B504B)
                {
                    Utils.iSetError("[ERROR]: Invalid magic of PAK archive file");
                    return;
                }

                if (m_Header.bMajorVersion != 2 && m_Header.bMajorVersion != 4 || m_Header.bMinorVersion != 0 && m_Header.bMinorVersion != 1 && m_Header.bMinorVersion != 2)
                {
                    Utils.iSetError("[ERROR]: Invalid version of PAK archive file -> " + m_Header.bMajorVersion.ToString() + "." + m_Header.bMinorVersion.ToString() + ", expected 2.0, 4.0, 4.1 & 4.2");
                    return;
                }

                if (m_Header.wFeature != Features.NONE && m_Header.wFeature != Features.ENCRYPTED_RESOURCES && m_Header.wFeature != Features.DLC_EXTRA_DATA1 && m_Header.wFeature != Features.EXTRA_DATA && m_Header.wFeature != Features.CHUNKED_RESOURCES && m_Header.wFeature != Features.DLC_EXTRA_DATA2)
                {
                    Utils.iSetError("[ERROR]: Archive is encrypted (obfuscated) with an unsupported algorithm or has unknown header flags");
                    return;
                }

                switch (m_Header.bMajorVersion)
                {
                    case 2: dwEntrySize = 24; break;
                    case 4: dwEntrySize = 48; break;
                    default: break;
                }

                var lpTable = TPakStream.ReadBytes(m_Header.dwTotalFiles * dwEntrySize);

                if (m_Header.wFeature == Features.ENCRYPTED_RESOURCES || m_Header.wFeature == Features.DLC_EXTRA_DATA1 || m_Header.wFeature == Features.EXTRA_DATA || m_Header.wFeature == Features.CHUNKED_RESOURCES || m_Header.wFeature == Features.DLC_EXTRA_DATA2)
                {
                    if (m_Header.wFeature == Features.EXTRA_DATA)
                    {
                        TPakStream.Seek(4, SeekOrigin.Current);
                    }
                    else if (m_Header.wFeature == Features.DLC_EXTRA_DATA1 || m_Header.wFeature == Features.DLC_EXTRA_DATA2)
                    {
                        TPakStream.Seek(9, SeekOrigin.Current);
                    }

                    var lpEncryptedKey = TPakStream.ReadBytes(128);

                    lpTable = PakCipher.iDecryptData(lpTable, lpEncryptedKey);

                    if (m_Header.wFeature == Features.CHUNKED_RESOURCES || m_Header.wFeature == Features.DLC_EXTRA_DATA2)
                    {
                        PakChunks.iReadMapTable(TPakStream);
                    }
                }

                m_EntryTable.Clear();
                using (var TEntryReader = new MemoryStream(lpTable))
                {
                    for (Int32 i = 0; i < m_Header.dwTotalFiles; i++)
                    {
                        var m_Entry = new PakEntry();

                        if (m_Header.bMajorVersion == 2 && m_Header.bMinorVersion == 0)
                        {
                            m_Entry.dwOffset = TEntryReader.ReadInt64();
                            m_Entry.dwDecompressedSize = TEntryReader.ReadInt64();
                            m_Entry.dwHashNameLower = TEntryReader.ReadUInt32();
                            m_Entry.dwHashNameUpper = TEntryReader.ReadUInt32();
                            m_Entry.dwCompressedSize = 0;
                            m_Entry.wCompressionType = 0;
                            m_Entry.dwChecksum = 0;
                        }
                        else if (m_Header.bMajorVersion == 4 && m_Header.bMinorVersion == 0 || m_Header.bMinorVersion == 1 || m_Header.bMinorVersion == 2)
                        {
                            m_Entry.dwHashNameLower = TEntryReader.ReadUInt32();
                            m_Entry.dwHashNameUpper = TEntryReader.ReadUInt32();
                            m_Entry.dwOffset = TEntryReader.ReadInt64();
                            m_Entry.dwCompressedSize = TEntryReader.ReadInt64();
                            m_Entry.dwDecompressedSize = TEntryReader.ReadInt64();
                            m_Entry.dwAttributes = TEntryReader.ReadInt64();
                            m_Entry.dwChecksum = TEntryReader.ReadUInt64();
                            m_Entry.wCompressionType = (Compression)(m_Entry.dwAttributes & 0xF);
                            m_Entry.wEncryptionType = (Encryption)((m_Entry.dwAttributes & 0x00FF0000) >> 16);
                        }
                        else
                        {
                            Utils.iSetError("[ERROR]: Something is wrong when reading the entry table");
                            return;
                        }

                        m_EntryTable.Add(m_Entry);
                    }
                }

                String m_Filter = Environment.GetEnvironmentVariable("REE_FILTER");
                if (m_Filter != null) m_Filter = m_Filter.ToLowerInvariant();

                Int32 dwCounter = 1;
                Int32 dwLastPercent = -1;
                Int32 dwTotal = (Int32)m_Header.dwTotalFiles;
                foreach (var m_Entry in m_EntryTable)
                {
                    String m_FileName = PakList.iGetNameFromHashList((UInt64)m_Entry.dwHashNameUpper << 32 | m_Entry.dwHashNameLower);

                    if (m_Filter != null && (m_FileName == null || !m_FileName.ToLowerInvariant().Contains(m_Filter)))
                    {
                        dwCounter++;
                        continue;
                    }

                    String m_FullPath = m_DstFolder + m_FileName.Replace("/", @"\");

                    Int32 dwCurrent = dwCounter++;
                    Int32 dwPercent = dwTotal > 0 ? (Int32)((Int64)dwCurrent * 100 / dwTotal) : 100;
                    if (dwPercent != dwLastPercent)
                    {
                        dwLastPercent = dwPercent;
                        if (Progress != null) Progress(dwCurrent, dwTotal, m_FileName);
                    }

                    Utils.iCreateDirectory(m_FullPath);

                    try
                    {
                        Boolean bChunkedStore = (m_Header.wFeature == Features.CHUNKED_RESOURCES || m_Header.wFeature == Features.DLC_EXTRA_DATA2)
                                                && (m_Entry.dwAttributes & 0x1000000) != 0;

                        if (bChunkedStore)
                        {
                            var lpBuffer = PakChunks.iUnwrapChunks(TPakStream, m_Entry);

                            m_FullPath = PakUtils.iDetectFileType(m_FullPath, lpBuffer);

                            File.WriteAllBytes(m_FullPath, lpBuffer);
                        }
                        else if (m_Entry.wCompressionType == Compression.NONE)
                        {
                            TPakStream.Seek(m_Entry.dwOffset, SeekOrigin.Begin);

                            var m_Chunks = PakUtils.iReadByChunks(TPakStream, m_Entry.dwCompressedSize);

                            PakUtils.iWriteByChunks(m_FullPath, m_Chunks);
                        }
                        else if (m_Entry.wCompressionType == Compression.DEFLATE || m_Entry.wCompressionType == Compression.ZSTD)
                        {
                            TPakStream.Seek(m_Entry.dwOffset, SeekOrigin.Begin);

                            var lpSrcBuffer = TPakStream.ReadBytes((Int32)m_Entry.dwCompressedSize);
                            var lpDstBuffer = new Byte[] { };

                            if (m_Entry.wEncryptionType != Encryption.None && m_Entry.wEncryptionType <= Encryption.Type_Invalid)
                            {
                                lpSrcBuffer = ResourceCipher.iDecryptResource(lpSrcBuffer);
                            }

                            switch (m_Entry.wCompressionType)
                            {
                                case Compression.DEFLATE: lpDstBuffer = DEFLATE.iDecompress(lpSrcBuffer); break;
                                case Compression.ZSTD: lpDstBuffer = ZSTD.iDecompress(lpSrcBuffer); break;
                            }

                            m_FullPath = PakUtils.iDetectFileType(m_FullPath, lpDstBuffer);

                            File.WriteAllBytes(m_FullPath, lpDstBuffer);
                        }
                        else
                        {
                            Utils.iSetWarning("[SKIP]: Unknown compression type -> " + m_Entry.wCompressionType.ToString() + " @ " + m_FileName);
                        }
                    }
                    catch (Exception ex)
                    {
                        Utils.iSetWarning("[SKIP]: " + ex.Message + " @ " + m_FileName + " [attr=0x" + m_Entry.dwAttributes.ToString("X") + " off=" + m_Entry.dwOffset + " csz=" + m_Entry.dwCompressedSize + "]");
                    }
                }

                TPakStream.Dispose();
            }
        }
    }
}
