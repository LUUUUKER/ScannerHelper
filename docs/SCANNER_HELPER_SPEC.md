# Scanner Helper — Product & Technical Specification

## 1. Purpose

Scanner Helper is a Windows desktop utility for warehouse workstations using USB barcode scanners in **HID Keyboard** mode.

The warehouse's existing business application already keeps focus in its own input field and internally switches between asking for an **SN** and asking for an **SKU**. The worker should not need to scan twice.

Scanner Helper sits between physical input and the focused business application:

- In **SN Mode**, a scanner read is emitted unchanged.
- In **SKU Mode**, the same scanner read is parsed to extract an SKU, validated, then emitted.
- The worker presses a global hotkey to toggle the output mode.
- The existing warehouse software must not require modification or integration.

The core product goal is **fast operation with visible state and minimal silent errors**.

---

## 2. Platform and Tech Stack

### Supported platform

- Windows 10 and Windows 11 only for V1.
- x64 is the primary deployment target.
- Barcode scanners may be different brands/models, as long as Windows exposes them as HID keyboard-like devices.

### 2.1 Deployment environment assumptions (confirmed with the warehouse)

These are **confirmed facts about the target environment**, not guesses. The V1 architecture depends on them. If any turns out to be false, the affected design decision must be revisited before the pilot.

| # | Assumption | Why it matters |
|---|---|---|
| A1 | The business application is a **web application** running in Chrome or Edge. | Browsers handle `SendInput` with `KEYEVENTF_UNICODE` reliably, so output can be emitted as Unicode characters instead of simulated scan codes. This sidesteps keyboard-layout mismatches entirely. |
| A2 | The browser runs at **normal (non-elevated) user privilege**. | Scanner Helper runs at the same integrity level. If the business application were elevated, UIPI would prevent the low-level hook from observing its keystrokes **and** prevent `SendInput` from reaching it — the entire user-mode architecture would fail. |
| A3 | Only one operator uses one workstation with one scanner at a time. | Justifies the single-scanner binding model. |

**Consequence of A2 — hard requirement:** Scanner Helper must **not** request elevation, and must be verified against the business application at matching integrity level. If the warehouse later switches to an elevated business application, stop and re-evaluate the architecture; do not attempt to work around UIPI.

### 2.2 Output encoding decision

Emit output using `SendInput` with `KEYEVENTF_UNICODE`, one character at a time.

Rationale: the raw scan is reconstructed into a `string` inside Scanner Helper, and re-emitting it as Unicode characters means the result does not depend on the active keyboard layout, on CapsLock state, or on the scanner's own configured layout. Simulating scan codes would reintroduce all three failure modes.

### Recommended stack

- **Language:** C#
- **Runtime:** .NET 8
- **UI:** WPF
- **Architecture:** MVVM
- **MVVM helper:** `CommunityToolkit.Mvvm`
- **Win32 interop:** direct P/Invoke for Raw Input, low-level keyboard hook, global hotkeys where needed, window activation/topmost behavior, and `SendInput`.
- **Configuration:** JSON via `System.Text.Json`
- **Localization:** `.resx` resources for `en-US` and `zh-CN`
- **Testing:** xUnit
- **Logging:** lightweight structured local logging; implementation may use `Microsoft.Extensions.Logging` plus a file provider, or a small focused logging abstraction. Avoid introducing a large dependency tree solely for logging.

### Explicitly not used in V1

- Kernel/filter driver
- Vendor-specific Zebra/Honeywell/Datalogic SDKs
- Cloud backend
- Accounts/authentication
- Multi-scanner concurrent processing
- Multi-profile customer rule system
- Scripting engine

---

## 3. Core Domain Concepts

### SN

Serial Number. In V1, SN Mode outputs the **entire scanned raw barcode string unchanged**.

### SKU

Stock Keeping Unit. In V1, SKU is derived from the same scanned raw string using one configured parsing rule.

### Scan Mode

```text
SN Mode  <---- F8 ---->  SKU Mode
```

- Application always starts in **SN Mode**.
- Previous scan mode is **not persisted** across restarts.
- Only a normal physical keyboard is allowed to trigger the F8 mode toggle.
- Scanner-generated F8 input must never toggle the mode.

### Force Send

If parsing or validation fails, Scanner Helper does **not automatically emit** data.

The operator can explicitly override the failure:

- `F10` = Force Send raw scanned code
- `Esc` = Cancel failed scan

Force Send does not change the current SN/SKU mode.

---

## 4. V1 Input Architecture

### 4.1 Why this design

The scanners are not uniform, and the product should avoid per-scanner prefix configuration and avoid kernel drivers.

V1 therefore uses a balanced user-mode architecture:

1. **Raw Input** identifies which physical HID device produced an input.
2. **Low-level keyboard hook (`WH_KEYBOARD_LL`)** sees keyboard events before the target application consumes them and can suppress them.
3. An **event correlator** associates the Raw Input event with the corresponding hook event.
4. Scanner events are buffered into one scan session.
5. The transformed result is emitted to the currently focused business application using `SendInput`.

### 4.2 Important constraint

The warehouse has confirmed:

> During an active barcode scan, the operator will not simultaneously type on the physical keyboard.

Treat one scan as an atomic operation. This materially simplifies event correlation.

### 4.3 Mandatory feasibility spike

Raw Input and `WH_KEYBOARD_LL` are separate Windows event streams. Device identity is available in Raw Input but not directly in the low-level hook callback.

The spike runs in **two stages**. Stage 4a answers questions; stage 4b builds on the answers. Combining them means debugging unknown logic against unknown hardware behavior simultaneously, which is how timing heuristics get quietly introduced to make symptoms go away.

#### Stage 4a — observe only (no interception)

Install the hook and Raw Input registration but **pass every event through unchanged**. Nothing is swallowed; the business application behaves exactly as if the tool were absent. Purely a measurement exercise, and consequently safe to run on a live machine.

It must answer:

