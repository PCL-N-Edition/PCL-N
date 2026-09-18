# Settings navigation and native chrome revision

The 2026-09-18 product revision replaces the global category rail with the same draggable segmented selector and horizontal pager used by Java installation. Only the current settings page materializes rows. Per-category scroll positions survive switching.

Cloud account and cloud synchronization settings are removed from the catalog, including instance synchronization sections. Local backups remain. This supersedes the cloud sections in the original IA source; that source remains a historical input.

Product projects, assemblies and namespaces move from PCL to Nexa; the window title is NexaCL. External upstream URLs and legacy compatibility identifiers require explicit preservation.

The main window must be opaque. Windows retains native resize and DWM capabilities with an 8 DIP content inset in normal state and 12 DIP inner rounding. Maximized/fullscreen content has no inset. macOS retains system traffic lights and native fullscreen; host geometry is projected through sealed window state. No custom traffic-light drawing or hand-written maximize-on-double-click on macOS.
