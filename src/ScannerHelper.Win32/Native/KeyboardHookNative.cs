// =============================================================================
// KeyboardHookNative.cs
//
// 中文：
//   WH_KEYBOARD_LL 低层键盘钩子的原生声明（规格 §4.1）。
//
//   这条通道的性质：
//     - Windows 会在按键送达焦点程序**之前**，**同步**调用你的回调；
//     - 回调可以吞掉这次按键（返回非 0），焦点程序将永远不知道有人按过它；
//     - 但 KBDLLHOOKSTRUCT 里**没有设备身份**——你无从判断这一下是扫码枪
//       按的还是人按的。
//
//   这正是需要事件关联的原因：Raw Input 知道是谁按的却拦不住，钩子拦得住
//   却不知道是谁。两条通道各缺一半。
//
//   ★ 回调必须极快（规格 §19）。
//
//     Windows 有一个 LowLevelHooksTimeout（默认 300 毫秒）。回调只要超时一次，
//     系统就跳过它，而且通常直接把钩子摘掉，**不发任何通知**。进程还在跑，
//     界面还显示着"SKU 模式"，实际上原始条码已经直接流进业务系统了——这种
//     故障比崩溃更糟，因为崩溃看得见。规格 §19.1 的心跳检测就是为它准备的。
//
//     因此回调里绝对不能有：文件 IO、正则、UI 操作、加锁、日志、内存分配。
//     它唯一该做的事是把事实记下来然后立刻返回。
//
// English:
//   Native declarations for the WH_KEYBOARD_LL low-level keyboard hook (spec §4.1).
//
//   What this channel is: Windows calls the callback *synchronously*, *before* the
//   keystroke reaches the focused application; the callback may swallow it by
//   returning non-zero, and the focused application never learns it happened. But
//   KBDLLHOOKSTRUCT carries no device identity, so there is no way to tell the
//   scanner from a person.
//
//   That is exactly why correlation is needed: Raw Input knows who but cannot stop
//   it; the hook can stop it but does not know who. Each channel has half the answer.
//
//   The callback must be extremely fast (spec §19). Windows enforces
//   LowLevelHooksTimeout, 300 ms by default. Exceed it once and the system skips the
//   callback and usually removes the hook outright, with no notification whatsoever.
//   The process keeps running and the UI keeps showing "SKU MODE" while raw codes
//   flow straight into the business system — worse than a crash, because a crash is
//   visible. Spec §19.1's heartbeat exists for precisely this.
//
//   So the callback must contain no file IO, no regex, no UI work, no locks, no
//   logging, and no allocation. Its only job is to record the fact and return.
//
// 包含的类型 / Types in this file:
//   KeyboardHookNative
//   KeyboardHookNative.KBDLLHOOKSTRUCT
//   KeyboardHookNative.LowLevelKeyboardProc
// =============================================================================

using System.Runtime.InteropServices;

namespace ScannerHelper.Win32.Native;

/// <summary>
/// 中文：低层键盘钩子的原生声明。
/// English: Native declarations for the low-level keyboard hook.
/// </summary>
internal static class KeyboardHookNative
{
    /// <summary>
    /// 中文：低层键盘钩子的类型编号。
    /// English: The low-level keyboard hook type.
    /// </summary>
    internal const int WH_KEYBOARD_LL = 13;

    /// <summary>
    /// 中文：按键被按下 / 弹起 / 系统键按下 / 系统键弹起。回调的 wParam 取这些值。
    ///       "系统键"指按住 Alt 时的按键，以及 F10。
    /// English: Key down / up / system key down / system key up, as passed in the
    ///          callback's wParam. "System" means a key pressed while Alt is held,
    ///          plus F10.
    /// </summary>
    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_KEYUP = 0x0101;
    internal const int WM_SYSKEYDOWN = 0x0104;
    internal const int WM_SYSKEYUP = 0x0105;