1. **Which arrives first for the same physical keypress — the `WH_KEYBOARD_LL` callback, or `WM_INPUT`?** This is the question the whole architecture rests on. The hook callback must decide swallow-or-pass *synchronously*, and cannot block waiting; if the hook consistently precedes `WM_INPUT`, device identity is simply not available at decision time, and the withhold-then-replay design of section 5.3 is the only option.
2. What is the distribution of the time delta between the two streams?
3. What is the inter-character interval for each scanner model, versus a human typing?
4. Do Raw Input device identities remain stable across replug and reboot?
5. What exactly does a full scan look like — modifier keys, CapsLock interaction, the terminating Enter?

Record the measurements. They are inputs to 4b, and they are the evidence base for any later architectural argument.

#### Stage 4b — intercept and replay

Only after 4a. Must demonstrate, on representative warehouse PCs:

- a scanner can be bound by device identity;
- hook events can be correlated reliably to Raw Input events;
- the first scanner character does not leak into the business application;
- a normal keyboard still types correctly, **including IME composition** — the withhold-and-replay path is the main threat to this;
- modifier keys, key repeat, and CapsLock survive replay intact;
- a scanner scan can be fully suppressed and replaced;
- injected output from Scanner Helper is not recursively captured;
- the design remains stable for many consecutive scans.

#### Hard gate

If the spike is not reliable, **stop and revisit the input architecture.** Do not compensate with timing heuristics and do not proceed on the assumption that it will be tuned later. Report the observed event ordering and failure modes as evidence.

---

## 5. Input State Machine

### 5.1 Top-level states

```text
IDLE
  -> IDENTIFYING
  -> SCANNING
  -> PROCESSING
  -> IDLE

Temporary/system states:
ERROR
DISCONNECTED
PAUSED
```

`PAUSED` is a **required V1 safety valve**, fully defined in section 5.7.

### 5.2 IDLE

- Normal keyboard input behaves normally, except registered global control hotkeys.
- Scanner Helper is waiting for physical input.

### 5.3 IDENTIFYING

- A physical key event is temporarily withheld while the corresponding Raw Input source is resolved.
- If source is a normal keyboard, replay it via `SendInput` and return to IDLE.
- If source is the bound scanner, enter SCANNING and do not pass raw scanner content to the business application.

### 5.4 SCANNING

- Collect scanner characters into a buffer.
- Scan suffix is assumed to be Enter for V1 unless later made configurable.
- Enter terminates the scan.
- **The terminating Enter is consumed, not forwarded.** It marks the end of the scan and is discarded along with the rest of the raw scanner input. Whether Scanner Helper emits an Enter of its own afterwards is a separate, configurable decision — see section 10.
- User keyboard input during active scanning is not supported by the business workflow; do not attempt interleaving.
- Apply a scan inactivity timeout. Initial implementation default: approximately 300 ms since the last scanner character, but keep it configurable internally and validate with real hardware before finalizing.

On timeout:

- discard the incomplete scan;
- emit no business input;
- return to IDLE;
- show a visible scan error.

### 5.5 PROCESSING

If current mode is SN:

```text
output = rawScan
```

If current mode is SKU:

```text
rawScan -> parse -> candidateSku -> validate -> output
```

### 5.6 Injected input safety

All input produced by Scanner Helper must be marked/detected as injected and ignored by the capture pipeline.

Never allow:

```text
SendInput -> Hook -> Scanner Helper -> SendInput -> ...
```

### 5.7 PAUSED — the safety valve

`PAUSED` is not a feature; it is the operator's escape hatch when Scanner Helper itself is the problem.

#### Behavior

While PAUSED, the capture pipeline is fully bypassed:

- the low-level hook passes **every** event through unchanged (or is unhooked entirely);
- no buffering, no parsing, no validation, no `SendInput`;
- the scanner's raw keystrokes reach the business application directly, exactly as if Scanner Helper were not installed.

#### PAUSED is not the same as SN Mode

This distinction is the whole point of the state and must not be collapsed during implementation:

| | Scanner keystrokes | Goes through the pipeline? |
|---|---|---|
| **SN Mode** | swallowed by the hook → buffered → decoded to a string → re-emitted via `SendInput` | ✅ yes — SN is the *identity transform inside* the pipeline |
| **PAUSED** | passed straight through, never swallowed | ❌ no — the pipeline is *bypassed entirely* |

SN Mode still depends on capture, decoding, and re-emission all working correctly. PAUSED depends on none of them. That is why PAUSED — and only PAUSED — can rescue the operator when capture, correlation, or replay is broken.

#### Why it is required

| Scenario | Without PAUSED |
|---|---|
| Wrong device bound, or the correlator misjudges the source | The operator's **keyboard stops producing text** in the business application. |
| The operator needs to type Chinese via an IME | Swallow-and-replay may corrupt IME composition. |
| The parsing rule is misconfigured, so every scan lands in ERROR | The operator must press F10 on every single scan; throughput collapses. |
| Diagnosing an issue ("is it us or the website?") | The whole application must be exited and later rebound. |

#### Control placement — hard requirement

> The pause control **must be reachable with the mouse alone**, from both the Full window and the Compact window.

The primary failure mode this state exists to rescue is *"the keyboard no longer works"*. A hotkey-only escape hatch would be unusable in exactly the situation it was built for. A hotkey **may** be offered in addition, never instead.

#### State rules

- `PAUSED` is orthogonal to SN/SKU: pausing does not change the current mode, and resuming restores it.
- While PAUSED, no hotkey is intercepted — including F8. Because nothing is being captured, those keys reach the business application like any other keystroke. This is the correct, expected consequence of bypassing the pipeline, not a bug.
- **`PAUSED` is never persisted.** Like scan mode, the application always starts in the active (non-paused) state.
- Both Full and Compact windows must make the paused state unmistakable — text plus visual treatment, never color alone (see section 11.2).
- Entering and leaving PAUSED are both logged.

---

## 6. Scanner Device Binding

V1 supports one active scanner per workstation.

### First-run / rebind flow

1. Show scanner setup screen.
2. Prompt: “Please scan any barcode / 请扫描任意条码”.
3. Use Raw Input to determine which physical device produced the scan.
4. Show a friendly confirmation.
5. Save device identity.

Do not force users to pick from opaque entries such as multiple identical “HID Keyboard Device” names.

### Persisted device identity

Persist as much useful identity as Windows exposes:

- Device Path / Device Instance identity
- VID
- PID
- Friendly/device name if available
- Serial number if available

