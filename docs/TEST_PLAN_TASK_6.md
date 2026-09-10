# Task 6 Test Plan — Scan session, correlation, and the input coordinator

> **Scope:** `ScannerHelper.Core` only — `InputEventCorrelator`, `ScanSession`, `ScanInputCoordinator` and the domain types they need. All of it is Phase A: pure logic, no Windows API, every timeout read through `ISystemClock`, fully testable on any platform.
> **Method:** TDD. Every case below is written and failing before its implementation exists.
> **Source of truth:** `SCANNER_HELPER_SPEC.md`. Where this plan and the spec disagree, the spec wins and this file is wrong.
> **Evidence base:** `TASK_4A_MEASUREMENTS_*.md`. Several decisions below are settled by measurement rather than by argument; each says so.

Test IDs are stable and map one-to-one onto test method names, continuing the scheme established in `TEST_PLAN_PHASE_A.md`.

---

## 1. What Task 4a settled

The spike was run before this task precisely so the correlator would have one unknown fewer. These are its findings, and each one closes a design question that would otherwise have been guesswork.

| Finding | Measurement | What it decides |
|---|---|---|
| The hook callback fires **before** `WM_INPUT`, essentially always | 1049/1049 in run 1; 3775/3799 in run 2. Minimum delta 110 µs — not one exception | Device identity is **never** available when the callback must decide. Withhold-then-replay (spec §5.3) is the only viable design, not one option among several |
| Both orderings were nonetheless observed | 24 of 3799 arrived Raw-Input-first | No "always first" assumption is safe. The correlator must handle the fast path too |
| The channels **disagree on virtual key codes** | Hook reports `VK_LSHIFT` (0xA0) / `VK_LCONTROL` (0xA2) where Raw Input reports `VK_SHIFT` (0x10) / `VK_CONTROL` (0x11); scan codes always match | Correlation must key on the scan code (D-11) |
| Scanner and human typing differ by two orders of magnitude | Scanner within-burst median ≈ 1.1 ms; built-in keyboard median ≈ 94–122 ms | The ~300 ms scan inactivity timeout has ample headroom in both directions |
| The built-in keyboard is not a USB HID device | `\\?\ACPI#MSFT0001#…`, no VID/PID | Device matching cannot rely on VID/PID (spec assumption A4) |
| A minority of events were seen by one channel only | ~1% in run 2, pending re-measurement with corrected pairing | The correlator must have a defined answer for "the counterpart never came" (D-13) |

**One finding came from a bug in the harness rather than from the hardware, and it is the most instructive of them.** The harness's first pairing implementation had no expiry on pending events. When one channel missed an event, the FIFO queue for that key shifted permanently and every later event paired with one from seconds earlier — producing a reported inter-channel delta of **minus 14.8 seconds**. The median stayed plausible; only the tail betrayed it. Had nobody looked at the minimum, the whole distribution would have been used to size 4b's withhold window. D-12 exists so the production correlator cannot repeat this.

---

## 2. Resolved design decisions

Continuing D-1…D-10 from `TEST_PLAN_PHASE_A.md`. The test cases below assume these. Reopening one means changing this file first, not changing implementation.

| # | Decision | Rationale |
|---|---|---|
| D-11 | Correlation is keyed on **(scan code, extended flag, direction)** and **never** on the virtual key. | Measured, not reasoned: the two channels report different virtual keys for modifiers while their scan codes always agree (Task 4a §2). Keying on the virtual key leaves every modifier uncorrelated — a scan containing an uppercase letter would have its source misidentified. |
| D-12 | A pending event **expires**. It never waits indefinitely and never pairs with an event that arrived much later. | The harness hit exactly this and reported a delta of −14.8 s. Worse than being wrong, it looked right: a shifted pairing produces noise indistinguishable from data. Expiry stops one lost event corrupting everything after it. |
| D-13 | An event whose counterpart never arrives is **replayed as keyboard input**, and raises a diagnostic. It is never swallowed. | Spec §19 states it directly: never swallow normal keyboard input indefinitely. The asymmetry decides it — a leaked scanner character lands visibly in a focused field and the operator can correct it, whereas a swallowed keystroke is invisible, and "the keyboard stopped working" is the very disaster §5.7 exists to rescue. The diagnostic is not optional: if this path is taken often, correlation is broken and must be seen to be broken. |
| D-14 | Decoding scan codes into characters happens in **`ScannerHelper.Win32`**. `Core`'s `KeyEvent` carries an already-decoded character, nullable — modifiers decode to nothing. | Keyboard layout, CapsLock and IME state are platform state (spec §22.3). Decoding in `Core` would require Windows APIs and destroy the layering boundary that all of Phase A rests on. |
| D-15 | The terminating Enter is consumed and never appears in the result. A scan whose buffer is empty when Enter arrives is a **failure**, not an empty success. | Spec §5.4 and §10 require the Enter to be discarded. An empty scan means the capture path delivered nothing — the same fault the parsers report as `EmptyInput` (D-1 family), and it must not travel onward as a successful scan of `""`. |
| D-16 | `ScanSession` has a maximum buffered length. Exceeding it fails the scan rather than growing without bound. | A stuck key or a misidentified device would otherwise accumulate forever on the scan-handling path. Spec §19 requires failing safely and surfacing diagnostically rather than degrading quietly. |
| D-17 | The correlation window and the scan inactivity timeout are **separate configuration values**, and neither is derived from the other. | They answer different questions — "how long can the two channels disagree" versus "how long a gap ends a scan" — and differ by roughly two orders of magnitude in the measurements. The spec already warns that the ~300 ms scan timeout and the 300 ms `LowLevelHooksTimeout` share a number and nothing else; this is the same trap one layer down. |
| D-18 | The correlator returns a **decision**, never an action. It emits nothing, buffers nothing, and touches no clock except through `ISystemClock`. | Spec §17 requires it to stay a pure function over timestamped events so its ordering, timeout and out-of-order branches are exhaustively testable with synthetic sequences. It is the highest-risk component in the product; the ability to test it exhaustively is the whole reason it is isolated. |

