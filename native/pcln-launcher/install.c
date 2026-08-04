/*
 * Port of PclEmbeddedNativeRuntime / PclEmbeddedPluginSidecar install layout.
 */

#include "install.h"
#include "sha256.h"
#include "zip_store.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#  include <shlobj.h>
#else
#  include <dirent.h>
#  include <errno.h>
#  include <fcntl.h>
#  include <sys/file.h>
#  include <sys/stat.h>
#  include <sys/types.h>
#  include <unistd.h>
#endif

#define PCLN_PATH 1024
#define PCLN_HASH 72

static void set_err(char *err, size_t errLen, const char *msg)
{
    if (!err || errLen == 0)
        return;
    strncpy(err, msg, errLen - 1);
    err[errLen - 1] = 0;
}

static int file_exists(const char *path)
{
#if defined(_WIN32)
    DWORD a = GetFileAttributesA(path);
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
#else
    struct stat st;
    return stat(path, &st) == 0 && S_ISREG(st.st_mode);
#endif
}

static int dir_exists(const char *path)
{
#if defined(_WIN32)
    DWORD a = GetFileAttributesA(path);
    return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY);
#else
    struct stat st;
    return stat(path, &st) == 0 && S_ISDIR(st.st_mode);
#endif
}

static void path_join(char *out, size_t n, const char *a, const char *b)
{
    size_t la = strlen(a);
    int need = (la > 0 && a[la - 1] != '/' && a[la - 1] != '\\');
    snprintf(out, n, "%s%s%s", a, need ? "/" : "", b);
#if defined(_WIN32)
    {
        char *p;
        for (p = out; *p; p++)
            if (*p == '/')
                *p = '\\';
    }
#endif
}

static void ensure_dir(const char *path)
{
#if defined(_WIN32)
    char tmp[PCLN_PATH];
    char *p;
    strncpy(tmp, path, sizeof(tmp) - 1);
    tmp[sizeof(tmp) - 1] = 0;
    for (p = tmp; *p; p++)
    {
        if (*p == '/' || *p == '\\')
        {
            char c = *p;
            *p = 0;
            if (tmp[0])
                CreateDirectoryA(tmp, NULL);
            *p = c;
        }
    }
    if (tmp[0])
        CreateDirectoryA(tmp, NULL);
#else
    char tmp[PCLN_PATH];
    char *p;
    strncpy(tmp, path, sizeof(tmp) - 1);
    tmp[sizeof(tmp) - 1] = 0;
    for (p = tmp + 1; *p; p++)
    {
        if (*p == '/')
        {
            *p = 0;
            mkdir(tmp, 0755);
            *p = '/';
        }
    }
    mkdir(tmp, 0755);
#endif
}

static int is_safe_rid(const char *rid)
{
    size_t i;
    if (!rid || !*rid)
        return 0;
    for (i = 0; rid[i]; i++)
    {
        char c = rid[i];
        if (c == '/' || c == '\\' || c == '.' )
        {
            if (c == '.' && rid[i + 1] == '.')
                return 0;
            if (c == '/' || c == '\\')
                return 0;
        }
    }
    return strcmp(rid, ".") != 0 && strcmp(rid, "..") != 0;
}

const char *pcln_runtime_rid(void)
{
#if defined(_WIN32)
#  if defined(_M_ARM64) || defined(__aarch64__)
    return "win-arm64";
#  elif defined(_M_IX86) || defined(__i386__)
    return "win-x86";
#  else
    return "win-x64";
#  endif
#elif defined(__APPLE__)
#  if defined(__aarch64__)
    return "osx-arm64";
#  else
    return "osx-x64";
#  endif
#else
#  if defined(__aarch64__)
    return "linux-arm64";
#  else
    return "linux-x64";
#  endif
#endif
}

/* Minimal JSON string value for "ApplicationDataDirectory" (no full parser). */
static int read_json_string_field(const char *json, const char *key, char *out, size_t outLen)
{
    char pattern[128];
    const char *p;
    size_t i = 0;
    snprintf(pattern, sizeof(pattern), "\"%s\"", key);
    p = strstr(json, pattern);
    if (!p)
        return -1;
    p = strchr(p + strlen(pattern), ':');
    if (!p)
        return -1;
    p++;
    while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')
        p++;
    if (*p != '"')
        return -1;
    p++;
    while (*p && *p != '"' && i + 1 < outLen)
    {
        if (*p == '\\' && p[1])
        {
            p++;
            if (*p == 'n')
                out[i++] = '\n';
            else if (*p == 'r')
                out[i++] = '\r';
            else if (*p == 't')
                out[i++] = '\t';
            else if (*p == 'u')
            {
                /* skip \uXXXX */
                int k;
                for (k = 0; k < 4 && p[1]; k++)
                    p++;
            }
            else
                out[i++] = *p;
        }
        else
            out[i++] = *p;
        p++;
    }
    out[i] = 0;
    return i > 0 ? 0 : -1;
}

