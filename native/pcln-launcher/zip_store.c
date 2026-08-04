/*
 * Minimal ZIP extractor for the C bootstrap launcher.
 * Supports compression method 0 (store) only — pack payload with store/no compression.
 * Zip-slip safe (rejects absolute / parent paths).
 */

#include "zip_store.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#  include <direct.h>
#  include <io.h>
#  define MKDIR(p) _mkdir(p)
#else
#  include <sys/stat.h>
#  include <sys/types.h>
#  include <unistd.h>
#  define MKDIR(p) mkdir((p), 0755)
#endif

#pragma pack(push, 1)
typedef struct {
    unsigned int signature; /* 0x06054b50 */
    unsigned short disk;
    unsigned short cd_disk;
    unsigned short cd_entries_disk;
    unsigned short cd_entries;
    unsigned int cd_size;
    unsigned int cd_offset;
    unsigned short comment_len;
} PclnEocd;

typedef struct {
    unsigned int signature; /* 0x02014b50 */
    unsigned short ver_made;
    unsigned short ver_need;
    unsigned short flags;
    unsigned short method;
    unsigned short mod_time;
    unsigned short mod_date;
    unsigned int crc32;
    unsigned int comp_size;
    unsigned int uncomp_size;
    unsigned short name_len;
    unsigned short extra_len;
    unsigned short comment_len;
    unsigned short disk_start;
    unsigned short int_attr;
    unsigned int ext_attr;
    unsigned int local_offset;
} PclnCdHeader;

typedef struct {
    unsigned int signature; /* 0x04034b50 */
    unsigned short ver_need;
    unsigned short flags;
    unsigned short method;
    unsigned short mod_time;
    unsigned short mod_date;
    unsigned int crc32;
    unsigned int comp_size;
    unsigned int uncomp_size;
    unsigned short name_len;
    unsigned short extra_len;
} PclnLocalHeader;
#pragma pack(pop)

static void set_err(char *err, size_t errLen, const char *msg)
{
    if (!err || errLen == 0)
        return;
    strncpy(err, msg, errLen - 1);
    err[errLen - 1] = 0;
}

static int ensure_parent_dirs(char *path)
{
    char *p = path;
    if (!p || !*p)
        return -1;
#if defined(_WIN32)
    if (p[0] && p[1] == ':')
        p += 2;
#endif
    if (*p == '/' || *p == '\\')
        p++;
    for (; *p; p++)
    {
        if (*p == '/' || *p == '\\')
        {
            char c = *p;
            *p = 0;
            MKDIR(path);
            *p = c;
        }
    }
    return 0;
}

static int path_is_unsafe(const char *name, size_t len)
{
    size_t i;
    if (len == 0)
        return 1;
    if (name[0] == '/' || name[0] == '\\')
        return 1;
    if (len >= 2 && name[1] == ':')
        return 1;
    for (i = 0; i + 1 < len; i++)
    {
        if (name[i] == '.' && name[i + 1] == '.')
        {
            /* ".." segment */
            int left_ok = (i == 0) || name[i - 1] == '/' || name[i - 1] == '\\';
            int right_ok = (i + 2 >= len) || name[i + 2] == '/' || name[i + 2] == '\\';
            if (left_ok && right_ok)
                return 1;
        }
    }
    return 0;
}

static int join_path(char *out, size_t outLen, const char *dir, const char *name, size_t nameLen)
{
    size_t dlen = strlen(dir);
    size_t i;
    if (dlen + 1 + nameLen + 1 > outLen)
        return -1;
    memcpy(out, dir, dlen);
    out[dlen] = '/';
    for (i = 0; i < nameLen; i++)
    {
        char c = name[i];
        out[dlen + 1 + i] = (c == '\\') ? '/' : c;
    }
    out[dlen + 1 + nameLen] = 0;
    return 0;
}

