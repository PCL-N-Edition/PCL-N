# Settings navigation and native chrome revision

The 2026-09-18 product revision replaces the global category rail with the same draggable segmented selector and horizontal pager used by Java installation. Only the current settings page materializes rows. Per-category scroll positions survive switching.

Cloud account and cloud synchronization settings are removed from the catalog, including instance synchronization sections. Local backups remain. This supersedes the cloud sections in the original IA source; that source remains a historical input.

Product projects, assemblies and namespaces move from Nexa to Nexa; the window title is NexaCL. External upstream URLs and legacy compatibility identifiers require explicit preservation.

The main window must be opaque. Windows retains native resize and DWM capabilities with an 8 DIP content inset in normal state and 12 DIP inner rounding. Maximized/fullscreen content has no inset. macOS retains system traffic lights and native fullscreen; host geometry is projected through sealed window state. No custom traffic-light drawing or hand-written maximize-on-double-click on macOS.

## Validation and compatibility

- New projects, assemblies and namespaces are `Nexa.*`; the solution is `NexaCL.slnx` and the title is NexaCL. Plugin consumers must rebuild against the renamed contract assemblies.
- Keep upstream URLs, historical IA input, old GitHub secret aliases, old environment aliases and legacy release asset formats intact. New application data defaults to Nexa, while existing PCL Nexa roots remain readable. New instance metadata writes use Nexa/InstanceMetadata.json and reads fall back to PCL/InstanceMetadata.json.
- Renderer scratch lists and backend reconciliation sets are reused without exposing mutable scene storage. In a warmed 501-node, 100-paint-frame comparison, managed allocation fell from 1,269,454 to 637,080 bytes/frame. This is allocation traffic, not process working-set measurement. Clean render remains zero allocation.
- Windows uses an opaque background equal to the shell palette. UI.Next owns the 8 DIP inset; the Skia/Avalonia presentation clip follows its 12 DIP inner radius. This repository has no dedicated SDF shader backend.
- macOS uses native title-bar hit testing, excludes interactive scene nodes, measures all three standard buttons, and publishes safe-area/fullscreen facts. x64 Objective-C struct returns use objc_msgSend_stret; arm64 uses objc_msgSend. Native macOS interaction still requires on-device validation.