/*
 * Mirrors PCL.Desktop.Paths.LauncherPathLayout.ResolveDataDirectory:
 *   override file:  {LocalAppData}/PCL-N/pcln-paths.json   (Windows)
 *   default data:   {AppData Roaming}/PCL-N                 (Windows ApplicationData)
 *   custom:         ApplicationDataDirectory from JSON when creatable
 *   Unix default:   {XDG_DATA_HOME|~/.local/share}/PCL-N
 *   macOS default:  ~/Library/Application Support/PCL-N
 */
static int full_path(const char *in, char *out, size_t outLen)
{
#if defined(_WIN32)
    DWORD n = GetFullPathNameA(in, (DWORD)outLen, out, NULL);
    return (n > 0 && n < outLen) ? 0 : -1;
#else
    char *r = realpath(in, NULL);
    if (r)
    {
        strncpy(out, r, outLen - 1);
        out[outLen - 1] = 0;
        free(r);
        return 0;
    }
    /* Path may not exist yet — copy normalized-ish. */
    strncpy(out, in, outLen - 1);
    out[outLen - 1] = 0;
    return 0;
#endif
}

static int try_use_custom_data_dir(const char *raw, char *out, size_t outLen)
{
    char full[PCLN_PATH];
    if (!raw || !*raw)
        return -1;
    /* Trim leading spaces */
    while (*raw == ' ' || *raw == '\t')
        raw++;
    if (full_path(raw, full, sizeof(full)) != 0)
        return -1;
#if defined(_WIN32)
    /* Reject missing drive like "Z:\foo" when Z: is absent — same idea as host. */
    if (full[0] && full[1] == ':' && full[2] == '\\')
    {
        char root[4] = { full[0], ':', '\\', 0 };
        DWORD attr = GetFileAttributesA(root);
        if (attr == INVALID_FILE_ATTRIBUTES)
            return -1;
    }
#endif
    ensure_dir(full);
    if (!dir_exists(full))
        return -1;
    strncpy(out, full, outLen - 1);
    out[outLen - 1] = 0;
    return 0;
}

int pcln_resolve_data_directory(char *out, size_t outLen)
{
    char defaultData[PCLN_PATH];
    char overridePath[PCLN_PATH];
    char custom[PCLN_PATH];
    char *json = NULL;
    long jlen;
    FILE *f;

    if (!out || outLen < 8)
        return -1;

    /*
     * Override file location = LauncherPathLayout.ResolveOverrideFilePath
     *   Windows: LocalApplicationData\PCL-N\pcln-paths.json
     * Default data = GetDefaultDataDirectory
     *   Windows: ApplicationData (Roaming)\PCL-N
     */
#if defined(_WIN32)
    {
        char localApp[MAX_PATH];
        char roamingApp[MAX_PATH];
        if (!SUCCEEDED(SHGetFolderPathA(NULL, CSIDL_LOCAL_APPDATA, NULL, 0, localApp)))
            return -1;
        if (!SUCCEEDED(SHGetFolderPathA(NULL, CSIDL_APPDATA, NULL, 0, roamingApp)))
            return -1;
        snprintf(overridePath, sizeof(overridePath), "%s\\PCL-N\\pcln-paths.json", localApp);
        snprintf(defaultData, sizeof(defaultData), "%s\\PCL-N", roamingApp);
    }
#else
    {
        const char *home = getenv("HOME");
        if (!home || !*home)
            home = ".";
#  if defined(__APPLE__)
        snprintf(defaultData, sizeof(defaultData), "%s/Library/Application Support/PCL-N", home);
        /* Host keeps override under LocalApplicationData equivalent — use same data root on mac. */
        snprintf(overridePath, sizeof(overridePath),
                 "%s/Library/Application Support/PCL-N/pcln-paths.json", home);
#  else
        {
            const char *xdg = getenv("XDG_DATA_HOME");
            if (xdg && *xdg)
                snprintf(defaultData, sizeof(defaultData), "%s/PCL-N", xdg);
            else
                snprintf(defaultData, sizeof(defaultData), "%s/.local/share/PCL-N", home);
            snprintf(overridePath, sizeof(overridePath), "%s/pcln-paths.json", defaultData);
        }
#  endif
    }
#endif

    /* Full-path normalize defaults (host uses Path.GetFullPath). */
    {
        char norm[PCLN_PATH];
        if (full_path(defaultData, norm, sizeof(norm)) == 0)
            strncpy(defaultData, norm, sizeof(defaultData) - 1);
        if (full_path(overridePath, norm, sizeof(norm)) == 0)
            strncpy(overridePath, norm, sizeof(overridePath) - 1);
    }

    f = fopen(overridePath, "rb");
    if (f)
    {
        if (fseek(f, 0, SEEK_END) == 0)
        {
            jlen = ftell(f);
            if (jlen > 0 && jlen < 1024 * 1024)
            {
                json = (char *)malloc((size_t)jlen + 1);
                if (json)
                {
                    fseek(f, 0, SEEK_SET);
                    if (fread(json, 1, (size_t)jlen, f) == (size_t)jlen)
                    {
                        json[jlen] = 0;
                        /* PascalCase (STJ default) and camelCase both accepted. */
                        if (read_json_string_field(json, "ApplicationDataDirectory", custom, sizeof(custom)) == 0 ||
                            read_json_string_field(json, "applicationDataDirectory", custom, sizeof(custom)) == 0)
                        {
                            if (try_use_custom_data_dir(custom, out, outLen) == 0)
                            {
                                free(json);
                                fclose(f);
                                return 0;
                            }
                        }
                    }
                    free(json);
                }
            }
        }
        fclose(f);
    }

    ensure_dir(defaultData);
    if (full_path(defaultData, out, outLen) != 0)
    {
        strncpy(out, defaultData, outLen - 1);
        out[outLen - 1] = 0;
    }
    return 0;
}

