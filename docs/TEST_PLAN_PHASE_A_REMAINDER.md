# Phase A Test Plan — Tasks 7a, 8a and 5a

> **Scope:** the remainder of Phase A after Task 6 — the output contract and Force Send workflow (7a), hotkey routing rules (8a), and device identity matching (5a). All of it is `ScannerHelper.Core` on `net8.0`, testable on any platform.
> **Method:** TDD. Every case is written and failing before its implementation exists.
> **Source of truth:** `SCANNER_HELPER_SPEC.md`. Where this plan and the spec disagree, the spec wins and this file is wrong.
> **Preceded by:** `TEST_PLAN_PHASE_A.md` (Tasks 1–3) and `TEST_PLAN_TASK_6.md`, whose decisions D-1…D-18 continue to apply.

This file is written task by task as each is implemented, rather than in full up front. Sections for 8a and 5a are marked as pending and will be filled in the same form.

---

## 1. Task 7a — output contract and Force Send

### 1.1 What this layer is

Output is the pipeline's **only exit**. Wrong data can reach the warehouse system only through here, which makes this the layer where spec §19.1's "silently wrong data" actually materializes: the program looking fine, the UI reporting success, and what left through this door being wrong.

Half these cases therefore assert that **nothing** was emitted. Spec §10 forbids failures from auto-emitting, and a thing that did not happen is both the easiest to omit in an implementation and the hardest to notice on site — the operator sees an error message and has no way to know something was quietly sent anyway.

### 1.2 Two design decisions

| # | Decision | Rationale |
|---|---|---|
| D-19 | `IKeyboardOutputService` exposes **two** operations: `EmitText` and `EmitEnter`. The Enter is never appended to the text as `'\r'`. | Not interface fastidiousness but a real technical difference. Sending U+000D through `KEYEVENTF_UNICODE` delivers a *character*, while a web form's submit behavior normally hangs off the Enter **key event** (`keydown` with `key === "Enter"`). Emitting `'\r'` as an ordinary character therefore tends to leave an invisible character in the field while the form does not submit — meaning `AppendEnterAfterScan` would be ineffective even when enabled, and on site it is very hard to tell a setting that did not take effect from a page that does not accept the input. Splitting it lets the Win32 implementation send a genuine `VK_RETURN`. |
| D-20 | The `AppendEnterAfterScan` decision lives in `ScanInputCoordinator`, not in the output service. `EmitText` emits exactly what it is given and nothing more. | Spec §10 requires the setting to apply equally to SN output, SKU output and Force Send. Routing all three through one private `Emit` is what makes that true by construction rather than by repeating the rule in three places — and repeating a rule in three places is how two of them eventually disagree. It also keeps `EmitText`'s contract clean enough to state in one sentence. |

### 1.3 Cases

| ID | Scenario | Expected |
|---|---|---|
| FS1 | Successful SN scan | Emits the complete raw code |
| FS2 | Successful SKU scan | Emits the parsed SKU, not the raw code |
| FS3 | `AppendEnterAfterScan` off (the default) | **Zero** Enters |
| FS4 | On, SN mode | **Exactly one** Enter |
| FS5 | On, SKU mode | **Exactly one** Enter |
| FS6 | Parse failure | Nothing emitted; pending error entered |
| FS7 | Validation failure | Nothing emitted; pending error entered |
| FS8 | Scan timeout | Nothing emitted; **no** pending error, so no F10 path |
| FS9 | F10 after a *validation* failure | Emits the **raw code**, not the parsed candidate |
| FS10 | F10 with the setting on | Exactly one Enter |
| FS11 | Esc during a pending error | Nothing emitted |
| FS12/FS13 | F10 and Esc | The mode is unchanged by both |
| FS14 | F10 or Esc with no pending error | Returns `false`, emits nothing |
| FS15 | Content with surrounding whitespace and lowercase | Emitted **exactly as produced** |

### 1.4 Why particular cases exist