Matching priority should favor the most specific stable identity. VID/PID alone is not guaranteed unique.

### Reconnection

- Detect scanner disconnect.
- Show `Scanner Disconnected / 扫码枪已断开` visibly.
- When the known device returns, reconnect automatically when confidently matched.
- Provide `Rebind Scanner / 重新绑定扫码枪` in Settings.

---

## 7. Mode Manager and Hotkeys

### Global controls

- `F8`: Toggle `SN <-> SKU`
- `F10`: Force Send raw code, only valid while an error is awaiting operator decision
- `Esc`: Cancel failed scan, only valid while an error is awaiting operator decision

### Pass-through rule for intercepted hotkeys

When Scanner Helper acts on a control hotkey, it **consumes the key and does not forward it** to the business application. Otherwise a mode toggle could simultaneously trigger an unrelated shortcut in the web application.

| Key | Condition | Behavior |
|---|---|---|
| `F8` | from a non-scanner physical keyboard, not PAUSED | Toggle mode, **swallow** |
| `F8` | from the bound scanner | Treated as scanner data, never a mode command |
| `F10` | while an error is pending | Force Send, **swallow** |
| `F10` | no error pending | **Pass through** unchanged |
| `Esc` | while an error is pending | Cancel, **swallow** |
| `Esc` | no error pending | **Pass through** unchanged — Esc is far too common in ordinary web use to swallow unconditionally |
| any | while PAUSED | **Pass through** unchanged (see 5.7) |

### F8 behavior

- Works while warehouse business software has focus.
- Only a physical non-scanner keyboard may trigger it.
- Scanner-originated F8 is treated as scanner data, never a mode command.
- Mode change updates all visible UI immediately.
- Play a short mode-change sound by default.
- SN->SKU and SKU->SN should be audibly distinguishable if practical.
- Sound can be disabled in Settings.

### Startup mode

Always SN. Do not persist last mode.

---

## 8. SKU Parsing

V1 supports two parsing strategies.

### 8.1 Fixed Position

User-facing positions are **1-based**.

Configuration:

- Start Position
- Length

Example:

```text
Raw:   ABCD12345678XYZ
Start: 5
Length: 8
SKU:   12345678
```

Invalid bounds cause parse failure.

### 8.2 Regular Expression

Configuration:

- Regex pattern
- Capture Group index

Example:

```regex
^.{4}([A-Z0-9]{8})
```

Capture Group `1` returns the SKU.

Requirements:

- Use a finite regex timeout.
- Invalid regex must never crash the app.
- Missing capture group is parse failure.
- Regex timeout is parse failure with a specific diagnostic reason.

### 8.3 Rule test UI

Every parsing mode includes a test area:

- Test Raw Code
- Parsed SKU
- Parse status

A user should be able to paste a rule supplied by the technical advisor and immediately verify it against an example before saving.

---

## 9. SKU Validation

Validation is a distinct layer after parsing.

```text
Raw -> Parsing -> Candidate SKU -> Validation -> Valid SKU
```

All validation rules are optional.

If no validation rules are enabled, successful parsing means the SKU is valid.

If multiple validation rules are enabled, **all enabled rules must pass**.

### 9.1 Length validation

Support:

- Minimum length
- Maximum length

Exact length is represented by equal min/max values.

### 9.2 Character set validation

V1 presets:

- Numbers only
- Letters only
- Letters + Numbers
- Letters + Numbers + `-` + `_`

#### Case sensitivity

The presets define **which kinds of characters** are allowed. A separate, independent **`Ignore case`** toggle defines whether letter case matters.

| `Ignore case` | `Letters only` accepts |
|---|---|
| enabled (**default**) | `A-Z` and `a-z` |
| disabled | `A-Z` only |

Default is enabled, because rejecting an otherwise-valid barcode purely for containing a lowercase letter is a worse failure than accepting one. A site that genuinely requires uppercase should express that through the validation regex (`^[A-Z]+$`), which is unaffected by this toggle.

The toggle applies only to character-set validation. It has no effect on parsing, on length validation, or on the validation regex.

### 9.3 Validation Regex

Optional independent regex applied to the already parsed SKU.

This is separate from Parsing Regex.

Example:

```regex
^[A-Z]{3}\d{8}$
```

Also use a finite regex timeout.

### 9.4 Validation failure

- Do not automatically emit.
- Enter ERROR state.
- Show specific failure reason.
- Allow `F10 Force Send` or `Esc Cancel`.

---

## 10. Error Handling and Force Send

### Output contract

Every emission — normal success and Force Send alike — follows the same contract:

- Emitted via `SendInput` with `KEYEVENTF_UNICODE`, one character at a time (see 2.2).
- Content is emitted exactly as produced by the current mode — no trimming, no padding, no case conversion.
- A trailing Enter is appended **only if** the `Append Enter after scan` setting is enabled. **Default: disabled.**

#### Why this is a setting rather than a constant

Because the entire raw scan is swallowed, the scanner's own terminating Enter never reaches the business application either. With the setting disabled, the web application therefore receives **no Enter at all** from a scan — not "one fewer than before", but zero.

Barcode entry in web forms commonly relies on Enter to submit or to advance to the next field. Whether this particular application does is not yet known, and it cannot be determined without the real page.

Making it a setting costs one boolean and one branch, and means the answer can be changed **on the pilot floor in seconds** instead of requiring a code change, a rebuild, and a redeployment. The default is disabled per the product decision; if the page stops submitting during the pilot, enable it and move on.

This is a deliberately cheap hedge against an unknown, not an unresolved design question.

### Normal success

- Automatically emit output to the business application's existing focused field.
- Do not show intrusive success dialogs.
- Optionally update `Last scan` status in the full window.
- Do not add an extra success beep by default; scanners already typically beep.

### Parse/validation/timeout failure

- Emit no automatic business input.
- Show highly visible error UI.
- Play distinct error sound.
- Display raw code if one exists.
- Explain failure reason.

Controls:

```text
F10 = Force Send raw code
Esc = Cancel
```

### Force Send semantics

- Force Send always emits the **raw scanned code**, not a partially parsed candidate.
- Preserve current mode.
- Log the override.
- Return to the UI state that existed before the error.

