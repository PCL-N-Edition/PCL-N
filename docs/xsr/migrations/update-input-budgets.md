# Update archive admission (SEC-03)

Update ZIP/TAR extraction and scatter members share the actual-byte copy primitive used
by modpack import. The primitive lives in Services.Files, independent of either product.
Each member must match its declaration, stays within a 512 MiB default limit,
and consumes an 8 GiB transaction budget before bytes are written. Full archives also
limit input size and entry count. Limits may be lowered for tests and host policy.
Archive metadata is an early rejection, never a substitute for stream accounting.
Failed extraction cannot return a verified inventory; partial member files are removed.
External hdiff output verification remains a separate boundary from archive extraction.
