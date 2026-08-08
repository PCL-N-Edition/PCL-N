/*
 * pcln-launcher — C bootstrap for PCL N.
 *
 * Responsibilities:
 *   1) Self-extract payload.zip (store-method) for host + crash + dep zips
 *   2) Install native-runtime.zip / sidecar.zip using the same content-addressed
 *      layout as the former AOT host self-extract (PclEmbeddedNativeRuntime /
 *      PclEmbeddedPluginSidecar): {data}/runtime/native|{sidecar}/...
 *   3) Start two child processes (crash-handler + AOT host) with env pointing at
 *      preinstalled deps so the host does not extract embedded zips.
 *
 * Layout (next to this launcher):
 *   pcln-launcher(.exe)
 *   payload.zip | payload/
 *     host/PCL-N-Host(.exe)
 *     crash/pcln-crash-handler(.exe)
 *     native-runtime.zip   (preferred; SHA256 content-addressed install)
 *     sidecar.zip          (optional)
 *     native/ | sidecar/   (fallback expanded trees)
 */

#include "install.h"
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
#  include <errno.h>
#  include <sys/stat.h>
#  include <sys/types.h>
#  include <sys/wait.h>
#  include <unistd.h>
#endif

#define PCLN_MAX 2048

static char g_self_dir[PCLN_MAX];
static char g_payload_zip[PCLN_MAX];
static char g_extract_root[PCLN_MAX];
static char g_host_path[PCLN_MAX];
static char g_crash_path[PCLN_MAX];
static char g_native_dir[PCLN_MAX];
static char g_sidecar_dir[PCLN_MAX];
static char g_sidecar_exe[PCLN_MAX];
static char g_data_dir[PCLN_MAX];
static char g_crash_dir[PCLN_MAX];
static char g_clean_flag[PCLN_MAX];