### Cancel semantics

- Emit nothing.
- Preserve current mode.
- Log cancellation if useful.
- Return to previous UI state.

---

## 11. Window and UI Behavior

### Design principle

If Scanner Helper is running, the worker should always be able to see that it is running and which mode it is in.

The application cannot be minimized to an invisible state.

### 11.1 Full Window

Core content:

- Current Mode — largest visual element
- `F8` shortcut hint
- Scanner connection status
- Last scan summary/status
- **Pause / Resume button — mouse-clickable (see 5.7)**
- Settings entry
- Topmost pin button
- Minimize/compact button
- Close button

When PAUSED, the mode display must be visibly overridden by a paused indication, so the window can never show a confident `SN MODE` while the pipeline is actually bypassed.

Illustrative layout:

```text
+----------------------------------+
| Scanner Helper       Pin  _  [] X|
+----------------------------------+
|                                  |
|          CURRENT MODE            |
|                                  |
|            SN MODE               |
|                                  |
|       F8  Switch Mode            |
|                                  |
|  Scanner Connected               |
|                                  |
|  Last Scan                       |
|  ABC123456789                    |
|                                  |
|  [ Pause ]           Settings    |
+----------------------------------+
```

Paused:

```text
+----------------------------------+
| Scanner Helper       Pin  _  [] X|
+----------------------------------+
|                                  |
|            PAUSED                |
|     Scanner input passes         |
|     through untouched            |
|                                  |
|  Mode on resume:  SN MODE        |
|                                  |
|  Scanner Connected               |
|                                  |
|  [ Resume ]          Settings    |
+----------------------------------+
```

### 11.2 Visual differentiation

SN and SKU modes must be distinguishable by more than color.

Use:

- different prominent labels;
- supporting subtitle/description;
- distinct visual treatment;
- color as an additional cue, not the only cue.

Do not rely on color alone because of color-vision deficiency and low-quality warehouse displays.

### 11.3 Minimize means Compact

Clicking the minimize button does **not** hide the application.

It transitions:

```text
Full Window -> Compact Window
```

Compact window:

- small;
- draggable;
- always visible;
- always on top;
- shows current SN/SKU mode prominently;
- shows scanner disconnected state;
- shows paused state unmistakably;
- **has a mouse-clickable Pause/Resume control (hard requirement, see 5.7)**;
- has Restore and Close controls.

Example:

```text
+---------------------+
|  SN MODE   ⏸  [] X  |
+---------------------+
```

Paused:

```text
+---------------------+
|  PAUSED    ▶  [] X  |
+---------------------+
```

The Compact window is the state the application spends most of its time in, which makes it the escape hatch the operator can actually reach. Omitting the pause control here would defeat the purpose of section 5.7.

### 11.4 Topmost behavior

- Full Window has a user-controlled Always On Top preference.
- Compact mode forces Always On Top.
- Returning from Compact to Full restores the user's previous Full Window topmost preference.

Persist the user's Full Window topmost preference.

### 11.5 Window position

- Full and Compact windows are draggable.
- Remember previous window position(s).
- On launch, validate stored coordinates so the window cannot reopen off-screen after monitor changes.

### 11.6 Close behavior

`X` means actual program exit.

Clicking X opens a confirmation dialog:

```text
Are you sure you want to exit Scanner Helper?
Scanner processing will stop after you exit.

Cancel | Exit
```

Chinese equivalent must exist.

Safety behavior:

- default focus/action should be Cancel;
- Esc cancels;
- avoid making Enter an easy accidental exit path.

### 11.7 Error while Compact

If an error requiring user action occurs while Compact:

1. remember that pre-error UI state was Compact;
2. automatically expand to the full error view;
3. make it visible and topmost;
4. **do not steal input focus from the warehouse business application**;
5. process global F10/Esc;
6. after Force Send or Cancel, automatically return to Compact.

If the app was Full before error, remain Full after resolution.

### 11.8 Focus rule

Showing/updating Scanner Helper must not steal the keyboard focus used by the warehouse application.

`Show Window != Activate Window`.

This is a hard requirement for notifications, mode updates, reconnect notices, and automatic error expansion.

### 11.9 Tray icon

A tray icon may exist as a secondary status/diagnostic entry point, but it must **not** provide a way for the running application to become completely invisible. V1 may omit it if it adds no operational value; the visible Compact window is the primary run-state indicator.

---

## 12. Localization

V1 supports exactly:

- English (`en-US`)
- Simplified Chinese (`zh-CN`)

### Default selection

- On first launch, inspect Windows UI culture.
- Chinese system -> Chinese.
- Otherwise -> English.
- Once the user manually chooses a language, persist that choice.
- Language changes apply immediately without restart.

### Resource strategy

All user-visible strings must come from resource files, e.g.:

```text
Resources/
  Strings.resx
  Strings.zh-CN.resx
```

or equivalent culture-specific organization.

Do not hardcode user-visible English/Chinese strings in ViewModels or services.

### Terminology

Keep `SN` and `SKU` untranslated in the primary mode labels in both languages.

Examples:

```text
SN MODE
SKU MODE
```

Supporting description may be localized.

Hotkeys never change with language.

---

## 13. Settings

Use a left-navigation Settings page with five sections.

### 13.1 General

- Language / 语言
- Start with Windows / 开机启动
- Mode switch sound / 模式切换提示音
- Error sound / 异常提示音
- Remember window position / 记住窗口位置
- Full Window Always On Top preference / 主窗口置顶偏好

Compact mode topmost is mandatory and cannot be disabled.

### 13.2 Scanner

- Current Scanner
- Connection Status
- Rebind Scanner
- Test Scanner

Advanced device information may be shown in diagnostics, not as the primary user experience.

#### Test Scanner behavior

`Test Scanner` opens a self-contained verification panel that lets the operator confirm the full pipeline works **without touching the business application**:

1. The panel takes focus and prompts `Scan any barcode / 请扫描任意条码`.
2. While it is open, results are displayed in the panel and **nothing is emitted via `SendInput`** — no output can reach the business application.
3. For each scan it shows: source device (bound scanner vs. other), raw code, parsed SKU, parse result, validation result.
4. Closing the panel returns to normal operation.

