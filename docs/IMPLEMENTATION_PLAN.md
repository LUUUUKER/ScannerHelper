# Scanner Helper V1 Implementation Plan

> ## ⚠ Task 4b 的硬性关卡失败，路线已改 / The Task 4b hard gate failed; the route has changed
>
> 2026-09-10。实测证明**按键一旦被 `WH_KEYBOARD_LL` 吞掉，Windows 就不再为它产生
> `WM_INPUT`**，所以 Task 4b 第 2 条不可达成。按本计划自己的规定：不得继续、不得用
> 时序上的经验值掩盖症状，应当记录并重新审视架构——三件都做了。
>
> 扫码枪改为工作在 **USB 虚拟串口**模式（21 枪实测零误差，357 字节 = 21 × 17）。
>
> - 失败证据：[`TASK_4B_FINDING_20260910.md`](TASK_4B_FINDING_20260910.md)
> - 新架构与逐条影响：[`ARCHITECTURE_CHANGE_SERIAL.md`](ARCHITECTURE_CHANGE_SERIAL.md)
>
> **对本计划的影响：**
> - Task 4a —— 已完成，测量有效，保留为历史记录。
> - Task 4b —— **已完成，结论是「此路不通」**。它做到了硬性关卡该做的事：在建完整应用
>   之前否决掉一个不成立的假设。不再重试。
> - Task 5（设备绑定）—— 改为绑定串口背后的 USB 设备身份。
> - Task 6（ScanSession / 关联器）—— 关联器与按键级拼装作废；模式、解析、校验、输出保留。
> - Task 7a/7b（输出）、8a（热键路由）、以及 Task 1–3 —— **不受影响**，230 个用例照常全绿。
> - 新增：串口读取（已完成）、端口↔设备身份解析、端口健康检测。
>
> 2026-09-10: measurement proved that a keystroke swallowed by `WH_KEYBOARD_LL` produces no
> `WM_INPUT`, so item 2 of Task 4b is unachievable. Per this plan's own rule the work stopped, the
> failure was documented, and the architecture was revisited. The scanner now runs as a USB virtual
> COM device, measured at 21 scans with zero error (357 bytes = 21 × 17).


> **For agentic workers:** Implement task-by-task. Use TDD for Core behaviors. Do not proceed past the input-correlation spike (Task 4b) if it is unreliable.
>
> Development begins on macOS, so tasks are split into **Phase A** (cross-platform `net8.0`, no hardware) and **Phase B** (Windows, real scanner). See *Development environment* below and the revised *Recommended implementation order* at the end — the task numbers are not executed in numeric order.

**Goal:** Build a Windows desktop Scanner Helper that binds one arbitrary HID Keyboard scanner, toggles between SN and SKU output, safely transforms scanner input, and remains visibly controllable in English and Chinese.

**Architecture:** WPF UI over a testable Core domain, with all Win32 input/output integration isolated in a Win32 project. Raw Input identifies the physical source; a low-level keyboard hook suppresses/replays events; an isolated correlator connects the two streams. Scanner output is transformed and emitted through SendInput without modifying the warehouse application.

**Tech Stack:** C#, .NET 8, WPF, CommunityToolkit.Mvvm, Win32 P/Invoke, System.Text.Json, `.resx`, xUnit.

**Spec:** `docs/SCANNER_HELPER_SPEC.md`

## Global Constraints

- Windows 10/11 only.
- No kernel driver in V1.
- One active scanner per workstation.
- Startup mode always SN.
- F8 toggle, F10 Force Send, Esc Cancel.
- Minimize = visible Compact mode.
- Compact = forced Always On Top.
- Error expansion must not steal business-app focus.
- English and Simplified Chinese required from first release.
- Mandatory Raw Input + hook correlation spike before full application implementation.
- Business application is a **web app at normal privilege**; output is `SendInput` + `KEYEVENTF_UNICODE`. A trailing Enter is appended only if `AppendEnterAfterScan` is enabled — **default disabled** (spec 2.1, 2.2, 10).
- `PAUSED` is a required safety valve with a **mouse-clickable** control in both Full and Compact (spec 5.7).
- Silent-unhook heartbeat detection is required (spec 19.1).
- `ScannerHelper.Core` targets `net8.0` and must build and test on non-Windows.

