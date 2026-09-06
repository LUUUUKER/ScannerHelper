# Claude Code Instructions — Scanner Helper

Before writing or changing code, read:

1. `docs/SCANNER_HELPER_SPEC.md`
2. `docs/IMPLEMENTATION_PLAN.md`
3. `docs/TEST_PLAN_PHASE_A.md` — approved test cases for Tasks 1–3, plus nine resolved design decisions (D-1…D-9) that the implementation must honor

These files are the source of truth. Do not silently reinterpret business behavior.

## Non-negotiable constraints

- Windows 10/11 only, C# + .NET 8 + WPF.
- Scanner devices are heterogeneous HID Keyboard scanners.
- V1 uses user-mode Raw Input + `WH_KEYBOARD_LL` + event correlation + `SendInput`; no kernel driver.
- A technical input-correlation spike must pass before building the complete application. It is split into 4a (observe only) and 4b (intercept); 4a's measurements must exist before 4b is written.
- `ScannerHelper.Core` targets `net8.0`, references nothing, and must build and unit-test on macOS. All native code lives in `ScannerHelper.Win32` (`net8.0-windows`) and implements interfaces `Core` defines. Dependencies point inward.
- The business application is a web app at normal (non-elevated) privilege. Output is `SendInput` + `KEYEVENTF_UNICODE`. A trailing Enter is appended only when `AppendEnterAfterScan` is enabled; **its default is disabled**. The scanner's own terminating Enter is always swallowed.
- `PAUSED` is a required safety valve: hook passes everything through, and the control must be reachable **with the mouse alone** from both Full and Compact.
- Silent-unhook heartbeat detection is required. The UI must never display a confident operational state it has not verified.
- Startup scan mode is always SN and is never persisted.
- F8 toggles SN/SKU and must only be triggered by a non-scanner physical keyboard.
- F10 means Force Send raw scan only while an error is pending.
- Esc cancels a pending failed scan.
- Scanner raw input must not leak into the warehouse application.
- Scanner Helper's injected output must not be recaptured.
- During an active scan, simultaneous ordinary keyboard typing is outside the supported workflow.
- Minimize means Compact; the program must never minimize to an invisible state while running.
- Compact mode is always topmost.
- Errors in Compact automatically expand to Full without stealing business-app focus, then return to Compact after resolution.
- X truly exits, but only after an explicit confirmation dialog.
- Support English and Simplified Chinese from V1. No user-facing text should be hardcoded outside localization resources.
- SKU parsing V1: Fixed Position and Regex + capture group.
- SKU validation V1: optional length, character-set preset (with an independent `Ignore case` toggle, default on), and validation regex; all enabled validators must pass.
- Parsing/validation failure never auto-sends. It enters error state and awaits F10 or Esc.
- Regex always uses a finite timeout.
- Low-level hook callback must remain extremely lightweight: no file I/O, regex, UI work, or slow logging.

## Engineering style

- Keep `App`, `Core`, and `Win32` responsibilities separated.
- Keep native interop isolated in `ScannerHelper.Win32`.
- Keep parsing/validation pure and heavily unit-tested.
- Do not put domain logic into WPF code-behind.
- Prefer small focused files with explicit interfaces.
- Follow TDD for core behavior.
- Make incremental commits after independently testable milestones.
- If the Raw Input/hook correlation approach proves unreliable, stop and report evidence rather than hiding the issue with timing heuristics.

## Before claiming completion

Run the full test suite and manually verify the acceptance criteria in `docs/SCANNER_HELPER_SPEC.md` on Windows with at least one real scanner and one normal keyboard.

**Phase A (macOS) caveat:** only `dotnet test` on `ScannerHelper.Core.Tests` can be run here. Never claim a Windows-dependent behavior works based on Phase A tests alone — say explicitly which parts are verified and which are still pending hardware.