This is the primary tool for verifying a newly configured parsing rule against real hardware, and for answering "is the scanner bound correctly?" without risking bad data in the warehouse system.

### 13.3 Scan Mode

- Toggle hotkey, default F8
- Force Send hotkey, default F10
- Cancel hotkey, default Esc
- Pause/Resume hotkey, **default unassigned** — optional convenience only. The mouse-clickable Pause control in the Full and Compact windows is mandatory and is never replaced by this hotkey (see 5.7).
- `Append Enter after scan` / `扫描后自动回车`, **default disabled** (see section 10). Applies to both SN and SKU output and to Force Send. Place it here rather than under SKU Parsing, since it is not mode-specific.

Startup mode is always SN and should not be exposed as an ordinary setting. The paused state is likewise never persisted and never exposed as a startup preference.

If hotkeys are configurable:

- detect conflicts;
- refuse invalid duplicate assignments;
- protect reserved workflow conflicts.

### 13.4 SKU Parsing

- Rule Type: Fixed Position / Regex
- Rule-specific fields
- SKU Validation section
  - optional min/max length
  - optional character-set preset
  - `Ignore case` toggle, default enabled (see 9.2)
  - optional validation regex
- Test Rule area
- Save
- Restore Defaults if defaults exist

### 13.5 Diagnostics

- App version
- Scanner device identity
- Recent scan/error logs
- Export logs
- Clear logs
- Optional `Mask barcode data in logs`

---

## 14. Configuration Persistence

Suggested settings model:

```text
AppSettings
  SchemaVersion
  Language
  StartWithWindows
  ModeSwitchSoundEnabled
  ErrorSoundEnabled
  RememberWindowPosition
  AppendEnterAfterScan     (default false)
  FullWindowAlwaysOnTop
  FullWindowBounds
  CompactWindowBounds
  Hotkeys                  (Toggle, ForceSend, Cancel, PauseResume)
  ScannerBinding
  SkuParsingRule
  SkuValidationRule        (includes IgnoreCase)
  DiagnosticsOptions
```

Store settings per user under an appropriate Windows application data directory, not beside the executable.

Use atomic save semantics where practical so a crash during save does not corrupt configuration.

Never persist the current SN/SKU mode.

Never persist the paused state either — the application always starts active (see 5.7).

---

## 15. Diagnostics and Logging

Log enough to troubleshoot production issues without creating unlimited files.

Recommended event fields:

- Timestamp
- Application version
- Scanner identity summary
- Mode
- Raw code (or masked value depending on setting)
- Parsed SKU if applicable
- Parse result
- Validation result
- Error reason
- Force Sent / Cancelled
- Scan timeout
- Device disconnect/reconnect
- Hotkey mode changes

Use rolling logs by date or total size. Suggested retention: configurable internally around 7–30 days; choose a conservative default and prevent unlimited growth.

Diagnostics must never block the hook callback.

---

## 16. Suggested Solution / Folder Structure

```text
ScannerHelper/
├─ ScannerHelper.sln
├─ CLAUDE.md
├─ README.md
├─ docs/
│  ├─ SCANNER_HELPER_SPEC.md
│  └─ IMPLEMENTATION_PLAN.md
├─ src/
│  ├─ ScannerHelper.App/
│  │  ├─ ScannerHelper.App.csproj
│  │  ├─ App.xaml
│  │  ├─ App.xaml.cs
│  │  ├─ Views/
│  │  │  ├─ MainWindow.xaml
│  │  │  ├─ MainWindow.xaml.cs
│  │  │  ├─ CompactWindow.xaml
│  │  │  ├─ CompactWindow.xaml.cs
│  │  │  ├─ SettingsWindow.xaml
│  │  │  └─ SettingsWindow.xaml.cs
│  │  ├─ ViewModels/
│  │  │  ├─ MainViewModel.cs
│  │  │  ├─ CompactViewModel.cs
│  │  │  ├─ SettingsViewModel.cs
│  │  │  └─ ErrorStateViewModel.cs
│  │  ├─ Localization/
│  │  │  ├─ Strings.resx
│  │  │  └─ Strings.zh-CN.resx
│  │  └─ UI/
│  │     ├─ WindowStateCoordinator.cs
│  │     ├─ FocusSafeWindowPresenter.cs
│  │     └─ NotificationSoundService.cs
│  │
│  ├─ ScannerHelper.Core/            ← net8.0, cross-platform, ZERO Windows dependencies
│  │  ├─ ScannerHelper.Core.csproj
│  │  ├─ Abstractions/
│  │  │  └─ ISystemClock.cs
│  │  ├─ Domain/
│  │  │  ├─ ScanMode.cs
│  │  │  ├─ ScanResult.cs
│  │  │  ├─ ParseResult.cs
│  │  │  ├─ ValidationResult.cs
│  │  │  ├─ KeyEvent.cs
│  │  │  ├─ RawInputEvent.cs
│  │  │  └─ ScannerDeviceIdentity.cs
│  │  ├─ Input/                      ← pure logic and interfaces only
│  │  │  ├─ IScannerInputSource.cs
│  │  │  ├─ InputEventCorrelator.cs
│  │  │  ├─ DeviceIdentityMatcher.cs
│  │  │  ├─ ScanSession.cs
│  │  │  ├─ ScanInputCoordinator.cs
│  │  │  └─ HotkeyCoordinator.cs
│  │  ├─ Modes/
│  │  │  └─ ModeManager.cs
│  │  ├─ Parsing/
│  │  │  ├─ ISkuParser.cs
│  │  │  ├─ FixedPositionSkuParser.cs
│  │  │  ├─ RegexSkuParser.cs
│  │  │  └─ SkuParserFactory.cs
│  │  ├─ Validation/
│  │  │  ├─ ISkuValidator.cs
│  │  │  ├─ CompositeSkuValidator.cs
│  │  │  ├─ LengthSkuValidator.cs
│  │  │  ├─ CharacterSetSkuValidator.cs
│  │  │  └─ RegexSkuValidator.cs
│  │  ├─ Output/
│  │  │  └─ IKeyboardOutputService.cs    ← interface only
│  │  ├─ Settings/
│  │  │  ├─ AppSettings.cs
│  │  │  ├─ ISettingsStore.cs
│  │  │  └─ JsonSettingsStore.cs
│  │  └─ Diagnostics/
│  │     ├─ IAppLogger.cs
│  │     └─ ScanDiagnosticEvent.cs
│  │
│  └─ ScannerHelper.Win32/           ← net8.0-windows, ALL native code lives here
│     ├─ ScannerHelper.Win32.csproj
│     ├─ Native/
│     │  ├─ NativeMethods.cs
│     │  ├─ RawInputNative.cs
│     │  ├─ KeyboardHookNative.cs
│     │  ├─ SendInputNative.cs
│     │  ├─ WindowNative.cs
│     │  └─ DeviceNotificationNative.cs
│     ├─ RawInputDeviceResolver.cs
│     ├─ LowLevelKeyboardHook.cs
│     ├─ HookHeartbeatMonitor.cs
│     ├─ ScannerDeviceWatcher.cs
│     ├─ Win32ScannerInputSource.cs         ← implements Core.IScannerInputSource
│     ├─ SendInputKeyboardOutputService.cs  ← implements Core.IKeyboardOutputService
│     └─ SystemClock.cs                     ← implements Core.ISystemClock
│
└─ tests/
   ├─ ScannerHelper.Core.Tests/      ← net8.0, runs anywhere including macOS
   │  ├─ Parsing/
   │  ├─ Validation/
   │  ├─ Modes/
   │  ├─ Input/
   │  └─ Settings/
   └─ ScannerHelper.Diagnostics.Harness/   ← net8.0-windows, MANUAL, not xUnit
      └─ (see section 20)
```

