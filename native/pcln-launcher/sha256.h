/* Compact SHA-256 (public domain style). */
#ifndef PCLN_SHA256_H
#define PCLN_SHA256_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    unsigned long long bitlen;
    unsigned int state[8];
    unsigned char data[64];
    unsigned int datalen;
} pcln_sha256_ctx;

void pcln_sha256_init(pcln_sha256_ctx *ctx);
void pcln_sha256_update(pcln_sha256_ctx *ctx, const void *data, size_t len);
void pcln_sha256_final(pcln_sha256_ctx *ctx, unsigned char out[32]);

/* Hash file; write 64 hex chars + NUL to outHex (needs >= 65 bytes). Returns 0 ok. */
int pcln_sha256_file(const char *path, char *outHex, size_t outLen);

#ifdef __cplusplus
}
#endif

#endif
