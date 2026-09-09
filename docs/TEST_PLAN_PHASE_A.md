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
| 2 | Domain types, mode, parsing, validation | 90 |
| 3 | Settings model and JSON persistence | 20 (28 case IDs) |
| | **Total** | **113** |

Task 2 breakdown: ModeManager 7, FixedPosition 17, Regex parser 13, Length 12, CharacterSet 15, Regex validator 9, Composite 11, result types 6.

Counts are xUnit executions, so a `[Theory]` row counts once. They are kept equal to what `dotnet test` reports, so the two can be compared directly.

One exception: Task 3's defaults, S1–S10, are asserted inside a single test method, since they describe one object's initial state and splitting them would produce ten near-identical methods. So Task 3's 28 case IDs correspond to fewer executions. Where ID count and execution count diverge, the section says so.

The count grows when implementation exposes a hole the plan missed — see F17. That is the plan working, not the plan failing.

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
| F17 | `ABCDEF` | `int.MaxValue` | 10 | Failure `OutOfBounds` — the bounds check must not overflow |

**F17 was added during implementation**, not planned. Writing the bounds check exposed the hole: the obvious form `startIndex + length > textLength` overflows to a negative number when the start position approaches `int.MaxValue`, so the check *passes*, and `Substring` then throws — violating F15 and the `ISkuParser` contract that no non-null input may throw. The correct form is `startIndex > textLength - length`, where neither side can overflow because `length >= 1` is guaranteed at construction and `textLength >= 0`.

Verified by reverting to the naive form: F17 fails with `ArgumentOutOfRangeException: startIndex cannot be larger than length of string`. The overflow is real, not hypothetical.

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
| R12 | Negative capture group index | Config rejected at construction |
| R13 | `""` raw | Failure `EmptyInput`, matching the fixed-position parser |

> R8 and R9 carry the weight here. A timeout must be distinguishable from a non-match, otherwise on-site troubleshooting cannot tell "the rule is wrong" from "the rule is too slow".

**R12 and R13 were added during implementation.**

R12 closes the same class of gap as F9: the capture-group index is a numeric field in Settings, and a negative value is an ordinary typo. Left unchecked, the runtime behavior would depend on .NET internals and no definite reason code could be guaranteed.

R13 makes both parsers report the same fault the same way. An empty scan means the scanner or capture path delivered nothing, which has no relation to the parsing rule. Letting the regex run against `""` would produce `NoMatch` and send the operator to inspect the rule when the fault is in hardware.

**R5 and R6 are a deliberate trap for the implementation.** Both produce an empty captured value:

| Case | Pattern | Input | `Group.Success` | Value | Expected |
|---|---|---|---|---|---|
| R6 | `(a*)` | `b` | `true` | `""` | **Success** (D-4) |
| R5 | `(?:x)(a)?` | `x` | `false` | `""` | `MissingCaptureGroup` |

An implementation that decides by testing whether the value is empty misreports R6 as a failure. `Group.Success` is the only correct discriminator.

**R8 verified as genuine:** the test runs in exactly 100 ms — the configured timeout — confirming the regex ran until it was cut off rather than failing fast.

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
| L12 | any | `null` | Throws `ArgumentNullException` (D-1) |

**L12 was added during implementation**, so that every public string-taking entry point in `Core` treats `null` identically — matching F16 and R11. Callers should not have to remember which entry points throw and which return a failure.

`L10` is the case worth understanding: a minimum above the maximum describes a range no length can satisfy. Allowed through to runtime it presents on site as "every scan fails validation", giving the operator no way to tell a contradictory rule from a bad barcode. Per D-2 it is rejected at construction.

`L8` and `L9` are the two directions of "not enabled means not constrained". The tempting wrong implementation substitutes defaults for the disabled bound — 0 for the minimum, `int.MaxValue` for the maximum. That looks equivalent but destroys information: the configuration can then no longer distinguish "the user set the minimum to 0" from "the user never enabled a minimum", which are different states in the Settings UI and cannot be restored from JSON. `int?` keeps "not enabled" expressible, persistable, and restorable.

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
| C14 | any | any | `null` | Throws `ArgumentNullException` (D-1) |

C10 runs as two rows (toggle on and off), so this group executes 15 assertions across 14 case IDs.

