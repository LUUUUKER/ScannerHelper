// =============================================================================
// KeyEvent.cs
//
// 中文：
//   低层键盘钩子观测到的一次按键。
//
//   ★ 字符是**已经解码好的**，解码在 ScannerHelper.Win32 完成（决策 D-14）。
//
//     把扫描码翻译成字符需要当前键盘布局、CapsLock 状态，以及输入法的状态——
//     这些全是平台状态，Windows 用 ToUnicodeEx 提供（规格 §22.3）。若把解码
//     放进 Core，Core 就必须引用 Windows API，目标框架被迫变成 net8.0-windows，
//     整个 Phase A 赖以存在的分层边界当场崩塌（守卫见测试 A1~A3）。
//
//     所以 Core 收到的是字符，不是扫描码加布局。这条边界划得很清楚：
//     **Win32 负责"这一下按的是什么字"，Core 负责"这些字凑起来是什么意思"。**
//
//   ★ 字符可空，null 表示这一下没有对应字符。
//
//     修饰键（Shift、Ctrl、Alt）本身不产出字符，只改变后续按键的解码结果。
//     用 null 而不是空字符，是因为"没有字符"和"字符是空格"是两回事，
//     而扫描内容里空格是合法字符。
//
//   ★ 虚拟键码在这里**有**，但它不参与关联。
//
//     关联走 <see cref="Identity"/>，那个类型在结构上就装不下虚拟键码
//     （决策 D-11，理由见 KeyIdentity）。虚拟键码留在这里是给 Task 8 的
//     热键路由用的——判断"这一下是不是 F8"需要它，而那与关联是两件事。
//
//     这个区分本身就是守卫：虚拟键码是可得的，身份却刻意不用它。
//
//   ★ 时间戳是**单调时刻**（TimeSpan），不是挂钟时间。
//
//     理由见 ISystemClock：挂钟会因 NTP 校时而跳变，用它算超时会在现场表现为
//     "偶尔莫名其妙丢一枪"且无法复现。两种时间的返回类型不同，因此把它们
//     相减根本不能编译——这条区分靠类型系统挡住，不靠人记住。
//
// English:
//   One keystroke as observed by the low-level keyboard hook.
//
//   The character is already decoded, in ScannerHelper.Win32 (decision D-14).
//   Translating a scan code into a character needs the active keyboard layout, CapsLock
//   state and IME state — all platform state, supplied by Windows through ToUnicodeEx
//   (spec §22.3). Decoding inside Core would force it to reference Windows APIs and
//   retarget to net8.0-windows, collapsing the layering boundary all of Phase A rests on
//   (guarded by tests A1–A3). The boundary is therefore drawn plainly: Win32 decides
//   *what character was typed*, Core decides *what those characters mean together*.
//
//   The character is nullable, with null meaning this keystroke produced none. Modifiers
//   produce no character of their own and only change how later keys decode. Null rather
//   than an empty character, because "no character" and "the character is a space" are
//   different things and a space is legitimate inside scanned content.
//
//   The virtual key is present but takes no part in correlation, which goes through
//   Identity — a type that structurally cannot hold one (decision D-11; see
//   KeyIdentity). The virtual key is here for Task 8's hotkey routing, which needs to
//   ask "was that F8" and is a different question from correlation. The separation is
//   itself the guard: the virtual key is available, and the identity deliberately
//   declines to use it.
//
//   The timestamp is a monotonic instant, not wall-clock time. Wall-clock time jumps
//   under NTP correction, and a timeout computed from it presents on the warehouse floor
//   as "we occasionally lose a scan for no reason", irreproducibly. The two kinds have
//   different return types, so subtracting one from the other does not compile — the
//   distinction is enforced by the type system rather than by remembering it.
//
// 包含的类型 / Types in this file:
//   KeyEvent
// =============================================================================

namespace ScannerHelper.Core.Domain;

/// <summary>
/// 中文：低层键盘钩子观测到的一次按键。不可变。
/// English: One keystroke observed by the low-level keyboard hook. Immutable.
/// </summary>
/// <param name="Timestamp">
/// 中文：单调时刻，取自 <see cref="Abstractions.ISystemClock.MonotonicNow"/>。
///       钩子回调必须在收到事件的**第一时间**取它——晚取一步，测出来的就
///       多混进一分我们自己代码的耗时。
/// English: A monotonic instant from
///          <see cref="Abstractions.ISystemClock.MonotonicNow"/>. The hook callback must
///          take it the instant the event arrives; any later and it folds our own cost
///          into the measurement.
/// </param>
/// <param name="ScanCode">中文：硬件扫描码。 English: The hardware scan code.</param>
/// <param name="IsExtended">
/// 中文：扫描码是否带 E0 前缀。 English: Whether the scan code carries an E0 prefix.
/// </param>
/// <param name="IsKeyUp">中文：弹起为 true。 English: True for key up.</param>
/// <param name="VirtualKey">
/// 中文：虚拟键码。供 Task 8 的热键路由使用，**不参与关联**——两条通道对修饰键
///       报告的虚拟键码不同（决策 D-11）。
/// English: The virtual key, for Task 8's hotkey routing. It takes no part in
///          correlation: the two channels disagree about it for modifiers (D-11).
/// </param>
/// <param name="Character">
/// 中文：已解码的字符，由 Win32 用当前键盘布局解出（决策 D-14）。
///       null 表示这一下没有对应字符，例如修饰键。
/// English: The decoded character, produced by Win32 from the active layout (D-14).
///          Null means this keystroke has no character, as for a modifier.
/// </param>
/// <param name="IsInjected">
/// 中文：是否为程序合成的事件。规格 §5.6 要求本程序自己发出的输出绝不能被自己
///       的捕获链路重新吃进去，否则形成 SendInput → 钩子 → SendInput 的无限递归。
/// English: Whether the event was synthesized. Spec §5.6 requires this application's own
///          output never to re-enter its own pipeline, which would otherwise recurse
///          endlessly through SendInput and the hook.
/// </param>
public readonly record struct KeyEvent(
    TimeSpan Timestamp,
    ushort ScanCode,
    bool IsExtended,
    bool IsKeyUp,
    ushort VirtualKey,
    char? Character,
    bool IsInjected)
{
    /// <summary>
    /// 中文：跨通道关联用的身份。不含虚拟键码（决策 D-11）。
    /// English: The identity used for cross-channel correlation, free of the virtual key
    ///          (decision D-11).
    /// </summary>
    public KeyIdentity Identity => new(ScanCode, IsExtended, IsKeyUp);
}
