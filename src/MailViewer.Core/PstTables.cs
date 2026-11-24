using System.Buffers.Binary;
using XstReader;

namespace MailViewer.Core;

internal sealed class PstTables(NDB database, Stream input, CancellationToken ct)
{
    private readonly NDB database = database;
    private readonly Stream input = input;
    private readonly CancellationToken ct = ct;

    internal readonly record struct Property(ushort Id, ushort Type, uint Value)
    {
        public uint Tag => ((uint)Id << 16) | Type;
    }

    internal sealed class PropertyContext(
        PstTables owner, Heap heap, BTree<Node>? subnodes, IReadOnlyDictionary<ushort, Property> properties)
    {
        public BTree<Node>? Subnodes { get; } = subnodes;

        public Property? Find(ushort id) => properties.TryGetValue(id, out var property) ? property : null;

        public byte[] ReadBytes(Property property, int limit)
        {
            using var output = new MemoryStream();
            foreach (var block in Blocks(property))
            {
                if (output.Length + block.Length > limit)
                    throw new InvalidDataException("PST 속성 크기가 판독 한도를 넘습니다.");
                output.Write(block.Span);
            }
            return output.ToArray();
        }

        public byte[] ReadRange(Property property, long inspectedLength, ByteRange range)
        {
            var output = new byte[checked((int)range.Length)];
            long position = 0;
            var written = 0;
            foreach (var block in Blocks(property))
            {
                var blockEnd = position + block.Length;
                if (blockEnd > inspectedLength)
                    throw new InvalidDataException("PST 속성 크기가 확인한 값과 다릅니다.");
                var start = Math.Max(position, range.Offset);
                var end = Math.Min(blockEnd, range.End);
                if (end > start)
                {
                    var length = (int)(end - start);
                    block.Span.Slice((int)(start - position), length).CopyTo(output.AsSpan(written));
                    written += length;
                }
                // Reinspection uses the same locked evidence. A preview only needs its requested
                // range; a complete retrieval also checks the entire block chain and total length.
                if (written == output.Length && range.End < inspectedLength)
                    return output;
                position = blockEnd;
            }
            if (position != inspectedLength || written != output.Length)
                throw new InvalidDataException("확인한 PST 속성 범위를 모두 다시 읽지 못했습니다.");
            return output;
        }

        private IEnumerable<ReadOnlyMemory<byte>> Blocks(Property property)
        {
            owner.ct.ThrowIfCancellationRequested();
            if (property.Value == 0)
                yield break;
            if ((property.Value & 0x1f) == 0)
            {
                yield return heap.Get(property.Value);
                yield break;
            }
            if ((EnidType)(property.Value & 0x1f) != EnidType.LTP || Subnodes is null)
                throw new InvalidDataException("PST 속성 값이 가리키는 하위 노드를 확인할 수 없습니다.");
            var node = NDB.LookupSubNode(Subnodes, new NID(property.Value));
            if (node is null || node.SubDataBid != 0)
                throw new InvalidDataException("PST 속성 값이 가리키는 하위 노드를 읽을 수 없습니다.");
            foreach (var block in owner.database.ReadDataBlocks(owner.input, node.DataBid))
            {
                owner.ct.ThrowIfCancellationRequested();
                yield return block;
            }
        }
    }

    public Node? FindNode(uint id) => database.LookupNode(new NID(id));

    public PropertyContext ReadProperties(Node node)
    {
        var heap = ReadHeap(node, 0xbc);
        var properties = new Dictionary<ushort, Property>();
        foreach (var leaf in heap.ReadIndex(heap.UserRoot, 2, 6, ct))
        {
            var bytes = leaf.Span;
            for (var offset = 0; offset < bytes.Length; offset += 8)
            {
                var id = U16(bytes[offset..]);
                var prop = new Property(id, U16(bytes[(offset + 2)..]), U32(bytes[(offset + 4)..]));
                if (!properties.TryAdd(id, prop))
                    throw new InvalidDataException("PST 속성 컨텍스트에 같은 속성 ID가 중복됩니다.");
            }
        }
        return new PropertyContext(this, heap, ReadSubnodes(node), properties);
    }

    public static Node? FindSubnode(BTree<Node>? tree, uint id) =>
        tree is null ? null : NDB.LookupSubNode(tree, new NID(id));