### Responsibility rule

- `App`: WPF UI and user interaction only.
- `Core`: business behavior; **targets `net8.0` and must compile and test on a non-Windows machine.**
- `Win32`: unsafe/native Windows integration only; targets `net8.0-windows`.
- `tests`: behavior verification.

Do not place parsing or input-state business logic in `MainWindow.xaml.cs`.

### The Core boundary is enforced by the target framework

`ScannerHelper.Core` targets `net8.0`, **not** `net8.0-windows`, and references neither `ScannerHelper.Win32` nor WPF. Dependencies point inward: `Win32` implements interfaces that `Core` defines, and `App` wires the two together.

This is not stylistic. Two concrete consequences:

1. **The build breaks immediately** if native code leaks into `Core` — the layering violation cannot pass silently.
2. **Core is developed and tested on macOS.** All parsing, validation, settings, mode, session, correlation, and hotkey-routing logic is written and unit-tested before any Windows hardware is involved.

Point 2 matters most for `InputEventCorrelator`, the single highest-risk component in the product. Because it is a pure function over a sequence of timestamped events — it consumes events and returns decisions, and never calls a Windows API — its ordering, timeout, out-of-order, and injected-event branches are all exhaustively unit-testable with synthetic event sequences. The Windows spike (section 4.3) is then left to answer only one question: *what do real event sequences actually look like?* — rather than validating logic and hardware simultaneously.

---

## 17. File Responsibilities

### `ScanInputCoordinator.cs`

Owns the high-level input state machine. It coordinates device resolution, hook event suppression/replay, scan session buffering, mode processing, and output. It must not contain WPF UI code.

It also **owns the paused flag** (section 5.7). While paused it returns a pass-through decision for every event without inspecting it, which is what makes PAUSED independent of the correctness of correlation, buffering, and replay.

### `InputEventCorrelator.cs`

Isolates the experimental Raw Input <-> hook correlation strategy. This is intentionally separate so the implementation can be replaced if the spike reveals reliability issues.

**It must remain a pure function over timestamped events.** It receives hook events and Raw Input events with timestamps and returns a decision (`swallow` / `pass through` / `replay` / `still undecided`). It never calls a Windows API, never touches the clock directly (it takes time via `ISystemClock`), and therefore is fully unit-testable on any platform with synthetic event sequences.

### `IScannerInputSource.cs` / `Win32ScannerInputSource.cs`

`IScannerInputSource` is the `Core`-side abstraction of "keyboard events arrive, tagged with their source device." `Win32ScannerInputSource` is the only implementation, living in `ScannerHelper.Win32`; it owns the hook, the Raw Input registration, and the plumbing between them. Tests substitute a fake source.

### `DeviceIdentityMatcher.cs`

Pure logic deciding whether an observed device identity matches the persisted binding, and with what confidence (device path/instance > serial > VID/PID). Separated from device enumeration so the matching rules can be unit-tested with synthetic identities.

### `HookHeartbeatMonitor.cs`

Detects silent unhooking (section 19). Lives in `ScannerHelper.Win32` because it must emit a real probe event.

### `ScanSession.cs`

Represents one atomic scan. Holds buffer, timestamps, terminator handling, timeout state, and completion result.

### `ModeManager.cs`

Owns only current scan mode and safe toggle behavior. Startup is SN. It exposes change notifications/events without knowing WPF.

### `ISkuParser` implementations

Pure deterministic parsing. No UI, no device code, no `SendInput`.

### Validators

Pure deterministic validation. Composite validator executes all enabled validators and returns structured failure reasons.

### `SendInputKeyboardOutputService.cs`

The only implementation of `IKeyboardOutputService`. Lives in `ScannerHelper.Win32` because it calls `SendInput` directly. Emits content as Unicode characters per section 2.2, appends no terminator per section 10, and tags every synthetic event so the capture pipeline can recognize and ignore it.

### `ISystemClock.cs`

Injected time source. Every timeout in `Core` — scan inactivity, correlation windows — reads the clock through this interface so timing behavior can be tested deterministically instead of with `Thread.Sleep`.

### `WindowStateCoordinator.cs`

Coordinates Full, Compact, and Error-expanded behavior, including restoration to pre-error state.

### `FocusSafeWindowPresenter.cs`

Encapsulates “show/update without stealing focus”. This requirement should not be scattered through arbitrary code-behind.

### `JsonSettingsStore.cs`

Owns load/save/default/migration behavior. Add a configuration schema version from V1 so future settings migrations are possible.

---

## 18. UI Design Style

Use a professional industrial utility style:

