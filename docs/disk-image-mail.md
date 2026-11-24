# Open mail from a disk image

1. Select **Disk image…**, open the image, and choose a readable NTFS volume.
2. The app looks in Outlook folders, the Desktop, and Downloads. Use **Search entire volume** if the files are stored elsewhere.
3. Check the files you need and select **Extract selected files…**. Choose an output folder, then select **Analyze**.

The search finds supported formats such as EML, MSG, MBOX, PST, OST, DBX, EMLX, and OLM. It includes hidden and system files and lists hard links to the same NTFS file as a single candidate. PST and OST files over 2 GiB appear in the results with extraction disabled.

See the image module documentation for RAW, E01, Ex01, and virtual disk variants. To open E01 or Ex01, keep `NtfsTracer.libewf.dll` next to the executable. This workflow covers files present on readable NTFS volumes. Locked BitLocker volumes, logical EWF, deleted-file recovery, and the new Outlook cache are outside its scope. [Image formats and limits](../third_party/ntfs-tracer/modules/EvidenceImage/README.md)

## Extraction output

The output folder's `manifest.json` records SHA-256 hashes for the image components and extracted files, along with NTFS file references. When analysis starts, the app checks the extracted files against the hashes in the manifest. Under **Message information → Evidence source**, you can see the image path, volume start offset, and path of the file inside the image.

The verification compared candidate paths and MFT record numbers with `fls` from The Sleuth Kit 4.15.0 on synthetic RAW and E01 images and a 22 GiB RAW test image. All 304 candidates in the large RAW image matched. EML and PST files extracted from the synthetic images matched the bytes and SHA-256 hashes obtained with `icat`. [Disk-image UI test](../tools/FlowSmoke/Program.DiskImage.cs) · [The Sleuth Kit 4.15.0](https://github.com/sleuthkit/sleuthkit/releases/tag/sleuthkit-4.15.0)