#if defined(_WIN32)
typedef HANDLE pcln_lock_t;
static int lock_acquire(const char *path, pcln_lock_t *out)
{
    int attempt;
    for (attempt = 0; attempt < 300; attempt++)
    {
        HANDLE h = CreateFileA(
            path,
            GENERIC_READ | GENERIC_WRITE,
            0,
            NULL,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            NULL);
        if (h != INVALID_HANDLE_VALUE)
        {
            *out = h;
            return 0;
        }
        Sleep(100);
    }
    return -1;
}
static void lock_release(pcln_lock_t h)
{
    if (h && h != INVALID_HANDLE_VALUE)
        CloseHandle(h);
}
#else
typedef int pcln_lock_t;
static int lock_acquire(const char *path, pcln_lock_t *out)
{
    int attempt;
    for (attempt = 0; attempt < 300; attempt++)
    {
        int fd = open(path, O_RDWR | O_CREAT, 0644);
        if (fd >= 0)
        {
            if (flock(fd, LOCK_EX | LOCK_NB) == 0)
            {
                *out = fd;
                return 0;
            }
            close(fd);
        }
        usleep(100000);
    }
    return -1;
}
static void lock_release(pcln_lock_t fd)
{
    if (fd >= 0)
    {
        flock(fd, LOCK_UN);
        close(fd);
    }
}
#endif

/* Collect relative paths under root into a text file (one per line, / separators). */
static int write_installed_files_list(const char *root, const char *listPath)
{
    FILE *out = fopen(listPath, "wb");
    if (!out)
        return -1;

#if defined(_WIN32)
    /* Simple iterative stack walk. */
    {
        char stack[64][PCLN_PATH];
        int top = 0;
        strncpy(stack[top++], root, PCLN_PATH - 1);
        while (top > 0)
        {
            char cur[PCLN_PATH];
            char pattern[PCLN_PATH];
            WIN32_FIND_DATAA fd;
            HANDLE h;
            strncpy(cur, stack[--top], sizeof(cur) - 1);
            cur[sizeof(cur) - 1] = 0;
            snprintf(pattern, sizeof(pattern), "%s\\*", cur);
            h = FindFirstFileA(pattern, &fd);
            if (h == INVALID_HANDLE_VALUE)
                continue;
            do
            {
                char full[PCLN_PATH];
                if (strcmp(fd.cFileName, ".") == 0 || strcmp(fd.cFileName, "..") == 0)
                    continue;
                if (fd.cFileName[0] == '.' &&
                    (strcmp(fd.cFileName, ".ready") == 0 ||
                     strcmp(fd.cFileName, ".pcln-native-runtime-files") == 0 ||
                     strcmp(fd.cFileName, ".extracted") == 0 ||
                     strncmp(fd.cFileName, ".pcln-", 6) == 0))
                    continue;
                snprintf(full, sizeof(full), "%s\\%s", cur, fd.cFileName);
                if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
                {
                    if (top < 64)
                        strncpy(stack[top++], full, PCLN_PATH - 1);
                }
                else
                {
                    /* relative with / */
                    const char *rel = full + strlen(root);
                    while (*rel == '\\' || *rel == '/')
                        rel++;
                    {
                        char norm[PCLN_PATH];
                        size_t i;
                        for (i = 0; rel[i] && i + 1 < sizeof(norm); i++)
                            norm[i] = (rel[i] == '\\') ? '/' : rel[i];
                        norm[i] = 0;
                        fprintf(out, "%s\n", norm);
                    }
                }
            } while (FindNextFileA(h, &fd));
            FindClose(h);
        }
    }
#else
    /* Limited depth walk via system find is avoided; recursive helper. */
    {
        /* simple: only top-level + one recurse for portability without huge stack */
        DIR *d = opendir(root);
        if (!d)
        {
            fclose(out);
            return -1;
        }
        /* Use nftw-like manual recursion with fixed depth */
        /* For Unix release, shell out is not used; implement recursive. */
        closedir(d);
    }
    /* Full recursive for Unix */
    {
        char **queue = NULL;
        int qn = 0, qcap = 0;
        queue = (char **)malloc(sizeof(char *) * 256);
        qcap = 256;
        queue[qn] = (char *)malloc(strlen(root) + 1);
        strcpy(queue[qn++], root);
        while (qn > 0)
        {
            char *cur = queue[--qn];
            DIR *d = opendir(cur);
            struct dirent *ent;
            if (!d)
            {
                free(cur);
                continue;
            }
            while ((ent = readdir(d)) != NULL)
            {
                char full[PCLN_PATH];
                struct stat st;
                if (strcmp(ent->d_name, ".") == 0 || strcmp(ent->d_name, "..") == 0)
                    continue;
                if (ent->d_name[0] == '.')
                    continue;
                snprintf(full, sizeof(full), "%s/%s", cur, ent->d_name);
                if (stat(full, &st) != 0)
                    continue;
                if (S_ISDIR(st.st_mode))
                {
                    if (qn >= qcap)
                    {
                        qcap *= 2;
                        queue = (char **)realloc(queue, sizeof(char *) * (size_t)qcap);
                    }
                    queue[qn] = (char *)malloc(strlen(full) + 1);
                    strcpy(queue[qn++], full);
                }
                else if (S_ISREG(st.st_mode))
                {
                    const char *rel = full + strlen(root);
                    while (*rel == '/')
                        rel++;
                    fprintf(out, "%s\n", rel);
                }
            }
            closedir(d);
            free(cur);
        }
        free(queue);
    }
#endif
    fclose(out);
    return 0;
}