---

## Development environment

Development starts on **macOS**, where only `net8.0` projects can be built. `.NET 8 SDK` is installed at `~/.dotnet` (user-local, no sudo).

This splits the plan in two:

| Phase | Where | Tasks |
|---|---|---|
| **Phase A — cross-platform** | macOS, no hardware | Tasks 1, 2, 3, 6, and the pure parts of 7 and 8 |
| **Phase B — Windows** | Windows + real scanner + real keyboard | Tasks 4, 5, 9–13, and the native halves of 7 and 8 |

Phase A is genuinely productive rather than a stopgap: it covers every parsing, validation, settings, mode, session, correlation, and hotkey-routing behavior in the product, all under TDD, before any hardware is involved.

---

## Target repository layout

```text
ScannerHelper/
├─ ScannerHelper.sln
├─ CLAUDE.md
├─ README.md
├─ .gitignore
├─ docs/
│  ├─ SCANNER_HELPER_SPEC.md
│  └─ IMPLEMENTATION_PLAN.md
├─ src/
│  ├─ ScannerHelper.App/            net8.0-windows   (Phase B)
│  ├─ ScannerHelper.Core/           net8.0           (Phase A)
│  └─ ScannerHelper.Win32/          net8.0-windows   (Phase B)
└─ tests/
   ├─ ScannerHelper.Core.Tests/            net8.0           (Phase A)
   └─ ScannerHelper.Diagnostics.Harness/   net8.0-windows   (Phase B, manual)
```

`ScannerHelper.Integration.Tests` from the original plan is replaced by `ScannerHelper.Diagnostics.Harness`: its checks need real hardware and a human observer, which makes them a manual verification tool rather than an automated suite (spec 20).

---

## Task 1 — Bootstrap solution and dependency boundaries

**Deliverable:** A solution that builds and tests green **on macOS**, containing only the two cross-platform projects.

**Phase A — create now:**

- `ScannerHelper.sln`
- `src/ScannerHelper.Core/ScannerHelper.Core.csproj` → `<TargetFramework>net8.0</TargetFramework>`
- `tests/ScannerHelper.Core.Tests/ScannerHelper.Core.Tests.csproj` → `net8.0`, xUnit

**Phase B — add later with `dotnet sln add`, on Windows:**

- `src/ScannerHelper.Win32/` → `net8.0-windows`
- `src/ScannerHelper.App/` → `net8.0-windows`, `<UseWPF>true</UseWPF>`
- `tests/ScannerHelper.Diagnostics.Harness/` → `net8.0-windows`

Do **not** create the `net8.0-windows` projects yet. Adding them to the solution now makes `dotnet build` fail on macOS, which destroys the fast feedback loop that all of Phase A depends on.

**Reference rules:**

```text
ScannerHelper.App   -> ScannerHelper.Core + ScannerHelper.Win32
ScannerHelper.Core  -> nothing. No WPF, no Win32, no App.
ScannerHelper.Win32 -> ScannerHelper.Core (implements its interfaces) + native wrappers
Core.Tests          -> ScannerHelper.Core
```

Dependencies point inward. `Core` defines interfaces; `Win32` implements them; `App` composes.

**Checks:**

- `dotnet build ScannerHelper.sln` — clean on macOS
- `dotnet test ScannerHelper.sln` — green on macOS
- `ScannerHelper.Core.csproj` contains no `net8.0-windows`, no `UseWPF`, and no `ProjectReference`

Commit after clean build/test.

---

## Task 2 — Build pure domain types, ModeManager, parsing and validation with TDD

**Deliverable:** All core transformation logic works independently of Windows UI/input APIs.

**Create:**

