/*
 * pcln-crash-handler — out-of-process crash companion for PCL N.
 *
 * The managed / NativeAOT host cannot safely run UI or complex I/O while
 * handling a segfault. This tiny C program is started early by the host and
 * watches the parent PID. When the parent disappears without writing a
 * "clean exit" flag, the handler writes a crash report and shows a native
 * message so the user is never left with a silent flash-quit.
 *
 * Usage:
 *   pcln-crash-handler --parent-pid <pid> --marker <session.active> \
 *       --crash-dir <Logs/Crashes> --clean-flag <path>
 *
 * Build (examples):
 *   Windows (MSVC): cl /O2 /Fe:pcln-crash-handler.exe main.c
 *   Windows (MinGW): gcc -O2 -o pcln-crash-handler.exe main.c
 *   Linux/macOS:    cc -O2 -o pcln-crash-handler main.c
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#if defined(_WIN32)
#  define WIN32_LEAN_AND_MEAN
#  include <windows.h>
#  include <shellapi.h>
#else
#  include <errno.h>
#  include <fcntl.h>
#  include <signal.h>
#  include <sys/stat.h>
#  include <sys/types.h>
#  include <unistd.h>
#endif

#define PCLN_MAX_PATH 1024
#define PCLN_ISSUES_URL "https://github.com/MuXue1230-owo/PCL-N/issues/new/choose"

static long g_parent_pid = 0;
static char g_marker[PCLN_MAX_PATH];
static char g_crash_dir[PCLN_MAX_PATH];
static char g_clean_flag[PCLN_MAX_PATH];

static int streq(const char *a, const char *b)
{
    return a && b && strcmp(a, b) == 0;
}

static int file_exists(const char *path)
{
#if defined(_WIN32)
    DWORD attr = GetFileAttributesA(path);
    return attr != INVALID_FILE_ATTRIBUTES && !(attr & FILE_ATTRIBUTE_DIRECTORY);
#else
    struct stat st;
    return stat(path, &st) == 0 && S_ISREG(st.st_mode);
#endif
}

static int parent_alive(long pid)
{
#if defined(_WIN32)
    HANDLE h = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, (DWORD)pid);
    if (!h)
        return 0;
    DWORD code = 0;
    int alive = 1;
    if (GetExitCodeProcess(h, &code) && code != STILL_ACTIVE)
        alive = 0;
    CloseHandle(h);
    return alive;
#else
    if (pid <= 0)
        return 0;
    if (kill((pid_t)pid, 0) == 0)
        return 1;
    return errno == EPERM; /* exists but we cannot signal it */
#endif
}

static void sleep_ms(unsigned ms)
{
#if defined(_WIN32)
    Sleep(ms);
#else
    struct timespec ts;
    ts.tv_sec = (time_t)(ms / 1000u);
    ts.tv_nsec = (long)((ms % 1000u) * 1000000u);
    nanosleep(&ts, NULL);
#endif
}

static void write_report(const char *path, int clean)
{
    FILE *f = fopen(path, "wb");
    if (!f)
        return;

    time_t now = time(NULL);
    fprintf(f, "### PCL N 进程外崩溃处理器\n\n");
    fprintf(f, "- format: pcln-crash-handler-v1\n");
    fprintf(f, "- parentPid: %ld\n", g_parent_pid);
    fprintf(f, "- cleanExitFlag: %s\n", clean ? "yes" : "no");
    fprintf(f, "- marker: %s\n", g_marker[0] ? g_marker : "(none)");
    fprintf(f, "- detectedUtc: %lld\n", (long long)now);
    fprintf(f, "\n");
    if (clean)
    {
        fprintf(f, "主进程已正常退出（检测到 clean-flag）。\n");
    }
    else
    {
        fprintf(f,
                "主进程异常消失且未写入正常退出标记。\n"
                "常见原因：原生段错误、FailFast、被任务管理器结束、断电。\n"
                "请附带 Logs/Crashes 下的 native-*.dmp / native-*.txt 与本报告提交 Issue。\n");
    }

    if (g_marker[0] && file_exists(g_marker))
    {
        fprintf(f, "\n### Session marker\n```text\n");
        FILE *m = fopen(g_marker, "rb");
        if (m)
        {
            char buf[512];
            size_t n;
            while ((n = fread(buf, 1, sizeof(buf), m)) > 0)
                fwrite(buf, 1, n, f);
            fclose(m);
        }
        fprintf(f, "\n```\n");
    }

    fprintf(f, "\nIssue: %s\n", PCLN_ISSUES_URL);
    fclose(f);
}

