# XSR-716 Multi-directory version selection

## Locked scope

The launch page's `选择版本` subpage has a directory selector above an installed-version list.
The active directory displays its full path; the selected version has a check and a textual
`当前版本` marker. Selecting a row updates the launch card immediately after persistence, without
starting a game or dismissing the page. Existing navigation/back focus and interruptible motion
remain in force; reduced motion uses the existing settled presentation.

- `MinecraftLibraryService` owns registered directory paths, the active directory, remembered
  selection per directory, and the current discovery snapshot in shared Host State. It reuses
  `MinecraftInstanceDiscovery` and `SettingsService`; Desktop owns only presentation projections.
- One additive text setting, `MinecraftLibrary`, stores a versioned JSON document containing the
  directory list and selections. This is local settings persistence, not Sidecar data-plane JSON.
  Saves complete before selection publication; failed saves leave the previous choice intact.
  Existing launcher settings and their unknown fields continue to round-trip unchanged.
- Directory identity is an absolute normalized path, compared using the platform path comparer.
  An instance identity is `(directory, instance ID)`, so equal version names in different roots
  never alias. Registering a directory deduplicates it; removing it only forgets the registration
  and never deletes game files. The final directory cannot be removed.
- Discovery is cancellable and generation-scoped. A stale scan cannot replace a newer directory
  or selection. Refresh preserves an available selected version, otherwise selects the first
  discovered version. Missing/unreadable/empty roots have explicit states and no launchable row.
- The native folder picker is an explicit Desktop effect implemented at the Avalonia edge.
  Cancellation is a no-op. Manual absolute-path entry is available in the PXML directory chooser.
  All worker completions publish state; only frame preparation reconciles UI.Next entities.
- `MinecraftStartCommand` retains its existing two-argument constructor and adds an optional
  `MinecraftRootDirectory` property. The coordinator retains its original overloads and accepts
  an explicit root for the product route. Manifest inheritance, classpath/assets and runtime
  acquisition resolve within that captured root; changing library selection during launch cannot
  redirect an in-flight request. Low-level launch planning remains in Services.

## Acceptance

Service tests cover two roots with identical IDs, persistence/restart, stale and cancelled scans,
failed persistence, refresh selection retention, empty/unavailable roots, and safe forgetting.
Desktop tests drive directory/version intents, selection markers, home projection, root-qualified
start commands, keyboard/back focus, minimum-window layout and overflow through PXML/UI.Next.
Existing managed suites, architecture and formatting gates, renderer benchmarks, Desktop NativeAOT
and linked-trim shell validation remain required. Screenshots inspect the actual Avalonia scene.