```text
src/ScannerHelper.Core/Abstractions/ISystemClock.cs
src/ScannerHelper.Core/Domain/ScanMode.cs
src/ScannerHelper.Core/Domain/ParseResult.cs
src/ScannerHelper.Core/Domain/ValidationResult.cs
src/ScannerHelper.Core/Domain/ScannerDeviceIdentity.cs
src/ScannerHelper.Core/Modes/ModeManager.cs
src/ScannerHelper.Core/Parsing/ISkuParser.cs
src/ScannerHelper.Core/Parsing/FixedPositionSkuParser.cs
src/ScannerHelper.Core/Parsing/RegexSkuParser.cs
src/ScannerHelper.Core/Parsing/SkuParserFactory.cs
src/ScannerHelper.Core/Validation/ISkuValidator.cs
src/ScannerHelper.Core/Validation/CompositeSkuValidator.cs
src/ScannerHelper.Core/Validation/LengthSkuValidator.cs
src/ScannerHelper.Core/Validation/CharacterSetSkuValidator.cs
src/ScannerHelper.Core/Validation/RegexSkuValidator.cs
```

**Required interfaces:**

```csharp
public enum ScanMode { Sn, Sku }

public interface ISkuParser
{
    ParseResult Parse(string rawCode);
}

public interface ISkuValidator
{
    ValidationResult Validate(string sku);
}
```

`ParseResult` and `ValidationResult` must carry structured failure reasons rather than throwing for expected bad input.

**Tests must include:**

- Fixed Position uses user-facing 1-based start position.
- Bounds failure.
- Regex capture group success.
- Invalid regex handled during configuration/creation.
- Missing capture group.
- Regex timeout surfaced as failure.
- Exact/min/max length validation.
- Character-set presets.
- **Character-set with `Ignore case` enabled: lowercase accepted.**
- **Character-set with `Ignore case` disabled: lowercase rejected.**
- **`Ignore case` does not affect length validation or the validation regex.**
- Validation regex.
- Composite validator: all enabled rules must pass.
- Zero validators means valid.
- `ModeManager` initializes to SN and toggles deterministically.

`ISystemClock` exists so later tasks can test timeouts deterministically. Introduce it here and never read `DateTime.UtcNow` directly from `Core`.

Commit after tests pass.

---

## Task 3 — Configuration model and JSON persistence

**Deliverable:** Versioned settings persist all user preferences except current scan mode.

**Create:**

```text
src/ScannerHelper.Core/Settings/AppSettings.cs
src/ScannerHelper.Core/Settings/HotkeySettings.cs
src/ScannerHelper.Core/Settings/ScannerBindingSettings.cs
src/ScannerHelper.Core/Settings/SkuParsingSettings.cs
src/ScannerHelper.Core/Settings/SkuValidationSettings.cs
src/ScannerHelper.Core/Settings/WindowSettings.cs
src/ScannerHelper.Core/Settings/DiagnosticsSettings.cs
src/ScannerHelper.Core/Settings/ISettingsStore.cs
src/ScannerHelper.Core/Settings/JsonSettingsStore.cs
```

**Required behavior:**

- Add `SchemaVersion` from V1.
- Use per-user AppData location.
- If file missing, return defaults.
- If malformed, preserve/rename bad file, surface diagnostic event, and load safe defaults.
- Save atomically via temporary file + replace/move.
- Never store current SN/SKU mode.
- **Never store the paused state** (spec 5.7).
- Persist language, window position, Full topmost preference, sound settings, `AppendEnterAfterScan` (**default false**), scanner binding, parsing, validation (**including `IgnoreCase`**), log masking, hotkeys (**including the optional unassigned Pause/Resume hotkey**).

`JsonSettingsStore` resolves its directory via `Environment.SpecialFolder.ApplicationData`, which works on macOS too. Tests must inject a temp directory rather than writing to the real profile.

**Tests:** defaults, round trip, malformed config fallback, atomic-save leaves no partial file, startup mode not persisted, paused state not persisted, `IgnoreCase` round-trips.

Commit.

---

## Task 4 — Mandatory Windows input-correlation spike (Phase B, hard gate)

Split into two stages per spec 4.3. Stage 4a measures; stage 4b builds on the measurements. Running them together means debugging unproven logic against unknown hardware behavior at the same time — which is exactly how timing heuristics get introduced to make symptoms disappear.

Note that `InputEventCorrelator` is **not** created here: it is pure logic written and unit-tested in Task 6 during Phase A. Task 4 supplies the real-world event data that tells us which of its branches actually matter.

### Task 4a — Observe only

**Deliverable:** a measurement report. No interception whatsoever.

**Create:**