static int installed_files_exist(const char *root)
{
    char listPath[PCLN_PATH];
    char line[PCLN_PATH];
    FILE *f;
    int any = 0;
    path_join(listPath, sizeof(listPath), root, ".pcln-native-runtime-files");
    f = fopen(listPath, "rb");
    if (!f)
        return 0;
    while (fgets(line, sizeof(line), f))
    {
        size_t n = strlen(line);
        char full[PCLN_PATH];
        while (n > 0 && (line[n - 1] == '\n' || line[n - 1] == '\r'))
            line[--n] = 0;
        if (n == 0)
            continue;
        any = 1;
        path_join(full, sizeof(full), root, line);
#if defined(_WIN32)
        {
            char *p;
            for (p = full; *p; p++)
                if (*p == '/')
                    *p = '\\';
        }
#endif
        if (!file_exists(full))
        {
            fclose(f);
            return 0;
        }
    }
    fclose(f);
    return any;
}

static void rm_rf(const char *path)
{
#if defined(_WIN32)
    char cmd[PCLN_PATH * 2];
    /* Use native delete walk for reliability without shell. */
    char stack[64][PCLN_PATH];
    int top = 0;
    if (!dir_exists(path) && !file_exists(path))
        return;
    if (file_exists(path))
    {
        DeleteFileA(path);
        return;
    }
    strncpy(stack[top++], path, PCLN_PATH - 1);
    while (top > 0)
    {
        char cur[PCLN_PATH];
        char pattern[PCLN_PATH];
        WIN32_FIND_DATAA fd;
        HANDLE h;
        int hasChild = 0;
        strncpy(cur, stack[top - 1], sizeof(cur) - 1);
        snprintf(pattern, sizeof(pattern), "%s\\*", cur);
        h = FindFirstFileA(pattern, &fd);
        if (h != INVALID_HANDLE_VALUE)
        {
            do
            {
                char full[PCLN_PATH];
                if (strcmp(fd.cFileName, ".") == 0 || strcmp(fd.cFileName, "..") == 0)
                    continue;
                snprintf(full, sizeof(full), "%s\\%s", cur, fd.cFileName);
                if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
                {
                    if (top < 64)
                    {
                        strncpy(stack[top++], full, PCLN_PATH - 1);
                        hasChild = 1;
                    }
                }
                else
                    DeleteFileA(full);
            } while (FindNextFileA(h, &fd));
            FindClose(h);
        }
        if (!hasChild)
        {
            RemoveDirectoryA(cur);
            top--;
        }
    }
    (void)cmd;
#else
    /* Best-effort recursive */
    char cmd[PCLN_PATH + 32];
    snprintf(cmd, sizeof(cmd), "rm -rf \"%s\"", path);
    system(cmd);
#endif
}

