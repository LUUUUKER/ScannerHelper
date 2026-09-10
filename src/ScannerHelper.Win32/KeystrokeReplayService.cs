// =============================================================================
// KeystrokeReplayService.cs
//
// 中文：
//   把被扣留的按键原样补发出去（规格 §5.3、§19）。
//
//   ★★ 这是扣留-重放这条路上最要命的一环，也是 Task 4b 硬性关卡的第 3、4 条。
//
//     Task 4a 实测：钩子 100% 先于 WM_INPUT 到达，最小时差 110 微秒。因此
//     钩子回调做决定时**从来**不知道是谁按的，只能先全吞掉。等 Raw Input
//     揭示来源之后，若是普通键盘，就必须把刚才吞掉的那些按键一个不差地
//     补回去。
//
//     补不回去 = 工人的按键凭空消失。规格 §19 明写"绝不无限期吞掉普通键盘
//     输入"，而规格假设 A4 说工位是笔记本，没有备用键盘可插——键盘失灵
//     就是彻底失灵。
//
//   ★ 必须用 KEYEVENTF_SCANCODE 复现**物理按键**，不能用 Unicode 字符。
//
//     这一点是本文件与 SendInputKeyboardOutputService 的根本差别，三个理由：
//
//       输入法   中文输入法的组字过程挂在真实的按键事件上。用 Unicode 字符
//                重放，组字会被打断——规格 §4.3 把 IME 单列为验收项，正是
//                因为扣留-重放这条路最威胁它。这也是 4b 硬性关卡第 3 条。
//       修饰键   Shift 按下必须作为按键事件重放，它才能影响后续按键的解码。
//                当成字符发，Shift 根本不产出字符，等于凭空消失，后面的
//                字母全变成小写。这是 4b 硬性关卡第 4 条。
//       按键重复 按住不放产生的重复，只有按键事件能表达。
//
//     所以扫描码、扩展位、按下/弹起三项必须原样复现。少一项，补出来的
//     就不是工人按下的那一下。
//
//   ★ 顺序必须保持。
//
//     一次补发多个按键时，它们之间的先后关系是有意义的：Shift 按下必须在
//     字母之前，弹起必须在之后。因此**一次 SendInput 调用发完一批**，
//     而不是逐个调用——SendInput 保证同一次调用内的事件不会被其他线程的
//     输入插进来。逐个调用的话，工人恰好在此刻按了一下键，那一下就可能
//     落在 Shift 与字母之间，于是大写变成了小写。
//
// English:
//   Re-emits withheld keystrokes exactly as they were pressed (spec §5.3, §19).
//
//   This is the most dangerous link in the withhold-and-replay path and covers points 3 and 4 of
//   Task 4b's hard gate.
//
//   Task 4a measured the hook arriving before WM_INPUT 100% of the time with a minimum delta of
//   110 µs, so the callback never knows who pressed a key when it must decide and can only
//   swallow everything. Once Raw Input reveals the source, keystrokes that turned out to be from
//   an ordinary keyboard must be put back, every one of them.
//
//   Failing to put them back means the operator's keystrokes vanish. Spec §19 forbids swallowing
//   normal keyboard input indefinitely, and spec assumption A4 puts the workstation on a laptop
//   with no spare keyboard to plug in: a dead keyboard is simply dead.
//
//   Replay must reproduce the physical keystroke through KEYEVENTF_SCANCODE rather than a Unicode
//   character, which is the fundamental difference between this file and
//   SendInputKeyboardOutputService. Three reasons: IME composition hangs off real key events and
//   Unicode replay breaks it, which is why spec §4.3 lists IME as its own acceptance item and why
//   it is point 3 of the 4b gate; a Shift press must be replayed as a key event to affect how
//   later keys decode, and as a character it produces nothing and vanishes, leaving every
//   following letter lowercase, which is point 4; and key repeat can only be expressed as key
//   events. Scan code, extended flag and direction must therefore all be reproduced exactly —
//   miss one and what comes back is not what the operator pressed.
//
//   Order must hold. Within a batch the sequence carries meaning: a Shift down must precede its
//   letter and the up must follow it. A batch therefore goes out in one SendInput call rather
//   than one call per event, since SendInput guarantees that events in a single call are not
//   interleaved with input from other threads. Called one at a time, a keystroke the operator
//   happens to make could land between the Shift and the letter, turning a capital into a
//   lowercase letter.
//
// 包含的成员 / Members in this file:
//   Replay  按原样补发一批被扣留的按键
// =============================================================================

using System.ComponentModel;
using System.Runtime.InteropServices;
using ScannerHelper.Core.Domain;
using ScannerHelper.Win32.Native;

namespace ScannerHelper.Win32;

/// <summary>
/// 中文：把被扣留的按键补发回去。
/// English: Puts withheld keystrokes back.
/// </summary>
public sealed class KeystrokeReplayService
{
    private static readonly int InputSize = Marshal.SizeOf<SendInputNative.INPUT>();

