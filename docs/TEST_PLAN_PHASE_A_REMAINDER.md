# Phase A Test Plan — Tasks 7a, 8a and 5a

> **Scope:** the remainder of Phase A after Task 6 — the output contract and Force Send workflow (7a), hotkey routing rules (8a), and device identity matching (5a). All of it is `ScannerHelper.Core` on `net8.0`, testable on any platform.
> **Method:** TDD. Every case is written and failing before its implementation exists.
> **Source of truth:** `SCANNER_HELPER_SPEC.md`. Where this plan and the spec disagree, the spec wins and this file is wrong.
> **Preceded by:** `TEST_PLAN_PHASE_A.md` (Tasks 1–3) and `TEST_PLAN_TASK_6.md`, whose decisions D-1…D-18 continue to apply.

This file was written task by task as each was implemented, rather than in full up front. All three sections are now complete, and with them Phase A.

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

### 2.1 One decision the spec forced and one it did not

| # | Decision | Rationale |
|---|---|---|
| D-21 | `Route` returns a **single action**. There is no separate "should I swallow it" flag, because acting always implies swallowing. | Spec §7 fixes it: "When Scanner Helper acts on a control hotkey, it consumes the key and does not forward it." The two are exactly equivalent, and a second field would be a second source of truth for one fact. The disagreement would also be unusually hard to diagnose — a hotkey that both fires *and* reaches the page would toggle the mode while simultaneously triggering an unrelated shortcut in the web application, which is the very thing spec §7 says the rule prevents. |
| D-22 | An event whose source could **not** be determined is replayed but is **never** treated as a hotkey. | The spec does not address this case; it falls out of D-13. Replaying an unresolved event is right — a leaked scanner character is visible and correctable while a swallowed keystroke is not. But acting on it as a hotkey is not, because the consequences diverge sharply: wrongly replaying adds one visible character, whereas wrongly toggling sends the next few dozen scans out in the wrong mode with no indication, which is spec §19.1's silently wrong data. Spec §7's "scanner F8 must never toggle" therefore deserves to treat "we do not know whether it was the scanner" exactly as it treats "we know it was". When in doubt, miss a mode switch — the operator presses again — rather than make an extra one. |

### 2.2 Cases

Every row of spec §7's table, plus the cases the table does not state.

| ID | Scenario | Expected |
|---|---|---|
| HK1 | F8 from a normal keyboard, not paused | `ToggleMode`, swallowed |
| HK2 | F8 from the bound scanner | `None` — scanner data, never a command |
| HK3/HK4 | F10 with / without a pending error | `ForceSend` / `None` |
| HK5/HK6 | Esc with / without a pending error | `Cancel` / `None` |
| HK7 | Any hotkey while paused, error pending | `None` for all of them |
| HK8 | Key-**up** for any hotkey | `None` |
| HK9 | An unassigned hotkey, and virtual key `0` | `None` |
| HK10 | An ordinary letter | `None` |
| HK11 | The pause hotkey **while paused** | `None` — it cannot resume |
| HK12 | A synthesized (injected) F8 or F10 | `None` |
| HK13 | Duplicate assignments, including differing case | Conflict reported, naming the key |
| HK14 | Unassigned, empty and whitespace entries | Not conflicts; the default configuration saves |
| CO16 | F8 from a keyboard, through the coordinator | Mode toggles; **not** replayed |
| CO17 | F8 from the scanner, through the coordinator | Mode unchanged |
| CO18 | F8 whose source was never resolved | Replayed; mode **unchanged** (D-22) |

### 2.3 Why particular cases exist

**HK7 is the definition of `PAUSED`, not a special case of it.** Spec §5.7 requires the pipeline to be bypassed entirely while paused, with input reaching the business application exactly as if this program were not installed. Intercepting even one key makes that untrue — and PAUSED exists to rescue the operator when capture, correlation and replay are broken, which a "bypass" that still intercepts some keys cannot do.