```text
src/ScannerHelper.Win32/Native/NativeMethods.cs
src/ScannerHelper.Win32/Native/RawInputNative.cs
src/ScannerHelper.Win32/Native/KeyboardHookNative.cs
src/ScannerHelper.Win32/Native/SendInputNative.cs
src/ScannerHelper.Win32/RawInputDeviceResolver.cs
src/ScannerHelper.Win32/LowLevelKeyboardHook.cs
tests/ScannerHelper.Diagnostics.Harness/     (observation mode)
```

The hook passes **every** event through unchanged. Nothing is swallowed, so this is safe to run on a live machine.

**Must answer and record:**

1. **For one physical keypress, does the `WH_KEYBOARD_LL` callback fire before or after `WM_INPUT` arrives?** The architecture rests on this answer. The callback must decide swallow-or-pass synchronously and cannot block; if the hook consistently precedes `WM_INPUT`, device identity is unavailable at decision time and withhold-then-replay is the only viable design.
2. Distribution of the time delta between the two streams (min / median / p99).
3. Inter-character interval per scanner model, versus a human typing.
4. Whether Raw Input device identity is stable across replug and reboot.
5. What a full scan looks like end to end: modifiers, CapsLock interaction, terminating Enter.
6. **Is the laptop's built-in keyboard enumerated by Raw Input, and what shape is its device name?** Spec assumption A4: it is normally a PS/2 or ACPI device rather than USB HID. Binding depends on it being a distinct, stable `hDevice`; an assumption that every keyboard is a HID device would be discovered too late.

**Report the machine the measurements came from.** The development machine and the pilot machines are not the same hardware, and event timing is a property of the machine, not of the code. Every figure recorded here is provisional until it is reproduced on a real pilot workstation — state that explicitly in the report rather than leaving a reader to assume the numbers are site-representative.

Commit the measurement report into `docs/`. It is the evidence base for every later architectural argument.

### Task 4b — Intercept and replay

> **⚠ 已执行，结论：失败（2026-09-10）。** 第 2 条不可达成——吞掉按键会切断它的 Raw Input。这一节保留原文，因为它正是发现这件事的手段。见 [`TASK_4B_FINDING_20260910.md`](TASK_4B_FINDING_20260910.md)。
> **Executed; failed (2026-09-10).** Item 2 is unachievable: swallowing a keystroke cuts off its Raw Input. Kept verbatim because it is what found this.


Only after 4a. **Must demonstrate on real Windows hardware:**

1. Raw Input identifies at least one ordinary keyboard and one HID scanner as distinct source devices.
2. A hook key event can be correlated with its Raw Input source under the supported constraint that the user does not type during the active scan.
3. Normal keyboard input can be suppressed temporarily and faithfully replayed — **including IME composition**, which the withhold-and-replay path most threatens.
4. Modifier keys, key repeat, and CapsLock survive replay intact.
5. Scanner input can be suppressed before raw content reaches a focused test input box.
6. The first scanner character does not leak.
7. Scanner Enter terminator is captured and **not** forwarded (spec 10).
8. Synthetic `SendInput` output is identifiable/marked and ignored by capture to prevent recursion.
9. At least 500 sequential scans are processed without visible corruption/leakage.

**Hard gate:**

If any of 2–6 is not reliably achievable, do **not** continue while pretending timing heuristics are sufficient. Document the observed event ordering and failure modes, and revisit the architecture.

Commit only if the spike is successful and the correlation strategy is documented in code comments and in tests.

---

## Task 5 — Scanner binding and device lifecycle

**Deliverable:** User can bind one scanner by scanning any code; known scanner reconnects automatically when possible.

**Create:**

```text
src/ScannerHelper.Core/Input/IScannerInputSource.cs        (Phase A — interface)
src/ScannerHelper.Core/Input/DeviceIdentityMatcher.cs      (Phase A — pure matching logic)
src/ScannerHelper.Win32/Win32ScannerInputSource.cs         (Phase B)
src/ScannerHelper.Win32/ScannerDeviceWatcher.cs            (Phase B)
src/ScannerHelper.Win32/Native/DeviceNotificationNative.cs (Phase B)
```

The matching rules are pure and belong in `Core`, unit-tested against synthetic identities during Phase A. Only enumeration and connect/disconnect notification need Windows.

