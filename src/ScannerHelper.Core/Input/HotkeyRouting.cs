// ⚠ 死代码提醒 / DEAD CODE
//
//   中文：
//     本文件自 2026-09-15 起**没有任何生产代码使用**（只有它自己的用例在跑）。
//
//     它是串口改造之前那套热键路由的遗留：那时需要判断"这个按键来自扫码枪还是
//     物理键盘"，并把 F10 / Esc 路由到强制发送与取消。这两件事现在都不存在了——
//     串口模式下扫码枪发不出按键（ARCHITECTURE_CHANGE_SERIAL.md），而强制发送
//     与取消已经取消（CHANGE_ERROR_HANDLING.md）。
//
//     留着它是为了避免在上线期做大改动，但**不要照它来做任何决定**：它描述的
//     业务行为已经作废。下一个整理周期应当连同用例一起删除。
//
//   English:
//     No production code has used this file since 2026-09-15; only its own cases exercise it.
//
//     It is a leftover of the pre-serial hotkey routing, which had to decide whether a keystroke came
//     from the scanner or a physical keyboard and route F10 / Escape to Force Send and Cancel.
//     Neither exists now: in serial mode the scanner emits no keystrokes at all
//     (ARCHITECTURE_CHANGE_SERIAL.md), and Force Send and Cancel were removed
//     (CHANGE_ERROR_HANDLING.md).
//
//     It is kept only to avoid a large change during a rollout. Do not take any decision from it —
//     the business behaviour it describes is void. It should be deleted, with its cases, in the next
//     cleanup.
//
// =============================================================================
// HotkeyRouting.cs
//
// 中文：
//   热键路由用到的三个类型：动作、绑定、上下文。
//
//   ★ 为什么只返回一个动作，不另外返回一个"要不要吞掉"的布尔。
//
//     规格 §7 把规则写死了：「当 Scanner Helper 处理一个控制热键时，它消费
//     该按键，不向业务软件转发。」也就是说——
//         动作不是 None  ⇒  一定吞掉
//         动作是 None    ⇒  一定放行
//     两者完全等价，多一个布尔就是给同一件事造了第二个事实来源，而两个
//     事实来源迟早会不一致。不一致之后的表现还特别难查：某个热键既触发了
//     动作又被转发给了网页，于是切换模式的同时顺手触发了网页里一个不相干
//     的快捷键——规格 §7 特意提到要防的正是这一条。
//
//   ★ 绑定用虚拟键码而不是字符串。
//
//     配置里存的是 "F8" 这样的名字（见 HotkeySettings），因为 Core 目标框架
//     是 net8.0，不能引用 System.Windows.Input.Key 那类 Windows 专有类型。
//     但把名字翻译成虚拟键码是平台细节，属于 Phase B 的 Win32 层。
//
//     到了本层，键已经是一个数字，Core 只做相等比较、从不解释它的含义。
//     这样 Core 既不必知道 "F8" 对应 0x77，也不必知道用户改了键盘布局之后
//     这个映射会不会变。
//
//   ★ 上下文里的三个条件，顺序和优先级都不是随手定的，见 HotkeyCoordinator。
//
// English:
//   The three types hotkey routing needs: the action, the bindings, and the context.
//
//   Why a single action is returned rather than an action plus a "should I swallow it"
//   boolean: spec §7 fixes the rule — "When Scanner Helper acts on a control hotkey, it
//   consumes the key and does not forward it." Acting therefore always implies swallowing and
//   not acting always implies passing through, making the two exactly equivalent. A separate
//   boolean would create a second source of truth for one fact, and two sources of truth
//   eventually disagree. The disagreement is also unusually hard to diagnose: a hotkey that
//   both fires its action *and* reaches the page would, say, toggle the mode while
//   simultaneously triggering an unrelated shortcut in the web application — precisely what
//   spec §7 says this rule exists to prevent.
//
//   Bindings hold virtual key codes rather than strings. Configuration stores names like "F8"
//   (see HotkeySettings) because Core targets net8.0 and cannot reference Windows-only types
//   such as System.Windows.Input.Key, but translating a name into a virtual key code is a
//   platform detail belonging to Win32 in Phase B. By this layer a key is already a number that
//   Core only compares and never interprets — so Core need not know that "F8" is 0x77, nor
//   whether that mapping shifts when the user changes keyboard layout.
//
// 包含的类型 / Types in this file:
//   HotkeyAction    路由的结论
//   HotkeyBindings  四个控制热键的虚拟键码
//   HotkeyContext   做出路由判断所需的全部外部条件
// =============================================================================

namespace ScannerHelper.Core.Input;

/// <summary>
/// 中文：热键路由的结论。
///       **不是 None 就意味着这个按键会被吞掉**（规格 §7），不需要另一个布尔。
/// English: The outcome of hotkey routing. Anything other than None means the key is
///          swallowed (spec §7); no separate boolean is needed.
/// </summary>
public enum HotkeyAction
{
    /// <summary>
    /// 中文：不是热键，或此刻不该被当作热键。按键照常放行。
    /// English: Not a hotkey, or not one right now. The key passes through normally.
    /// </summary>
    None,

