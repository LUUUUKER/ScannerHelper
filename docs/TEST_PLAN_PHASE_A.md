# Phase A Test Plan — Tasks 1–3

> **Scope:** everything buildable and testable on macOS — `ScannerHelper.Core` (`net8.0`) and `ScannerHelper.Core.Tests`.
> **Method:** TDD. Every case below is written and failing before its implementation exists.
> **Source of truth:** `SCANNER_HELPER_SPEC.md`. Where this plan and the spec disagree, the spec wins and this file is wrong.

Test IDs are stable and map one-to-one onto test method names. Reference them in commits and reviews.

---

## 1. Overview

| Task | Area | Cases |
|---|---|---|
| 1 | Solution boundaries and layering guards | 6 (3 build checks + 3 tests) |
| 2 | Domain types, mode, parsing, validation | 80 |
| 3 | Settings model and JSON persistence | 28 |
| | **Total** | **114** |

Task 2 breakdown: ModeManager 7, FixedPosition 16, Regex parser 11, Length 11, CharacterSet 13, Regex validator 7, Composite 9, result types 6.

---

## 2. Resolved design decisions

These were open questions during planning. All are now settled; the test cases below assume them. Reopening one means changing tests here first, not changing implementation.

| # | Decision | Rationale |
|---|---|---|
| D-1 | `Parse(null)` throws `ArgumentNullException`. Empty string `""` returns a structured failure. | `null` is a programming defect, not expected bad input. Folding it into the structured-failure path would let a real bug masquerade as a routine scan failure. |
| D-2 | Fixed Position `StartPosition < 1` or `Length < 1` fails at **construction/config-validation** time, not per-parse. | These are independent of the scanned code. A misconfiguration must be caught when the operator saves the Settings page, not hours later when a worker scans. |
| D-3 | Regex capture group index `0` is allowed and means the entire match. | Standard regex semantics; some rules are cleaner expressed as a whole-match. |
| D-4 | A capture group matching the **empty string** is a parse **success**. | Spec §9 makes parsing and validation separate layers. Parsing extracts; it does not judge whether the result is sensible. Validation rejects it. |
| D-5 | Character-set validation **passes** on the empty string. | An empty string contains no illegal characters. "Must not be empty" is the length validator's responsibility. |
| D-6 | The composite validator **collects all** failure reasons rather than short-circuiting. | The operator sees "too short **and** contains an illegal character" at once, instead of fixing one problem and immediately discovering the next. |
| D-7 | A failed result makes its value **unreachable at compile time** — a discriminated result type, not a runtime throw. | Unreachable beats throwing: the mistake cannot ship. |
| D-8 | A `SchemaVersion` **newer than** the running application is treated as corruption: back up, load defaults, emit a diagnostic. | Guessing at a future format silently discards configuration. |
| D-9 | The raw code is **never trimmed** of surrounding whitespace. | A scanner should not emit whitespace. If it does, that is an anomaly validation must surface, not something the parser quietly hides. |
| D-10 | Appending a trailing Enter to emitted output is a setting, `AppendEnterAfterScan`, **default disabled**. | The scanner's own Enter is always swallowed, so with the default the page receives none at all. Whether it needs one cannot be known before the pilot. One boolean makes the answer switchable on the floor instead of requiring a rebuild. The output behavior itself is Task 7; only the setting's default and round-trip are tested here. |

---

## 3. Task 1 — Solution boundaries

### 3.1 Build checks

Not xUnit; verified by running the commands on macOS.

| ID | Check | Pass criteria |
|---|---|---|
| B1 | `dotnet build ScannerHelper.sln` | Zero errors, zero warnings |
| B2 | `dotnet test ScannerHelper.sln` | Green |
| B3 | `ScannerHelper.Core.csproj` contents | No `net8.0-windows`, no `UseWPF`, no `ProjectReference` |

### 3.2 Layering guard tests

`ArchitectureTests.cs`. These keep the boundary enforced after the Windows projects are added in Phase B, when the temptation to reference them from `Core` becomes real.

| ID | Test | Assertion |
|---|---|---|
| A1 | `A1_Core_references_no_wpf_assemblies` | Referenced assemblies exclude `PresentationFramework`, `PresentationCore`, `WindowsBase`, `System.Windows.Forms` |
| A2 | `A2_Core_references_no_win32_project` | Referenced assemblies exclude anything starting `ScannerHelper.Win32` |
| A3 | `A3_Core_targets_no_specific_os_platform` | The Core assembly carries no `TargetPlatformAttribute` |