static void die(const char *msg)
{
#if defined(_WIN32)
    MessageBoxA(NULL, msg, "PCL N Launcher", MB_OK | MB_ICONERROR);
#else
    fputs(msg, stderr);
    fputc('\n', stderr);
#endif
    exit(1);
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

static void dirname_of(const char *path, char *out, size_t n)
{
    size_t i;
    size_t len = strlen(path);
    if (len >= n)
        len = n - 1;
    memcpy(out, path, len);
    out[len] = 0;
    for (i = len; i > 0; i--)
    {
        if (out[i - 1] == '/' || out[i - 1] == '\\')
        {
            out[i - 1] = 0;
            return;
        }
    }
    out[0] = '.';
    out[1] = 0;
}

static void resolve_self_dir(void)
{
#if defined(_WIN32)
    char buf[PCLN_MAX];
    DWORD n = GetModuleFileNameA(NULL, buf, PCLN_MAX);
    if (n == 0 || n >= PCLN_MAX)
        die("GetModuleFileName failed");
    dirname_of(buf, g_self_dir, sizeof(g_self_dir));
#else
    char buf[PCLN_MAX];
    ssize_t n = readlink("/proc/self/exe", buf, sizeof(buf) - 1);
    if (n <= 0)
    {
        /* macOS fallback: argv0 handled by caller — use cwd */
        if (!getcwd(buf, sizeof(buf)))
            die("cannot resolve launcher directory");
        strncpy(g_self_dir, buf, sizeof(g_self_dir) - 1);
        return;
    }
    buf[n] = 0;
    dirname_of(buf, g_self_dir, sizeof(g_self_dir));
#endif
}

/* FNV-1a 64-bit of file for cheap content stamp (not cryptographic). */
static int hash_file(const char *path, char *outHex, size_t outLen)
{
    FILE *f = fopen(path, "rb");
    unsigned long long h = 14695981039346656037ull;
    unsigned char buf[8192];
    size_t n;
    if (!f)
        return -1;
    while ((n = fread(buf, 1, sizeof(buf), f)) > 0)
    {
        size_t i;
        for (i = 0; i < n; i++)
        {
            h ^= buf[i];
            h *= 1099511628211ull;
        }
    }
    fclose(f);
    snprintf(outHex, outLen, "%016llx", (unsigned long long)h);
    return 0;
}

static void ensure_dir(const char *path)
{
#if defined(_WIN32)
    char tmp[PCLN_MAX];
    char *p;
    strncpy(tmp, path, sizeof(tmp) - 1);
    tmp[sizeof(tmp) - 1] = 0;
    for (p = tmp; *p; p++)
    {
        if (*p == '/' || *p == '\\')
        {
            char c = *p;
            *p = 0;
            CreateDirectoryA(tmp, NULL);
            *p = c;
        }
    }
    CreateDirectoryA(tmp, NULL);
#else
    char tmp[PCLN_MAX];
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

/*
 * Payload extract root = same data root as host LauncherPathLayout.ResolveDataDirectory
 * plus runtime/launcher-payload/<content-hash>/ (host-equivalent under data dir, not next to exe).
 */
static void resolve_extract_root(const char *hash)
{
    char base[PCLN_MAX];
    if (!g_data_dir[0])
        die("data directory not resolved");
    path_join(base, sizeof(base), g_data_dir, "runtime/launcher-payload");
    path_join(g_extract_root, sizeof(g_extract_root), base, hash);
}

static int extract_if_needed(void)
{
    char hash[32];
    char ready[PCLN_MAX];
    char err[256];
    char pre[PCLN_MAX];

    path_join(pre, sizeof(pre), g_self_dir, "payload");
    path_join(g_payload_zip, sizeof(g_payload_zip), g_self_dir, "payload.zip");

    /* Dev convenience: pre-extracted tree next to launcher (no re-copy). */
    if (dir_exists(pre))
    {
        char readyPre[PCLN_MAX];
        path_join(readyPre, sizeof(readyPre), pre, ".ready");
        if (file_exists(readyPre))
        {
            strncpy(g_extract_root, pre, sizeof(g_extract_root) - 1);
            return 0;
        }
    }

    if (!file_exists(g_payload_zip))
    {
        /* Dev: no payload — run host beside launcher if present. */
        path_join(g_extract_root, sizeof(g_extract_root), g_self_dir, ".");
        return 0;
    }

    if (hash_file(g_payload_zip, hash, sizeof(hash)) != 0)
        die("cannot hash payload.zip");
    resolve_extract_root(hash);
    path_join(ready, sizeof(ready), g_extract_root, ".ready");
    if (file_exists(ready))
        return 0;

    ensure_dir(g_extract_root);
    if (pcln_zip_extract(g_payload_zip, g_extract_root, err, sizeof(err)) != 0)
    {
        char msg[512];
        snprintf(msg, sizeof(msg), "解压 payload.zip 失败：%s", err);
        die(msg);
    }
    {
        FILE *f = fopen(ready, "wb");
        if (f)
        {
            fputs(hash, f);
            fputc('\n', f);
            fclose(f);
        }
    }
    return 0;
}

static void resolve_children(void)
{
#if defined(_WIN32)
    const char *hostName = "PCL-N-Host.exe";
    const char *crashName = "pcln-crash-handler.exe";
#else
    const char *hostName = "PCL-N-Host";
    const char *crashName = "pcln-crash-handler";
#endif
    char hostA[PCLN_MAX], hostB[PCLN_MAX], crashA[PCLN_MAX], crashB[PCLN_MAX];

    path_join(hostA, sizeof(hostA), g_extract_root, "host");
    path_join(g_host_path, sizeof(g_host_path), hostA, hostName);
    path_join(hostB, sizeof(hostB), g_extract_root, hostName);
    if (!file_exists(g_host_path) && file_exists(hostB))
        strncpy(g_host_path, hostB, sizeof(g_host_path) - 1);

    path_join(crashA, sizeof(crashA), g_extract_root, "crash");
    path_join(g_crash_path, sizeof(g_crash_path), crashA, crashName);
    path_join(crashB, sizeof(crashB), g_extract_root, crashName);
    if (!file_exists(g_crash_path) && file_exists(crashB))
        strncpy(g_crash_path, crashB, sizeof(g_crash_path) - 1);
    /* Also allow crash handler next to launcher. */
    if (!file_exists(g_crash_path))
    {
        char beside[PCLN_MAX];
        path_join(beside, sizeof(beside), g_self_dir, crashName);
        if (file_exists(beside))
            strncpy(g_crash_path, beside, sizeof(g_crash_path) - 1);
    }

    /* Logs/Crashes under host ResolveLogDirectory layout: {data}/Logs/Crashes */
    if (g_data_dir[0])
        path_join(g_crash_dir, sizeof(g_crash_dir), g_data_dir, "Logs/Crashes");
    else
        path_join(g_crash_dir, sizeof(g_crash_dir), g_self_dir, "Logs/Crashes");
    ensure_dir(g_crash_dir);
}

/*
 * Resolve native/sidecar paths for the host.
 *
 * Release scatter layout is fully expanded next to the product entry:
 *   native/  sidecar/  host/  crash/
 * No runtime zip extract — CI/installers already expanded the trees.
 *
 * Optional legacy fallback: native-runtime.zip / sidecar.zip still install into
 * LauncherPathLayout data directory (content-addressed) when present.
 */
static void resolve_sidecar_exe(const char *dir)
{
#if defined(_WIN32)
    const char *exeName = "PCL.Plugin.Sidecar.exe";
#else
    const char *exeName = "PCL.Plugin.Sidecar";
#endif
    path_join(g_sidecar_exe, sizeof(g_sidecar_exe), dir, exeName);
    if (file_exists(g_sidecar_exe))
        return;
    /* Shallow search one level down (nested zip root). */
#if defined(_WIN32)
    {
        char pattern[PCLN_MAX];
        WIN32_FIND_DATAA fd;
        HANDLE h;
        snprintf(pattern, sizeof(pattern), "%s\\*", dir);
        h = FindFirstFileA(pattern, &fd);
        if (h == INVALID_HANDLE_VALUE)
        {
            g_sidecar_exe[0] = 0;
            return;
        }
        do
        {
            char sub[PCLN_MAX], cand[PCLN_MAX];
            if (fd.cFileName[0] == '.')
                continue;
            if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY))
                continue;
            snprintf(sub, sizeof(sub), "%s\\%s", dir, fd.cFileName);
            path_join(cand, sizeof(cand), sub, exeName);
            if (file_exists(cand))
            {
                strncpy(g_sidecar_exe, cand, sizeof(g_sidecar_exe) - 1);
                FindClose(h);
                return;
            }
        } while (FindNextFileA(h, &fd));
        FindClose(h);
    }
#endif
    g_sidecar_exe[0] = 0;
}