- minimal visual noise;
- high readability at a glance;
- generous whitespace in Full mode;
- prominent mode status;
- large typography for `SN MODE` / `SKU MODE`;
- clear connected/disconnected/error states;
- no decorative animation;
- mode transition feedback may use a short subtle animation/fade but must never slow operation;
- avoid consumer-app aesthetics that reduce information density or clarity;
- support common Windows display scaling (100%, 125%, 150% at minimum).

Accessibility:

- never use color alone to express mode or error;
- maintain readable contrast;
- keyboard operation must remain possible for the core workflow.

---

## 19. Security and Reliability Constraints

- Keep low-level hook callback extremely fast.
- Do not parse regex, perform file I/O, log synchronously, update UI, or perform expensive correlation logic directly inside the hook callback.
- Marshal work to dedicated processing components/queues.
- Never swallow normal keyboard input indefinitely.
- Any event that cannot be confidently resolved should fail safely and be surfaced diagnostically.
- Prevent recursion from injected input.
- Handle device disconnect/reconnect.
- Handle app shutdown by unhooking and unregistering input cleanly.
- Protect configuration against corruption.
- Regex operations must use finite timeouts.
- No kernel driver in V1.

### 19.1 Silent unhook detection (required)

Windows enforces `LowLevelHooksTimeout` (default 300 ms). If a hook callback takes too long even once, Windows **removes the hook and does not notify the application**.

The resulting failure is silent and worse than a crash:

```text
Scanner Helper process:  still running
UI:                      still showing "SKU MODE"
Hook:                    gone
   ↓
Raw scan ABCD12345678XYZ goes straight into the business application.
The operator believes an SKU was sent. Nothing indicates otherwise.
```

A crash is visible; this is not. It produces **silently wrong data** in the warehouse system.

The bullets above are *prevention*. Prevention alone is not sufficient, because a single GC pause or a blocked write can trip the timeout on an otherwise correct implementation. V1 must therefore also **detect** the condition:

- A background monitor periodically emits a marked probe event via `SendInput` and confirms the hook callback observes it.
- The probe uses the same injected-event tagging as normal output (section 5.6), so it is swallowed by the capture pipeline and can never reach the business application.
- If the probe is not observed within a bounded window, the hook is presumed removed:
  1. attempt to re-install the hook automatically;
  2. on success, log the incident — repeated occurrences indicate a callback that is too slow and must be investigated;
  3. on failure, raise a **prominent, unmistakable** error and enter `PAUSED` (section 5.7), so the operator sees a stopped tool rather than a lying one.
- The probe interval must be low-frequency enough to be negligible; a few seconds is ample.

**Design rule:** the application must never display a confident operational state it has not verified. When in doubt, fail loudly and stop.

---

## 20. Testing Strategy

### Unit tests

Must cover:

- Fixed position parser boundaries
- Regex parser success/failure/capture group/timeout
- Length validation
- Character-set validation
- Validation regex
- Composite validation
- Mode toggling and startup mode
- Force Send semantics
- Scan timeout state machine
- Configuration defaults and persistence
- Localization key coverage where feasible
- Character-set validation with `Ignore case` enabled and disabled
- `InputEventCorrelator` decisions over synthetic event sequences: hook-before-RawInput, RawInput-before-hook, out-of-order arrival, missing counterpart, correlation timeout, injected-event rejection
- `ScanInputCoordinator` in PAUSED: every event returns pass-through, nothing is buffered, nothing is emitted
- Hotkey pass-through rules from section 7 (swallow vs. forward per condition)

All of the above run on `net8.0` and must pass on a non-Windows development machine.

### Manual diagnostics harness — not automated tests

The checks below require real hardware, a real focused application, and a human observer. They are **not** xUnit tests and must not be written as such; an xUnit project that cannot run in CI and needs a person watching a screen is a test suite in name only.

Instead, V1 ships `tests/ScannerHelper.Diagnostics.Harness/`: a small WPF utility with a focused input box, a live event log, and an explicit pass/fail checklist the operator ticks off. It is the artifact used to satisfy the section 4.3 spike gate and the section 21 acceptance run, and its results are recorded per machine and per scanner model.

The only genuinely automatable Windows-side checks — `SendInput` reaching a focused control, settings round-tripping on a real filesystem — may stay in a small xUnit project guarded by a Windows-only condition.

Must be verified on real Windows hardware via the harness:

- Scanner binding through Raw Input
- Ordinary keyboard unaffected
- Scanner first character does not leak
- Scanner raw input fully suppressed
- SN mode emits exact raw string
- SKU mode emits exact parsed SKU
- F8 works globally from physical keyboard
- Scanner-originated F8 cannot toggle mode
- F10 only applies to pending error
- Esc cancels pending error
- Injected output is not recaptured
- Business application focus remains intact during UI changes
- Compact -> error Full -> resolve -> Compact restoration
- Disconnect/reconnect behavior
- Hundreds/thousands of sequential scans without degradation
- With `Append Enter after scan` disabled (the default), emitted output contains no trailing Enter and the business web application still behaves acceptably. **If the page no longer submits, enable the setting on the spot and confirm it does** — this is the specific scenario the setting exists for, and the pilot must record which way it went
- Ordinary typing and IME composition still work while Scanner Helper is active
- PAUSED: scanner input passes straight through untouched; the pause control is reachable **with the mouse only**; resuming restores the previous mode
- Silent-unhook detection: with the hook forcibly removed, the heartbeat notices, re-installs or fails loudly, and never keeps displaying a confident operational state (section 19.1)

---

## 21. Acceptance Criteria for V1

V1 is ready for warehouse pilot only when all are true:

