// =============================================================================
// HotkeyNative.cs
//
// 中文：
//   全局热键的原生声明。
//
//   ★ 为什么用 RegisterHotKey，而不是低层键盘钩子。
//
//     旧架构里我们已经装着一个 WH_KEYBOARD_LL，热键顺手在里面判断就行。那个钩子
//     随架构变更退役之后（TASK_4B_FINDING_20260910.md），为了三个热键再装一个
//     回来是很坏的交易：钩子回调有 LowLevelHooksTimeout 这条隐形的时限，超时一次
//     Windows 就悄悄摘掉它、不通知任何人（规格 §19.1）。那是这个项目吃过大亏的
//     地方，不该为了 F8 再请回来一次。
//
//     RegisterHotKey 没有那一整类风险：它不在按键路径上跑我们的代码，只是在键被
//     按下时投递一条 WM_HOTKEY。
//
//   ★ 它还顺便解决了规格 §7 的「放行表」。
//
//     §7 要求：F10 与 Esc 只在有待决错误时才被吞掉，没有错误时必须原样放行——
//     "Esc 在日常网页操作里太常用，不能无条件吞掉"。用钩子实现的话，这是回调里
//     的一个判断，判断写错就会把工人的 Esc 吃掉。
//
//     用 RegisterHotKey 则是：**没有待决错误时根本不注册那两个键**。于是"放行"
//     不是一个判断的结果，而是我们压根没去抢它。同理，暂停期间把全部热键注销，
//     §7 最后一行"暂停时一律放行"就自动成立。
//
//   ★ 注册失败是正常情形，必须能被调用方看见。
//
//     别的程序先注册了 F8，我们就拿不到——返回 false，不抛异常。而界面上那句
//     "F8 切换模式"在那种情况下是一句假话，必须换掉：规格 §19.1 的原则是界面
//     绝不显示一个它没有验证过的东西，那既包括运行状态，也包括一句操作提示。
//
// English:
//   Native declarations for global hotkeys.
//
//   RegisterHotKey rather than a low-level keyboard hook: the old architecture already installed a
//   WH_KEYBOARD_LL and could judge hotkeys inside it, but that hook retired with the architecture
//   (TASK_4B_FINDING_20260910.md) and bringing one back for three keys would be a poor trade. A
//   hook callback carries the invisible LowLevelHooksTimeout deadline, and exceeding it once has
//   Windows silently remove the hook without telling anyone (spec §19.1) — the very thing this
//   project lost days to. RegisterHotKey has none of that: it runs no code of ours on the keystroke
//   path and merely posts a WM_HOTKEY.
//
//   It also settles spec §7's pass-through table for free. §7 requires F10 and Esc to be swallowed
//   only while an error is pending and passed through otherwise — "Esc is far too common in
//   ordinary web use to swallow unconditionally". Implemented in a hook that is a decision in the
//   callback, and a wrong decision eats the operator's Esc. With RegisterHotKey the two keys are
//   simply not registered while nothing is pending: passing through is not the outcome of a
//   judgment but the absence of any attempt to take the key. Likewise, unregistering everything
//   while paused makes §7's last row — pass everything through while PAUSED — hold by itself.
//
//   Failing to register is a normal situation and must be visible to the caller: another program
//   may hold F8 already, which returns false rather than throwing. The UI's "F8 switches mode" hint
//   is then a false statement and must change — spec §19.1's principle that the UI never presents
//   something it has not verified covers instructions as much as operational state.
//
// 包含的类型 / Types in this file:
//   HotkeyNative
// =============================================================================

using System.Runtime.InteropServices;

namespace ScannerHelper.Win32.Native;

/// <summary>
/// 中文：全局热键的原生声明。
/// English: Native declarations for global hotkeys.
/// </summary>
internal static class HotkeyNative
{
    /// <summary>
    /// 中文：热键被按下时投递的消息号。wParam 是注册时给的 id。
    /// English: The message posted when a hotkey is pressed; wParam carries the id used to
    ///          register it.
    /// </summary>
    internal const uint WM_HOTKEY = 0x0312;

    /// <summary>
    /// 中文：
    ///   不带任何修饰键。
    ///
    ///   ★ F8 / F10 / Esc 都是**裸键**，这是规格 §7 定的。裸键注册是允许的，
    ///     但它会把这个键从**整个系统**里拿走——所以只在真正需要的那段时间里注册，
    ///     这一点见文件头。
    /// English:
    ///   No modifier keys. F8, F10 and Escape are bare keys per spec §7. Registering a bare key is
    ///   allowed but takes that key away from the entire system, which is why registration is
    ///   confined to the moments it is genuinely needed; see the file header.
    /// </summary>
    internal const uint MOD_NONE = 0x0000;

    /// <summary>
    /// 中文：
    ///   不让按住不放产生重复的 WM_HOTKEY。
    ///
    ///   ★ 不加这个标志，工人手指在 F8 上多停半秒，模式就会来回翻十几次，
    ///     最后停在哪一边全凭运气——而模式错了，接下来几十枪都会以错误的形式
    ///     进仓库系统。
    /// English:
    ///   Suppresses repeated WM_HOTKEY while the key is held.
    ///
    ///   Without it, resting a finger on F8 for half a second flips the mode a dozen times and
    ///   where it lands is luck — and a wrong mode sends the next few dozen scans into the
    ///   warehouse system in the wrong shape.
    /// </summary>
    internal const uint MOD_NOREPEAT = 0x4000;

    /// <summary>
    /// 中文：
    ///   修饰键。只有模式切换键可以带修饰键（见 GlobalHotkeyListener），
    ///   因为它是常驻注册的那一个，也就是现场撞上别的软件的那一个——
    ///   带上 Ctrl+Alt 之后撞车概率几乎归零。
    ///
    ///   ★ 修饰键自己不能当那个"键"。RegisterHotKey 要的是「修饰键 + 一个键」，
    ///     所以"单独用 Alt 切换模式"这个想法在 Windows 上根本不成立。
    /// English:
    ///   Modifiers. Only the mode toggle may carry them (see GlobalHotkeyListener), because it is
    ///   the one held permanently and therefore the one that clashed on site; with Ctrl+Alt in front
    ///   of it the chance of a clash is essentially zero.
    ///
    ///   A modifier cannot itself be the key: RegisterHotKey takes modifiers plus a key, so "use
    ///   Alt alone to switch modes" is not something Windows can express.
    /// </summary>
    internal const uint MOD_ALT = 0x0001;

    /// <inheritdoc cref="MOD_ALT" />
    internal const uint MOD_CONTROL = 0x0002;

    /// <inheritdoc cref="MOD_ALT" />
    internal const uint MOD_SHIFT = 0x0004;

    /// <summary>
    /// 中文：
    ///   注册一个全局热键。
    ///   输出：成功返回 true。失败最常见的原因是**别的程序已经注册了同一个键**，
    ///         那不是异常情形，因此这里返回 false 而不抛。
    /// English:
    ///   Registers a global hotkey, returning false on failure. The usual cause is another program
    ///   already holding the key, which is not exceptional — hence a bool rather than a throw.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(
        IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    /// <summary>
    /// 中文：注销一个全局热键。注销之后这个键立刻回到别的程序手里。
    /// English: Unregisters a global hotkey; the key returns to other programs immediately.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr windowHandle, int id);
}
