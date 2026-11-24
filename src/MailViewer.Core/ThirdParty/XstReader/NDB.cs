#nullable disable
// Copyright (c) 2016, Dijji, and released under Ms-PL.  This can be found in the root of this distribution. 

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace XstReader
{
    /// <summary>
    /// This class implements the NDB (Node Database) layer, Which provides lower-level storage facilities
    /// </summary>
    class NDB
    {
        private readonly Func<Stream> openEvidence;
        private const uint MaximumNodeBytes = 128 * 1024 * 1024;
        private const int LegacyPageSize = 512;
        private const int LegacyBlockAlignment = 64;
        private const int OstPageSize = 4096;
        private const int OstPageTrailerSize = 24;
        private const int OstBlockAlignment = 512;
        private const int OstBlockTrailerSize = 24;
        private static readonly uint[] CrcTable = BuildCrcTable();
        private EbCryptMethod bCryptMethod;
        private bool validateLegacyOst;
        private BTree<Node> nodeTree = new BTree<Node>();
        private BTree<DataRef> dataTree = new BTree<DataRef>();

        // deferReadingNodes is a switch set only here
        // If true, reading node and data tree b-trees is deferred until look-up time,
        // and only required portions of the tree are read
        // If false, the full b-trees are read at file open time
        // The former setting (the default) gets the folder structure almost instantly after opening,
        // and consumes less memory in many scenarios,
        // but may make opening a folder slower and net less efficient
        private bool deferReadingNodes = true;
        private Action<TreeIntermediate> deferredReadAction;

        public int BlockSize { get; private set; } = 8192;
        public int BlockSize4K { get; private set; } = 65536;
        public bool IsUnicode { get; private set; }
        public bool IsUnicode4K { get; private set; } = false;

        #region Public methods

        public NDB(Func<Stream> openEvidence)
        {
            this.openEvidence = openEvidence;
            deferredReadAction = new Action<TreeIntermediate>(p => this.ReadDeferredIndex(p));
        }

        // Prepare to read the contents of a file
        public void Initialise()
        {
            ReadHeaderAndIndexes();
        }
        
        // Return a file stream that will allow the caller to read the current file
        public Stream GetReadStream()
        {
            return openEvidence();
        }

        // Decrypt the contents of a data buffer if necessary
        public void Decrypt(ref byte[] buffer, UInt32 key, int offset = 0, int length = 0)
        {
            Crypto.Decrypt(ref buffer, bCryptMethod, key, offset, length > 0 ? length : buffer.Length - offset);
        }

        // Look up a node in the main node b-tree
        public Node LookupNode(NID nid)
        {
            return nodeTree.Lookup(nid.dwValue, deferredReadAction);
        }

        // Look up a data block in the main data block b-tree
        public DataRef LookupDataBlock(UInt64 dataBid)
        {
            // Clear the LSB, which is reserved, but sometimes set
            return dataTree.Lookup(dataBid & ~1UL, deferredReadAction);
        }

        // Read a sub-node's b-tree from the specified data block
        public BTree<Node> ReadSubNodeBtree(Stream fs, UInt64 subDataBid)
        {
            var tree = new BTree<Node>();
            if (IsUnicode)
                ReadSubNodeBtreeUnicode(fs, subDataBid, tree.Root);
            else
                ReadSubNodeBtreeANSI(fs, subDataBid, tree.Root);
            return tree;
        }

        // Look up a node in a sub-node's b-tree
        public static Node LookupSubNode(BTree<Node> subNodeTree, NID nid)
        {
            if (subNodeTree == null)
                throw new XstException("No sub node data tree found");

            return subNodeTree.Lookup(nid.dwValue);
        }

        // Read raw data, accessed via a sub node
        // If it has a multiblock structure, return all of the blocks' contents concatenated
        public byte[] ReadSubNodeDataBlock(Stream fs, BTree<Node> subNodeTree, NID nid)
        {
            if (!nid.HasValue)
                return null;
            var n = LookupSubNode(subNodeTree, nid);
            if (n == null)
                throw new XstException("Node not found in sub node tree");
            if (n.SubDataBid != 0)
                throw new XstException("Sub-nodes of sub-nodes not yet implemented");
            return ReadDataBlock(fs, n.DataBid);
        }

        // Read all the data blocks for a given BID, concatenating them into a single buffer
        public byte[] ReadDataBlock(Stream fs, UInt64 dataBid)
        {
            int offset = 0;
            return ReadDataBlockInternal(fs, dataBid, ref offset);
        }

        // Read all the data blocks for a given BID, enumerating them one by one
        public IEnumerable<byte[]> ReadDataBlocks(Stream fs, UInt64 dataBid)
        {
            long totalLength = 0;
            foreach (var buf in ReadDataBlocksInternal(fs, dataBid))
            {
                totalLength += buf.Length;
                if (totalLength > MaximumNodeBytes)
                    throw new XstException("PST node exceeds the read limit.");
                yield return buf;
            }
        }

        // Copy data to the specified file stream
        // We write each external data block as we read it, so that we never have more than one in memory at the same time
        public void CopyDataBlocks(Stream fs, Stream s, UInt64 dataBid)
        {
            foreach (var buf in ReadDataBlocksInternal(fs, dataBid))
            {
                s.Write(buf, 0, buf.Length);
            }
        }

        // Some nodes use sub-nodes to hold local data, so we need to give access to both levels
        public Node LookupNodeAndReadItsSubNodeBtree(Stream fs, NID nid, out BTree<Node> subNodeTree)
        {
            subNodeTree = null;
            var rn = LookupNode(nid);
            if (rn == null)
                throw new XstException("Node block does not exist");
            // If there is a sub-node, read its btree so that we can resolve references to nodes in it later
            if (rn.SubDataBid != 0)
            {
                subNodeTree = ReadSubNodeBtree(fs, rn.SubDataBid);
            }
            return rn;
        }

        // Some sub-nodes use sub-sub-nodes to hold local data, so we need to give access to both levels
        public Node LookupSubNodeAndReadItsSubNodeBtree(Stream fs, BTree<Node> subNodeTree, NID nid, out BTree<Node> childSubNodeTree)
        {
            childSubNodeTree = null;
            var rn = LookupSubNode(subNodeTree, nid);
            if (rn == null)
                throw new XstException("Node block does not exist");
            // If there is a sub-node, read its btree so that we can resolve references to nodes in it later
            if (rn.SubDataBid != 0)
            {
                childSubNodeTree = ReadSubNodeBtree(fs, rn.SubDataBid);
            }
            return rn;
        }

        #endregion


        #region Private methods

        // Read the file header, and the B trees that give us access to nodes and data blocks
        private void ReadHeaderAndIndexes()
        {
            using (var fs = GetReadStream())
            {
                var h = Map.ReadType<FileHeader1>(fs);

                if (h.dwMagic != 0x4e444221)
                    throw new XstException("File is not a .ost or .pst file: the magic cookie is missing");

                if (h.wVer == 0x15 || h.wVer == 0x17 )
                {
                    var h2 = Map.ReadType<FileHeader2Unicode>(fs);
                    bCryptMethod = h2.bCryptMethod;
                    IsUnicode = true;
                    validateLegacyOst = h.wMagicClient == 0x4f53;
                    ReadBTPageUnicode(fs, h2.root.BREFNBT, Eptype.ptypeNBT, nodeTree.Root);
                    ReadBTPageUnicode(fs, h2.root.BREFBBT, Eptype.ptypeBBT, dataTree.Root);
                }
                else if (h.wVer == 0x24)
                {
                    // This value indicates the use of 4K pages, as opposed to 512 bytes
                    // It is used only in .ost files, and was introduced in Office 2013
                    // It is not documented in [MS-PST], being .ost only
                    var h2 = Map.ReadType<FileHeader2Unicode>(fs);
                    bCryptMethod = h2.bCryptMethod;
                    IsUnicode = true;
                    IsUnicode4K = true;
                    ReadBTPageUnicode4K(fs, h2.root.BREFNBT, Eptype.ptypeNBT, nodeTree.Root);
                    ReadBTPageUnicode4K(fs, h2.root.BREFBBT, Eptype.ptypeBBT, dataTree.Root);
                }
                else if (h.wVer == 0x0e || h.wVer == 0x0f)
                {
                    var h2 = Map.ReadType<FileHeader2ANSI>(fs);
                    bCryptMethod = h2.bCryptMethod;
                    IsUnicode = false;
                    validateLegacyOst = h.wMagicClient == 0x4f53;
                    ReadBTPageANSI(fs, h2.root.BREFNBT, Eptype.ptypeNBT, nodeTree.Root);
                    ReadBTPageANSI(fs, h2.root.BREFBBT, Eptype.ptypeBBT, dataTree.Root);
                }
                else
                    throw new XstException("Unrecognised header type");
            }
        }

        // A callback to be used when searching a tree to read part of the index whose loading has been deferred
        private void ReadDeferredIndex(TreeIntermediate inter)
        {
            using (var fs = GetReadStream())
            {
                if (IsUnicode4K)
                    ReadBTPageUnicode4K(fs, new BREFUnicode { bid = inter.PageBid, ib = (ulong)inter.fileOffset }, inter.PageType, inter);
                else if (IsUnicode)
                    ReadBTPageUnicode(fs, new BREFUnicode { bid = inter.PageBid, ib = (ulong)inter.fileOffset }, inter.PageType, inter);
                else
                    ReadBTPageANSI(fs, new BREFANSI { bid = (uint)inter.PageBid, ib = (uint)inter.fileOffset }, inter.PageType, inter);

                // Don't read it again
                inter.fileOffset = null;
            }
        }
        // Read a page containing part of a node or data block B-tree, and build the corresponding data structure
        private void ReadBTPageUnicode(Stream fs, BREFUnicode address, Eptype type, TreeIntermediate parent)
        {
            var bytes = ReadLegacyIndexPage(fs, address.ib);
            var p = Map.MapType<BTPAGEUnicode>(bytes);
            if (p.cLevel > 16 || (parent.ExpectedLevel is { } level && p.cLevel != level)
                || p.cbEnt == 0 || p.cEnt > p.cEntMax || p.cEnt * p.cbEnt > LayoutsU.BTPAGEEntryBytes
                || p.pageTrailer.ptype != p.pageTrailer.ptypeRepeat
                || p.pageTrailer.ptype is not (Eptype.ptypeNBT or Eptype.ptypeBBT))
                throw new XstException("Unicode 인덱스 페이지의 구조가 올바르지 않습니다.");
            var trailer = p.pageTrailer;
            ValidateLegacyIndexTrailer(bytes.AsSpan(0, LegacyPageSize - Marshal.SizeOf<PAGETRAILERUnicode>()),
                address.ib, address.bid, type, trailer.ptype, trailer.bid, trailer.wSig, trailer.dwCRC);

            var children = new List<TreeNode>(p.cEnt);
            for (int i = 0; i < p.cEnt; i++)
            {
                TreeNode entry;
                if (p.cLevel > 0)
                {
                    BTENTRYUnicode e;
                    unsafe
                    {
                        e = Map.MapType<BTENTRYUnicode>(p.rgentries, LayoutsU.BTPAGEEntryBytes, i * p.cbEnt, p.cbEnt);
                    }
                    var inter = new TreeIntermediate
                    {
                        Key = e.btkey, ExpectedLevel = p.cLevel - 1, PageBid = e.BREF.bid, PageType = type
                    };
                    entry = inter;

                    if (deferReadingNodes)
                        inter.fileOffset = e.BREF.ib;
                    else
                    {
                        // Read child nodes in tree
                        ReadBTPageUnicode(fs, e.BREF, type, inter);
                        inter.fileOffset = null;
                    }
                }
                else
                {
                    if (p.pageTrailer.ptype == Eptype.ptypeNBT)
                    {
                        unsafe
                        {
                            var e = Map.MapType<NBTENTRYUnicode>(p.rgentries, LayoutsU.BTPAGEEntryBytes, i * p.cbEnt, p.cbEnt);
                            entry = new Node { Key = e.nid.dwValue, Type = e.nid.nidType, DataBid = e.bidData, SubDataBid = e.bidSub, Parent = e.nidParent };
                        }
                    }
                    else if (p.pageTrailer.ptype == Eptype.ptypeBBT)
                    {
                        unsafe
                        {
                            var e = Map.MapType<BBTENTRYUnicode>(p.rgentries, LayoutsU.BTPAGEEntryBytes, i * p.cbEnt, p.cbEnt);
                            entry = new DataRef { Key = e.BREF.bid, Offset = e.BREF.ib, Length = e.cb };
                        }
                    }
                    else
                        throw new XstException("Unexpected page entry type");
                }
                AddLegacyIndexEntry(children, entry);
            }
            parent.Children = children;
        }

        // Read a page containing part of a node or data block B-tree, and build the corresponding data structure
        private void ReadBTPageUnicode4K(Stream fs, BREFUnicode address, Eptype type, TreeIntermediate parent)
        {
            if (address.ib % OstPageSize != 0 || !FitsInStream(fs, address.ib, OstPageSize))
                throw new XstException("OST 인덱스 페이지 위치가 올바르지 않습니다.");
            var bytes = new byte[OstPageSize];
            fs.Seek((long)address.ib, SeekOrigin.Begin);
            fs.ReadExactly(bytes);
            var p = Map.MapType<BTPAGEUnicode4K>(bytes);
            var trailer = p.pageTrailer;
            if (p.cLevel > 16 || (parent.ExpectedLevel is { } level && p.cLevel != level)
                || p.cbEnt == 0 || p.cEnt > p.cEntMax || p.cEnt * p.cbEnt > LayoutsU4K.BTPAGEEntryBytes
                || trailer.ptype != type || trailer.ptypeRepeat != type || trailer.bid != address.bid
                || trailer.wSig != Signature(address.ib, address.bid)
                || trailer.dwCRC != CalculateWeakCrc(bytes.AsSpan(0, OstPageSize - OstPageTrailerSize)))
                throw new XstException("OST 인덱스 페이지의 구조 또는 CRC가 일치하지 않습니다.");

            var children = new List<TreeNode>(p.cEnt);
            for (int i = 0; i < p.cEnt; i++)
            {
                TreeNode entry;
                if (p.cLevel > 0)
                {
                    BTENTRYUnicode e;
                    unsafe
                    {
                        e = Map.MapType<BTENTRYUnicode>(p.rgentries, LayoutsU4K.BTPAGEEntryBytes, i * p.cbEnt, p.cbEnt);
                    }
                    var inter = new TreeIntermediate
                    {
                        Key = e.btkey, ExpectedLevel = p.cLevel - 1, PageBid = e.BREF.bid, PageType = type
                    };
                    entry = inter;

                    if (deferReadingNodes)
                        inter.fileOffset = e.BREF.ib;
                    else
                    {
                        // Read child nodes in tree
                        ReadBTPageUnicode4K(fs, e.BREF, type, inter);
                        inter.fileOffset = null;
                    }
                }
                else
                {
                    if (type == Eptype.ptypeNBT)
                    {
                        unsafe
                        {
                            var e = Map.MapType<NBTENTRYUnicode>(p.rgentries, LayoutsU4K.BTPAGEEntryBytes, i * p.cbEnt, p.cbEnt);
                            entry = new Node { Key = e.nid.dwValue, Type = e.nid.nidType, DataBid = e.bidData, SubDataBid = e.bidSub, Parent = e.nidParent };
                        }
                    }
                    else if (p.pageTrailer.ptype == Eptype.ptypeBBT)
                    {
                        unsafe
                        {
                            var e = Map.MapType<BBTENTRYUnicode4K>(p.rgentries, LayoutsU4K.BTPAGEEntryBytes, i * p.cbEnt, p.cbEnt);
                            entry = new DataRef { Key = e.BREF.bid, Offset = e.BREF.ib, Length = e.cbStored, InflatedLength = e.cbInflated };
                        }
                    }
                    else
                        throw new XstException("Unexpected page entry type");
                }
                if (i > 0 && children[^1].Key >= entry.Key)
                    throw new XstException("OST 인덱스 페이지의 키 순서가 올바르지 않습니다.");
                children.Add(entry);
            }
            parent.Children = children;
        }

        private void ReadBTPageANSI(Stream fs, BREFANSI address, Eptype type, TreeIntermediate parent)
        {
            var bytes = ReadLegacyIndexPage(fs, address.ib);
            var p = Map.MapType<BTPAGEANSI>(bytes);
            var trailer = p.pageTrailer;
            if (p.cLevel > 16 || (parent.ExpectedLevel is { } level && p.cLevel != level)
                || p.cbEnt == 0 || p.cEnt > p.cEntMax || p.cEnt * p.cbEnt > LayoutsA.BTPAGEEntryBytes
                || trailer.ptype != trailer.ptypeRepeat)
                throw new XstException("ANSI 인덱스 페이지의 구조가 올바르지 않습니다.");
            ValidateLegacyIndexTrailer(bytes.AsSpan(0, LegacyPageSize - Marshal.SizeOf<PAGETRAILERANSI>()),
                address.ib, address.bid, type, trailer.ptype, trailer.bid, trailer.wSig, trailer.dwCRC);
            var children = new List<TreeNode>(p.cEnt);
            for (int i = 0; i < p.cEnt; i++)
            {
                TreeNode entry;
                if (p.cLevel > 0)
                {
                    BTENTRYANSI e;
                    unsafe
                    {
                        e = Map.MapType<BTENTRYANSI>(p.rgentries, LayoutsA.BTPAGEEntryBytes, i * p.cbEnt, p.cbEnt);
                    }
                    var inter = new TreeIntermediate
                    {
                        Key = e.btkey, ExpectedLevel = p.cLevel - 1, PageBid = e.BREF.bid, PageType = type
                    };
                    entry = inter;

                    if (deferReadingNodes)
                        inter.fileOffset = e.BREF.ib;
                    else
                    {
                        // Read child nodes in tree
                        ReadBTPageANSI(fs, e.BREF, type, inter);
                        inter.fileOffset = null;
                    }
                }
                else
                {
                    if (p.pageTrailer.ptype == Eptype.ptypeNBT)
                    {
                        unsafe
                        {
                            var e = Map.MapType<NBTENTRYANSI>(p.rgentries, LayoutsA.BTPAGEEntryBytes, i * p.cbEnt, p.cbEnt);
                            entry = new Node { Key = e.nid.dwValue, Type = e.nid.nidType, DataBid = e.bidData, SubDataBid = e.bidSub, Parent = e.nidParent };
                        }
                    }
                    else if (p.pageTrailer.ptype == Eptype.ptypeBBT)
                    {
                        unsafe
                        {
                            var e = Map.MapType<BBTENTRYANSI>(p.rgentries, LayoutsA.BTPAGEEntryBytes, i * p.cbEnt, p.cbEnt);
                            entry = new DataRef { Key = e.BREF.bid, Offset = e.BREF.ib, Length = e.cb };
                        }
                    }
                    else
                        throw new XstException("Unexpected page entry type");
                }
                AddLegacyIndexEntry(children, entry);
            }
            parent.Children = children;
        }

        private byte[] ReadLegacyIndexPage(Stream fs, ulong offset)
        {
            if (!FitsInStream(fs, offset, LegacyPageSize) || (validateLegacyOst && offset % LegacyPageSize != 0))
                throw new XstException("인덱스 페이지 위치가 올바르지 않습니다.");
            var bytes = new byte[LegacyPageSize];
            fs.Seek((long)offset, SeekOrigin.Begin);
            fs.ReadExactly(bytes);
            return bytes;
        }

        private void ValidateLegacyIndexTrailer(ReadOnlySpan<byte> payload, ulong offset, ulong bid,
            Eptype expectedType, Eptype observedType, ulong observedBid, ushort signature, uint crc)
        {
            if (validateLegacyOst &&
                (observedType != expectedType || observedBid != bid || signature != Signature(offset, bid)
                || crc != CalculateWeakCrc(payload)))
                throw new XstException("OST 인덱스 페이지의 구조 또는 CRC가 일치하지 않습니다.");
        }

        private void AddLegacyIndexEntry(List<TreeNode> children, TreeNode entry)
        {
            if (validateLegacyOst && children.Count != 0 && children[^1].Key >= entry.Key)
                throw new XstException("OST 인덱스 페이지의 키 순서가 올바르지 않습니다.");
            children.Add(entry);
        }

        // Return all the data blocks referenced by a particular data Id
        private IEnumerable<byte[]> ReadDataBlocksInternal(Stream fs, UInt64 dataBid, int depth = 0)
        {
            if (depth > 2)
                throw new XstException("Invalid PST data block nesting.");
            var rb = LookupDataBlock(dataBid);
            int read;
            byte[] buffer = ReadAndDecompress(fs, rb, out read);

            if (rb.IsInternal)
            {
                // XBlock and XXBlock structures are very similar, we can use the same layout for both
                // Unicode and ANSI structures are also close, enabling us to use the same layout for them, too
                var xb = Map.MapType<XBLOCKUnicode>(buffer, 0);
                if (xb.btype != EbType.bTypeX || xb.cLevel is < 1 or > 2 || xb.lcbTotal > MaximumNodeBytes)
                    throw new XstException("Invalid PST data block.");
                long totalLength = 0;
                if (IsUnicode)
                {
                    var rgbid = Map.MapArray<UInt64>(buffer, Marshal.SizeOf(typeof(XBLOCKUnicode)), xb.cEnt);

                    for (int i = 0; i < rgbid.Length; i++)
                    {
                        foreach (var buf in ReadDataBlocksInternal(fs, rgbid[i], depth + 1))
                        {
                            totalLength += buf.Length;
                            if (totalLength > xb.lcbTotal)
                                throw new XstException("PST data block length exceeds its declared total.");
                            yield return buf;
                        }
                    }
                }
                else
                {
                    // The ANSI difference is that IDs are only 32 bits
                    var rgbid = Map.MapArray<UInt32>(buffer, Marshal.SizeOf(typeof(XBLOCKUnicode)), xb.cEnt);

                    for (int i = 0; i < rgbid.Length; i++)
                    {
                        foreach (var buf in ReadDataBlocksInternal(fs, rgbid[i], depth + 1))
                        {
                            totalLength += buf.Length;
                            if (totalLength > xb.lcbTotal)
                                throw new XstException("PST data block length exceeds its declared total.");
                            yield return buf;
                        }
                    }
                }
                if (totalLength != xb.lcbTotal)
                    throw new XstException("PST data block length differs from its declared total.");
            }
            else
            {
                // Key for cyclic algorithm is the low 32 bits of the BID, so supply it in case it's needed
                Decrypt(ref buffer, unchecked((uint)rb.Key));

                yield return buffer;
            }
        }

        // Return all the data blocks referenced by a particular data Id, concatenating them into a single buffer
        private byte[] ReadDataBlockInternal(Stream fs, UInt64 dataBid, ref int offset, byte[] buffer = null, int depth = 0)
        {
            if (depth > 2)
                throw new XstException("Invalid PST data block nesting.");
            bool first = (buffer == null);  // Remember if we're at the top of a potential recursion
            var rb = LookupDataBlock(dataBid);
            if (rb == null)
                throw new XstException("Data block does not exist");
            if (first)
                offset = 0;

            // First guy in allocates enough to hold the initial block
            // This is either the one and only block, or gets replaced when we find out how much data there is in total
            int read;
            buffer = ReadAndDecompress(fs, rb, out read, buffer, offset);

            if (rb.IsInternal)
            {
                // XBlock and XXBlock structures are very similar, we can use the same layout for both
                // Unicode and ANSI structures are also close, enabling us to use the same layout for them, too
                var xb = Map.MapType<XBLOCKUnicode>(buffer, offset);
                if (xb.btype != EbType.bTypeX || xb.cLevel is < 1 or > 2 || xb.lcbTotal > MaximumNodeBytes)
                    throw new XstException("Invalid PST data block.");

                if (IsUnicode)
                {
                    var rgbid = Map.MapArray<UInt64>(buffer, offset + Marshal.SizeOf(typeof(XBLOCKUnicode)), xb.cEnt);

                    if (first)
                    {
                        // First block, allocate a buffer big enough to hold all the data
                        buffer = new byte[xb.lcbTotal];
                        offset = 0;
                    }

                    for (int i = 0; i < rgbid.Length; i++)
                    {
                        // Recurse. XBlock and XXBlock can have common handling
                        ReadDataBlockInternal(fs, rgbid[i], ref offset, buffer, depth + 1);
                    }
                }
                else
                {
                    // The ANSI difference is that IDs are only 32 bits 
                    var rgbid = Map.MapArray<UInt32>(buffer, offset + Marshal.SizeOf(typeof(XBLOCKUnicode)), xb.cEnt);

                    if (first)
                    {
                        // First block, allocate a buffer big enough to hold all the data
                        buffer = new byte[xb.lcbTotal];
                        offset = 0;
                    }

                    for (int i = 0; i < rgbid.Length; i++)
                    {
                        // Recurse. XBlock and XXBlock can have common handling
                        ReadDataBlockInternal(fs, rgbid[i], ref offset, buffer, depth + 1);
                    }
                }
            }
            else
            {
                // Key for cyclic algorithm is the low 32 bits of the BID
                // Assume that this means the BID of each block, rather than all using the BID from the head of the tree
                Decrypt(ref buffer, unchecked((uint)rb.Key), offset, read);

                // Increment the offset by the length of the real data that we have read
                offset += read;
            }

            if (first)
            {
                // The recursion is over, check the results
                if (offset != buffer.Length)
                    throw new XstException("Data xblock length mismatch");

                return buffer;
            }
            else
                return null;  // Value only returned from the top level guy
        }

        private byte[] ReadAndDecompress(Stream fs, DataRef rb, out int read, byte[] buffer = null, int offset = 0)
        {
            if (rb == null)
                throw new XstException("Data block does not exist");

            if (IsUnicode4K)
            {
                var stored = ReadOstBlock(fs, rb);
                read = rb.InflatedLength;
                buffer ??= new byte[read];
                if (rb.Length == read)
                    stored.CopyTo(buffer, offset);
                else
                {
                    // The BBT describes the zlib member without the following block padding and trailer.
                    using var source = new MemoryStream(stored, writable: false);
                    using var decompression = new ZLibStream(source, CompressionMode.Decompress);
                    decompression.ReadExactly(buffer, offset, read);
                    if (decompression.ReadByte() != -1)
                        throw new XstException("OST 블록의 압축 해제 크기가 인덱스와 다릅니다.");
                }
            }
            else if (validateLegacyOst)
            {
                buffer ??= new byte[rb.Length];
                read = rb.Length;
                if (read > buffer.Length - offset)
                    throw new XstException("OST 데이터 블록 길이가 노드에서 지정한 범위를 넘습니다.");
                ReadLegacyOstBlock(fs, rb, buffer.AsSpan(offset, read));
            }
            else
            {
                if (buffer == null)
                    buffer = new byte[rb.Length];
                fs.Seek((long)rb.Offset, SeekOrigin.Begin);
                fs.ReadExactly(buffer, offset, rb.Length);
                read = rb.Length;
            }

            return buffer;
        }

        private void ReadLegacyOstBlock(Stream fs, DataRef block, Span<byte> payload)
        {
            int trailerSize = IsUnicode ? Marshal.SizeOf<BLOCKTRAILERUnicode>() : Marshal.SizeOf<BLOCKTRAILERANSI>();
            int sizeWithTrailer = block.Length + trailerSize;
            int allocationSize = (sizeWithTrailer + LegacyBlockAlignment - 1) & ~(LegacyBlockAlignment - 1);
            if (block.Offset % LegacyBlockAlignment != 0 || allocationSize > BlockSize || block.Length == 0
                || !FitsInStream(fs, block.Offset, allocationSize))
                throw new XstException("OST 데이터 블록 범위가 올바르지 않습니다.");

            fs.Seek((long)block.Offset, SeekOrigin.Begin);
            fs.ReadExactly(payload);
            fs.Seek((long)block.Offset + allocationSize - trailerSize, SeekOrigin.Begin);
            if (IsUnicode)
            {
                var trailer = Map.ReadType<BLOCKTRAILERUnicode>(fs);
                ValidateLegacyBlockTrailer(block, payload, trailer.cb, trailer.bid, trailer.wSig, trailer.dwCRC);
            }
            else
            {
                var trailer = Map.ReadType<BLOCKTRAILERANSI>(fs);
                ValidateLegacyBlockTrailer(block, payload, trailer.cb, trailer.bid, trailer.wSig, trailer.dwCRC);
            }
        }

        private static void ValidateLegacyBlockTrailer(DataRef block, ReadOnlySpan<byte> payload,
            ushort length, ulong bid, ushort signature, uint crc)
        {
            if (length != block.Length || bid != block.Key || signature != Signature(block.Offset, block.Key)
                || crc != CalculateWeakCrc(payload))
                throw new XstException("OST 데이터 블록의 꼬리 또는 CRC가 인덱스와 일치하지 않습니다.");
        }

        private static byte[] ReadOstBlock(Stream fs, DataRef block)
        {
            const int maximumAllocationSize = 65536;
            var sizeWithTrailer = block.Length + OstBlockTrailerSize;
            var allocationSize = (sizeWithTrailer + OstBlockAlignment - 1) & ~(OstBlockAlignment - 1);
            if (block.Offset % OstBlockAlignment != 0 || allocationSize > maximumAllocationSize || block.Length == 0
                || block.InflatedLength is <= 0 or > maximumAllocationSize - OstBlockTrailerSize
                || !FitsInStream(fs, block.Offset, allocationSize))
                throw new XstException("OST 데이터 블록 범위가 올바르지 않습니다.");

            var bytes = new byte[block.Length];
            fs.Seek((long)block.Offset, SeekOrigin.Begin);
            fs.ReadExactly(bytes);
            fs.Seek((long)block.Offset + allocationSize - OstBlockTrailerSize, SeekOrigin.Begin);
            var trailer = Map.ReadType<BLOCKTRAILERUnicode4K>(fs);
            if (trailer.cb != block.Length || trailer.cbInflated != block.InflatedLength || trailer.bid != block.Key
                || trailer.wSig != Signature(block.Offset, block.Key) || trailer.dwCRC != CalculateWeakCrc(bytes))
                throw new XstException("OST 데이터 블록의 꼬리 또는 CRC가 인덱스와 일치하지 않습니다.");
            return bytes;
        }

        private static bool FitsInStream(Stream fs, ulong offset, int length) =>
            fs.Length >= length && offset <= (ulong)(fs.Length - length);

        private static ushort Signature(ulong offset, ulong bid)
        {
            var value = offset ^ bid;
            return (ushort)(value ^ (value >> 16));
        }

        internal static uint CalculateWeakCrc(ReadOnlySpan<byte> bytes)
        {
            var crc = 0u;
            foreach (var value in bytes)
                crc = (crc >> 8) ^ CrcTable[(crc ^ value) & 0xff];
            return crc;
        }

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint index = 0; index < (uint)table.Length; index++)
            {
                var crc = index;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
                table[index] = crc;
            }
            return table;
        }

        // When a data block has a subnode, it can be a simple node, or a two-level tree
        // This reads a sub node and builds suitable data structures, so that we can later access data held in it
        private void ReadSubNodeBtreeUnicode(Stream fs, UInt64 subDataBid, TreeIntermediate parent, int depth = 0)
        {
            if (depth > 1)
                throw new XstException("Invalid PST subnode nesting.");
            var rb = LookupDataBlock(subDataBid);
            if (rb == null)
                throw new XstException("SubNode data block does not exist");

            int read;
            byte[] buffer = ReadAndDecompress(fs, rb, out read);
            var sl = Map.MapType<SLBLOCKUnicode>(buffer);

            if (sl.btype != EbType.bTypeS || sl.cLevel > 1 || (depth != 0 && sl.cLevel != 0))
                throw new XstException("Invalid PST subnode block.");
            if (sl.cLevel > 0)
            {
                var rgbid = Map.MapArray<SIENTRYUnicode>(buffer, Marshal.SizeOf(typeof(SLBLOCKUnicode)), sl.cEnt);

                foreach (var sie in rgbid)
                {
                    var inter = new TreeIntermediate { Key = (sie.nid & 0xffffffff) };
                    parent.Children.Add(inter);

                    // Read child nodes in tree
                    ReadSubNodeBtreeUnicode(fs, sie.bid, inter, depth + 1);
                }
            }
            else
            {
                var rgbid = Map.MapArray<SLENTRYUnicode>(buffer, Marshal.SizeOf(typeof(SLBLOCKUnicode)), sl.cEnt);

                foreach (var sle in rgbid)
                {
                    // Only use low order dword of nid
                    var nb = new Node { Key = (sle.nid & 0xffffffff), DataBid = sle.bidData, SubDataBid = sle.bidSub };
                    parent.Children.Add(nb);
                }
            }
        }

        private void ReadSubNodeBtreeANSI(Stream fs, UInt64 subDataBid, TreeIntermediate parent, int depth = 0)
        {
            if (depth > 1)
                throw new XstException("Invalid PST subnode nesting.");
            var rb = LookupDataBlock(subDataBid);
            if (rb == null)
                throw new XstException("SubNode data block does not exist");

            int read;
            byte[] buffer = ReadAndDecompress(fs, rb, out read);
            var sl = Map.MapType<SLBLOCKANSI>(buffer);

            if (sl.btype != EbType.bTypeS || sl.cLevel > 1 || (depth != 0 && sl.cLevel != 0))
                throw new XstException("Invalid PST subnode block.");
            if (sl.cLevel > 0)
            {
                var rgbid = Map.MapArray<SIENTRYANSI>(buffer, Marshal.SizeOf(typeof(SLBLOCKANSI)), sl.cEnt);

                foreach (var sie in rgbid)
                {
                    var inter = new TreeIntermediate { Key = (sie.nid & 0xffffffff) };
                    parent.Children.Add(inter);

                    // Read child nodes in tree
                    ReadSubNodeBtreeANSI(fs, sie.bid, inter, depth + 1);
                }
            }
            else
            {
                var rgbid = Map.MapArray<SLENTRYANSI>(buffer, Marshal.SizeOf(typeof(SLBLOCKANSI)), sl.cEnt);

                foreach (var sle in rgbid)
                {
                    var nb = new Node { Key = sle.nid, DataBid = sle.bidData, SubDataBid = sle.bidSub };
                    parent.Children.Add(nb);
                }
            }
        }
        #endregion
    }
}