**A3 was changed from the original plan.** It was specified as "referenced assemblies exclude `Microsoft.WindowsDesktop.App`", but that is a *framework* reference and never appears in `GetReferencedAssemblies()` — the test would have passed unconditionally and caught nothing. Checking for `TargetPlatformAttribute` watches the action actually worth preventing: retargeting Core to `net8.0-windows`, which makes the compiler emit that attribute.

#### Verified guard behavior

The guards were confirmed by deliberately introducing the violation:

| Violation | Caught by | Outcome |
|---|---|---|
| Core retargeted to `net8.0-windows` | **NuGet, at build time** | `error NU1201: Project ScannerHelper.Core is not compatible with net8.0` — the build fails and the tests never run |
| Core **and** Core.Tests both retargeted | **A3, at test time** | Build succeeds; A3 fails with `Detected platform: Windows7.0` |

This is a two-layer defense, and the second layer is the one that matters. A developer who hits `NU1201` will find that retargeting the test project too is the quickest way to make the error disappear — at which point the build goes green and only A3 stands between that change and a `Core` that no longer compiles on macOS.

`A1` has a known limitation, documented in the test file: `GetReferencedAssemblies()` reflects references actually *used*. A declared-but-unused reference is not written to assembly metadata, so `A1` cannot see it. Build check B3 covers the declaration side, and the guard turns red the moment such a reference is actually used. Walking the filesystem from a unit test to parse the `.csproj` would close the gap at the cost of environment fragility; that trade is not worth it here.

---

## 4. Task 2 — Domain, mode, parsing, validation

### 4.1 `ModeManager`

| ID | Test | Expected |
|---|---|---|
| M1 | `Starts_in_Sn` | A new instance reports `Sn` |
| M2 | `Toggle_from_Sn_gives_Sku` | `Sn` → `Sku` |
| M3 | `Toggle_from_Sku_gives_Sn` | `Sku` → `Sn` |
| M4 | `Toggle_twice_returns_to_original` | Back to `Sn` |
| M5 | `Toggle_raises_event_with_old_and_new` | Event carries `(Sn, Sku)` |
| M6 | `Setting_same_mode_raises_no_event` | Idempotent; the UI is not notified spuriously |
| M7 | `Constructor_takes_no_settings_store` | Mode cannot be persisted, guaranteed at compile time (spec §7) |

### 4.2 `FixedPositionSkuParser`

Positions are 1-based (spec §8.1).

| ID | Raw | Start | Length | Expected |
|---|---|---|---|---|
| F1 | `ABCD12345678XYZ` | 5 | 8 | `12345678` — the spec's worked example |
| F2 | `ABCDEF` | 1 | 6 | `ABCDEF` (entire string) |
| F3 | `ABCDEF` | 1 | 1 | `A` (first character) |
| F4 | `ABCDEF` | 6 | 1 | `F` (last character) |
| F5 | `ABCDEF` | 7 | 1 | Failure `OutOfBounds` — start past end |
| F6 | `ABCDEF` | 6 | 2 | Failure `OutOfBounds` — length overruns |
| F7 | `ABCDEF` | 100 | 1 | Failure `OutOfBounds` |
| F8 | `""` | 1 | 1 | Failure `EmptyInput` |
| F9 | — | 0 | 3 | Config rejected at construction (D-2) |
| F10 | — | -1 | 3 | Config rejected at construction |
| F11 | — | 3 | 0 | Config rejected at construction |
| F12 | — | 3 | -1 | Config rejected at construction |
| F13 | `AB CD` | 1 | 5 | `AB CD` — whitespace is an ordinary character (D-9) |
| F14 | `  ABCDEF  ` | 1 | 3 | `  A` — leading whitespace is **not** trimmed (D-9) |
| F15 | any valid config | — | — | Never throws for any non-null raw (D-1) |
| F16 | `null` | 1 | 1 | Throws `ArgumentNullException` (D-1) |

### 4.3 `RegexSkuParser`