static void install_aot_dependencies(void)
{
    char err[320];
    char nativeZip[PCLN_MAX];
    char sidecarZip[PCLN_MAX];
    char nativeTree[PCLN_MAX];
    char sidecarTree[PCLN_MAX];
    const char *rid = pcln_runtime_rid();

    g_native_dir[0] = 0;
    g_sidecar_dir[0] = 0;
    g_sidecar_exe[0] = 0;

    if (!g_data_dir[0])
        die("数据目录未解析");

    path_join(nativeTree, sizeof(nativeTree), g_extract_root, "native");
    if (!dir_exists(nativeTree))
        path_join(nativeTree, sizeof(nativeTree), g_self_dir, "native");

    path_join(sidecarTree, sizeof(sidecarTree), g_extract_root, "sidecar");
    if (!dir_exists(sidecarTree))
        path_join(sidecarTree, sizeof(sidecarTree), g_self_dir, "sidecar");

    path_join(nativeZip, sizeof(nativeZip), g_extract_root, "native-runtime.zip");
    if (!file_exists(nativeZip))
        path_join(nativeZip, sizeof(nativeZip), g_self_dir, "native-runtime.zip");

    path_join(sidecarZip, sizeof(sidecarZip), g_extract_root, "sidecar.zip");
    if (!file_exists(sidecarZip))
        path_join(sidecarZip, sizeof(sidecarZip), g_self_dir, "sidecar.zip");

    /* Fully-expanded scatter: use trees in place (no copy / no unzip). */
    if (dir_exists(nativeTree))
    {
        strncpy(g_native_dir, nativeTree, sizeof(g_native_dir) - 1);
        g_native_dir[sizeof(g_native_dir) - 1] = 0;
    }
    else if (file_exists(nativeZip))
    {
        if (pcln_install_native_runtime_zip(
                nativeZip, g_data_dir, rid,
                g_native_dir, sizeof(g_native_dir),
                err, sizeof(err)) != 0)
        {
            char msg[512];
            snprintf(msg, sizeof(msg), "安装 native-runtime.zip 失败：%s", err);
            die(msg);
        }
    }

    if (dir_exists(sidecarTree))
    {
        strncpy(g_sidecar_dir, sidecarTree, sizeof(g_sidecar_dir) - 1);
        g_sidecar_dir[sizeof(g_sidecar_dir) - 1] = 0;
        resolve_sidecar_exe(g_sidecar_dir);
    }
    else if (file_exists(sidecarZip))
    {
        if (pcln_install_sidecar_zip(
                sidecarZip, g_data_dir,
                g_sidecar_dir, sizeof(g_sidecar_dir),
                g_sidecar_exe, sizeof(g_sidecar_exe),
                err, sizeof(err)) != 0)
        {
            char msg[512];
            snprintf(msg, sizeof(msg), "安装 sidecar.zip 失败：%s", err);
            die(msg);
        }
    }
}

