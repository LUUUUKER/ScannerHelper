// =============================================================================
// SendInputNative.cs
//
// 中文：
//   SendInput 的原生声明（规格 §2.2、§5.3、§5.6）。
//
//   ★★ 本程序用 SendInput 做**两件性质完全不同的事**，用的标志也不同。
//      混用会各自出错，而且错法都很隐蔽，所以必须先分清楚：
//
//      一、输出扫描结果（规格 §2.2）—— KEYEVENTF_UNICODE
//
//        原始扫描在程序内部已经被还原成一个 string，再以 Unicode 字符逐个
//        发出。这样结果**不依赖**当前键盘布局、不依赖 CapsLock、也不依赖
//        扫码枪自己配的键盘布局。改用模拟扫描码，这三种失效模式会全部回来。
//
//      二、重放被扣留的按键（规格 §5.3）—— KEYEVENTF_SCANCODE
//
//        这里要复现的是**工人按下的那个物理键**，不是"某个字符"。理由有三：
//
//          输入法   中文输入法的组字过程挂在真实的按键事件上。用 Unicode
//                   字符重放，组字过程会被打断——规格 §4.3 把 IME 单列为
//                   验收项，正是因为扣留-重放这条路最威胁它。
//          修饰键   Shift 按下必须作为按键事件重放，它才能影响后续按键的
//                   解码。当成字符发出去，Shift 根本不产出字符，等于凭空
//                   消失，后面的字母全变成小写。
//          按键重复 按住不放产生的重复，只有按键事件能表达。
//
//        所以重放必须原样复现扫描码、扩展位、按下/弹起三项。少一项，
//        重放出来的就不是工人按下的那一下。
//
//   ★ 每一个合成事件都必须带上标记（规格 §5.6）。
//
//     标记放在 dwExtraInfo 里。不带标记的话，本程序发出去的输出会被自己的
//     钩子重新看到、再当成输入处理一遍，形成
//         SendInput → 钩子 → SendInput → …
//     的无限递归。规格 §19.1 的心跳探测也走这条通道，它靠的同样是这个标记。
//
//   ★ 一处必须知道的限制：SendInput 受 UIPI 约束。
//
//     完整性级别更高的窗口收不到我们发出的输入。规格 §2.1 的假设 A2 已经
//     把这件事写成了硬性要求：业务软件以普通权限运行，本程序必须同级。
//     若业务软件被提权，SendInput 会**静默失败**——返回值小于请求数，
//     而不是抛异常。所以返回值必须检查，否则现场表现是"扫了码什么都没发生"，
//     而日志里一片正常。
//
// English:
//   Native declarations for SendInput (spec §2.2, §5.3, §5.6).
//
//   This application uses SendInput for two entirely different purposes, with different
//   flags, and mixing them fails in ways that are hard to spot. They must be kept apart.
//
//   Emitting scan results (spec §2.2) uses KEYEVENTF_UNICODE. The raw scan has already been
//   reconstructed into a string inside the program, and re-emitting it as Unicode characters
//   makes the result independent of the active keyboard layout, of CapsLock, and of the
//   scanner's own configured layout. Simulating scan codes would bring all three failure modes
//   back.
//
//   Replaying withheld keystrokes (spec §5.3) uses KEYEVENTF_SCANCODE, because what must be
//   reproduced is the physical key the operator pressed rather than "some character". Three
//   reasons: IME composition hangs off real key events and Unicode replay breaks it, which is
//   why spec §4.3 lists IME as its own acceptance item; a Shift press must be replayed as a key
//   event to affect how later keys decode, and as a character it produces nothing at all and
//   simply vanishes, leaving every following letter lowercase; and key repeat can only be
//   expressed as key events. Replay must therefore reproduce the scan code, the extended flag
//   and the up/down direction exactly — miss one and what is replayed is not what the operator
//   pressed.
//
//   Every synthesized event carries a tag in dwExtraInfo (spec §5.6). Without it this
//   application's output is seen again by its own hook and processed as input, recursing
//   endlessly through SendInput. Spec §19.1's heartbeat probe relies on the same tag.
//
//   One limitation worth knowing: SendInput is subject to UIPI, and windows at a higher
//   integrity level do not receive our input. Spec §2.1's assumption A2 already makes this a
//   hard requirement — the business application runs unelevated and this program must match. If
//   the business application were elevated, SendInput would fail *silently*, returning fewer
//   than requested rather than throwing. The return value must therefore be checked, or the
//   symptom on site is "scanning does nothing" with a completely clean log.
//
// 包含的类型 / Types in this file:
//   SendInputNative
//   SendInputNative.INPUT / InputUnion / KEYBDINPUT / MOUSEINPUT / HARDWAREINPUT
// =============================================================================