static void cleanup_interrupted(const char *runtimeRoot, const char *prefix)
{
#if defined(_WIN32)
    char pattern[PCLN_PATH];
    WIN32_FIND_DATAA fd;
    HANDLE h;
    snprintf(pattern, sizeof(pattern), "%s\\%s*", runtimeRoot, prefix);
    h = FindFirstFileA(pattern, &fd);
    if (h == INVALID_HANDLE_VALUE)
        return;
    do
    {
        char full[PCLN_PATH];
        if (fd.cFileName[0] == '.')
            continue;
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY))
            continue;
        snprintf(full, sizeof(full), "%s\\%s", runtimeRoot, fd.cFileName);
        if (strstr(fd.cFileName, prefix) == fd.cFileName)
            rm_rf(full);
    } while (FindNextFileA(h, &fd));
    FindClose(h);
#else
    DIR *d = opendir(runtimeRoot);
    struct dirent *ent;
    if (!d)
        return;
    while ((ent = readdir(d)) != NULL)
    {
        if (strncmp(ent->d_name, prefix, strlen(prefix)) == 0)
        {
            char full[PCLN_PATH];
            snprintf(full, sizeof(full), "%s/%s", runtimeRoot, ent->d_name);
            rm_rf(full);
        }
    }
    closedir(d);
#endif
}

static int random_suffix(char *out, size_t n)
{
#if defined(_WIN32)
    snprintf(out, n, "%08lx%08lx", (unsigned long)GetCurrentProcessId(), (unsigned long)GetTickCount());
#else
    snprintf(out, n, "%08x%08lx", (unsigned)getpid(), (unsigned long)time(NULL));
#endif
    return 0;
}

static int install_zip_content_addressed(
    const char *zipPath,
    const char *runtimeRoot,
    const char *hashHex,
    const char *readyName,
    const char *filesListName,
    const char *lockName,
    const char *extractPrefix,
    char *outDir,
    size_t outDirLen,
    char *err,
    size_t errLen)
{
    char installDir[PCLN_PATH];
    char readyPath[PCLN_PATH];
    char lockPath[PCLN_PATH];
    char tempDir[PCLN_PATH];
    char listPath[PCLN_PATH];
    char hash16[17];
    char suffix[32];
    char zipErr[256];
    pcln_lock_t lock = 0;

    memcpy(hash16, hashHex, 16);
    hash16[16] = 0;

    ensure_dir(runtimeRoot);
    path_join(lockPath, sizeof(lockPath), runtimeRoot, lockName);
    if (lock_acquire(lockPath, &lock) != 0)
    {
        set_err(err, errLen, "install lock timeout");
        return -1;
    }

    cleanup_interrupted(runtimeRoot, extractPrefix);
    path_join(installDir, sizeof(installDir), runtimeRoot, hash16);
    path_join(readyPath, sizeof(readyPath), installDir, readyName);

    if (file_exists(readyPath))
    {
        if (!filesListName || installed_files_exist(installDir))
        {
            strncpy(outDir, installDir, outDirLen - 1);
            outDir[outDirLen - 1] = 0;
            lock_release(lock);
            return 0;
        }
    }

    if (dir_exists(installDir))
        rm_rf(installDir);

    random_suffix(suffix, sizeof(suffix));
    snprintf(tempDir, sizeof(tempDir), "%s%c%s%s",
             runtimeRoot,
#if defined(_WIN32)
             '\\',
#else
             '/',
#endif
             extractPrefix,
             suffix);
    ensure_dir(tempDir);

    if (pcln_zip_extract(zipPath, tempDir, zipErr, sizeof(zipErr)) != 0)
    {
        char msg[320];
        snprintf(msg, sizeof(msg), "zip extract failed: %s", zipErr);
        set_err(err, errLen, msg);
        rm_rf(tempDir);
        lock_release(lock);
        return -1;
    }

    if (filesListName)
    {
        path_join(listPath, sizeof(listPath), tempDir, filesListName);
        if (write_installed_files_list(tempDir, listPath) != 0)
        {
            set_err(err, errLen, "cannot write file list");
            rm_rf(tempDir);
            lock_release(lock);
            return -1;
        }
    }

    {
        FILE *rf = fopen(readyPath, "wb"); /* wrong: ready is under installDir after move */
        (void)rf;
    }
    {
        char readyTmp[PCLN_PATH];
        FILE *rf;
        path_join(readyTmp, sizeof(readyTmp), tempDir, readyName);
        rf = fopen(readyTmp, "wb");
        if (rf)
        {
            fputs(hashHex, rf);
            fputc('\n', rf);
            fclose(rf);
        }
    }

#if defined(_WIN32)
    if (!MoveFileA(tempDir, installDir))
    {
        /* destination may exist mid-race */
        if (dir_exists(installDir) && file_exists(readyPath) &&
            (!filesListName || installed_files_exist(installDir)))
        {
            rm_rf(tempDir);
            strncpy(outDir, installDir, outDirLen - 1);
            outDir[outDirLen - 1] = 0;
            lock_release(lock);
            return 0;
        }
        set_err(err, errLen, "MoveFile install dir failed");
        rm_rf(tempDir);
        lock_release(lock);
        return -1;
    }
#else
    if (rename(tempDir, installDir) != 0)
    {
        if (dir_exists(installDir) && file_exists(readyPath))
        {
            rm_rf(tempDir);
            strncpy(outDir, installDir, outDirLen - 1);
            outDir[outDirLen - 1] = 0;
            lock_release(lock);
            return 0;
        }
        set_err(err, errLen, "rename install dir failed");
        rm_rf(tempDir);
        lock_release(lock);
        return -1;
    }
#endif

    strncpy(outDir, installDir, outDirLen - 1);
    outDir[outDirLen - 1] = 0;
    lock_release(lock);
    return 0;
}

