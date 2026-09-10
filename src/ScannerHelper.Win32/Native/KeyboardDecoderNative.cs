// =============================================================================
// KeyboardDecoderNative.cs
//
// 中文：
//   把扫描码翻译成字符所需的原生声明（决策 D-14、规格 §22.3）。
//
//   这是 Core 与 Win32 分工的落点：Core 收到的是**已经解码好的字符**，
//   而解码需要当前键盘布局、CapsLock 状态与输入法状态——全是平台状态，
//   放进 Core 会毁掉整个 Phase A 赖以存在的分层边界。
//
//   ★ 必须用**前台窗口**的键盘布局，不是本进程的。
//
//     扫码枪发出的是扫描码，Windows 用**接收方**的键盘布局把它翻译成字符。
//     业务软件是那个接收方。若拿本进程的布局去解码，在两者布局不同的机器上
//     （例如工人给业务软件切了输入法而本程序没切），解出来的字符与业务软件
//     实际会收到的不是一回事——SN 模式下我们发出去的就是一个被改写过的码。
//
//     规格 §22.3 已经点明输入侧仍然受布局影响，这就是那句话的具体落点。
//
//   ★ ToUnicodeEx 会修改内核的键盘状态，必须用 wFlags 的第 2 位关掉。
//
//     不关的话，一次解码会把死键（dead key）状态写进内核，从而影响工人
//     **接下来真正按下的那个键**。表现是"打字时偶尔多出一个音调符号"，
//     而且只在特定布局下出现——极难联想到是我们在解码时留下的。
//
//     第 2 位（0x4）自 Windows 10 1607 起提供，而规格 §2 限定只支持
//     Windows 10/11，因此可以放心使用。
//
// English:
//   Native declarations for translating scan codes into characters (decision D-14, spec §22.3).
//
//   This is where the Core/Win32 split lands: Core receives characters already decoded, and
//   decoding needs the active keyboard layout, CapsLock state and IME state — all platform state,
//   which inside Core would collapse the layering boundary all of Phase A rests on.
//
//   The layout must be the *foreground window's*, not this process's. A scanner emits scan codes
//   and Windows translates them using the receiving application's layout, and the business
//   application is that receiver. Decoding with this process's layout would, on a machine where
//   the two differ — the operator having switched input method for the business application while
//   this program did not — produce characters other than what the business application would
//   actually receive, so SN mode would emit a rewritten code. Spec §22.3 already notes that the
//   input side remains layout-sensitive; this is where that sentence lands.
//
//   ToUnicodeEx modifies the kernel's keyboard state and that must be turned off through bit 2 of
//   wFlags. Left on, one decode writes dead-key state into the kernel and affects the *next key
//   the operator actually presses*, presenting as "an accent character occasionally appears while
//   typing" on certain layouts only — and almost impossible to connect to our decoding. Bit 2
//   (0x4) is available from Windows 10 version 1607, and spec §2 limits support to Windows 10/11,
//   so it can be relied on.
//
// 包含的类型 / Types in this file:
//   KeyboardDecoderNative
// =============================================================================

using System.Runtime.InteropServices;
using System.Text;

namespace ScannerHelper.Win32.Native;

/// <summary>
/// 中文：扫描码解码所需的原生声明。
/// English: Native declarations for scan-code decoding.
/// </summary>
internal static class KeyboardDecoderNative
{
    /// <summary>
    /// 中文：
    ///   ToUnicodeEx 的标志位 2：**不修改内核的键盘状态**。
    ///
    ///   不带它的话，解码留下的死键状态会影响工人接下来真正按下的那个键。
    ///   自 Windows 10 1607 起提供；规格 §2 限定 Windows 10/11，可以放心使用。
    /// English:
    ///   ToUnicodeEx flag bit 2: do not modify the kernel keyboard state.
    ///
    ///   Without it, dead-key state left behind by decoding affects the next key the operator
    ///   actually presses. Available from Windows 10 version 1607; spec §2 limits support to
    ///   Windows 10/11, so it can be relied on.
    /// </summary>
    internal const uint TOUNICODE_DO_NOT_CHANGE_KERNEL_STATE = 0x4;

    /// <summary>中文：CapsLock 的虚拟键码。 English: CapsLock's virtual key code.</summary>
    internal const int VK_CAPITAL = 0x14;

    /// <summary>
    /// 中文：把扫描码与按键状态翻译成字符。
    ///       返回值：-1 死键，0 无对应字符，正数为写入缓冲区的字符个数。
    /// English: Translates a scan code and key state into characters. Returns -1 for a dead key,
    ///          0 for no translation, or the number of characters written.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int ToUnicodeEx(
        uint wVirtKey,
        uint wScanCode,
        byte[] lpKeyState,
        StringBuilder pwszBuff,
        int cchBuff,
        uint wFlags,
        IntPtr dwhkl);

    /// <summary>
    /// 中文：取指定线程的键盘布局。传 0 表示当前线程。
    /// English: Returns a thread's keyboard layout; zero means the current thread.
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetKeyboardLayout(uint idThread);

    /// <summary>
    /// 中文：取当前前台窗口。业务软件通常就是它。
    /// English: Returns the current foreground window, normally the business application.
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// 中文：取拥有指定窗口的线程 id。
    /// English: Returns the id of the thread owning the given window.
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    /// <summary>
    /// 中文：取某个虚拟键的状态。最低位表示切换态（CapsLock 用得到）。
    /// English: Returns a virtual key's state; the low bit is the toggle state, which CapsLock
    ///          needs.
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern short GetKeyState(int nVirtKey);
}