using System.Runtime.InteropServices;

namespace ScannerHelper.Win32.Native;

/// <summary>
/// 中文：SendInput 的原生声明。
/// English: Native declarations for SendInput.
/// </summary>
internal static class SendInputNative
{
    /// <summary>中文：输入类型：键盘。 English: Input type: keyboard.</summary>
    internal const uint INPUT_KEYBOARD = 1;

    /// <summary>
    /// 中文：扩展键。扫描码带 E0 前缀的键（右 Ctrl、小键盘回车等）必须带上它，
    ///       否则重放出来的是左边那个同扫描码的键。
    /// English: An extended key. Keys whose scan code carries an E0 prefix — right Ctrl, the
    ///          numeric-keypad Enter — need this, or what is replayed is the left-hand key that
    ///          shares the scan code.
    /// </summary>
    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    /// <summary>中文：弹起。不带此位即为按下。 English: Key up; its absence means key down.</summary>
    internal const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>
    /// 中文：
    ///   把 wScan 当作一个 Unicode 码元发送，wVk 必须为 0（规格 §2.2）。
    ///
    ///   ★ 这是**输出扫描结果**用的标志，不是重放用的。它送出去的是一个字符，
    ///     与键盘布局无关——这正是规格 §2.2 选它的理由。但也正因为它送的是
    ///     字符而不是按键，它不能用来重放：修饰键在这里根本无从表达。
    /// English:
    ///   Treat wScan as a Unicode code unit, with wVk required to be zero (spec §2.2).
    ///
    ///   This is the flag for *emitting scan results*, not for replay. It delivers a character
    ///   independently of keyboard layout, which is exactly why spec §2.2 chose it — and
    ///   equally why it cannot replay: a modifier key has no expression here at all.
    /// </summary>
    internal const uint KEYEVENTF_UNICODE = 0x0004;

    /// <summary>
    /// 中文：
    ///   把 wScan 当作硬件扫描码发送，wVk 被忽略。
    ///
    ///   ★ 这是**重放被扣留按键**用的标志（规格 §5.3）。它复现的是一次真实的
    ///     按键，因此输入法的组字过程、修饰键的作用、按键重复都能正常工作。
    /// English:
    ///   Treat wScan as a hardware scan code, with wVk ignored.
    ///
    ///   This is the flag for *replaying withheld keystrokes* (spec §5.3). It reproduces a real
    ///   keypress, so IME composition, modifier effects and key repeat all behave normally.
    /// </summary>
    internal const uint KEYEVENTF_SCANCODE = 0x0008;