**HK8 guards a bug that is routinely misdiagnosed.** Each keystroke produces a down and an up, so acting on both means one F8 press toggles twice for a net effect of nothing. On site that reads as "F8 does nothing", and the investigation goes entirely the wrong way: the operator suspects an unregistered hotkey, a broken keyboard, or the program not running. Nobody suspects it toggled twice — and stepping through it on a development machine shows both toggles plainly, which is why defects of this kind so often "fix themselves" during a demonstration.

**HK11 pins a consequence the spec states and which still looks like a bug.** No hotkey is intercepted while paused, the pause hotkey included, so it can only ever pause and never resume. Spec §5.7 calls that "the correct, expected consequence of bypassing the pipeline, not a bug". Without a test, somebody will eventually fix it — and in doing so re-create the situation §5.7 was written to prevent, since resuming must remain reachable with the mouse alone. Assumption A4 sharpens it further: the workstation is a laptop with no spare keyboard, and the touchpad is the only way back.

**HK12 matters more than it looks.** Spec §19.1's heartbeat emits a synthesized event **every few seconds**. If a synthesized event could fire a hotkey and the probe key happened to collide with one, the mode would toggle on a regular cycle — presenting on site as "the mode changes by itself".

**HK13's conflict check belongs at save time, not runtime.** Suppose Cancel is also set to F8: at runtime Toggle Mode matches first, F8 switches the mode, and the Cancel hotkey simply never works with no indication. On site F8 switches modes fine while nothing cancels a pending error, so the operator concludes error handling is broken when two hotkeys have collided — symptom and cause with nothing visibly connecting them. The check returns the conflicting **names** rather than a boolean so Settings can say "F8 is assigned to more than one function" instead of leaving the operator guessing across four fields.

**HK14 exists because the default configuration must be savable.** Spec §13.3 leaves the pause hotkey unassigned, and treating "both unassigned" as a conflict would refuse the operator the first time they open Settings, change nothing, and click save.

### 2.4 Guards verified by injecting the violation

| Violation injected | Result |
|---|---|
| Let key-**up** events act | **HK8 fails** |
| Drop the "source must be resolved" gate before hotkey routing | **CO18 fails**, reporting D-22's reasoning back |

---

## 3. Task 5a — device identity matching

### 3.1 The case the original plan could not have contained

`TEST_PLAN_PHASE_A.md` and the implementation plan both list the matching cases as: exact device-path match, serial match, VID/PID-only flagged as low confidence, no match, and an ambiguous match between two identical models. Every one of those assumes each device *has* a VID and a PID, that being ordinary for USB devices.

Task 4a's measurements broke the assumption. The devices actually present on the pilot-class machine were:

```text
HID Keyboard Device       VID_0581 PID_0115   \\?\HID#VID_0581&PID_0115&MI_00#...
Standard PS/2 Keyboard    —        —          \\?\ACPI#MSFT0001#4&b6e66aa&0#...
```

The built-in keyboard arrives over ACPI with **neither a VID nor a PID** (spec assumption A4, added after that run). DM7 exists because of it, and it is the most consequential case in this section — see §3.4.

### 3.2 Two decisions

| # | Decision | Rationale |
|---|---|---|
| D-23 | A field absent on **both** sides is never a match. | `a == b` returns true for two nulls, and the C# semantics are right while answering a different question: "neither device has a serial number" is not "the two devices have the same serial number". With the built-in keyboard having no VID/PID at all, treating "both absent" as agreement lets it match any binding record missing those fields — and being taken for the scanner means every character the operator types is swallowed into the scan buffer. That is the disaster spec §5.7 gives as the reason `PAUSED` exists, produced by a matching bug. |
| D-24 | Contradictory hard identifiers defeat a weaker agreement. Both sides reporting different VIDs means no match, even if the serials agree. | Serial numbers are unique only within a vendor, and lazy ones such as `1` or `0000` collide across vendors readily. Checking the contradiction first is the safer order: better to ask the operator to rebind than to mistake another device for the scanner. |

### 3.3 Cases