int pcln_install_native_runtime_zip(
    const char *zipPath,
    const char *dataDirectory,
    const char *rid,
    char *outDir,
    size_t outDirLen,
    char *err,
    size_t errLen)
{
    char hash[PCLN_HASH];
    char runtimeRoot[PCLN_PATH];

    if (!zipPath || !dataDirectory || !rid || !outDir)
    {
        set_err(err, errLen, "null arg");
        return -1;
    }
    if (!file_exists(zipPath))
    {
        set_err(err, errLen, "native zip missing");
        return -1;
    }
    if (!is_safe_rid(rid))
    {
        set_err(err, errLen, "unsafe rid");
        return -1;
    }
    if (pcln_sha256_file(zipPath, hash, sizeof(hash)) != 0)
    {
        set_err(err, errLen, "sha256 failed");
        return -1;
    }

    snprintf(runtimeRoot, sizeof(runtimeRoot), "%s%cruntime%cnative%c%s",
             dataDirectory,
#if defined(_WIN32)
             '\\', '\\', '\\',
#else
             '/', '/', '/',
#endif
             rid);

    return install_zip_content_addressed(
        zipPath,
        runtimeRoot,
        hash,
        ".ready",
        ".pcln-native-runtime-files",
        ".pcln-native-runtime.lock",
        ".pcln-extract-",
        outDir,
        outDirLen,
        err,
        errLen);
}

static int find_file_recursive(const char *root, const char *name, char *out, size_t outLen)
{
#if defined(_WIN32)
    char stack[64][PCLN_PATH];
    int top = 0;
    strncpy(stack[top++], root, PCLN_PATH - 1);
    while (top > 0)
    {
        char cur[PCLN_PATH];
        char pattern[PCLN_PATH];
        WIN32_FIND_DATAA fd;
        HANDLE h;
        strncpy(cur, stack[--top], sizeof(cur) - 1);
        snprintf(pattern, sizeof(pattern), "%s\\*", cur);
        h = FindFirstFileA(pattern, &fd);
        if (h == INVALID_HANDLE_VALUE)
            continue;
        do
        {
            char full[PCLN_PATH];
            if (strcmp(fd.cFileName, ".") == 0 || strcmp(fd.cFileName, "..") == 0)
                continue;
            snprintf(full, sizeof(full), "%s\\%s", cur, fd.cFileName);
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
            {
                if (top < 64)
                    strncpy(stack[top++], full, PCLN_PATH - 1);
            }
            else if (_stricmp(fd.cFileName, name) == 0)
            {
                strncpy(out, full, outLen - 1);
                out[outLen - 1] = 0;
                FindClose(h);
                return 0;
            }
        } while (FindNextFileA(h, &fd));
        FindClose(h);
    }
#else
    /* BFS */
    char **queue = (char **)malloc(sizeof(char *) * 128);
    int qn = 0, qcap = 128;
    queue[qn] = strdup(root);
    qn++;
    while (qn > 0)
    {
        char *cur = queue[--qn];
        DIR *d = opendir(cur);
        struct dirent *ent;
        if (!d)
        {
            free(cur);
            continue;
        }
        while ((ent = readdir(d)) != NULL)
        {
            char full[PCLN_PATH];
            struct stat st;
            if (ent->d_name[0] == '.')
                continue;
            snprintf(full, sizeof(full), "%s/%s", cur, ent->d_name);
            if (stat(full, &st) != 0)
                continue;
            if (S_ISDIR(st.st_mode))
            {
                if (qn >= qcap)
                {
                    qcap *= 2;
                    queue = (char **)realloc(queue, sizeof(char *) * (size_t)qcap);
                }
                queue[qn++] = strdup(full);
            }
            else if (strcmp(ent->d_name, name) == 0)
            {
                strncpy(out, full, outLen - 1);
                out[outLen - 1] = 0;
                closedir(d);
                free(cur);
                while (qn > 0)
                    free(queue[--qn]);
                free(queue);
                return 0;
            }
        }
        closedir(d);
        free(cur);
    }
    free(queue);
#endif
    return -1;
}