static void notify_user(const char *report_path)
{
    char msg[PCLN_MAX_PATH * 2];
    snprintf(msg, sizeof(msg),
             "PCL N 上次未能正常退出。\n\n"
             "已由进程外崩溃处理器写入报告：\n%s\n\n"
             "请将 Logs/Crashes 中的 native-* 与该报告一并提交 Issue。",
             report_path);

#if defined(_WIN32)
    MessageBoxA(NULL, msg, "PCL N 异常退出", MB_OK | MB_ICONERROR | MB_SETFOREGROUND | MB_TOPMOST);
    /* Best-effort open the crashes folder. */
    if (g_crash_dir[0])
        ShellExecuteA(NULL, "open", g_crash_dir, NULL, NULL, SW_SHOWNORMAL);
#else
    /* No portable GUI; write to stderr so a terminal-attached session still sees it. */
    fputs(msg, stderr);
    fputc('\n', stderr);
#endif
}

static int parse_args(int argc, char **argv)
{
    g_marker[0] = 0;
    g_crash_dir[0] = 0;
    g_clean_flag[0] = 0;

    for (int i = 1; i < argc; i++)
    {
        if (streq(argv[i], "--parent-pid") && i + 1 < argc)
        {
            g_parent_pid = strtol(argv[++i], NULL, 10);
        }
        else if (streq(argv[i], "--marker") && i + 1 < argc)
        {
            strncpy(g_marker, argv[++i], PCLN_MAX_PATH - 1);
            g_marker[PCLN_MAX_PATH - 1] = 0;
        }
        else if (streq(argv[i], "--crash-dir") && i + 1 < argc)
        {
            strncpy(g_crash_dir, argv[++i], PCLN_MAX_PATH - 1);
            g_crash_dir[PCLN_MAX_PATH - 1] = 0;
        }
        else if (streq(argv[i], "--clean-flag") && i + 1 < argc)
        {
            strncpy(g_clean_flag, argv[++i], PCLN_MAX_PATH - 1);
            g_clean_flag[PCLN_MAX_PATH - 1] = 0;
        }
        else if (streq(argv[i], "--help") || streq(argv[i], "-h"))
        {
            fputs(
                "pcln-crash-handler --parent-pid N --marker PATH --crash-dir DIR --clean-flag PATH\n",
                stdout);
            return 2;
        }
    }

    if (g_parent_pid <= 0 || !g_crash_dir[0] || !g_clean_flag[0])
        return 1;
    return 0;
}

int main(int argc, char **argv)
{
    int rc = parse_args(argc, argv);
    if (rc == 2)
        return 0;
    if (rc != 0)
    {
        fputs("pcln-crash-handler: missing required arguments\n", stderr);
        return 2;
    }

#if !defined(_WIN32)
    /* Detach from terminal signals so the watcher is not killed with the host TTY. */
    signal(SIGHUP, SIG_IGN);
#endif

    /* Poll until parent is gone. Keep the interval small for snappy detection. */
    while (parent_alive(g_parent_pid))
        sleep_ms(250);

    /* Give the host a brief window to create the clean-flag on normal exit. */
    for (int i = 0; i < 20; i++)
    {
        if (file_exists(g_clean_flag))
            return 0;
        sleep_ms(50);
    }

    if (file_exists(g_clean_flag))
        return 0;

    /* Abnormal exit path. */
    char report_path[PCLN_MAX_PATH];
    time_t now = time(NULL);
    snprintf(report_path, sizeof(report_path),
             "%s%cwatchdog-%lld-p%ld.md",
             g_crash_dir,
#if defined(_WIN32)
             '\\',
#else
             '/',
#endif
             (long long)now,
             g_parent_pid);

    write_report(report_path, /*clean=*/0);
    notify_user(report_path);
    return 1;
}
