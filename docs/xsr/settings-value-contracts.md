# Settings value contracts (stages 1–2)

`SettingsCatalog.json` and `settings-entry-map.md` enumerate all 566 final IA nodes. Groups, choices, actions and facts do not own persisted values. Reserved settings keep their final position and `NotImplemented`; a future capability must declare its real value contract before enabling them. Their default/range is intentionally not fabricated from the label.

The following foundation contracts are declared in `SettingsPolicySchema`. Owner: `Nexa.Services.Settings`. Scope `G/I` permits global and instance overrides; `G` is global only. `Auto` is a payload-free mode; reset means remove the override. Enum strings are stable encodings, not localized labels.

| Key | Type / domain | Builtin | Scope | Legacy source | Applies | Export |
|---|---|---|---|---|---|---|
| general.language | Text | auto | G | UiLanguage | Restart | Yes |
| general.region | Text | auto | G | UiFormatCulture | Restart | Yes |
| appearance.animations-disabled | Bool | false | G | SystemDisableUiAnimations | Immediate | Yes |
| appearance.animation-fps | Number, 1–240 fps | 59 | G | UiAniFPS | Immediate | Yes |
| appearance.lock-window | Bool | false | G | UiLockWindowSize | Immediate | Yes |
| appearance.low-power | Bool | false | G | UiUltraLowPowerMode | Immediate | Yes |
| java.runtime | Fully qualified path / Auto | Auto | G/I | New | Next launch | No |
| java.auto-install | Bool | false | G/I | New | Next launch | Yes |
| java.vendor | Text | empty | G/I | New | Next launch | Yes |
| java.compatibility | Bool | true | G/I | New | Next launch | Yes |
| game.memory | Number, 256–1048576 MiB / Auto | Auto | G/I | LaunchRamType + LaunchRamCustom | Next launch | Yes |
| game.window-mode | windowed / fullscreen | windowed | G/I | LaunchArgumentWindowType | Next launch | Yes |
| game.width | Number, 1–32768 px | 854 | G/I | LaunchArgumentWindowWidth | Next launch | Yes |
| game.height | Number, 1–32768 px | 480 | G/I | LaunchArgumentWindowHeight | Next launch | Yes |
| game.title | Text | empty | G/I | LaunchArgumentTitle | Next launch | Yes |
| game.jvm | Text | existing LauncherDefaults JVM string | G/I | LaunchAdvanceJvm | Next launch | No |
| game.arguments | Text | empty | G/I | LaunchAdvanceGame | Next launch | No |
| game.wrapper | Text | empty | G/I | LaunchWrapperCommand | Next launch | No |
| game.pre-launch | Text | empty | G/I | LaunchAdvanceRun | Next launch | No |
| game.auto-repair | Bool | true | G/I | LaunchAutoRepairGame | Next launch | Yes |
| game.server | Text | empty | G/I | New | Next launch | Yes |
| network.proxy-mode | 0 / 1 / 2 (none / system / custom) | 1 | G | SystemHttpProxyType | Next task | Yes |
| network.proxy-address | Text, absolute HTTP/HTTPS/SOCKS5 URI when custom | empty | G | SystemHttpProxy | Next task | No |
| network.proxy-user | Text | empty | G | SystemHttpProxyCustomUsername | Next task | No |
| network.proxy-password | Text | empty | G | SystemHttpProxyCustomPassword | Next task | No |
| network.doh | Bool | true | G | SystemNetEnableDoH | Next task | Yes |
| diagnostics.telemetry | Bool | false | G | TelemetryExperienceProgram | Immediate | Yes |
| updates.channel | stable / alpha / beta / ci | alpha | G | New (no guessed numeric conversion) | Next task | Yes |
| developer.enabled | Bool | false | G | New (not SystemDebugMode) | Immediate | Yes |

The animation row is positive UI wording backed by a negative legacy flag; catalog `InvertBoolean` makes this explicit. Repeated title rows share `game.title`, and instance server defaults use `game.server`.

Legacy memory uses the existing piecewise slider-to-MiB conversion. A custom new MiB value is stored exactly; it is not rounded back into a lossy slider coordinate. Global Auto/reset clears the old manual policy. Stage 4 consumers must read the effective contract before exposing the new editor. Window mode maps fullscreen to legacy 0 and windowed to 1. Existing unchanged legacy keys remain byte-compatible.

Apply timing is a contract for future consumers, not a claim that this stage has enabled a setting in the UI. The first settings page enables window width/height/mode, JVM/game arguments and developer visibility after connecting their consumers; remaining entries stay unavailable. Profile and Temporary have resolution semantics but reject public mutation.

Import/export format: `{ "version": 1, "scope": "global" | "instance", "values": { "key": { "mode": "Custom" | "Auto" | "Inherit", "value": "..." } } }`. Auto/Inherit omit the payload. Instance imports use a directory identity supplied separately; exports do not carry machine-specific instance locations. Unknown keys, local-only keys, wrong scopes, invalid values and unsupported versions are rejected. Missing keys leave current values unchanged. Preview has no persistence or state effects; apply revalidates and checks its revision.