    /// <summary>
    /// 中文：
    ///   补发一批被扣留的按键，顺序与扣留时一致。
    ///   输入：keyEvents 待补发的按键，不得为 null；空集合是空操作。
    ///   输出：无。
    ///
    ///   ★ 一次调用发完一批，不逐个发送——顺序在一次调用内才有保证，
    ///     理由见文件头。
    ///
    ///   ★ 补发出去的事件会被本程序自己的钩子重新看到，因此每一个都带上
    ///     合成标记（规格 §5.6）。钩子据此放行，不会把它们再当成新的输入
    ///     扣留一次——那会形成一个"扣留→补发→再扣留"的死循环，而且是
    ///     在工人打字的路径上。
    /// English:
    ///   Replays a batch of withheld keystrokes in the order they were withheld. An empty
    ///   collection is a no-op.
    ///
    ///   One call per batch rather than one per event: order is only guaranteed within a call,
    ///   for the reason in the file header.
    ///
    ///   Replayed events are seen again by this application's own hook, so each carries the
    ///   synthesized tag (spec §5.6). The hook passes them through rather than withholding them
    ///   as fresh input, which would form a withhold-replay-withhold loop sitting directly on the
    ///   path of the operator's typing.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：keyEvents 为 null。 English: keyEvents is null.
    /// </exception>
    /// <exception cref="Win32Exception">
    /// 中文：部分或全部按键未能送入输入流。**这意味着工人的按键真的丢了**，
    ///       不是一个可以忽略的失败。
    /// English: Some or all keystrokes could not be inserted. This means the operator's
    ///          keystrokes really were lost and is not an ignorable failure.
    /// </exception>
    public void Replay(IReadOnlyList<KeyEvent> keyEvents)
    {
        ArgumentNullException.ThrowIfNull(keyEvents);

        if (keyEvents.Count == 0)
        {
            return;
        }

        var inputs = new SendInputNative.INPUT[keyEvents.Count];
        for (var index = 0; index < keyEvents.Count; index++)
        {
            inputs[index] = ToScanCodeInput(keyEvents[index]);
        }

        var inserted = SendInputNative.SendInput((uint)inputs.Length, inputs, InputSize);

        if (inserted == inputs.Length)
        {
            return;
        }

        throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            $"补发被扣留的按键失败：请求 {inputs.Length} 个，实际送入 {inserted} 个。"
            + "**工人的按键真的丢了**——规格 §19 要求绝不无限期吞掉普通键盘输入，"
            + "而笔记本工位没有备用键盘可插（规格假设 A4）。"
            + " 最常见的原因是业务软件以更高的完整性级别运行，UIPI 阻止了输入"
            + "（规格 §2.1 假设 A2）。"
            + $" Replay failed: requested {inputs.Length} keystrokes and inserted {inserted}. The"
            + " operator's keystrokes were genuinely lost. The usual cause is the business"
            + " application running at a higher integrity level, with UIPI blocking the input.");
    }

    /// <summary>
    /// 中文：
    ///   把一个被扣留的按键还原成一个输入事件。
    ///
    ///   ★ 用扫描码而不是虚拟键码，是为了复现**物理按键**。虚拟键码在两条
    ///     通道上本来就不一致（Task 4a 实测：钩子报 VK_LSHIFT，Raw Input 报
    ///     VK_SHIFT），拿它去重放等于把这个分歧带进补发出去的事件里——
    ///     左右 Shift 会被混为一谈。扫描码没有这个问题，两条通道始终一致。
    ///
    ///   ★ wVk 必须为 0。使用 KEYEVENTF_SCANCODE 时它会被忽略，但显式置零
    ///     可以避免"既给了扫描码又给了虚拟键码、两者不一致"这种自相矛盾的
    ///     事件——那种事件在不同 Windows 版本上的行为无法保证。
    /// English:
    ///   Turns one withheld keystroke back into an input event.
    ///
    ///   Scan codes rather than virtual keys, to reproduce the physical key. The two channels
    ///   disagree about virtual keys in the first place — Task 4a measured the hook reporting
    ///   VK_LSHIFT where Raw Input reported VK_SHIFT — and replaying by virtual key would carry
    ///   that disagreement into the events sent back, conflating left and right Shift. Scan codes
    ///   have no such problem, both channels always agreeing on them.
    ///
    ///   wVk must be zero. KEYEVENTF_SCANCODE ignores it, but setting it explicitly avoids a
    ///   self-contradictory event carrying both a scan code and a disagreeing virtual key, whose
    ///   behavior is not guaranteed across Windows versions.
    /// </summary>
    private static SendInputNative.INPUT ToScanCodeInput(in KeyEvent keyEvent)
    {
        var flags = SendInputNative.KEYEVENTF_SCANCODE;

        if (keyEvent.IsExtended)
        {
            flags |= SendInputNative.KEYEVENTF_EXTENDEDKEY;
        }

        if (keyEvent.IsKeyUp)
        {
            flags |= SendInputNative.KEYEVENTF_KEYUP;
        }

        return new SendInputNative.INPUT
        {
            type = SendInputNative.INPUT_KEYBOARD,
            U = new SendInputNative.InputUnion
            {
                ki = new SendInputNative.KEYBDINPUT
                {
                    wVk = 0,
                    wScan = keyEvent.ScanCode,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = InjectedInputTag.Value,
                },
            },
        };
    }
}