| ID | Scenario | Expected |
|---|---|---|
| R1 | `^.{4}([A-Z0-9]{8})` against `ABCD12345678XYZ`, group 1 | `12345678` — the spec's worked example |
| R2 | Same pattern, group 0 | `ABCD12345678` — whole match, allowed (D-3) |
| R3 | Pattern does not match | Failure `NoMatch` |
| R4 | Group index 2 requested, pattern has one group | Failure `MissingCaptureGroup` |
| R5 | Optional group that did not participate, e.g. `(?:x)(a)?` | Failure `MissingCaptureGroup` |
| R6 | Group matches the empty string | **Success**, value `""` (D-4) |
| R7 | Invalid pattern `[` | Config rejected as `InvalidPattern`; never throws |
| R8 | `^(a+)+$` against `aaaaaaaaaaaaaaaaaaaaaaaaaaaaaX` | Failure `RegexTimeout` — **a distinct reason code from `NoMatch`** |
| R9 | Constructed `Regex` | `MatchTimeout != Regex.InfiniteMatchTimeout` (spec §8.2) |
| R10 | Pattern with several groups, index 2 requested | Returns the second group |
| R11 | `null` raw | Throws `ArgumentNullException` (D-1) |

> R8 and R9 carry the weight here. A timeout must be distinguishable from a non-match, otherwise on-site troubleshooting cannot tell "the rule is wrong" from "the rule is too slow".

### 4.4 `LengthSkuValidator`

| ID | Config | Input | Expected |
|---|---|---|---|
| L1 | min=8 | `12345678` | Pass (boundary) |
| L2 | min=8 | `1234567` | Failure `TooShort` |
| L3 | max=8 | `12345678` | Pass (boundary) |
| L4 | max=8 | `123456789` | Failure `TooLong` |
| L5 | min=max=8 | `12345678` | Pass (exact length) |
| L6 | min=max=8 | `1234567` | Failure |
| L7 | neither enabled | anything | Pass |
| L8 | min only | very long string | Pass |
| L9 | max only | `""` | Pass |
| L10 | min=9, max=8 | — | Config rejected |
| L11 | min=-1 | — | Config rejected |

### 4.5 `CharacterSetSkuValidator`

| ID | Preset | IgnoreCase | Input | Expected |
|---|---|---|---|---|
| C1 | Numbers | — | `12345` | Pass |
| C2 | Numbers | — | `12A45` | Failure, reporting offending character `A` at index 2 |
| C3 | Letters | **true** | `abc` | **Pass** |
| C4 | Letters | **false** | `abc` | **Failure** |
| C5 | Letters | false | `ABC` | Pass |
| C6 | Letters | true | `AbC` | Pass |
| C7 | LettersNumbers | true | `AB12` | Pass |
| C8 | LettersNumbers | true | `AB-12` | Failure |
| C9 | LettersNumbersDashUnderscore | true | `AB-12_3` | Pass |
| C10 | Numbers | true and false | `12345` | Identical result — the toggle does not affect digits |
| C11 | any | any | `AB中` | Failure (non-ASCII) |
| C12 | any | any | `""` | **Pass** (D-5) |
| C13 | not enabled | — | anything | Pass |

### 4.6 `RegexSkuValidator`

| ID | Scenario | Expected |
|---|---|---|
| V1 | `^[A-Z]{3}\d{8}$` against `ABC12345678` | Pass |
| V2 | Same against `AB12345678` | Failure |
| V3 | Invalid pattern | Config rejected; never throws |
| V4 | Catastrophic backtracking | Failure `RegexTimeout`, distinct reason code |
| V5 | **`IgnoreCase = true`, `^[A-Z]+$` against `abc`** | **Still fails** — proves the toggle is confined to character-set validation (spec §9.2) |
| V6 | Parsing regex and validation regex configured separately | Changing one does not affect the other |
| V7 | Constructed `Regex` | `MatchTimeout != Regex.InfiniteMatchTimeout` (spec §9.3) |

### 4.7 `CompositeSkuValidator`

| ID | Scenario | Expected |
|---|---|---|
| P1 | Zero validators | Valid (spec §9) |
| P2 | One passing | Valid |
| P3 | One failing | Invalid, carrying that validator's reason |
| P4 | Two passing | Valid |
| P5 | First fails, second passes | Invalid |
| P6 | First passes, second fails | Invalid |
| P7 | Both fail | Invalid, carrying **both** reasons (D-6) |
| P8 | Validator order swapped | Same result |
| P9 | All three validator types enabled and passing | Valid |

### 4.8 `ParseResult` / `ValidationResult`

| ID | Test | Expected |
|---|---|---|
| D1 | Success carries its value | `IsSuccess = true`, correct value |
| D2 | Failure carries a reason code | `IsSuccess = false`, `FailureReason` present |
| D3 | Failure always retains the raw code | Needed by both Force Send and the error display (spec §10) |
| D4 | The value of a failed result is unreachable | Compile-time, via a discriminated result type (D-7) |
| D5 | `FailureReason` is an enum, not a string | Reflection assertion |
| D6 | No user-facing English text anywhere in `Core` | Reason codes only; the UI maps them to `.resx` |

