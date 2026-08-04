/* Minimal ZIP reader: store (method 0) only. pack-payload.ps1 re-encodes AOT zips. */
#ifndef PCLN_ZIP_STORE_H
#define PCLN_ZIP_STORE_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Extract zipPath into destDir. Returns 0 on success. */
int pcln_zip_extract(const char *zipPath, const char *destDir, char *err, size_t errLen);

#ifdef __cplusplus
}
#endif

#endif