1. User can bind an arbitrary supported HID keyboard scanner without changing scanner firmware/prefix configuration.
2. Normal keyboard typing continues to work.
3. Scanner input is not leaked raw into the warehouse application before processing.
4. SN Mode outputs the full code unchanged.
5. SKU Mode supports Fixed Position and Regex extraction.
6. Optional SKU Validation works as specified.
7. F8 globally toggles mode from the physical keyboard only.
8. Parse/validation failure automatically produces visible error UI and no automatic output.
9. F10 Force Send and Esc Cancel work globally during an error.
10. Compact window is always visible/topmost while the program runs in Compact mode.
11. Error from Compact automatically expands without stealing business-app focus and restores afterward.
12. X requires confirmation and truly exits.
13. English and Simplified Chinese are complete and switch without restart.
14. Scanner disconnect is visible and reconnection is handled.
15. Logging is sufficient to diagnose parse/input failures without blocking input handling.
16. The mandatory input-correlation spike passes on representative warehouse hardware.
17. PAUSED can be entered and left **using the mouse alone**, from both Full and Compact, and while paused all input reaches the business application untouched.
18. Silent unhook is detected and either self-healed or surfaced loudly; the UI never displays a confident operational state that has not been verified.
19. Ordinary typing and IME composition in the business application remain usable while Scanner Helper is active.
20. `ScannerHelper.Core` builds and its full unit test suite passes on a non-Windows machine, proving the layering boundary holds.

---

## 22. Deployment Risks

Risks that do not show up during development but can block or break a warehouse rollout. Each needs an owner before the pilot.

### 22.1 Antivirus / EDR interference — highest deployment risk

A global low-level keyboard hook that captures keystrokes and synthesizes input via `SendInput` is, behaviorally, **indistinguishable from a keylogger**. Endpoint protection (Windows Defender, CrowdStrike, SentinelOne, 360, and similar) may quarantine the executable, silently block the hook, or flag the workstation.

This is not hypothetical, and it is not something the code can defend against. Mitigation is organizational and must start **before** the pilot, not after a machine is quarantined:

- **Code-sign the executable** with a real code-signing certificate (see 22.1.1). An unsigned binary doing this is close to guaranteed to be flagged. Lead time is measured in days to weeks — start early.
- **Get an explicit allow-list entry from warehouse IT** for the signed binary and its install path, on every pilot machine.
- **Have a named IT contact** who can re-allow the application if a definition update flags it later.
- Note that a *blocked hook* may present exactly like the silent-unhook failure in section 19.1. The heartbeat detection is what turns this into a visible failure instead of silent bad data.

#### 22.1.1 Obtaining a code-signing certificate

Practical guidance, since this has the longest lead time of anything in the project and is easy to discover too late.

**Certificate types**

| Type | What it gives you | Trade-off |
|---|---|---|
| **OV** (Organization Validation) | A valid signature; the publisher name shows instead of "Unknown Publisher" | SmartScreen reputation must still accumulate over downloads/time, so early installs may still warn |
| **EV** (Extended Validation) | Same, plus **immediate SmartScreen reputation** — no warning from day one | More expensive; stricter identity vetting |

For a warehouse tool installed by IT on a handful of machines, **OV is usually sufficient**, because IT is allow-listing the binary anyway and SmartScreen reputation matters most for public downloads. Choose EV if the warehouse's security policy demands it or if SmartScreen prompts would confuse operators during rollout.

**What is required**

- **A legal business entity.** Certificate authorities verify an organization, not a person. Individual code-signing certificates exist at some CAs but are harder to obtain and less useful here. **Decide early whose entity signs this** — yours or the warehouse's. This is a business decision that gates everything else.
- Verifiable business registration, plus presence in a third-party business directory or an acceptable legal/accountant attestation.
- A verifiable phone number at the registered organization; CAs typically place a verification call.
- **Hardware key storage.** Since 2023, publicly trusted code-signing private keys must live on a FIPS-validated hardware token or a cloud HSM. Expect either a shipped USB token or a cloud signing service. Physical token shipping adds days — factor it in.

**Common issuers:** DigiCert, Sectigo, GlobalSign, SSL.com. Pricing is typically low hundreds of USD per year, varying by type and term; verify current pricing directly, as it changes.

**Realistic timeline**

| Stage | Typical |
|---|---|
| Gathering documents | 1–5 days, longer if registration details are stale or inconsistent |
| CA verification | 1–5 business days once documents are clean |
| Hardware token delivery | several days, if physical |
| **Total** | **roughly 1–3 weeks**, and considerably longer if the entity is not already in the business directories CAs consult |

**Signing**

Sign the release build, and **always timestamp it**:

```text
signtool sign /fd SHA256 /tr <timestamp-server-url> /td SHA256 ScannerHelper.exe
```

Timestamping is not optional. Without it, every signature becomes invalid the moment the certificate expires, and already-deployed copies start failing.

**If signing is not possible for the pilot**

A pilot can proceed unsigned, but understand what is being accepted:

- SmartScreen warns on every machine at install time.
- EDR is substantially more likely to quarantine the binary.
- IT must allow-list **per machine**, by hash or path, and a rebuild changes the hash — every new build needs re-allow-listing.
- A definition update can re-flag the application at any time, with no warning.

Treat unsigned deployment as a time-boxed pilot concession, not a shipping posture. Start the certificate process in parallel regardless.

**Signing is not a complete answer.** A valid signature establishes *who* wrote the software; it does not make hooking-plus-`SendInput` look less like a keylogger to behavioral detection. Signing plus an explicit IT allow-list entry is the combination that works. Plan for both.

### 22.2 Privilege mismatch

Covered by assumption A2 in section 2.1. If the business application is ever run elevated, the user-mode architecture fails outright. Confirm integrity levels on real pilot machines rather than assuming.

### 22.3 Keyboard layout and scanner configuration drift

Output is emitted as Unicode (section 2.2), which protects the *output* path. The *input* path still decodes scan codes to characters and remains sensitive to the active layout and to CapsLock. Verify decoding on every pilot machine, and re-verify if a machine's locale or a scanner's configured layout changes.

### 22.4 Barcode content assumptions

V1 assumes scans consist of printable characters terminated by Enter. Barcodes containing embedded Tab or GS (`0x1D`) separators — common in GS1 — are **not supported by the V1 model**. If such codes appear in the pilot, the scan model needs revisiting; do not paper over it with parsing rules.

---

## 23. Deferred Features

Explicitly postpone unless warehouse pilot proves necessary:

- Kernel filter driver
- Vendor-specific scanner adapters
- Serial/COM scanner mode
- Multiple active scanners
- Multiple customer rule profiles
- Cloud-managed configuration
- Remote telemetry
- Role-based settings permissions
- Automatic software updater
- Admin-only advanced mode