**`IllegalCharacter` reports a 1-based `Position`, not a 0-based `Index`.** The plan originally said "`A` at index 2" for `12A45`; the implementation reports position 3. This value exists solely to be read by an operator, and people count characters from one — spec §8.1 already fixes user-facing positions as 1-based for the fixed-position parser, and one product should not carry two conventions. The field is named `Position` rather than `Index` precisely so it is not misread.

**ASCII-only is the load-bearing implementation detail.** `char.IsLetter('中')` and `char.IsDigit('٣')` both return `true`, so the obvious implementation admits non-ASCII characters. C11 exists to forbid this. Verified by swapping in `char.IsLetter`: C11 fails, `"AB中"` passing "letters only". Beyond the test, a non-ASCII character in an SKU usually signals that keyboard-layout decoding went wrong — precisely the fault most worth catching, never one to accept quietly.

**C3/C4 and C10a/C10b cage the ignore-case toggle from both sides.** C3 vs C4 proves it does something: identical input `abc` passes with the toggle on and fails with it off. C10a vs C10b proves it does not do too much: the same digits pass identically either way. V5 in the next group closes the third side by proving the toggle never reaches the validation regex.

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
| V8 | `null` sku | Throws `ArgumentNullException` (D-1) |
| V9 | `null` pattern | Not enabled; everything passes, matching L7 and C13 |

**V5 closes the ignore-case cage.** Three tests bound the toggle's scope from three sides: C3 vs C4 proves it does something, C10a vs C10b proves it does not touch digits, and V5 proves it cannot reach the validation regex. Verified by adding `RegexOptions.IgnoreCase` to the validator: V5 fails with the message written for exactly that reader.

`RegexSkuValidator` therefore accepts **no** case parameter at all. Spec §9.2's toggle governs character-set validation only; wiring it in here would let one checkbox change the meaning of two unrelated rules, leaving the operator unable to predict how much shifted when they ticked it. A rule author wanting case-insensitive matching writes `(?i)^[a-z]+$` — visible in the rule and reviewable.

**Empty string is handled differently here than in C12, deliberately.** The character-set validator passes `""` because "must not be empty" belongs to the length rule. The validation regex applies no special case at all: `^\d+$` rejects `""` and `^\d*$` accepts it, which is the rule author's decision and not the validator's to override.

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
| P10 | `null` sku | Throws `ArgumentNullException` (D-1) |
| P11 | `null` validator collection | Rejected at construction |

**P7 is decision D-6 in executable form**, and P8 turned out to guard the same property independently. Verified by adding a `break` after the first failure: **both** fail. P7 sees only one failure where two were expected; P8 sees the forward and reversed orderings report *different* failures, because a short-circuiting composite reports whichever validator happens to run first. Two tests, two angles, one property.

**P1 (empty) and P11 (null) are deliberately different.** An empty collection means "no validation rule is enabled" — a common configuration spec §9 explicitly supports, since many sites need parsing only. `null` can only be a caller defect. The same distinction the parsers draw between `""` and `null` (D-1).

**P5 and P6 are both required.** P5 alone cannot rule out an implementation that consults only the first validator; P6 alone cannot rule out one that consults only the last.

**Stubs, not real validators.** Every test here except P9 uses a stub validator whose result is preset. What is under test is the combining logic — how many failures, how they merge, whether order matters — not the length or character-set rules. Real validators would mix "why did this one fail" into the subject under test, and a change to any base validator would turn this file inexplicably red. P9 is the exception, confirming the three real types do assemble together — an interface-conformance surface that stubbing cannot cover.

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

**These are meta-tests: they pass on arrival.** They encode structure the implementation already has, so passing proves nothing by itself — which is why each was verified by injecting the violation it exists to catch. Adding an `Sku` property to `ParseResult.Failure` and a `Message` property to `ValidationFailure.TooShort` turns D4, D5 and D6 red together.

**D4 asserts less than originally intended, and the reason is worth recording.** The plan called for "the value of a failed result is unreachable at compile time". A unit test cannot verify that directly — one cannot write code that must fail to compile. What it can do is pin the structural preconditions: `Failure` has no `Sku`/`Value`/`Result` property, `Valid` has no `Failures` property, both base types' parameterless constructors are private, and all nine case types are `sealed`.

Full closure is *not* asserted, because it is not true. C# generates a `protected` copy constructor for a non-sealed record and the language forbids declaring it `private`, so an outside assembly could derive a third case through it. The hole does not matter in practice — such code is obviously wrong and nothing outside inherits from `Core` — but a test must not assert something false.

