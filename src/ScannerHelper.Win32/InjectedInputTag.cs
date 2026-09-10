// =============================================================================
// InjectedInputTag.cs
//
// 中文：
//   本程序自己合成的输入事件所携带的标记（规格 §5.6）。
//
//   ★ 它防的是一个会让程序自己锁死的循环：
//
//         SendInput → 钩子看到 → 当成输入处理 → SendInput → …
//
//     没有标记的话，我们发出去的每一个字符都会被自己的钩子重新看到。
//     在 SN 模式下，那意味着发出的内容又被当成一次新的扫描收进缓冲区，
//     再发出去，再收进来——一枪扫描能把整个进程拖死。
//
//   ★ 为什么需要**自己的**标记，而不是只看 Windows 的 LLKHF_INJECTED 位。
//
//     LLKHF_INJECTED 说明"这是某个程序合成的"，但不说明是**谁**合成的。
//     对防递归而言这已经够用——别的程序合成的输入同样不来自扫码枪，
//     放行是对的。
//
//     但规格 §19.1 的心跳探测需要更强的判断：它要确认**自己发出的那一个**
//     探测事件确实被钩子看到了。若只看 LLKHF_INJECTED，另一个程序恰好在
//     同一时刻合成了一次输入，心跳就会误判"钩子还活着"——而钩子可能已经
//     被 Windows 摘掉了。那正是 §19.1 要检测的静默失效，用一个会误报的
//     判据去检测它，等于没检测。
//
//   ★ 取值本身不重要，重要的是它不太可能与别的程序撞上。
//
//     这里取一个固定的、明显不像"随手写的数字"的常量。它不需要保密，也不
//     提供任何安全性——它只是一个约定。若某天真的与别的程序撞上，后果是
//     我们把对方的输入误认成自己的然后放行，而放行本来就是对合成输入的
//     正确处理，所以撞车是良性的。
//
// English:
//   The tag this application places on the input events it synthesizes (spec §5.6).
//
//   It guards a loop that would otherwise lock the program against itself:
//   SendInput → the hook sees it → it is processed as input → SendInput → and so on. Untagged,
//   every character emitted would be seen again by our own hook. In SN mode that means the
//   emitted content is taken as a fresh scan, buffered, emitted, taken again — one scan could
//   drag the whole process down.
//
//   Why a tag of our own rather than relying on Windows' LLKHF_INJECTED bit: that bit says the
//   event was synthesized by *some* program and not by which one. For recursion prevention that
//   suffices, since another program's synthesized input is equally not from the scanner and
//   passing it through is right.
//
//   Spec §19.1's heartbeat needs something stronger. It must confirm that the specific probe
//   *it* emitted was seen by the hook. Relying on LLKHF_INJECTED alone, another program
//   synthesizing input at the same moment would have the heartbeat conclude the hook is alive
//   when Windows may already have removed it — the silent failure §19.1 exists to detect, chased
//   with a test that can report a false positive, which is no test at all.
//
//   The value itself does not matter beyond being unlikely to collide. It is a fixed constant
//   that plainly is not a casually chosen number. It is not secret and provides no security; it
//   is only a convention. Should it ever collide with another program, the consequence is that
//   we mistake their input for ours and pass it through — and passing through is already the
//   correct handling for synthesized input, so a collision is benign.
//
// 包含的类型 / Types in this file:
//   InjectedInputTag
// =============================================================================

namespace ScannerHelper.Win32;

/// <summary>
/// 中文：本程序合成输入事件所用的标记。
/// English: The tag this application marks its synthesized input events with.
/// </summary>
public static class InjectedInputTag
{
    /// <summary>
    /// 中文：
    ///   放进 <c>KEYBDINPUT.dwExtraInfo</c> 的标记值。
    ///
    ///   钩子回调据此认出本程序自己发出的事件并直接放行（规格 §5.6），
    ///   规格 §19.1 的心跳探测也据此确认钩子仍在工作。
    /// English:
    ///   The value placed in <c>KEYBDINPUT.dwExtraInfo</c>. The hook callback recognizes this
    ///   application's own events by it and passes them through (spec §5.6), and spec §19.1's
    ///   heartbeat uses it to confirm the hook is still working.
    /// </summary>
    public static readonly UIntPtr Value = unchecked((UIntPtr)0x5343_4E48_5250_0001UL);

    /// <summary>
    /// 中文：
    ///   心跳探测事件专用的标记（规格 §19.1）。
    ///
    ///   ★ 与普通输出的标记**不同**，这一点是有意的。
    ///
    ///     两者在防递归上作用相同——都要被钩子放行。但心跳需要分辨"这一个
    ///     事件是不是我刚发出的那个探测"，而不是"这是不是本程序发的"。
    ///     共用一个值的话，一次正常的扫描输出就会被心跳当成探测回波，
    ///     于是钩子即便已经被摘掉，只要工人还在扫码，心跳就一直报告"正常"。
    ///
    ///     而钩子被摘掉之后，原始条码正直接流进业务系统——那恰恰是 §19.1
    ///     要检测的情形。用一个会被正常输出触发的判据去检测它，等于在最需要
    ///     报警的时候保证不报警。
    /// English:
    ///   The tag reserved for heartbeat probes (spec §19.1), deliberately different from the one
    ///   on ordinary output.
    ///
    ///   For recursion prevention the two are equivalent — both must be passed through by the
    ///   hook. But the heartbeat needs to distinguish "is this the probe I just emitted" from
    ///   "was this emitted by this application". Sharing one value would let an ordinary scan's
    ///   output be mistaken for a probe echo, so that even with the hook removed the heartbeat
    ///   would keep reporting health for as long as the operator kept scanning.
    ///
    ///   And with the hook removed, raw barcodes are flowing straight into the business system —
    ///   precisely the condition §19.1 exists to detect. A test that ordinary output can satisfy
    ///   is guaranteed not to fire at the one moment it is needed.
    /// </summary>
    public static readonly UIntPtr HeartbeatValue = unchecked((UIntPtr)0x5343_4E48_5250_0002UL);

    /// <summary>
    /// 中文：某个 <c>dwExtraInfo</c> 是否是本程序打的标记（含心跳）。
    /// English: Whether a <c>dwExtraInfo</c> value is one of this application's tags, heartbeat
    ///          included.
    /// </summary>
    public static bool IsOurs(UIntPtr extraInfo)
        => extraInfo == Value || extraInfo == HeartbeatValue;
}