**Behavior:**

- Setup enters bind mode.
- First complete scan is used to identify source device.
- Save stable identity fields: device path/instance data, VID, PID, friendly name, serial if exposed.
- Prefer specific identity over VID/PID-only matching.
- Expose Connected/Disconnected state.
- On disconnect: do not silently continue as if scanner is available.
- On reconnect: auto-match when confidence is high.
- Support explicit Rebind.

Unit-test `DeviceIdentityMatcher` against synthetic identities during Phase A: exact device-path match, serial match, VID/PID-only match flagged as low confidence, no match, and ambiguous match between two identical models. Actual replug/reconnect behavior is verified in Phase B via the diagnostics harness.

Commit.

---

## Task 6 — ScanSession and ScanInputCoordinator

**Deliverable:** End-to-end input state machine transforms one scanner scan into a domain-level completed scan without UI coupling.

**Create:**

```text
src/ScannerHelper.Core/Input/ScanSession.cs
src/ScannerHelper.Core/Input/ScanInputCoordinator.cs
src/ScannerHelper.Core/Input/InputEventCorrelator.cs
src/ScannerHelper.Core/Domain/ScanResult.cs
src/ScannerHelper.Core/Domain/KeyEvent.cs
src/ScannerHelper.Core/Domain/RawInputEvent.cs
```

All of this is Phase A: pure logic, no Windows API, timeouts read through `ISystemClock`, fully testable on macOS.

`InputEventCorrelator` is the highest-risk component in the product, so it is built here as a pure function over timestamped events — it consumes hook events and Raw Input events and returns a decision (`swallow` / `pass through` / `replay` / `undecided`). Every ordering, timeout, and out-of-order branch is exercised with synthetic sequences now, so that Task 4 has only one job left: discovering what real sequences look like.

**State logic:**

```text
IDLE -> IDENTIFYING -> SCANNING -> PROCESSING -> IDLE
```

**Requirements:**

- Enter terminates scan. **The terminating Enter is consumed, never forwarded** (spec 5.4, 10).
- Inactivity timeout initial default ~300 ms, configurable internally, read via `ISystemClock`.
- Timeout produces failure and resets state.
- During SCANNING, unsupported simultaneous ordinary keyboard input must not be interleaved into scanner data.
- SN Mode result = exact raw string.
- SKU Mode result = parse + validate.
- Failure results contain raw code and specific reason.
- **The coordinator owns the paused flag.** While paused it returns pass-through for every event without inspecting it, buffers nothing, and emits nothing (spec 5.7).
- No Windows UI calls inside coordinator.

> The ~300 ms scan inactivity timeout is unrelated to the 300 ms `LowLevelHooksTimeout` in spec 19.1, despite the identical number. Do not conflate them or derive one from the other.

**Tests:** scan completion, timeout, mode behavior, parse failure, validation failure, reset after failure, terminating Enter consumed and absent from the result, PAUSED passes everything through and produces no output, resuming restores the prior mode.

Commit.

---

## Task 7 — Safe keyboard output and Force Send workflow

**Deliverable:** Successful results and explicit Force Send reach the current focused application; injected events never recurse.

**Create:**

```text
src/ScannerHelper.Core/Output/IKeyboardOutputService.cs          (Phase A — interface)
src/ScannerHelper.Win32/SendInputKeyboardOutputService.cs        (Phase B — implementation)
```

**Output contract (spec 2.2, 10):**

- Emit via `SendInput` with `KEYEVENTF_UNICODE`, one character at a time.
- Content emitted exactly as the mode produced it — no trimming, padding, or case conversion.
- Append a trailing Enter **only if** `AppendEnterAfterScan` is enabled. **Default disabled.**
- The setting applies to SN output, SKU output, and Force Send alike.
- Tag every synthetic event so the capture pipeline recognizes and ignores it.

> The scan's own Enter is always swallowed, so with the default the web application receives no Enter at all. Whether that page needs one is unknown until the pilot. One boolean and one branch makes it switchable on the floor in seconds instead of requiring a rebuild and redeploy.

**Behavior:**

