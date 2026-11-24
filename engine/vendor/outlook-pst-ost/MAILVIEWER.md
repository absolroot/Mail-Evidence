# Mail Viewer changes

This copy is the MIT-licensed outlook-pst-ost crate version 1.3.0. Its
published source identifies commit 84f8fe977968593fd33b17c16aaa7fb80369cfba.
The original license, publishing metadata and upstream vendor notes are in
this directory.

- For an OST client signature, use the 512-byte Unicode layout for version
  21 and accept client version 12 on the 512-byte layout. Check the original
  header CRCs through the existing reader.
- Expose the stored MAPI transport-header bytes before string decoding drops
  terminal bytes. The 1 MiB limit includes heap and subnode-backed values.
  Read errors are separate from absent properties.
- Bound individual external property and attachment reads to 128 MiB.

[Published package](https://crates.io/crates/outlook-pst-ost/1.3.0) ·
[Microsoft implementation](https://github.com/microsoft/outlook-pst-rs)