| ID | Scenario | Expected |
|---|---|---|
| DM1 | Identical device path | `Exact` |
| DM2 | Path changed, serial agrees | `High` |
| DM3 | Only VID/PID agree | `Low` |
| DM4 | Nothing in common | `None` |
| DM5 | Neither side has a device path | `None` — **not** an Exact match |
| DM6 | Neither side has a serial | `None` |
| DM7 | Built-in keyboard (no VID/PID) against a binding missing them | `None` |
| DM8 | Empty and whitespace-only fields | Treated as absent |
| DM9 | Device paths differing only in case | `Exact` |
| DM10 | Contradicting VID, agreeing serial | `None` (D-24) |
| DM11 | Two devices of one model | Ambiguous; **cannot** auto-reconnect |
| DM12 | One exact match, one model-only match | Exact wins; not ambiguous |
| DM13 | Empty candidate list | No match; no throw |
| DM14 | Every confidence level against `CanReconnectAutomatically` | Only `High`/`Exact` and unambiguous |
| DM15 | `null` arguments | Throw `ArgumentNullException` (D-1) |

### 3.4 Why particular cases exist

**DM5, DM6 and DM7 are one rule seen from three angles**, and the rule is D-23. DM5 makes the device paths absent while forcing the VID/PID to differ, so an `Exact` verdict could only come from two nulls comparing equal. DM6 does the same for serials, and matters more than it looks: not every scanner exposes a serial to Windows, so "both absent" is the ordinary case taken on **every reconnect**, not an edge case.

**DM7 is the one that would end the pilot.** If "both absent" counted as agreement, a binding record missing VID/PID — because the device did not report them when bound, or because the configuration was hand-edited — would match the built-in keyboard. The keyboard would be bound as the scanner and every character the operator types swallowed into the scan buffer. Assumption A4 removes the escape: a laptop has no spare keyboard to plug in, and the only way out is the touchpad and the mouse-clickable pause control that spec §5.7 insists on.

**DM9 guards a fault nobody would connect to letter case.** Windows device paths are case-insensitive and the same device can come back differently cased from different APIs or Windows versions. Comparing case-sensitively would fail to match it on reconnect, presenting as "it asks me to rebind every time I plug it in".

**DM11 is spec §6's named scenario in executable form.** Two scanners of one model are indistinguishable at the VID/PID level. Auto-binding one would give an operator holding the wrong gun no warning at all — they would keep scanning while the data came from an unbound device. The result still returns a device rather than null, because the UI needs it to say that two apparently identical devices were found; what prevents the automatic bind is `CanReconnectAutomatically`, not hiding the device.

**DM14 checks every level because that predicate is the only thing standing between a confident-looking match and an automatic bind to the wrong device.** Lowering the bar to `Low` would let two guns of one model stand in for each other; forgetting the ambiguity check would pick arbitrarily between two tied devices. Both look identical on site: the operator scans away with an unbound gun while the UI reports everything as normal.

### 3.5 Guard verified by injecting the violation

| Violation injected | Result |
|---|---|
| Replace `BothPresentAndEqual` with a plain case-insensitive `==` | **DM5, DM7, DM3 and DM11 fail together.** DM7 reported the built-in keyboard matching at **`High`** — a confidence that permits automatic reconnection, so the built-in keyboard would have been auto-bound as the scanner. DM5 reported `Exact` from two null paths. |

The injected result is worth recording precisely because it was not a near miss. The naive implementation does not merely weaken the match; it produces the highest confidence that still permits an automatic bind, on the one device whose misidentification disables the operator's keyboard.

---

## 4. Phase A is complete

All of Tasks 1–3, 5a, 6, 7a and 8a are implemented and green: **230 xUnit executions**, zero build warnings, passing on Windows and required to pass on macOS through `ScannerHelper.CrossPlatform.slnf` (acceptance criterion 20).

What remains is Phase B, and it begins at the hard gate. Task 4a is done and its measurements are recorded, but two things it raised are still open and both belong to whoever runs 4b:

- **The figures have not been reproduced on pilot hardware.** Event timing is a property of the machine, and the development machine is not a pilot machine.
- **Whether the hook sees a complete code.** Task 4a established that this scanner already loses characters between itself and Windows with Scanner Helper not running at all (spec §22.5). No test in Phase A can tell whether the hook receives a complete code or a damaged one — and if the hardware does not deliver it, all of this correct logic produces a confidently wrong result, which is the failure mode spec §19.1 cares about most.