> D5 and D6 are easy to skip and expensive to retrofit. A literal `"SKU too short"` inside `Core` cannot be rendered in Chinese, and the defect stays invisible until localization work begins (spec §12).

---

## 5. Task 3 — Settings and persistence

### 5.1 Defaults

| ID | Field | Default |
|---|---|---|
| S1 | `SchemaVersion` | `1` |
| S2 | `Language` | unset — the system UI culture is consulted only on first launch (spec §12) |
| S3 | `StartWithWindows` | `false` |
| S4 | `ModeSwitchSoundEnabled` | `true` |
| S5 | `ErrorSoundEnabled` | `true` |
| S6 | `RememberWindowPosition` | `true` |
| S7 | `FullWindowAlwaysOnTop` | `false` |
| S8 | `Hotkeys` | F8 / F10 / Esc; **Pause/Resume unassigned** (spec §13.3) |
| S9 | `SkuValidation.IgnoreCase` | `true` (spec §9.2) |
| S10 | `AppendEnterAfterScan` | **`false`** (D-10, spec §10) |

### 5.2 Must never be persisted

| ID | Test | Expected |
|---|---|---|
| S11 | `AppSettings` exposes no scan-mode property | Reflection assertion — impossible by construction (spec §14) |
| S12 | `AppSettings` exposes no paused-state property | Reflection assertion (spec §5.7) |
| S13 | Serialized JSON text | Contains no `mode` or `paused` key in any casing |

### 5.3 Load and save

| ID | Scenario | Expected |
|---|---|---|
| S14 | File missing → Load | Defaults returned, no exception, **no file created** |
| S15 | Directory missing → Save | Directory created |
| S16 | Save → Load round trip | Every field equal, including nested Hotkeys, Parsing, Validation, and `AppendEnterAfterScan` |
| S17 | Atomic save | No `.tmp` file remains after a successful save |
| S18 | Failure midway through a save | The previous file is **intact**; no partial file is left behind |
| S19 | IO error on save | Raises a catchable error; does not crash and does not corrupt the existing file |
| S20 | Store resolves its own directory | Via `Environment.SpecialFolder.ApplicationData`; tests inject a temp directory instead |

### 5.4 Corruption and compatibility

| ID | Input file | Expected |
|---|---|---|
| S21 | Malformed JSON `{{{` | Defaults returned, file **renamed** to `settings.corrupt-1.json`, diagnostic event emitted |
| S22 | Empty file | As S21 |
| S23 | Content is `null` | As S21 |
| S24 | Well-formed JSON of the wrong shape, e.g. `[1,2,3]` | As S21 |
| S25 | `settings.corrupt-1.json` already exists and corruption recurs | Backed up as `-2`; the earlier backup is not overwritten |
| S26 | Missing newer field, e.g. an older config without `IgnoreCase` | That field takes its default; everything else loads normally |
| S27 | Unknown extra fields written by a future version | Ignored without error |
| S28 | `SchemaVersion` newer than the application | Treated as corruption per D-8 |

---

## 6. Out of scope for Phase A

Deferred to Phase B, on Windows with real hardware. Do not simulate these with unit tests — a passing fake would be worse than no test, because it would imply coverage that does not exist.

- Raw Input device enumeration and identity
- `WH_KEYBOARD_LL` installation, event ordering, and suppression
- `SendInput` emission and injected-event tagging
- Hook heartbeat and silent-unhook detection (spec §19.1)
- Window focus behavior, Compact/Full transitions, DPI scaling
- Real scanner timing characteristics

`InputEventCorrelator`, `ScanSession`, and `ScanInputCoordinator` are Phase A work but belong to Task 6; they get their own test plan.

---

## 7. Summary

114 cases across three tasks, all runnable on macOS with `dotnet test`.

Two of them do disproportionate work. **R8/V4** force parse and validation timeouts to carry a reason code distinct from a plain non-match, which is what makes an on-site rule problem diagnosable. **D5/D6** keep user-facing text out of `Core`, without which the Chinese UI cannot be correct and the defect stays hidden until localization begins.

The layering guards A1–A3 matter most later rather than now: they are cheap today and become the thing that stops `Core` from acquiring a Windows dependency once the Phase B projects exist.
