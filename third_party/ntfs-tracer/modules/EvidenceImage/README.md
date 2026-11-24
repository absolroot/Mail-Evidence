# EvidenceImage

EvidenceImage is the .NET module NTFS Tracer uses to read disk images and logical evidence files and to extract NTFS artifacts. This repository includes the module source and the notices needed to build it.

For a physical disk image, select an NTFS volume. For logical EWF, select files from the acquisition list. The module writes extracted files and their manifest to the chosen output directory.

| Input | Supported variants |
| --- | --- |
| RAW | Single NTFS volume, MBR or GPT disk, or RAW image split into sequential three-digit extensions |
| EWF | E01 and Ex01 physical containers, and L01 and Lx01 logical containers. Select the first segment. Encryption, bzip2, missing segments, and images with recorded acquisition errors are unsupported. |
| QCOW and QCOW2 | The current disk contents of standalone QCOW1 and QCOW2 versions 2 and 3 using standard L2 tables. External backing files, encryption, external data, and extended L2 are unsupported. |
| VHD and VHDX | Fixed and dynamic VHD, and standalone VHDX |
| VMDK | Single sparse extent, stream-optimized image, or a descriptor referencing a FLAT extent in the same directory. Parent disk chains and extents outside the directory are unsupported. |
| AFF | Single AFF1 image with uncompressed, zlib, or zero pages |

See [build the native EWF library](Logical/Native/README.md) and [DiscUtils notices](Notices/DiscUtils.txt) for the disk and NTFS reader.