---

## 3. Domain types — `EV1`–`EV6`

`KeyEvent`, `RawInputEvent`, `ScanResult`.

| ID | Test | Expected |
|---|---|---|
| EV1 | `KeyEvent` carries scan code, extended flag, direction, decoded character, injected flag, timestamp | All present; the character is nullable (D-14) |
| EV2 | `KeyEvent`'s correlation identity **excludes** the virtual key | Reflection/structural assertion (D-11) |
| EV3 | `RawInputEvent` carries a device identity plus the same correlation identity | Both channels expose one comparable identity |
| EV4 | `ScanResult` is a closed discriminated type; a failed result exposes no scan value | Same construction as `ParseResult` (D-7) |
| EV5 | A failed `ScanResult` retains the raw code | Force Send and the error display both need it (spec §10) |
| EV6 | No user-facing prose on the new public API | **Already covered; deliberately no separate test** — see below |

**EV6 has no test of its own, and that is a decision rather than an omission.** `ResultTypeGuardTests`' D6 sweeps every exported type in `typeof(ParseResult).Assembly`, which is Core, so Task 6's types fall under it automatically. Writing a second test would duplicate an existing guard, and a duplicated guard is worse than a missing one: both have to change together, and missing one leaves two rules quietly contradicting each other. The reason is recorded here because "why is there no EV6" needs an answer, or the next reader assumes it was forgotten.

**EV2 is the one that would be skipped and shouldn't be.** It looks like a restatement of D-11, but D-11 is a property of the *correlator* while EV2 is a property of the *type*. Making the identity a member of `KeyEvent` that structurally cannot contain the virtual key means no future correlator implementation can reintroduce the fault, however it is rewritten. Guarding the algorithm alone would leave the next rewrite free to repeat a bug that cost a full measurement run to find.

---

## 4. `InputEventCorrelator` — `CR1`–`CR17`

The highest-risk component in the product. Every case here uses synthetic event sequences and a fake clock; none of it needs Windows.

### 4.1 The four decisions

Spec §17 fixes the vocabulary: `Swallow`, `PassThrough`, `Replay`, `Undecided`.

| ID | Sequence | Expected |
|---|---|---|
| CR1 | Hook event, then matching Raw Input from the **bound scanner** | `Undecided`, then `Swallow` |
| CR2 | Hook event, then matching Raw Input from **another device** | `Undecided`, then `Replay` |
| CR3 | Raw Input **first**, then the matching hook event | Decided immediately; **no** `Undecided` |
| CR4 | Hook reports `VK_LSHIFT`, Raw Input reports `VK_SHIFT`, same scan code | Correlates (D-11) |
| CR5 | Same scan code, **different** extended flag | Does **not** correlate |
| CR6 | Key-down does not correlate with the key-up of the same key | Direction is part of the identity |

**CR3 is the case the measurements say is rare and the design must still handle.** 24 of 3799 events arrived Raw-Input-first. An implementation that assumed hook-always-first would be correct 99.4% of the time and wrong roughly once per forty scans — frequent enough to matter, rare enough to survive a demonstration.

**CR4 is Task 4a's finding in executable form — with one honest caveat.** It cannot be made to fail by changing the correlator, and that was attempted rather than assumed. `RawInputEvent` carries no virtual key and neither does `KeyIdentity`, so "pair on the virtual key" is not expressible: the correlator does not avoid the mistake, the types make it impossible. That is stronger than a test that can go red, but it means CR4 guards a *future* change rather than today's implementation — adding a virtual key to `RawInputEvent` and folding it into `KeyIdentity` turns it red immediately. Stating it as more than that would repeat the error D4 was written to avoid.