- Normal successful scan auto-emits transformed result.
- Error never auto-emits.
- Pending error stores raw scan.
- F10 during pending error emits raw scan exactly.
- Esc during pending error emits nothing.
- Force Send preserves current mode.
- Injected events are marked/detected and bypass scanner capture.
- F10/Esc outside pending error do not alter business input unexpectedly.

Phase A: test the whole domain workflow against a fake `IKeyboardOutputService` — success auto-emits, error never auto-emits, F10 emits the stored raw scan exactly, Esc emits nothing, mode preserved in both cases, and **both states of `AppendEnterAfterScan` produce exactly the expected output** (disabled → no trailing Enter; enabled → exactly one, on SN, SKU, and Force Send alike).

Phase B: verify actual `SendInput` reaching a focused control. This is one of the few Windows-side checks that genuinely automates, so it may live in a small Windows-only-guarded xUnit project rather than the manual harness.

Commit.

---

## Task 8 — Global hotkey routing with device-source rules

**Deliverable:** Physical keyboard F8 toggles globally; scanner-originated F8 cannot toggle mode.

**Create:**

```text
src/ScannerHelper.Core/Input/HotkeyCoordinator.cs
```

**Behavior:**

- F8 from bound scanner remains scanner data.
- F8 from ordinary physical keyboard toggles mode even when Scanner Helper lacks focus.
- During pending error: F10/Esc handled globally.
- Validate configured hotkeys for duplicates/conflicts before settings save.

**Pass-through rules (spec 7)** — an acted-upon hotkey is swallowed, so it cannot also trigger an unrelated shortcut in the web application:

| Key | Condition | Behavior |
|---|---|---|
| `F8` | non-scanner keyboard, not paused | toggle, **swallow** |
| `F8` | bound scanner | scanner data, never a mode command |
| `F10` | error pending | Force Send, **swallow** |
| `F10` | no error pending | **pass through** |
| `Esc` | error pending | Cancel, **swallow** |
| `Esc` | no error pending | **pass through** — far too common in web use to swallow unconditionally |
| any | paused | **pass through** |

This is pure routing logic and belongs to Phase A. Unit-test every row of the table with synthetic source identities. Real keyboard/scanner verification happens in Phase B via the harness.

Commit.

---

## Task 9 — WPF shell, Full/Compact window behavior, and focus safety

**Deliverable:** Production shell implements the visible-state rules without stealing warehouse-app focus.

**Create:**

```text
src/ScannerHelper.App/Views/MainWindow.xaml
src/ScannerHelper.App/Views/MainWindow.xaml.cs
src/ScannerHelper.App/Views/CompactWindow.xaml
src/ScannerHelper.App/Views/CompactWindow.xaml.cs
src/ScannerHelper.App/ViewModels/MainViewModel.cs
src/ScannerHelper.App/ViewModels/CompactViewModel.cs
src/ScannerHelper.App/ViewModels/ErrorStateViewModel.cs
src/ScannerHelper.App/UI/WindowStateCoordinator.cs
src/ScannerHelper.App/UI/FocusSafeWindowPresenter.cs
```

**Full Window requirements:**

- Current Mode is dominant visual.
- Scanner connection status.
- F8 hint.
- Last scan status.
- **Mouse-clickable Pause/Resume control.**
- Settings entry.
- Pin/Always On Top control.
- Minimize control means transition to Compact.
- X opens exit confirmation.
- When paused, the mode display is visibly overridden so the window never shows a confident `SN MODE` while the pipeline is bypassed.

**Compact requirements:**

- Always visible and always topmost.
- Draggable.
- Current mode obvious by text/shape plus color, using the normative palette in spec 11.2 (SN `#1B7A3D`, SKU `#1B5FA8`, error `#B3261E` + hazard stripe, paused/disconnected `#414B56`).
- Disconnected state visible, with its red top bar.
- Paused state visible.
- Scanner connection indicator uses `✓` plus text in a neutral color — **not a green dot**, since SN Mode owns green (spec 11.2).
- **Mouse-clickable Pause/Resume control — hard requirement (spec 5.7).** The failure this rescues is "the keyboard stopped working", so a hotkey-only escape hatch would be unusable in exactly the situation it exists for. Compact is where the application spends most of its time, which makes it the control the operator can actually reach.
- Restore + close controls.
- Never become invisible while process is running.