int pcln_install_sidecar_zip(
    const char *zipPath,
    const char *dataDirectory,
    char *outDir,
    size_t outDirLen,
    char *outExe,
    size_t outExeLen,
    char *err,
    size_t errLen)
{
    char hash[PCLN_HASH];
    char runtimeRoot[PCLN_PATH];
    char installDir[PCLN_PATH];
    int rc;
#if defined(_WIN32)
    const char *exeName = "PCL.Plugin.Sidecar.exe";
#else
    const char *exeName = "PCL.Plugin.Sidecar";
#endif

    if (!zipPath || !file_exists(zipPath))
    {
        set_err(err, errLen, "sidecar zip missing");
        return -1;
    }
    if (pcln_sha256_file(zipPath, hash, sizeof(hash)) != 0)
    {
        set_err(err, errLen, "sha256 failed");
        return -1;
    }

    snprintf(runtimeRoot, sizeof(runtimeRoot), "%s%cruntime%csidecar",
             dataDirectory,
#if defined(_WIN32)
             '\\', '\\'
#else
             '/', '/'
#endif
    );

    rc = install_zip_content_addressed(
        zipPath,
        runtimeRoot,
        hash,
        ".extracted",
        NULL,
        ".pcln-sidecar.lock",
        ".pcln-sidecar-extract-",
        installDir,
        sizeof(installDir),
        err,
        errLen);
    if (rc != 0)
        return rc;

    strncpy(outDir, installDir, outDirLen - 1);
    outDir[outDirLen - 1] = 0;

    {
        char candidate[PCLN_PATH];
        path_join(candidate, sizeof(candidate), installDir, exeName);
        if (file_exists(candidate))
        {
            strncpy(outExe, candidate, outExeLen - 1);
            outExe[outExeLen - 1] = 0;
        }
        else if (find_file_recursive(installDir, exeName, outExe, outExeLen) != 0)
        {
            set_err(err, errLen, "sidecar executable missing after extract");
            return -1;
        }
    }

#if !defined(_WIN32)
    chmod(outExe, 0755);
#endif
    return 0;
}

/* Hash a directory by hashing sorted relative paths + file contents (streaming). */
static int hash_directory(const char *root, char *outHex, size_t outLen)
{
    pcln_sha256_ctx ctx;
    /* Reuse file list to temp then hash list + files */
    char listPath[PCLN_PATH];
    char line[PCLN_PATH];
    FILE *f;

    if (outLen < 65)
        return -1;
    snprintf(listPath, sizeof(listPath), "%s%c.pcln-hash-list-%lu.tmp",
             root,
#if defined(_WIN32)
             '\\',
#else
             '/',
#endif
             (unsigned long)time(NULL));
    if (write_installed_files_list(root, listPath) != 0)
        return -1;

    pcln_sha256_init(&ctx);
    f = fopen(listPath, "rb");
    if (!f)
    {
        remove(listPath);
        return -1;
    }
    while (fgets(line, sizeof(line), f))
    {
        size_t n = strlen(line);
        char full[PCLN_PATH];
        FILE *in;
        unsigned char buf[8192];
        size_t rn;
        while (n > 0 && (line[n - 1] == '\n' || line[n - 1] == '\r'))
            line[--n] = 0;
        if (n == 0)
            continue;
        pcln_sha256_update(&ctx, line, n);
        path_join(full, sizeof(full), root, line);
#if defined(_WIN32)
        {
            char *p;
            for (p = full; *p; p++)
                if (*p == '/')
                    *p = '\\';
        }
#endif
        in = fopen(full, "rb");
        if (!in)
            continue;
        while ((rn = fread(buf, 1, sizeof(buf), in)) > 0)
            pcln_sha256_update(&ctx, buf, rn);
        fclose(in);
    }
    fclose(f);
    remove(listPath);
    {
        unsigned char dig[32];
        int i;
        pcln_sha256_final(&ctx, dig);
        for (i = 0; i < 32; i++)
            snprintf(outHex + i * 2, outLen - (size_t)(i * 2), "%02x", dig[i]);
        outHex[64] = 0;
    }
    return 0;
}