    /// <summary>
    /// 中文：一次输入事件。type 决定联合体里哪一支有效。
    /// English: One input event; type selects which arm of the union is valid.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        internal uint type;
        internal InputUnion U;
    }

    /// <summary>
    /// 中文：
    ///   输入事件的联合体。
    ///
    ///   ★ 三支必须全部声明，即便本程序只用键盘那一支。
    ///
    ///     联合体的大小由**最大的那一支**决定（鼠标那支），而 SendInput 要求
    ///     调用方传入正确的结构体大小。只声明键盘那支，Marshal.SizeOf 会算出
    ///     一个偏小的值，SendInput 直接失败并返回 0——而且不抛异常。
    ///     现场表现是"扫了码什么都没发生"，日志里一片正常。
    /// English:
    ///   The input union. All three arms must be declared even though only the keyboard arm is
    ///   used: a union's size is that of its largest member (the mouse arm), and SendInput
    ///   requires the caller to pass the correct structure size. Declaring only the keyboard arm
    ///   makes Marshal.SizeOf report too small a value and SendInput fails outright, returning
    ///   zero without throwing — presenting as "scanning does nothing" with a clean log.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] internal MOUSEINPUT mi;
        [FieldOffset(0)] internal KEYBDINPUT ki;
        [FieldOffset(0)] internal HARDWAREINPUT hi;
    }

    /// <summary>
    /// 中文：键盘输入事件。
    /// English: A keyboard input event.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        /// <summary>
        /// 中文：虚拟键码。使用 KEYEVENTF_UNICODE 或 KEYEVENTF_SCANCODE 时必须为 0。
        /// English: The virtual key. Must be zero with KEYEVENTF_UNICODE or KEYEVENTF_SCANCODE.
        /// </summary>
        internal ushort wVk;

        /// <summary>
        /// 中文：扫描码，或 Unicode 码元——取决于 dwFlags。
        /// English: A scan code, or a Unicode code unit, depending on dwFlags.
        /// </summary>
        internal ushort wScan;

        internal uint dwFlags;

        /// <summary>
        /// 中文：时间戳。填 0 表示由系统填充，这正是我们要的：伪造时间戳没有意义，
        ///       而且会让诊断日志里的时间与系统看到的对不上。
        /// English: The timestamp. Zero lets the system supply it, which is what is wanted:
        ///          fabricating one serves no purpose and would put the diagnostic log's times
        ///          out of step with what the system saw.
        /// </summary>
        internal uint time;

        /// <summary>
        /// 中文：
        ///   附加信息。★ 本程序在这里放合成事件的标记（规格 §5.6）。
        ///
        ///   钩子回调据此认出"这是我自己发的"并直接放行，从而杜绝
        ///   SendInput → 钩子 → SendInput 的无限递归。规格 §19.1 的心跳探测
        ///   也靠它——探测事件带着同样的标记，被捕获链路吞掉，绝不会到达
        ///   业务软件。
        /// English:
        ///   Extra information, where this application places its synthesized-event tag
        ///   (spec §5.6). The hook callback recognizes its own output by it and passes it
        ///   straight through, closing off the endless SendInput → hook → SendInput recursion.
        ///   Spec §19.1's heartbeat relies on it too: the probe carries the same tag, is
        ///   swallowed by the capture pipeline, and can never reach the business application.
        /// </summary>
        internal UIntPtr dwExtraInfo;
    }

    /// <summary>
    /// 中文：鼠标输入事件。本程序不发送鼠标输入，声明它只是为了让联合体大小正确。
    /// English: A mouse input event. This application sends none; it is declared only so the
    ///          union has the correct size.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        internal int dx;
        internal int dy;
        internal uint mouseData;
        internal uint dwFlags;
        internal uint time;
        internal UIntPtr dwExtraInfo;
    }

    /// <summary>
    /// 中文：硬件输入事件。同样只为联合体大小而声明。
    /// English: A hardware input event, likewise declared only for the union's size.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        internal uint uMsg;
        internal ushort wParamL;
        internal ushort wParamH;
    }

    /// <summary>
    /// 中文：
    ///   合成输入事件。
    ///   返回成功送入输入流的事件数；小于 cInputs 即为部分或全部失败。
    ///
    ///   ★ 返回值必须检查。失败时它**不抛异常**，只是返回一个偏小的数字。
    ///     最常见的失败原因是 UIPI：业务软件的完整性级别高于本程序
    ///     （规格 §2.1 假设 A2 要求两者同级）。不检查的话，现场表现是
    ///     "扫了码什么都没发生"，而日志里一片正常——规格 §19.1 说的正是
    ///     这种"看起来在工作"的失效。
    /// English:
    ///   Synthesizes input events, returning how many were successfully inserted into the input
    ///   stream; fewer than cInputs means partial or total failure.
    ///
    ///   The return value must be checked. Failure does not throw and merely returns a smaller
    ///   number. The usual cause is UIPI, the business application sitting at a higher integrity
    ///   level than this program (spec §2.1's assumption A2 requires them to match). Unchecked,
    ///   the symptom on site is "scanning does nothing" with a completely clean log — spec
    ///   §19.1's "looks like it is working" failure.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint cInputs, [In] INPUT[] pInputs, int cbSize);
}