#if defined(_WIN32)
static void set_env(const char *k, const char *v)
{
    SetEnvironmentVariableA(k, v);
}

static int spawn_process(const char *path, char *cmdline, PROCESS_INFORMATION *pi)
{
    STARTUPINFOA si;
    ZeroMemory(&si, sizeof(si));
    si.cb = sizeof(si);
    ZeroMemory(pi, sizeof(*pi));
    if (!CreateProcessA(path, cmdline, NULL, NULL, FALSE, 0, NULL, g_extract_root, &si, pi))
        return -1;
    return 0;
}
#else
static void set_env(const char *k, const char *v)
{
    setenv(k, v, 1);
}

static pid_t spawn_process(const char *path, char *const argv[])
{
    pid_t pid = fork();
    if (pid == 0)
    {
        execv(path, argv);
        _exit(127);
    }
    return pid;
}
#endif

static void configure_host_env(void)
{
    char bootstrapPid[32];
    set_env("PCL_LAUNCHER_BOOTSTRAP", "1");
    set_env("PCL_LAUNCHER_ROOT", g_self_dir);
#if defined(_WIN32)
    snprintf(bootstrapPid, sizeof(bootstrapPid), "%lu", (unsigned long)GetCurrentProcessId());
#else
    snprintf(bootstrapPid, sizeof(bootstrapPid), "%d", (int)getpid());
#endif
    set_env("PCL_LAUNCHER_BOOTSTRAP_PID", bootstrapPid);
    set_env("PCL_SKIP_EXTERNAL_CRASH_HANDLER", "1");
    if (g_clean_flag[0])
        set_env("PCL_CRASH_CLEAN_FLAG", g_clean_flag);
    if (dir_exists(g_crash_dir))
        set_env("PCL_CRASH_DIR", g_crash_dir);
    if (g_data_dir[0])
        set_env("PCL_DATA_DIRECTORY", g_data_dir);
    if (dir_exists(g_native_dir))
        set_env("PCL_NATIVE_RUNTIME_DIR", g_native_dir);
    if (dir_exists(g_sidecar_dir))
        set_env("PCL_PLUGIN_SIDECAR_DIR", g_sidecar_dir);
    if (g_sidecar_exe[0] && file_exists(g_sidecar_exe))
        set_env("PCL_PLUGIN_SIDECAR_EXE", g_sidecar_exe);

#if defined(_WIN32)
    if (dir_exists(g_native_dir))
    {
        char pathBuf[4096];
        DWORD n = GetEnvironmentVariableA("PATH", pathBuf, sizeof(pathBuf));
        char neu[8192];
        if (n > 0 && n < sizeof(pathBuf))
            snprintf(neu, sizeof(neu), "%s;%s", g_native_dir, pathBuf);
        else
            snprintf(neu, sizeof(neu), "%s", g_native_dir);
        set_env("PATH", neu);
    }
#else
    if (dir_exists(g_native_dir))
    {
        const char *key = "LD_LIBRARY_PATH";
#if defined(__APPLE__)
        key = "DYLD_LIBRARY_PATH";
#endif
        const char *old = getenv(key);
        char neu[4096];
        if (old && *old)
            snprintf(neu, sizeof(neu), "%s:%s", g_native_dir, old);
        else
            snprintf(neu, sizeof(neu), "%s", g_native_dir);
        set_env(key, neu);
    }
#endif
}