**Error behavior:**

- Compact error automatically shows Full error view.
- Must not activate/steal focus from warehouse app.
- F10/Esc remain global.
- After resolution, restore pre-error UI state.

**Position behavior:**

- Remember Full and Compact positions.
- Clamp invalid/off-screen saved coordinates to a visible monitor.

Manual UI tests on 100%, 125%, 150% DPI.

Commit.

---

## Task 10 — Localization and bilingual copy

**Deliverable:** Complete en-US and zh-CN UI with runtime switching.

**Create:**

```text
src/ScannerHelper.App/Localization/Strings.resx
src/ScannerHelper.App/Localization/Strings.zh-CN.resx
src/ScannerHelper.App/Localization/LocalizationService.cs
```

**Behavior:**

- First launch: Chinese Windows UI culture -> zh-CN; otherwise en-US.
- Persist explicit user selection.
- Switch immediately without restart.
- Keep `SN`, `SKU`, `SN MODE`, `SKU MODE`, and hotkey names consistent.
- No user-visible strings hardcoded in ViewModels/services.

Add a localization test that detects missing keys between resource sets.

Commit.

---

## Task 11 — Settings UI and rule testing

**Deliverable:** Settings exposes only V1-supported controls with live rule validation.

**Create:**

```text
src/ScannerHelper.App/Views/SettingsWindow.xaml
src/ScannerHelper.App/Views/SettingsWindow.xaml.cs
src/ScannerHelper.App/ViewModels/SettingsViewModel.cs
```

**Sections:**

1. General
2. Scanner
3. Scan Mode
4. SKU Parsing
5. Diagnostics

**SKU Parsing UI:**

- Rule Type: Fixed Position / Regex
- Fixed: Start Position + Length
- Regex: Pattern + Capture Group
- Validation: optional min/max length, character-set preset, **`Ignore case` toggle (default on)**, validation regex
- Test Raw Code
- Parsed SKU
- Separate Parse and Validation status
- Invalid configuration cannot be saved silently
- Regex syntax/timeout errors shown clearly

**Scanner UI:**

- Current scanner
- Connected/disconnected
- Test Scanner
- Rebind Scanner

`Test Scanner` opens a self-contained verification panel (spec 13.2): it takes focus, shows source device / raw code / parsed SKU / parse result / validation result per scan, and **emits nothing via `SendInput`** while open, so a misconfigured rule can be diagnosed without risking bad data in the warehouse system.

**General:** language, startup, sounds, remember window position, Full topmost preference.

**Scan Mode:** F8/F10/Esc defaults, optional **unassigned-by-default Pause/Resume hotkey**, conflict validation, and the **`Append Enter after scan` toggle (default off)**. The hotkey never replaces the mandatory mouse-clickable pause control.

Commit.

---

## Task 12 — Sounds, exit behavior, startup, and diagnostics

**Deliverable:** Operational polish and supportability.

**Create:**

```text
src/ScannerHelper.App/UI/NotificationSoundService.cs
src/ScannerHelper.Core/Diagnostics/IAppLogger.cs
src/ScannerHelper.Core/Diagnostics/ScanDiagnosticEvent.cs
src/ScannerHelper.Win32/HookHeartbeatMonitor.cs
```

**Silent-unhook heartbeat (spec 19.1):**

- A background monitor periodically emits a marked probe event via `SendInput` and confirms the hook callback observes it.
- The probe carries the same injected-event tag as normal output, so the capture pipeline swallows it and it can never reach the business application.
- If the probe is not observed within a bounded window: attempt automatic re-hook; on success log the incident (repeats mean the callback is too slow and must be investigated); on failure raise a prominent error and enter `PAUSED`.
- Probe interval of a few seconds is ample.

Rationale: exceeding `LowLevelHooksTimeout` once causes Windows to remove the hook **without notification**. The process keeps running and the UI keeps showing `SKU MODE` while raw codes flow straight into the warehouse system — silently wrong data, which is worse than a crash because nothing reveals it. Prevention alone is insufficient; a single GC pause can trip the timeout in an otherwise correct implementation.

**Mode sound:** short, non-intrusive, configurable off.