**FS3 asserts zero rather than "fewer".** Spec §10 goes out of its way to say that with the setting off the page receives *no* Enter at all — not one fewer than before. The whole raw scan is swallowed, the scanner's own terminating Enter included. This is the sentence the pilot will be judged against if the page stops submitting.

**FS4 and FS5 assert "exactly one", not "at least one".** One extra Enter could mean one extra form submission, which in the warehouse system is a spurious record rather than a cosmetic glitch. That is also why the fake records the Enter count separately from the text instead of merging it as a trailing `'\r'`: merged, "text then an Enter keystroke" and "text ending in a carriage return" become indistinguishable in an assertion, and D-19 says they are different things.

**FS5 exists because FS4 does not cover it.** Spec §10 requires the setting to apply to SN, SKU and Force Send alike; with only one path tested, either of the other two could omit it with nothing turning red. FS10 closes the third.

**FS8 is distinct from FS6 and FS7 and the distinction is the point.** All three emit nothing, but a scan timeout does not even enter the pending-error state, so the operator has no F10 path at all. Spec §5.4 requires exactly that: half a code was never a complete barcode and emitting it would volunteer bad data — and Task 4a confirmed such damage genuinely occurs on this hardware (spec §22.5). The difference between the cases is whether what is in hand is **complete**, not how severe the error was.

**FS9 uses a validation failure deliberately.** A parse failure leaves no candidate SKU, so "raw or candidate" would not be a real choice and the assertion would be hollow. After a validation failure the candidate exists, and the case pins spec §10's "Force Send always emits the raw scanned code, not a partially parsed candidate". The reasoning is about the operator's situation: they press F10 having judged the code right and the rule not yet configured, and emitting a half-parsed candidate would override that judgement with an intermediate product of the very rule they do not trust.

**FS14's return value is not cosmetic.** Spec §7 swallows F10 and Esc while an error is pending and passes them through otherwise, so Task 8's routing needs to know whether anything happened. The Esc row matters most, and the spec explains why: Esc is far too common in web use, and swallowing it unconditionally would leave the operator with a "broken" cancel key and no reason to suspect this tool — a fault whose source cannot be found. Pressing either key with no pending error is normal rather than exceptional, hence a boolean rather than an exception.

**FS15 guards the rule most likely to be "tidied away".** A scanner should not emit whitespace; if it does, that is an anomaly for validation to surface (spec §9's character-set rule rejects a space). Trimmed quietly at the output layer it could never be found, and the warehouse system would accumulate data that looks normal and came from nowhere identifiable. Case is the same: spec §9.2 already assigns case handling to validation's ignore-case toggle and the validation regex, so touching it here would put two places in charge of one decision.

### 1.5 Guards verified by injecting the violation

| Violation injected | Result |
|---|---|
| Call `EmitEnter` unconditionally, ignoring the setting | **FS3 fails** |
| Add `.Trim()` inside the coordinator's `Emit` | **FS15 fails** |
| Force-send the parsed candidate instead of the raw code | **Cannot be injected** — `PendingScanError` stores only the raw code and has no candidate field, so it is not expressible. FS9 therefore guards a future change rather than today's code, exactly as CR4 and D4 do. |

---

## 2. Task 8a — hotkey routing

_Pending. Will cover every row of spec §7's pass-through table with synthetic source identities: F8 from a non-scanner keyboard toggles and is swallowed; F8 from the bound scanner stays scanner data; F10 and Esc swallowed only while an error is pending and passed through otherwise; everything passed through while paused._

---

## 3. Task 5a — device identity matching

_Pending. Will cover `DeviceIdentityMatcher` against synthetic identities: exact device-path match, serial match, VID/PID-only flagged as low confidence, no match, and an ambiguous match between two identical models. Task 4a's measurements make one case mandatory that the original plan did not anticipate — the laptop's built-in keyboard arrives over ACPI with **no VID/PID at all** (spec assumption A4), so matching must not assume those fields exist._