#if defined(_WIN32)
/* Append one argument using the CommandLineToArgvW/CreateProcess quoting rules. */
static int append_windows_arg(char *command, size_t capacity, const char *arg)
{
    size_t used = strlen(command);
    size_t slashes = 0;
    const char *p;
    int quote = !*arg || strpbrk(arg, " \t\"") != NULL;
    if (used + 2 >= capacity)
        return -1;
    command[used++] = ' ';
    if (quote)
        command[used++] = '"';
    for (p = arg; *p; p++)
    {
        if (*p == '\\')
        {
            slashes++;
            continue;
        }
        if (*p == '"')
        {
            size_t count = slashes * 2 + 1;
            while (count-- > 0)
            {
                if (used + 1 >= capacity)
                    return -1;
                command[used++] = '\\';
            }
            if (used + 1 >= capacity)
                return -1;
            command[used++] = '"';
            slashes = 0;
            continue;
        }
        while (slashes > 0)
        {
            slashes--;
            if (used + 1 >= capacity)
                return -1;
            command[used++] = '\\';
        }
        slashes = 0;
        if (used + 1 >= capacity)
            return -1;
        command[used++] = *p;
    }
    if (quote)
    {
        while (slashes > 0)
        {
            slashes--;
            if (used + 2 >= capacity)
                return -1;
            command[used++] = '\\';
            command[used++] = '\\';
        }
        if (used + 1 >= capacity)
            return -1;
        command[used++] = '"';
    }
    else
    {
        while (slashes > 0)
        {
            slashes--;
            if (used + 1 >= capacity)
                return -1;
            command[used++] = '\\';
        }
    }
    command[used] = 0;
    return 0;
}
#endif

/* Allocate clean-flag path before spawn so host inherits PCL_CRASH_CLEAN_FLAG. */
static void prepare_clean_flag_path(void)
{
#if defined(_WIN32)
    snprintf(g_clean_flag, sizeof(g_clean_flag), "%s\\clean-%lu-%lu.flag",
             g_crash_dir,
             (unsigned long)GetCurrentProcessId(),
             (unsigned long)GetTickCount());
#else
    snprintf(g_clean_flag, sizeof(g_clean_flag), "%s/clean-%d-%ld.flag",
             g_crash_dir, (int)getpid(), (long)time(NULL));
#endif
}

static void write_clean_flag(void)
{
    FILE *f;
    if (!g_clean_flag[0])
        return;
    f = fopen(g_clean_flag, "wb");
    if (!f)
        return;
    fputs("ok\n", f);
    fclose(f);
}