int pcln_zip_extract(const char *zipPath, const char *destDir, char *err, size_t errLen)
{
    FILE *fp;
    long fileSize;
    unsigned char *map = NULL;
    long eocdOff = -1;
    PclnEocd eocd;
    unsigned int i;
    long off;

    if (!zipPath || !destDir)
    {
        set_err(err, errLen, "null path");
        return -1;
    }

    fp = fopen(zipPath, "rb");
    if (!fp)
    {
        set_err(err, errLen, "cannot open zip");
        return -1;
    }
    if (fseek(fp, 0, SEEK_END) != 0)
    {
        fclose(fp);
        set_err(err, errLen, "seek end failed");
        return -1;
    }
    fileSize = ftell(fp);
    if (fileSize < (long)sizeof(PclnEocd) || fileSize > 512L * 1024L * 1024L)
    {
        fclose(fp);
        set_err(err, errLen, "zip size invalid");
        return -1;
    }
    map = (unsigned char *)malloc((size_t)fileSize);
    if (!map)
    {
        fclose(fp);
        set_err(err, errLen, "oom");
        return -1;
    }
    if (fseek(fp, 0, SEEK_SET) != 0 ||
        fread(map, 1, (size_t)fileSize, fp) != (size_t)fileSize)
    {
        free(map);
        fclose(fp);
        set_err(err, errLen, "read zip failed");
        return -1;
    }
    fclose(fp);

    /* Scan EOCD from end (max 64k comment). */
    {
        long start = fileSize - 22;
        long minOff = fileSize - 22 - 65535;
        if (minOff < 0)
            minOff = 0;
        for (off = start; off >= minOff; off--)
        {
            if (map[off] == 0x50 && map[off + 1] == 0x4b &&
                map[off + 2] == 0x05 && map[off + 3] == 0x06)
            {
                eocdOff = off;
                break;
            }
        }
    }
    if (eocdOff < 0)
    {
        free(map);
        set_err(err, errLen, "EOCD not found");
        return -1;
    }
    memcpy(&eocd, map + eocdOff, sizeof(eocd));
    if (eocd.cd_offset + eocd.cd_size > (unsigned int)fileSize)
    {
        free(map);
        set_err(err, errLen, "central directory out of range");
        return -1;
    }

    MKDIR(destDir);
    off = (long)eocd.cd_offset;
    for (i = 0; i < eocd.cd_entries; i++)
    {
        PclnCdHeader cd;
        PclnLocalHeader lh;
        const char *name;
        char outPath[2048];
        long dataOff;
        FILE *out;
        size_t written;

        if (off + (long)sizeof(cd) > fileSize)
        {
            free(map);
            set_err(err, errLen, "truncated central header");
            return -1;
        }
        memcpy(&cd, map + off, sizeof(cd));
        if (cd.signature != 0x02014b50u)
        {
            free(map);
            set_err(err, errLen, "bad central signature");
            return -1;
        }
        name = (const char *)(map + off + (long)sizeof(cd));
        if (path_is_unsafe(name, cd.name_len))
        {
            free(map);
            set_err(err, errLen, "unsafe zip path");
            return -1;
        }
        if (join_path(outPath, sizeof(outPath), destDir, name, cd.name_len) != 0)
        {
            free(map);
            set_err(err, errLen, "path too long");
            return -1;
        }

        /* Directory entry */
        if (cd.name_len > 0 &&
            (name[cd.name_len - 1] == '/' || name[cd.name_len - 1] == '\\'))
        {
            ensure_parent_dirs(outPath);
            MKDIR(outPath);
            off += (long)sizeof(cd) + cd.name_len + cd.extra_len + cd.comment_len;
            continue;
        }

        if (cd.method != 0)
        {
            free(map);
            set_err(err, errLen, "only store (method 0) supported; repack payload with -0");
            return -1;
        }
        if (cd.local_offset + sizeof(lh) > (unsigned int)fileSize)
        {
            free(map);
            set_err(err, errLen, "local header OOB");
            return -1;
        }
        memcpy(&lh, map + cd.local_offset, sizeof(lh));
        if (lh.signature != 0x04034b50u)
        {
            free(map);
            set_err(err, errLen, "bad local signature");
            return -1;
        }
        dataOff = (long)cd.local_offset + (long)sizeof(lh) + lh.name_len + lh.extra_len;
        if (dataOff + (long)cd.comp_size > fileSize)
        {
            free(map);
            set_err(err, errLen, "payload OOB");
            return -1;
        }

        ensure_parent_dirs(outPath);
        out = fopen(outPath, "wb");
        if (!out)
        {
            free(map);
            set_err(err, errLen, "cannot create output file");
            return -1;
        }
        written = fwrite(map + dataOff, 1, cd.comp_size, out);
        fclose(out);
        if (written != cd.comp_size)
        {
            free(map);
            set_err(err, errLen, "short write");
            return -1;
        }

        off += (long)sizeof(cd) + cd.name_len + cd.extra_len + cd.comment_len;
    }

    free(map);
    return 0;
}
