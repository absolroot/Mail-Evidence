"""Generate explicitly derived OST layout and encoding controls from pinned sources."""

import argparse
from hashlib import sha256
from pathlib import Path
import re
from struct import pack_into, unpack_from
from zlib import crc32


HERE = Path(__file__).resolve().parent
SOURCE_NAME = "SampleOstFile.ost"
SOURCE_SHA256 = "5ff8bc133935a03fad8241f271a604e1cc41e36640d6538d6bcc9bd39e6d9866"
OUTPUT_NAME = "generated-aspose-v23-cyclic.ost"
ANSI_SOURCE_SHA256 = "587a1ae2785eb218d7d42a98d43ded546aa72670908269d7b3ab25a711a91871"


def weak_crc(data):
    return crc32(data, 0xFFFFFFFF) ^ 0xFFFFFFFF


def tables():
    # The constants already shipped under XstReader's source notice are the
    # [MS-PST] 5.1 tables. Decode correctness is checked independently by libpff.
    crypt = HERE.parents[1] / "src/MailViewer.Core/ThirdParty/XstReader/Crypto.cs"
    source = crypt.read_text(encoding="utf-8")
    result = []
    for name in ("Cyclic1", "Cyclic2", "Compressible"):
        body = re.search(rf"\b{name}\s*=\s*new byte\[\]\s*{{([^}}]+)}}", source)[1]
        table = bytes(int(value, 16) for value in re.findall("0x[0-9a-fA-F]{2}", body))
        if len(table) != 256 or len(set(table)) != 256:
            raise ValueError(f"Invalid [MS-PST] lookup table {name}.")
        result.append(table)
    return result


def blocks(data):
    # [MS-PST] 2.2.2.5 and 2.2.2.7.7.3. Unicode ROOT/BREF
    # locate the BBT; each 512-byte page has a 16-byte PAGETRAILER.
    pending = [unpack_from("<Q", data, 240)[0]]
    visited = set()
    while pending:
        address = pending.pop()
        if address in visited or address % 512 or address + 512 > len(data):
            raise ValueError("Expected a distinct, in-range Unicode BBT page.")
        visited.add(address)
        page = data[address:address + 512]
        entries, capacity, stride, level = unpack_from("<4B", page, 488)
        if page[496:498] != b"\x80\x80" or weak_crc(page[:496]) != unpack_from("<I", page, 500)[0]:
            raise ValueError("The BBT page failed its type or CRC check.")
        if stride != 24 or not 0 < entries <= capacity or capacity * stride > 488:
            raise ValueError("Unexpected BBT entry geometry.")
        for entry in range(0, entries * stride, stride):
            if level:
                pending.append(unpack_from("<Q", page, entry + 16)[0])
            else:
                yield unpack_from("<QQH", page, entry)


def encode_cyclic(data, bid, lookups):
    right, shuffle, inverse = lookups
    salt = (bid ^ (bid >> 16)) & 0xFFFF
    result = bytearray(len(data))
    for index, value in enumerate(data):
        low, high = salt & 0xFF, salt >> 8
        stage = right[(value + low) & 0xFF]
        stage = shuffle[(stage + high) & 0xFF]
        result[index] = (inverse[(stage - high) & 0xFF] - low) & 0xFF
        salt = (salt + 1) & 0xFFFF
    return result


def create(source, destination):
    stored = bytearray(source.read_bytes())
    if sha256(stored).hexdigest() != SOURCE_SHA256 or stored[8:12] != b"SO\x17\x00" or stored[513] != 1:
        raise ValueError("Cyclic control requires the exact pinned Aspose OST source.")
    if destination.resolve() == source.resolve():
        raise ValueError("Select a separate destination for the generated control.")
    lookups = tables()
    count = 0
    for bid, offset, length in blocks(stored):
        if bid & 2:
            continue
        extent = (length + 16 + 63) & ~63
        trailer = offset + extent - 16
        original = stored[offset:offset + length]
        size, signature, checksum, trailer_bid = unpack_from("<HHIQ", stored, trailer)
        signature_key = offset ^ bid
        if (size, signature, checksum, trailer_bid) != (
            length, (signature_key ^ (signature_key >> 16)) & 0xFFFF, weak_crc(original), bid
        ):
            raise ValueError(f"Original external block {bid:#x} failed validation.")
        decoded = original.translate(lookups[2])
        encoded = encode_cyclic(decoded, bid & 0xFFFFFFFF, lookups)
        stored[offset:offset + length] = encoded
        pack_into("<I", stored, trailer + 4, weak_crc(encoded))
        count += 1
    stored[513] = 2
    pack_into("<I", stored, 524, weak_crc(stored[8:524]))
    save_control(destination, stored)
    return count, sha256(stored).hexdigest()


def wrap_ansi(source, destination, version):
    stored = bytearray(source.read_bytes())
    if sha256(stored).hexdigest() != ANSI_SOURCE_SHA256 or stored[8:12] != b"SM\x0e\x00" or stored[461] != 1:
        raise ValueError("ANSI control requires the exact pinned Microsoft PST SDK fixture.")
    if version not in (14, 15) or destination.resolve() == source.resolve():
        raise ValueError("Choose version 14 or 15 and a separate destination for the generated control.")
    if weak_crc(stored[8:479]) != unpack_from("<I", stored, 4)[0]:
        raise ValueError("The original ANSI header failed its CRC check.")
    stored[8:10] = b"SO"
    pack_into("<H", stored, 10, version)
    pack_into("<I", stored, 4, weak_crc(stored[8:479]))
    save_control(destination, stored)
    return sha256(stored).hexdigest()


def wrap_unicode21(source, destination):
    stored = bytearray(source.read_bytes())
    if sha256(stored).hexdigest() != SOURCE_SHA256 or destination.resolve() == source.resolve():
        raise ValueError("Unicode 21 control requires the exact pinned Aspose OST at a separate path.")
    pack_into("<H", stored, 10, 21)
    pack_into("<I", stored, 4, weak_crc(stored[8:479]))
    pack_into("<I", stored, 524, weak_crc(stored[8:524]))
    save_control(destination, stored)
    return sha256(stored).hexdigest()


def save_control(destination, data):
    if destination.exists():
        if destination.read_bytes() != data:
            raise ValueError(f"Generated destination differs from existing file {destination}.")
        return
    with destination.open("xb") as output:
        output.write(data)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=HERE / "ost" / SOURCE_NAME)
    parser.add_argument("--destination", type=Path, default=HERE / "ost" / OUTPUT_NAME)
    args = parser.parse_args()
    changed, digest = create(args.source, args.destination)
    print(f"{args.destination.name}  {changed} re-encoded blocks  {digest}")
