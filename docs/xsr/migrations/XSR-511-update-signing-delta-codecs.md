# XSR-511 update signature and delta codecs

## Outcome

The update family gains its two trust/integrity codecs: detached GPG signature verification
over the pinned release key, and the managed VCDIFF (RFC 3284) decoder for protocol v2 block
deltas. Both are strict decoders with hard failure semantics — the update flow treats any
failure as "untrusted" and falls back to the full signed package.

## Locked contract

- Signature verification: `IUpdateSignatureVerifier.VerifyAsync(content, signature)` throws
  `InvalidDataException` on every failure shape — invalid detached signature, key not in the
  supplied keyring, fingerprint mismatch, or a signature that does not verify. The armored
  public key is supplied by the composition root (no embedded resource, no magic lookup);
  the expected fingerprint defaults to the pinned PCL N release key
  (`5701218D…A273AEE`), so only that key can authorize an update even when a foreign
  signature is well-formed. The small signature stream is buffered (armored decoding probes
  and rewinds); the content is verified streaming through a 128 KiB buffer.
- VCDIFF: `UpdateVcdiff.Decode/TryDecode` accepts the RFC 3284 default code table only.
  Secondary compression (VCD_DECOMPRESS) and custom code tables (VCD_CODETABLE) are refused;
  application headers parse and skip; target-as-source windows are refused; source windows
  are range-checked against the declared source; every integer is the RFC 7-bit varint;
  section lengths must sum exactly to the window. ADD/RUN/COPY semantics follow the RFC
  including size-zero-in-stream encoding, overlapping COPY (read-back of freshly written
  target bytes), and the near/same address cache (s_near=4, s_same=3). `TryDecode` converts
  every decode failure into a plain false. The algorithm identifier stays
  `vcdiff-rfc3284` — the value blockmap deltas declare.
- Signing chain placement: the verifier authenticates full packages, block maps, and patch
  bundles before any of them is applied; a failed verification can only route the updater to
  the next source or the full package, never to "install anyway".

## Deliberate scope

The key distribution story (how the pinned public key reaches the composition root) is a
release-pipeline decision and lands with the installer unit; the codecs and the pinning
model are frozen here. BouncyCastle.Cryptography stays at the legacy version 2.6.2.

## Verification

`tests/PCL.Services.Tests` (88 executable tests, 4 new) covers: a hand-built VCDIFF window
exercising ADD, COPY from source (SELF addressing), and RUN with byte-exact output; an
empty-source window with an overlapping HERE-mode COPY; and rejection of short headers,
secondary compression, custom code tables, truncated varints, and section overflows. GPG
verification runs end to end against generated test keys: genuine detached signatures pass;
tampered payloads fail the signature check; well-formed signatures from unknown keys are
refused as unauthorized; a correct signature under the pinned default fingerprint is refused
when the key does not match; and garbage input is rejected as invalid. Runs under CoreCLR
and NativeAOT in CI.