**Error sound:** distinct, configurable off.

**Exit:**

- X opens bilingual confirmation.
- Cancel is safe default.
- Exit unhooks input and unregisters native resources before process termination.

**Start with Windows:** implement using a standard per-user Windows startup mechanism; failures surface in Settings rather than silently succeeding.

**Diagnostics:** rolling local logs with timestamp, version, scanner identity summary, mode, parse/validation outcome, timeout, disconnect/reconnect, Force Send/Cancel. Respect optional barcode masking.

Ensure logging is asynchronous/non-blocking relative to input hooks.

Commit.

---

## Task 13 — Pilot hardening and acceptance test pass

**Deliverable:** Release candidate proven on representative warehouse systems.

Run automated tests:

```bash
dotnet test ScannerHelper.sln
```

Perform manual acceptance matrix on at least:

- Windows 10 workstation if available
- Windows 11 workstation
- At least two different scanner models/brands if available
- **The laptop's built-in keyboard** (spec assumption A4). An external USB keyboard is not a substitute: the internal keyboard is usually a PS/2 or ACPI device rather than USB HID, which is precisely the axis device identification turns on. Test an external keyboard additionally if one is available, never instead.

Verify every acceptance criterion in `docs/SCANNER_HELPER_SPEC.md`.

Stress test:

- 500+ sequential scans per device
- rapid F8 toggles between scans
- scanner disconnect during idle
- scanner disconnect mid-scan
- invalid fixed-position input
- invalid regex config
- non-matching regex scan
- validation failure + F10
- validation failure + Esc
- Compact error expansion without focus theft
- Full/Compact switching under 125%/150% DPI
- pause and resume **using the mouse only**, from both Full and Compact; verify scanner input passes through untouched while paused and the prior mode returns on resume
- forcibly remove the hook and confirm the heartbeat notices, re-hooks or fails loudly, and never keeps showing a confident operational state
- with `AppendEnterAfterScan` off (default), confirm the business web application still submits correctly; **if it does not, enable the setting on the spot, confirm it fixes the page, and record which way the pilot went**
- ordinary typing and IME composition remain usable in the business application while Scanner Helper is active

**Deployment readiness (spec 22) — start early, these have lead times:**

- executable is **code-signed**; an unsigned binary doing global hooking plus `SendInput` is close to guaranteed to be flagged as a keylogger
- signed binary and install path are **allow-listed by warehouse IT** on every pilot machine
- a named IT contact exists who can re-allow the application after a definition update
- integrity levels confirmed on real machines: business application and Scanner Helper both non-elevated (assumption A2)

Do not call V1 production-ready until the input-correlation mechanism has demonstrated no raw-character leakage in the pilot environment.

---

## Recommended implementation order

Do not begin with WPF polishing.

Use this sequence:

```text
── Phase A — macOS, no hardware ─────────────────────────
1.  Solution boundaries (Core + Core.Tests only)
2.  Pure parsing/validation/mode logic
3.  Settings model
6.  Scan state machine + InputEventCorrelator + ScanSession
7a. IKeyboardOutputService interface + Force Send workflow (fake output)
8a. HotkeyCoordinator routing rules
5a. DeviceIdentityMatcher

── Phase B — Windows + real scanner ─────────────────────
4a. INPUT OBSERVATION SPIKE (measure event ordering)
4b. INPUT INTERCEPTION SPIKE (hard gate)
5b. Scanner binding + device lifecycle
7b. SendInput output implementation
8b. Hotkeys against real keyboard/scanner
9.  Full/Compact UI (incl. mouse-clickable pause)
10. Localization
11. Settings UI
12. Diagnostics, heartbeat, sounds, exit, startup
13. Pilot validation
```

Two ordering principles:

1. **The input spike stays as early as Windows access allows**, because it is the architecture's highest-risk assumption. Within Phase B it comes first.
2. **Pure logic is written before the spike, not after.** This is not merely a workaround for lacking a Windows machine. Building `InputEventCorrelator` as a tested pure function first means the spike has exactly one unknown to resolve — what real event sequences look like — instead of debugging unproven logic against unknown hardware behavior at the same time. That confusion is precisely how timing heuristics get introduced to make symptoms disappear, which the spec forbids.