### 4.2 Ordering and interleaving

| ID | Scenario | Expected |
|---|---|---|
| CR7 | Two rapid presses of the same key | Correlate in FIFO order, first with first |
| CR8 | Two different keys interleaved across both channels | Each correlates independently |
| CR9 | Raw Input events arrive in a different order than the hook events | Each still finds its own counterpart |

### 4.3 Expiry and failure — the cases that matter most

| ID | Scenario | Expected |
|---|---|---|
| CR10 | A hook event's counterpart never arrives | After the correlation window: `Replay`, plus a diagnostic (D-13) |
| CR11 | An event marked injected (our own `SendInput` output) | `PassThrough` immediately; never enters correlation (spec §5.6) |
| CR12 | A Raw Input event whose hook counterpart never arrives | Discarded after the window, counted; produces no output |
| CR13 | The correlation window is read through `ISystemClock` | A fake clock drives it; no `DateTime.UtcNow`, no `Thread.Sleep` |
| CR14 | Sustained mismatched traffic | Pending events are bounded; memory does not grow without limit |
| CR15 | The correlator's public surface | Structural assertion: no Windows types, no output service, no clock other than `ISystemClock` (D-18) |
| CR16 | `null` arguments | Throw `ArgumentNullException`, matching D-1 |
| CR17 | A pending event, then a matching event far later than the window | They must **not** pair; the first expires, the second starts fresh |

**CR17 is the harness's bug written as a test, and it is the most valuable case in this file.** Without expiry, one missing event shifts a FIFO queue permanently and every subsequent event pairs with one from seconds earlier. The consequence is not an obvious failure but a plausible-looking one: the median stays sane while the tail goes to −14.8 seconds. In the product the same fault would attribute keystrokes to the wrong device long after the event that caused it, with nothing pointing back at the cause.

**Verified by removing the expiry: CR17 fails, and CR10 and CR12 fail with it** — exactly as predicted, the three being one property seen from three directions. CR11 was verified separately by deleting its early return, and fails alone.

**CR11 deserves its own note.** Injected events must be recognized and passed through *before* correlation is attempted, not after. Task 4a confirmed that synthesized events never appear on the Raw Input channel at all, so an injected event entering correlation would wait for a counterpart that cannot exist, expire, and be replayed — feeding our own output back into our own pipeline. That is precisely the recursion spec §5.6 forbids, arriving by a route that looks like correct timeout handling.

---

## 5. `ScanSession` — `SS1`–`SS12`

One atomic scan: buffer, timestamps, terminator, timeout, result.

| ID | Scenario | Expected |
|---|---|---|
| SS1 | Characters arrive in sequence | Buffered in order |
| SS2 | Enter arrives | Scan completes; the Enter is **not** in the result (spec §5.4, §10) |
| SS3 | No character for longer than the inactivity timeout | Incomplete scan discarded, nothing emitted, state returns to idle, a visible scan error is reported |
| SS4 | Timeout is measured from the **last character**, not from session start | A long scan does not time out merely by being long |
| SS5 | Timeout boundary | One tick before: still scanning. At and after: timed out |
| SS6 | Enter arrives with an empty buffer | Failure, not an empty success (D-15) |
| SS7 | After a completed scan | Session resets; the next scan starts clean |
| SS8 | After a timed-out scan | Session resets; no residue leaks into the next scan |
| SS9 | Modifier keys during a scan | Never appear as characters (they decode to nothing, D-14) |
| SS10 | Leading and trailing whitespace in a scan | Never trimmed (D-9) |
| SS11 | A scan exceeding the maximum length | Fails; the buffer does not grow without bound (D-16) |
| SS12 | Every timeout is read through `ISystemClock` | A fake clock drives them; no `Thread.Sleep` anywhere |

**SS4 and SS5 are the pair that pins the timeout's meaning.** Measuring from session start would abort any scan longer than the timeout regardless of how fast its characters arrived — with the measured 1.1 ms interval, that would cap a barcode at roughly 270 characters, which sounds safe until a site adopts a longer symbology. SS5 covers the boundary in both directions, because "greater than" and "greater than or equal" are the same length of code and differ by one character interval.

**SS8 is the case that fails silently in production.** A session that resets on completion but not on timeout leaves the failed scan's characters in the buffer, and the *next* scan reports a code that is part previous scan, part current. Task 4a observed exactly that shape coming out of the hardware — `DGKJRDC5679F5NDGKF5NF`, two transmissions overlapping — which means the symptom is already known to occur here for unrelated reasons and would be indistinguishable in a bug report.

