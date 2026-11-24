use std::io;

pub const MAX_DECODED: usize = 16 * 1024 * 1024;
const DICTIONARY_SIZE: usize = 4096;
const INITIAL: &[u8; 207] = b"{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}\
{\\f0\\fnil \\froman \\fswiss \\fmodern \\fscript \\fdecor MS Sans SerifSymbolArial\
Times New RomanCourier{\\colortbl\\red0\\green0\\blue0\r\n\\par \
\\pard\\plain\\f0\\fs20\\b\\i\\u\\tab\\tx";

const fn crc_table() -> [u32; 256] {
    let mut table = [0; 256];
    let mut index = 0;
    while index < table.len() {
        let mut word = index as u32;
        let mut count = 0;
        while count < 8 {
            word = (word >> 1) ^ ((word & 1) * 0xedb88320);
            count += 1;
        }
        table[index] = word;
        index += 1;
    }
    table
}

const CRC: [u32; 256] = crc_table();

fn invalid(detail: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, detail)
}

pub struct Decoded {
    pub bytes: Vec<u8>,
    pub declared_bytes: usize,
}

pub fn decompress(bytes: &[u8]) -> io::Result<Decoded> {
    let (header, payload) = bytes
        .split_at_checked(16)
        .ok_or_else(|| invalid("RTF header is truncated."))?;
    let words = header.as_chunks::<4>().0;
    let stored = u32::from_le_bytes(words[0]) as usize;
    let size = u32::from_le_bytes(words[1]) as usize;
    if stored != bytes.len() - 4 {
        return Err(invalid("RTF stored length is invalid."));
    }
    let decoded = match &words[2] {
        b"MELA" if payload.len() <= MAX_DECODED => payload.to_vec(),
        b"LZFu" if size <= MAX_DECODED => {
            let checksum = payload.iter().fold(0u32, |crc, &byte| {
                (crc >> 8) ^ CRC[(crc as u8 ^ byte) as usize]
            });
            if checksum != u32::from_le_bytes(words[3]) {
                return Err(invalid("RTF checksum does not match."));
            }
            decode_lz(payload, size)?
        }
        _ => {
            return Err(invalid(
                "RTF compression type or decoded length is invalid.",
            ));
        }
    };
    Ok(Decoded {
        bytes: decoded,
        declared_bytes: size,
    })
}

fn decode_lz(payload: &[u8], size: usize) -> io::Result<Vec<u8>> {
    let mut dictionary = [0; DICTIONARY_SIZE];
    dictionary[..INITIAL.len()].copy_from_slice(INITIAL);
    let mut written = INITIAL.len();
    let mut out = Vec::with_capacity(size);
    let mut tokens = payload.iter().copied();
    while let Some(control) = tokens.next() {
        for bit in 0..8 {
            let first = tokens
                .next()
                .ok_or_else(|| invalid("RTF token is truncated."))?;
            if control & (1 << bit) == 0 {
                append(first, size, &mut out, &mut dictionary, &mut written)?;
                continue;
            }
            let second = tokens
                .next()
                .ok_or_else(|| invalid("RTF dictionary reference is truncated."))?;
            let reference = u16::from_be_bytes([first, second]) as usize;
            let read = reference >> 4;
            if read == written % DICTIONARY_SIZE {
                return if out.len() == size {
                    Ok(out)
                } else {
                    Err(invalid(
                        "RTF end marker precedes the declared decoded length.",
                    ))
                };
            }
            for offset in 0..(reference & 15) + 2 {
                let index = (read + offset) % DICTIONARY_SIZE;
                if index >= written {
                    return Err(invalid("RTF dictionary reference is uninitialised."));
                }
                append(
                    dictionary[index],
                    size,
                    &mut out,
                    &mut dictionary,
                    &mut written,
                )?;
            }
        }
    }
    Err(invalid("RTF dictionary end marker is absent."))
}

fn append(
    byte: u8,
    size: usize,
    out: &mut Vec<u8>,
    dictionary: &mut [u8; DICTIONARY_SIZE],
    written: &mut usize,
) -> io::Result<()> {
    if out.len() == size {
        return Err(invalid("RTF decoded data exceeds its declared length."));
    }
    out.push(byte);
    dictionary[*written % DICTIONARY_SIZE] = byte;
    *written += 1;
    Ok(())
}