    /// <summary>
    /// 中文：KBDLLHOOKSTRUCT.flags 的位。
    ///
    ///       LLKHF_INJECTED 尤其重要：它标记这个事件是被程序合成出来的
    ///       （SendInput），而不是真人按的。规格 §5.6 要求本程序自己发出的
    ///       输出绝不能被自己的捕获链路重新吃进去，否则会形成
    ///       SendInput → 钩子 → SendInput 的无限递归。
    /// English: Bits in KBDLLHOOKSTRUCT.flags.
    ///
    ///          LLKHF_INJECTED matters most: it marks an event synthesized by a
    ///          program (SendInput) rather than pressed by a person. Spec §5.6
    ///          requires this application's own output never to be recaptured by its
    ///          own pipeline, which would otherwise recurse endlessly through
    ///          SendInput to hook and back.
    /// </summary>
    internal const uint LLKHF_EXTENDED = 0x01;
    internal const uint LLKHF_LOWER_IL_INJECTED = 0x02;
    internal const uint LLKHF_INJECTED = 0x10;
    internal const uint LLKHF_ALTDOWN = 0x20;
    internal const uint LLKHF_UP = 0x80;

    /// <summary>
    /// 中文：
    ///   低层键盘钩子的回调签名。
    ///   输入：nCode 小于 0 时必须原样转交、不得处理；wParam 为上面四个消息
    ///         之一；lParam 指向一个 <see cref="KBDLLHOOKSTRUCT"/>。
    ///   输出：返回 <see cref="CallNextHookEx"/> 的结果表示放行；返回非 0
    ///         表示吞掉这次按键。
    ///
    ///   ★ 这个委托实例必须被托管代码**持有**，不能只作为参数传进去就不管了。
    ///     否则 GC 会回收它，而 Windows 手里还留着那个函数指针，下一次按键
    ///     就会跳进一段已经失效的内存——表现为进程随机崩溃，且崩溃点与真正
    ///     的原因毫无关系。
    /// English:
    ///   The low-level keyboard hook callback. nCode below zero must be forwarded
    ///   untouched; wParam is one of the four messages above; lParam points at a
    ///   <see cref="KBDLLHOOKSTRUCT"/>. Returning <see cref="CallNextHookEx"/>'s
    ///   result passes the keystroke on; returning non-zero swallows it.
    ///
    ///   The delegate instance must be *held* by managed code rather than merely
    ///   passed in. Otherwise the GC collects it while Windows still holds the
    ///   function pointer, and the next keystroke jumps into freed memory — presenting
    ///   as a random process crash whose location has nothing to do with the cause.
    /// </summary>
    internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 中文：
    ///   低层键盘钩子回调收到的按键数据。
    ///
    ///   注意这里**没有**任何设备标识。想知道是哪台设备按的，只能靠 Raw Input
    ///   那条通道，而那条通道拦不住按键——整个 V1 架构的难点就在这一句话里。
    /// English:
    ///   The keystroke data handed to the callback.
    ///
    ///   Note the complete absence of any device identifier. Learning which device
    ///   produced it requires the Raw Input channel, which cannot block the
    ///   keystroke — the entire difficulty of the V1 architecture sits in that
    ///   sentence.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        /// <summary>中文：虚拟键码。 English: The virtual key code.</summary>
        internal uint vkCode;

        /// <summary>中文：硬件扫描码。 English: The hardware scan code.</summary>
        internal uint scanCode;

        /// <summary>中文：标志位，见 LLKHF_*。 English: Flags; see LLKHF_*.</summary>
        internal uint flags;

        /// <summary>
        /// 中文：事件的时间戳，单位毫秒，来自 GetTickCount 的时基。
        ///       **不要用它做时序测量**：分辨率通常只有 10~16 毫秒，而我们要测的
        ///       两条通道的时差很可能远小于这个量级。测量一律用
        ///       Stopwatch.GetTimestamp，两条通道读同一个高分辨率时钟。
        /// English: The event timestamp in milliseconds, on GetTickCount's time base.
        ///          Do not use it for timing measurements: its resolution is typically
        ///          10–16 ms, while the inter-channel delta being measured is likely
        ///          far smaller. Measurement uses Stopwatch.GetTimestamp so both
        ///          channels read one high-resolution clock.
        /// </summary>
        internal uint time;

        /// <summary>
        /// 中文：附加信息。规格 §5.6 用它给本程序自己合成的事件盖标记，
        ///       从而在钩子里认出"这是我自己发的"并直接放行。
        /// English: Extra information. Spec §5.6 uses it to tag this application's own
        ///          synthesized events so the hook recognizes its own output and
        ///          passes it straight through.
        /// </summary>
        internal UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
    internal static extern IntPtr SetWindowsHookExW(
        int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(
        IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
}