**SS11's bound is a safety property, not an optimization.** Spec §19 requires any unresolvable event to fail safely and be surfaced. A device misidentified as the scanner would otherwise feed a buffer that grows for as long as someone types.

---

## 6. `ScanInputCoordinator` — `CO1`–`CO15`

The high-level state machine: `IDLE → IDENTIFYING → SCANNING → PROCESSING → IDLE`, plus `PAUSED`.

### 6.1 PAUSED — the safety valve

| ID | Scenario | Expected |
|---|---|---|
| CO1 | Any event while paused | `PassThrough`, without inspecting it |
| CO2 | Scanner input while paused | Nothing buffered, nothing emitted, no session started |
| CO3 | Pausing and resuming | The SN/SKU mode is unchanged by both |
| CO4 | Hotkeys while paused | Not intercepted; they reach the business application (spec §5.7) |

**CO1's "without inspecting it" is the whole point and is testable.** Spec §5.7 makes `PAUSED` independent of the correctness of correlation, buffering and replay — that independence is what lets it rescue an operator when those are broken. A coordinator that checked the paused flag *after* consulting the correlator would inherit every correlator bug into the one state that exists to escape them. The test drives the coordinator with a correlator substitute that throws if called.

Assumption A4 raises the stakes: on a laptop there is no second keyboard to plug in, so the mouse-clickable pause control is the only escape route there is.

### 6.2 Mode behavior

| ID | Scenario | Expected |
|---|---|---|
| CO5 | Completed scan in SN mode | Output is the exact raw string, unmodified |
| CO6 | Completed scan in SKU mode | Parsed, then validated |
| CO7 | Parse failure | Error state; **no** automatic output; the raw code is retained |
| CO8 | Validation failure | Same as CO7, carrying every failure (D-6) |
| CO9 | Successful scan | Emits automatically |
| CO10 | Mode toggled between scans | Each scan uses the mode in force when it completed |

**CO5 is worth stating even though it looks trivial.** SN mode is the identity transform *inside* the pipeline (spec §5.7's table), not a bypass of it: the characters were swallowed, decoded and re-emitted. The test exists because "SN just passes it through" is the natural mental shorthand, and acting on that shorthand — skipping the pipeline for SN — would break the injected-output tagging and the paused/SN distinction at once.

### 6.3 State machine and structure

| ID | Scenario | Expected |
|---|---|---|
| CO11 | Full cycle | `IDLE → IDENTIFYING → SCANNING → PROCESSING → IDLE` |
| CO12 | Scan timeout | Returns to `IDLE` with a visible error; the next scan is unaffected |
| CO13 | Parse or validation failure | Enters the pending-error state and stays there awaiting a decision |
| CO14 | The coordinator's public surface | No WPF, no Windows types, no direct clock (spec §17) |
| CO15 | An event arriving in an unexpected state | Handled deterministically; never throws on the scan-handling path |

**CO15 is the one that decides whether a defect becomes an outage.** Parsing, correlation and session handling all sit on the path a keystroke travels. Spec §19 is explicit: an uncaught exception there means the worker scans and nothing happens — and, worse, may leave the hook installed but the pipeline dead, which is the silent-failure shape §19.1 is built to detect.

---

## 7. Deliberately not covered here

- **Force Send, Cancel, and the output contract** are Task 7. The coordinator's error state is tested here; what happens when `F10` is pressed is not.
- **Hotkey routing rules** are Task 8.
- **`DeviceIdentityMatcher`** is Task 5a.
- **Anything requiring Windows** — hook installation, `SendInput`, real device identity, IME behavior under withhold-and-replay — is Task 4b and the diagnostics harness. Spec §20 is explicit that simulating these with unit tests would be worse than not testing them, because a passing fake implies coverage that does not exist.

**One specific thing this plan cannot answer, and must not appear to.** Task 4a established that this scanner already loses characters between itself and Windows, with Scanner Helper not running at all (spec §22.5). No test in this file tells us whether the hook sees a complete code or a damaged one — that needs the harness and real hardware. Every case here assumes the correlator is handed the events that actually occurred. If the hardware does not deliver them, correct logic produces a confidently wrong result, which is the failure mode spec §19.1 cares about most.

---

## 8. Summary

Roughly 50 cases across four components, all running on `net8.0` and all passing on a non-Windows machine.

Three do disproportionate work. **CR4** encodes the measured fact that the two channels disagree about virtual keys, without which every scan containing a capital letter is attributed to the wrong device. **CR17** encodes the expiry rule, without which one lost event quietly corrupts every correlation after it while the statistics still look healthy. **CO1** keeps `PAUSED` independent of everything it exists to escape from — and on a laptop, with no spare keyboard to plug in, that independence is the only escape route the operator has.