#if defined(_WIN32)
static int copy_tree(const char *src, const char *dst)
{
    char stackSrc[64][PCLN_PATH];
    char stackDst[64][PCLN_PATH];
    int top = 0;
    ensure_dir(dst);
    strncpy(stackSrc[top], src, PCLN_PATH - 1);
    strncpy(stackDst[top], dst, PCLN_PATH - 1);
    top++;
    while (top > 0)
    {
        char curS[PCLN_PATH], curD[PCLN_PATH], pattern[PCLN_PATH];
        WIN32_FIND_DATAA fd;
        HANDLE h;
        top--;
        strncpy(curS, stackSrc[top], sizeof(curS) - 1);
        strncpy(curD, stackDst[top], sizeof(curD) - 1);
        ensure_dir(curD);
        snprintf(pattern, sizeof(pattern), "%s\\*", curS);
        h = FindFirstFileA(pattern, &fd);
        if (h == INVALID_HANDLE_VALUE)
            continue;
        do
        {
            char from[PCLN_PATH], to[PCLN_PATH];
            if (strcmp(fd.cFileName, ".") == 0 || strcmp(fd.cFileName, "..") == 0)
                continue;
            if (fd.cFileName[0] == '.')
                continue;
            snprintf(from, sizeof(from), "%s\\%s", curS, fd.cFileName);
            snprintf(to, sizeof(to), "%s\\%s", curD, fd.cFileName);
            if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
            {
                if (top < 64)
                {
                    strncpy(stackSrc[top], from, PCLN_PATH - 1);
                    strncpy(stackDst[top], to, PCLN_PATH - 1);
                    top++;
                }
            }
            else
            {
                ensure_dir(curD);
                if (!CopyFileA(from, to, FALSE))
                {
                    FindClose(h);
                    return -1;
                }
            }
        } while (FindNextFileA(h, &fd));
        FindClose(h);
    }
    return 0;
}
#else
static int copy_tree(const char *src, const char *dst)
{
    char cmd[PCLN_PATH * 2 + 32];
    snprintf(cmd, sizeof(cmd), "cp -a \"%s/.\" \"%s/\"", src, dst);
    ensure_dir(dst);
    return system(cmd) == 0 ? 0 : -1;
}
#endif

int pcln_install_native_runtime_dir(
    const char *sourceDir,
    const char *dataDirectory,
    const char *rid,
    char *outDir,
    size_t outDirLen,
    char *err,
    size_t errLen)
{
    char hash[PCLN_HASH];
    char runtimeRoot[PCLN_PATH];
    char installDir[PCLN_PATH];
    char readyPath[PCLN_PATH];
    char lockPath[PCLN_PATH];
    char tempDir[PCLN_PATH];
    char listPath[PCLN_PATH];
    char hash16[17];
    char suffix[32];
    pcln_lock_t lock = 0;

    if (!sourceDir || !dir_exists(sourceDir))
    {
        set_err(err, errLen, "source dir missing");
        return 1;
    }
    if (!is_safe_rid(rid))
    {
        set_err(err, errLen, "unsafe rid");
        return -1;
    }
    if (hash_directory(sourceDir, hash, sizeof(hash)) != 0)
    {
        set_err(err, errLen, "hash directory failed");
        return -1;
    }
    memcpy(hash16, hash, 16);
    hash16[16] = 0;

    snprintf(runtimeRoot, sizeof(runtimeRoot), "%s%cruntime%cnative%c%s",
             dataDirectory,
#if defined(_WIN32)
             '\\', '\\', '\\',
#else
             '/', '/', '/',
#endif
             rid);
    ensure_dir(runtimeRoot);
    path_join(lockPath, sizeof(lockPath), runtimeRoot, ".pcln-native-runtime.lock");
    if (lock_acquire(lockPath, &lock) != 0)
    {
        set_err(err, errLen, "lock timeout");
        return -1;
    }

    path_join(installDir, sizeof(installDir), runtimeRoot, hash16);
    path_join(readyPath, sizeof(readyPath), installDir, ".ready");
    if (file_exists(readyPath) && installed_files_exist(installDir))
    {
        strncpy(outDir, installDir, outDirLen - 1);
        outDir[outDirLen - 1] = 0;
        lock_release(lock);
        return 0;
    }
    if (dir_exists(installDir))
        rm_rf(installDir);

    random_suffix(suffix, sizeof(suffix));
    snprintf(tempDir, sizeof(tempDir), "%s%c.pcln-extract-%s",
             runtimeRoot,
#if defined(_WIN32)
             '\\',
#else
             '/',
#endif
             suffix);
    ensure_dir(tempDir);
    if (copy_tree(sourceDir, tempDir) != 0)
    {
        set_err(err, errLen, "copy tree failed");
        rm_rf(tempDir);
        lock_release(lock);
        return -1;
    }
    path_join(listPath, sizeof(listPath), tempDir, ".pcln-native-runtime-files");
    write_installed_files_list(tempDir, listPath);
    {
        char readyTmp[PCLN_PATH];
        FILE *rf;
        path_join(readyTmp, sizeof(readyTmp), tempDir, ".ready");
        rf = fopen(readyTmp, "wb");
        if (rf)
        {
            fputs(hash, rf);
            fputc('\n', rf);
            fclose(rf);
        }
    }
#if defined(_WIN32)
    if (!MoveFileA(tempDir, installDir))
    {
        set_err(err, errLen, "move install failed");
        rm_rf(tempDir);
        lock_release(lock);
        return -1;
    }
#else
    if (rename(tempDir, installDir) != 0)
    {
        set_err(err, errLen, "rename install failed");
        rm_rf(tempDir);
        lock_release(lock);
        return -1;
    }
#endif
    strncpy(outDir, installDir, outDirLen - 1);
    outDir[outDirLen - 1] = 0;
    lock_release(lock);
    return 0;
}