    public uint[] ReadRowIds(Node? table)
    {
        if (table is null)
            return [];
        var heap = ReadHeap(table, 0x7c);
        var context = heap.Get(heap.UserRoot).Span;
        if (context.Length < 18 || context[0] != 0x7c)
            throw new InvalidDataException("PST 테이블 컨텍스트 머리글이 올바르지 않습니다.");
        var result = new List<(uint Id, uint Position)>();
        var seenIds = new HashSet<uint>();
        var seenPositions = new HashSet<uint>();
        const int idSize = sizeof(uint);
        int positionSize = database.IsUnicode ? sizeof(uint) : sizeof(ushort);
        int rowIndexSize = idSize + positionSize;
        foreach (var leaf in heap.ReadIndex(U32(context[10..]), idSize, positionSize, ct))
        {
            var bytes = leaf.Span;
            for (var offset = 0; offset < bytes.Length; offset += rowIndexSize)
            {
                var id = U32(bytes[offset..]);
                var positionField = bytes[(offset + idSize)..];
                uint position = database.IsUnicode ? U32(positionField) : U16(positionField);
                if (!seenIds.Add(id) || !seenPositions.Add(position) || result.Count >= 250_000)
                    throw new InvalidDataException("PST 테이블 행 인덱스가 중복되었거나 판독 한도를 넘습니다.");
                result.Add((id, position));
            }
        }
        return result.OrderBy(row => row.Position).Select(row => row.Id).ToArray();
    }

    private BTree<Node>? ReadSubnodes(Node node) =>
        node.SubDataBid == 0 ? null : database.ReadSubNodeBtree(input, node.SubDataBid);

    private Heap ReadHeap(Node node, byte client) =>
        new(database.ReadDataBlocks(input, node.DataBid).ToArray(), client);

    private static ushort U16(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt16LittleEndian(bytes);
    private static uint U32(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32LittleEndian(bytes);

    internal sealed class Heap
    {
        private readonly byte[][] pages;

        public Heap(byte[][] pages, byte client)
        {
            this.pages = pages;
            if (pages.Length == 0 || pages[0].Length < 8 || pages[0][2] != 0xec || pages[0][3] != client)
                throw new InvalidDataException("PST Heap-on-Node 머리글이 올바르지 않습니다.");
            UserRoot = U32(pages[0].AsSpan(4));
        }

        public uint UserRoot { get; }

        public ReadOnlyMemory<byte> Get(uint hid)
        {
            if ((hid & 0x1f) != 0)
                throw new InvalidDataException("PST 힙 주소가 올바르지 않습니다.");
            var index = (int)((hid >> 5) & 0x7ff);
            var pageIndex = (int)(hid >> 16);
            if (index == 0 || pageIndex >= pages.Length || pages[pageIndex].Length < 2)
                throw new InvalidDataException("PST 힙 할당을 찾을 수 없습니다.");
            var page = pages[pageIndex];
            var mapOffset = U16(page);
            if (mapOffset > page.Length - 6)
                throw new InvalidDataException("PST 힙 페이지 맵의 위치가 올바르지 않습니다.");
            var count = U16(page.AsSpan(mapOffset));
            if (index > count || 6 + count * 2 > page.Length - mapOffset)
                throw new InvalidDataException("PST 힙 페이지 맵의 길이가 올바르지 않습니다.");
            var previous = U16(page.AsSpan(mapOffset + 4 + (index - 1) * 2));
            var next = U16(page.AsSpan(mapOffset + 4 + index * 2));
            if (next < previous || next > mapOffset)
                throw new InvalidDataException("PST 힙 할당 범위가 올바르지 않습니다.");
            return page.AsMemory(previous, next - previous);
        }

        public IEnumerable<ReadOnlyMemory<byte>> ReadIndex(uint hid, int keyBytes, int dataBytes, CancellationToken ct)
        {
            var root = Get(hid).Span;
            if (root.Length < 8 || root[0] != 0xb5 || root[1] != keyBytes || root[2] != dataBytes || root[3] > 8)
                throw new InvalidDataException("PST 힙 트리 인덱스 머리글이 올바르지 않습니다.");

            var pending = new Stack<(uint Hid, int Level)>();
            var seen = new HashSet<uint>();
            var rootNode = U32(root[4..]);
            if (rootNode != 0)
                pending.Push((rootNode, root[3]));
            while (pending.TryPop(out var current))
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Add(current.Hid) || seen.Count > 250_000)
                    throw new InvalidDataException("PST 힙 트리에 반복 노드가 있거나 판독 한도를 넘습니다.");
                var bytes = Get(current.Hid);
                var stride = keyBytes + (current.Level == 0 ? dataBytes : 4);
                if (bytes.Length % stride != 0)
                    throw new InvalidDataException("PST 힙 트리 인덱스의 항목 길이가 올바르지 않습니다.");
                if (current.Level == 0)
                {
                    yield return bytes;
                    continue;
                }
                for (var offset = bytes.Length - stride; offset >= 0; offset -= stride)
                    pending.Push((U32(bytes.Span[(offset + keyBytes)..]), current.Level - 1));
            }
        }
    }
}