    /// <summary>
    /// 中文：切换 SN / SKU 模式（规格 §7，默认 F8）。
    /// English: Toggle SN/SKU (spec §7, F8 by default).
    /// </summary>
    ToggleMode,

    /// <summary>
    /// 中文：强制发送原始码（规格 §10，默认 F10）。仅在有待决错误时才会出现。
    /// English: Force Send the raw code (spec §10, F10 by default). Only ever returned while
    ///          an error is pending.
    /// </summary>
    ForceSend,

    /// <summary>
    /// 中文：取消失败的扫描（规格 §10，默认 Esc）。仅在有待决错误时才会出现。
    /// English: Cancel a failed scan (spec §10, Esc by default). Only ever returned while an
    ///          error is pending.
    /// </summary>
    Cancel,

    /// <summary>
    /// 中文：
    ///   暂停 / 恢复接管（规格 §13.3，**默认未分配**）。
    ///
    ///   ★ 实际上它只能用来**暂停**，永远不能用来恢复。
    ///
    ///     规格 §5.7 规定：暂停期间不拦截任何热键，包括 F8。既然什么都不拦，
    ///     这个键当然也不会被拦——它会像普通按键一样直接进入业务软件。
    ///
    ///     规格明说这是"旁路整条流水线的正确且预期的后果，不是缺陷"。
    ///     恢复只能靠鼠标点界面上的按钮，而这正是规格 §5.7 硬性要求
    ///     暂停控件必须**鼠标可点**的原因之一：它要救的场景是"键盘失灵"，
    ///     一个只能用键盘脱困的逃生口，在最需要它的时候恰好按不出去。
    /// English:
    ///   Pause/Resume (spec §13.3, unassigned by default).
    ///
    ///   In practice it can only ever pause, never resume. Spec §5.7 states that no hotkey is
    ///   intercepted while paused, F8 included — nothing is being captured, so this key too
    ///   reaches the business application like any other keystroke. The spec calls that the
    ///   correct and expected consequence of bypassing the pipeline rather than a bug.
    ///
    ///   Resuming is therefore only possible through the mouse-clickable control, which is part
    ///   of why spec §5.7 insists on one: the situation it rescues is "the keyboard stopped
    ///   working", and an escape hatch reachable only from the keyboard fails exactly when it
    ///   is needed.
    /// </summary>
    PauseResume,
}

/// <summary>
/// 中文：四个控制热键的虚拟键码。null 表示该热键未分配。
/// English: Virtual key codes for the four control hotkeys; null means unassigned.
/// </summary>
/// <param name="ToggleMode">
/// 中文：切换模式，默认 F8。 English: Toggle mode, F8 by default.
/// </param>
/// <param name="ForceSend">
/// 中文：强制发送，默认 F10。 English: Force Send, F10 by default.
/// </param>
/// <param name="Cancel">
/// 中文：取消，默认 Esc。 English: Cancel, Esc by default.
/// </param>
/// <param name="PauseResume">
/// 中文：暂停 / 恢复，**默认未分配**（规格 §13.3）。
///
///       刻意不给默认值。暂停这个安全阀要救的正是"键盘失灵"的场景，
///       默认配一个热键会制造一种错觉，让人以为按键就能脱困——而在最需要
///       它的时候恰恰按不出去。
/// English: Pause/Resume, unassigned by default (spec §13.3). Deliberately so: the valve
///          exists to rescue "the keyboard stopped working", and shipping a default hotkey
///          would suggest a keystroke is the way out — exactly what fails when it is needed.
/// </param>
public readonly record struct HotkeyBindings(
    ushort? ToggleMode = null,
    ushort? ForceSend = null,
    ushort? Cancel = null,
    ushort? PauseResume = null);

/// <summary>
/// 中文：做出一次路由判断所需的全部外部条件。
/// English: Everything outside the key event that one routing decision depends on.
/// </summary>
/// <param name="IsFromBoundScanner">
/// 中文：这一下是否来自已绑定的扫码枪。
///
///       ★ 这个判断只有在关联完成之后才拿得到（Task 4a 实测：钩子 100% 先于
///         WM_INPUT 到达）。因此热键路由必然发生在关联之后，不可能在钩子
///         回调里当场完成——规格 §7 要求"扫码枪发出的 F8 绝不能切换模式"，
///         而在回调那一刻我们根本不知道是谁按的。
/// English: Whether the keystroke came from the bound scanner.
///
///          This is only knowable once correlation completes (Task 4a: the hook precedes
///          WM_INPUT 100% of the time), so hotkey routing necessarily happens after
///          correlation and cannot be done inside the hook callback. Spec §7 requires that
///          scanner-originated F8 never toggle the mode, and at callback time there is no way
///          to know who pressed it.
/// </param>
/// <param name="IsPaused">
/// 中文：是否处于暂停。暂停时不拦截任何热键（规格 §5.7）。
/// English: Whether paused. No hotkey is intercepted while paused (spec §5.7).
/// </param>
/// <param name="HasPendingError">
/// 中文：是否有一个错误在等工人决定。F10 与 Esc 只在这时才生效（规格 §7）。
/// English: Whether an error awaits the operator. F10 and Esc act only then (spec §7).
/// </param>
public readonly record struct HotkeyContext(
    bool IsFromBoundScanner,
    bool IsPaused,
    bool HasPendingError);