**D6's coverage is deliberately narrow.** Reflection cannot see string literals inside method bodies, so `return "The scan was empty"` is not caught. What is caught reliably is prose appearing in the *public API*: a `Message` property on a failure type, or a new `XxxMessages` class — the usual shape of "let me just put the text in Core".

The alternative, scanning every IL string literal, was rejected for producing false positives: it would flag exception messages, regex patterns and configuration keys. This project's exception messages are deliberately long bilingual sentences aimed at developers rather than operators, and blocking those would be wrong. A test that cries wolf acquires exceptions, then gets ignored, then gets deleted. Narrow and reliable beats broad and noisy.

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

S1–S13 run as **5 executions**: S1–S7, S9 and S10 are asserted together in one method describing the default object's state.

**The name checks must be precise, not substring matches.** `ModeSwitchSoundEnabled` is a perfectly legitimate setting whose name contains "Mode", and the JSON key `modeSwitchSoundEnabled` contains "mode". A substring search would fire immediately and then either acquire an exception — the first exception is where a guard starts rotting — or force a well-named field to be renamed. S11 therefore checks two precise criteria: whether a property is *typed* `ScanMode` (the most reliable signal, since storing the mode would naturally use that type) and whether its name is one of a specific list. S13 parses the JSON and compares whole property names rather than searching the text.

**S13 does not duplicate S11/S12.** Those inspect the shape of the C# types; S13 inspects what is actually written to disk. The two can disagree — a property annotated `[JsonPropertyName("currentMode")]` slips past a type check and is caught here.

**Three independent layers defend "the mode is never persisted"**: M7 (ModeManager takes no persistence dependency), S11/S12 (no such field exists), S13 (no such key is written). Verified by adding `CurrentMode` and `IsPaused` to `AppSettings`: S11, S12 and S13 fail together. Any one layer failing leaves two.

The settings-type collector walks from `AppSettings` into any property type in the same namespace, so a newly added nested settings type is covered automatically. A hand-maintained list of types to scan would eventually go stale, and would do so silently.

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

S14–S28 run as **15 executions** across 11 methods, S21–S24 and S28 sharing one `[Theory]`.

**S18 was too weak when first written, and passing did not reveal it.** The fake file system threw immediately on write, so no bytes reached disk — and with nothing written, even a non-atomic implementation writing straight to the target left the original intact. The test passed because the fake was gentle, not because the implementation was correct.

Confirmed by switching `Save` to a non-atomic write: the suite stayed **fully green**. The fake now writes half the content before throwing, which is what a full disk, a power loss or a killed process actually does. With that fix the same experiment turns **S18 and S19 both red**, and the atomic implementation restores them to green. The test now discriminates; before, it only looked like it did.

This is the argument for verifying every guard by injecting the violation it exists to catch. A guard that has never failed is not known to work — and here the guard was genuinely broken while showing green.

**Load and Save take opposite stances on failure, deliberately.** `Load` never throws and falls back to defaults, because an operator facing an application that will not open has no recourse, whereas defaults at least leave SN mode working. `Save` must throw, because a swallowed write failure leaves the operator believing their settings were saved until the next launch proves otherwise, with no way left to tell which step failed.

**S27 and S28 are not in conflict.** S28 rejects a file whose *declared version* is higher — an explicit "this is a newer format" signal. S27 handles a file at the same version that merely carries extra fields, an in-version extension with no reason to discard the whole configuration. Treating unknown fields as corruption would wipe every station's settings on every release.

**Corrupt files are renamed, never overwritten or deleted.** The parsing rule and scanner binding are configured remotely by the technical advisor and cannot be restored by the operator; destroying them because a file broke would be the worst possible response. S25 additionally pins that a second corruption does not overwrite the first backup, since the earliest copy is the last complete configuration before things went wrong.

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

113 xUnit executions across three tasks, all green on macOS. Phase A is complete.

Two of them do disproportionate work. **R8/V4** force parse and validation timeouts to carry a reason code distinct from a plain non-match, which is what makes an on-site rule problem diagnosable. **D5/D6** keep user-facing text out of `Core`, without which the Chinese UI cannot be correct and the defect stays hidden until localization begins.

The layering guards A1–A3 matter most later rather than now: they are cheap today and become the thing that stops `Core` from acquiring a Windows dependency once the Phase B projects exist.