int main(int argc, char **argv)
{
    resolve_self_dir();
    /* Same root as host LauncherPathLayout.ResolveDataDirectory — all extracts go here. */
    if (pcln_resolve_data_directory(g_data_dir, sizeof(g_data_dir)) != 0)
        die("无法解析数据目录（与本体 LauncherPathLayout 一致）");
    extract_if_needed();
    resolve_children();
    install_aot_dependencies();

    if (!file_exists(g_host_path))
    {
        char msg[PCLN_MAX * 2];
        snprintf(msg, sizeof(msg),
                 "未找到 AOT 主机程序：\n%s\n\n请确认 payload.zip 含 host/PCL-N-Host。",
                 g_host_path);
        die(msg);
    }

    /* Clean-flag path must exist before host starts so CompleteSession can write it. */
    prepare_clean_flag_path();
    configure_host_env();

#if defined(_WIN32)
    {
        PROCESS_INFORMATION hostPi, crashPi;
        DWORD hostExit = 1;
        char hostCmd[PCLN_MAX * 8];
        char crashCmd[PCLN_MAX * 2];
        int hasCrash = file_exists(g_crash_path);

        ZeroMemory(&hostPi, sizeof(hostPi));
        ZeroMemory(&crashPi, sizeof(crashPi));

        /* Start host first so we know its PID for the crash watcher. */
        snprintf(hostCmd, sizeof(hostCmd), "\"%s\"", g_host_path);
        {
            int argi;
            for (argi = 1; argi < argc; argi++)
                if (append_windows_arg(hostCmd, sizeof(hostCmd), argv[argi]) != 0)
                    die("启动参数过长");
        }
        if (spawn_process(g_host_path, hostCmd, &hostPi) != 0)
            die("无法启动 AOT 主机进程");

        if (hasCrash)
        {
            snprintf(crashCmd, sizeof(crashCmd),
                     "\"%s\" --parent-pid %lu --crash-dir \"%s\" --clean-flag \"%s\"",
                     g_crash_path,
                     (unsigned long)hostPi.dwProcessId,
                     g_crash_dir,
                     g_clean_flag);
            if (spawn_process(g_crash_path, crashCmd, &crashPi) != 0)
            {
                /* Non-fatal: host can still run. */
                ZeroMemory(&crashPi, sizeof(crashPi));
            }
        }

        WaitForSingleObject(hostPi.hProcess, INFINITE);
        GetExitCodeProcess(hostPi.hProcess, &hostExit);
        /*
         * Host should write clean-flag via CompleteSession.
         * Fallback: exit code 0 without flag still silences the watcher.
         */
        if (hostExit == 0)
            write_clean_flag();

        if (crashPi.hProcess)
        {
            WaitForSingleObject(crashPi.hProcess, 3000);
            CloseHandle(crashPi.hThread);
            CloseHandle(crashPi.hProcess);
        }
        CloseHandle(hostPi.hThread);
        CloseHandle(hostPi.hProcess);
        return (int)hostExit;
    }
#else
    {
        pid_t hostPid, crashPid = -1;
        int status = 0;
        char pidStr[32];
        char **hostArgv;
        char *crashArgv[12];
        int ai = 0;

        hostArgv = (char **)calloc((size_t)argc + 1, sizeof(char *));
        if (!hostArgv)
            die("cannot allocate host arguments");
        hostArgv[0] = g_host_path;
        {
            int argi;
            for (argi = 1; argi < argc; argi++)
                hostArgv[argi] = argv[argi];
        }
        hostPid = spawn_process(g_host_path, hostArgv);
        free(hostArgv);
        if (hostPid < 0)
            die("fork host failed");

        snprintf(pidStr, sizeof(pidStr), "%d", (int)hostPid);

        if (file_exists(g_crash_path))
        {
            crashArgv[ai++] = g_crash_path;
            crashArgv[ai++] = "--parent-pid";
            crashArgv[ai++] = pidStr;
            crashArgv[ai++] = "--crash-dir";
            crashArgv[ai++] = g_crash_dir;
            crashArgv[ai++] = "--clean-flag";
            crashArgv[ai++] = g_clean_flag;
            crashArgv[ai] = NULL;
            crashPid = spawn_process(g_crash_path, crashArgv);
        }

        if (waitpid(hostPid, &status, 0) < 0)
            return 1;
        if (WIFEXITED(status) && WEXITSTATUS(status) == 0)
            write_clean_flag();

        if (crashPid > 0)
        {
            int cs = 0;
            /* Give watcher a moment to observe exit. */
            sleep(1);
            waitpid(crashPid, &cs, WNOHANG);
        }

        if (WIFEXITED(status))
            return WEXITSTATUS(status);
        return 1;
    }
#endif
}