/*
 * Content-addressed zip install — ports PclEmbeddedNativeRuntime /
 * PclEmbeddedPluginSidecar extract layout from the managed host into C.
 *
 * Native:  {data}/runtime/native/{rid}/{sha256[:16]}/ + .ready + .pcln-native-runtime-files
 * Sidecar: {data}/runtime/sidecar/{sha256[:16]}/ + .extracted
 */
#ifndef PCLN_INSTALL_H
#define PCLN_INSTALL_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Resolve data root — same rules as host LauncherPathLayout.ResolveDataDirectory:
 *   %LocalAppData%/PCL-N/pcln-paths.json → ApplicationDataDirectory when valid
 *   else %AppData%/PCL-N (Roaming) on Windows / platform default on Unix.
 */
int pcln_resolve_data_directory(char *out, size_t outLen);

/* Compile-time RID string, e.g. "win-x64". */
const char *pcln_runtime_rid(void);

/*
 * Install native-runtime.zip like PclEmbeddedNativeRuntime.EnsurePayloadInstalled.
 * On success writes absolute install dir to outDir.
 */
int pcln_install_native_runtime_zip(
    const char *zipPath,
    const char *dataDirectory,
    const char *rid,
    char *outDir,
    size_t outDirLen,
    char *err,
    size_t errLen);

/*
 * Install sidecar.zip like PclEmbeddedPluginSidecar.
 * outExe receives path to PCL.Plugin.Sidecar(.exe) when found.
 */
int pcln_install_sidecar_zip(
    const char *zipPath,
    const char *dataDirectory,
    char *outDir,
    size_t outDirLen,
    char *outExe,
    size_t outExeLen,
    char *err,
    size_t errLen);

/*
 * If zip missing but expanded directory exists, copy into content-addressed tree
 * keyed by a hash of the directory listing + file digests (or a .source-sha256 stamp).
 * Returns 0 on success, 1 if source missing, -1 on error.
 */
int pcln_install_native_runtime_dir(
    const char *sourceDir,
    const char *dataDirectory,
    const char *rid,
    char *outDir,
    size_t outDirLen,
    char *err,
    size_t errLen);

#ifdef __cplusplus
}
#endif

#endif
