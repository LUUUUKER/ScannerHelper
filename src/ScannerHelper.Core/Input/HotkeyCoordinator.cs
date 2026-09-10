// =============================================================================
// HotkeyCoordinator.cs
//
// 中文：
//   全局控制热键的路由规则（规格 §7）。纯逻辑，不执行任何动作——它只回答
//   "这一下按键此刻应当被当作什么"，真正去切模式、去强制发送的是调用方。
//
//   规格 §7 的表格，逐行落在 Route 里：
//
//     | 键   | 条件                    | 行为                     |
//     |------|-------------------------|--------------------------|
//     | F8   | 非扫码枪键盘，未暂停    | 切换模式，**吞掉**       |
//     | F8   | 来自绑定的扫码枪        | 当作扫描数据，绝不是命令 |
//     | F10  | 有待决错误              | 强制发送，**吞掉**       |
//     | F10  | 无待决错误              | **透传**                 |
//     | Esc  | 有待决错误              | 取消，**吞掉**           |
//     | Esc  | 无待决错误              | **透传**                 |
//     | 任意 | 暂停中                  | **透传**                 |
//
//   ★ 判断顺序即优先级，而顺序是有意排的，不是随手写的。
//
//     暂停排在最前，压过一切。规格 §5.7 明说暂停期间不拦截任何热键，包括 F8。
//     "什么都不拦"就是这个状态的定义本身——只要还有哪怕一个键被拦，
//     PAUSED 就不再是一个纯粹的旁路，也就不再能在捕获链路坏掉时救人。
//
//     扫码枪排第二。规格 §7 规定扫码枪发出的 F8 永远是数据、绝不是命令。
//     若把它排在按键匹配之后，某个恰好含 F8 的条码就会在扫描途中把模式
//     切走——而工人正盯着货，等他发现时已经有一批数据以错误的模式发出去了。
//
//   ★ 弹起事件必须被挡掉，这是一条容易漏的规则。
//
//     一次按键产生按下与弹起两个事件。若两个都触发动作，按一下 F8 就是
//     切两次，净效果是**什么都没变**。现场表现为"F8 按了没反应"，而排查
//     方向会全跑偏——工人会去怀疑热键没注册、键盘坏了、程序没在运行，
//     没人会想到它其实切了两次。
//
//   ★ 合成事件也必须被挡掉。
//
//     本程序自己用 SendInput 发出去的内容（包括规格 §19.1 的心跳探测），
//     若能触发热键，就会出现"程序自己把自己的模式切了"这种荒唐情形。
//     规格 §5.6 要求注入的输入绝不能被自己的捕获链路重新吃进去，这一层
//     同样适用。
//
// English:
//   Routing rules for the global control hotkeys (spec §7). Pure logic that performs no
//   action — it answers only what a keystroke should count as right now, and the caller does
//   the toggling and force-sending.
//
//   Spec §7's table lands row by row inside Route: F8 from a non-scanner keyboard while not
//   paused toggles and is swallowed; F8 from the bound scanner is scanner data and never a
//   command; F10 and Esc are swallowed while an error is pending and pass through otherwise;
//   and everything passes through while paused.
//
//   The order of the checks is the priority order, and it is deliberate. Paused comes first and
//   overrides everything: spec §5.7 states that no hotkey is intercepted while paused, F8
//   included. "Nothing is intercepted" is the definition of the state — intercept even one key
//   and PAUSED stops being a pure bypass and can no longer rescue the operator when the capture
//   path is broken.
//
//   The scanner comes second. Spec §7 requires scanner-originated F8 to be data and never a
//   command. Placed after key matching, a barcode that happens to contain F8 would switch the
//   mode mid-scan — with the operator looking at goods rather than the screen, and a batch of
//   data already emitted in the wrong mode by the time they notice.
//
//   Key-up events must be rejected, an easily missed rule. Each keystroke produces a down and an
//   up, and acting on both means one F8 press toggles twice for a net effect of nothing. On site
//   that reads as "F8 does nothing", sending the investigation entirely the wrong way — the
//   operator suspects an unregistered hotkey, a broken keyboard, or the program not running, and
//   nobody suspects it toggled twice.
//
//   Synthesized events must be rejected too. If this application's own SendInput output —
//   including spec §19.1's heartbeat probe — could fire a hotkey, the program would end up
//   switching its own mode. Spec §5.6 requires injected input never to re-enter this
//   application's own pipeline, and that applies at this layer as well.
//
// 包含的成员 / Members in this file:
//   Bindings  四个控制热键的虚拟键码，设置变更时替换
//   Route     判断一次按键此刻应当被当作什么
// =============================================================================

using ScannerHelper.Core.Domain;

namespace ScannerHelper.Core.Input;

/// <summary>
/// 中文：全局控制热键的路由规则。纯逻辑，不执行动作。
/// English: Routing rules for the global control hotkeys. Pure logic; performs no action.
/// </summary>
public sealed class HotkeyCoordinator
{
    /// <summary>
    /// 中文：
    ///   构造路由器。
    ///   输入：bindings 四个热键的虚拟键码，未分配的项为 null。
    ///
    ///   全部未分配是合法配置——那意味着一个热键都不生效，所有按键照常放行。
    ///   听起来像是坏了，其实是安全的那一侧：不确定该拦什么的时候，什么都
    ///   不拦，工人的键盘至少完全正常。
    /// English:
    ///   Creates the router from the four hotkeys' virtual key codes, with null for unassigned.
    ///
    ///   All-unassigned is a legitimate configuration meaning no hotkey acts and every key
    ///   passes through. It sounds broken and is in fact the safe side: when it is unclear what
    ///   should be intercepted, intercepting nothing at least leaves the operator's keyboard
    ///   entirely normal.
    /// </summary>
    public HotkeyCoordinator(HotkeyBindings bindings) => Bindings = bindings;

    /// <summary>
    /// 中文：当前的热键绑定。设置变更时替换（规格 §13.3）。
    /// English: The current bindings, replaced when settings change (spec §13.3).
    /// </summary>
    public HotkeyBindings Bindings { get; set; }

    /// <summary>
    /// 中文：
    ///   判断一次按键此刻应当被当作什么。
    ///   输入：keyEvent 按键事件；context 外部条件。
    ///   输出：应当执行的动作。不是 <see cref="HotkeyAction.None"/> 就意味着
    ///         这个按键会被**吞掉**，不转发给业务软件（规格 §7）。
    ///   步骤：
    ///     1. 弹起事件不触发任何动作；
    ///     2. 合成事件不触发任何动作；
    ///     3. 暂停中：一律不拦（规格 §5.7）；
    ///     4. 来自绑定的扫码枪：一律是数据，绝不是命令（规格 §7）；
    ///     5. 按虚拟键码匹配四个绑定，其中 F10 与 Esc 还要看有没有待决错误。
    ///
    ///   ★ 步骤 1 防的是"按一下切两次"。一次按键产生按下与弹起两个事件，
    ///     两个都触发的话净效果是什么都没变，而现场看到的是"F8 按了没反应"
    ///     ——排查方向会全跑偏。
    ///
    ///   ★ 步骤 3 必须在步骤 5 之前。暂停期间不拦截**任何**热键（包括 F8），
    ///     这是 PAUSED 这个状态的定义本身：只要还有一个键被拦，它就不再是
    ///     纯粹的旁路，也就不再能在捕获链路坏掉时救人。
    ///
    ///   ★ 步骤 4 必须在步骤 5 之前。若排在按键匹配之后，某个恰好含 F8 的
    ///     条码会在扫描途中把模式切走——工人正盯着货，等他发现时已经有一批
    ///     数据以错误的模式发出去了。
    ///
    ///   ★ 步骤 5 里 F10 与 Esc 在**没有**待决错误时返回 None，也就是透传。
    ///     Esc 这一条规格特意解释过：它在网页里太常用，无条件吞掉会让工人
    ///     发现"网页的取消键坏了"，却完全想不到是本程序所为——一个查不到
    ///     源头的故障。
    /// English:
    ///   Decides what a keystroke should count as right now. Anything other than
    ///   <see cref="HotkeyAction.None"/> means the key is swallowed rather than forwarded
    ///   (spec §7).
    ///   Steps: (1) key-ups act on nothing; (2) synthesized events act on nothing; (3) while
    ///   paused nothing is intercepted (spec §5.7); (4) anything from the bound scanner is data,
    ///   never a command (spec §7); (5) match the virtual key against the four bindings, with
    ///   F10 and Esc additionally requiring a pending error.
    ///
    ///   Step 1 prevents one press toggling twice. Both the down and the up would fire, netting
    ///   no change, and on site that reads as "F8 does nothing" — sending the investigation
    ///   entirely the wrong way.
    ///
    ///   Step 3 must precede step 5. No hotkey at all is intercepted while paused, F8 included;
    ///   that is the definition of the state, and intercepting even one key stops PAUSED being a
    ///   pure bypass and stops it rescuing the operator when capture is broken.
    ///
    ///   Step 4 must precede step 5. Placed after key matching, a barcode containing F8 would
    ///   switch the mode mid-scan, with the operator looking at goods and a batch of data
    ///   already emitted in the wrong mode by the time they notice.
    ///
    ///   In step 5, F10 and Esc return None without a pending error, meaning pass through. The
    ///   spec explains the Esc row specifically: it is far too common in web use, and swallowing
    ///   it unconditionally would leave the operator with a "broken" cancel key and no reason to
    ///   suspect this tool — a fault whose source cannot be found.
    /// </summary>
    public HotkeyAction Route(in KeyEvent keyEvent, in HotkeyContext context)
    {
        // 步骤 1 / Step 1
        if (keyEvent.IsKeyUp)
        {
            return HotkeyAction.None;
        }

        // 步骤 2 / Step 2
        if (keyEvent.IsInjected)
        {
            return HotkeyAction.None;
        }

        // 步骤 3 / Step 3 —— 暂停压过一切（规格 §5.7）
        // Step 3 — paused overrides everything (spec §5.7)
        if (context.IsPaused)
        {
            return HotkeyAction.None;
        }

        // 步骤 4 / Step 4 —— 扫码枪发出的永远是数据（规格 §7）
        // Step 4 — anything from the scanner is data (spec §7)
        if (context.IsFromBoundScanner)
        {
            return HotkeyAction.None;
        }

        // 步骤 5 / Step 5
        var virtualKey = keyEvent.VirtualKey;

        if (Matches(Bindings.ToggleMode, virtualKey))
        {
            return HotkeyAction.ToggleMode;
        }

        if (Matches(Bindings.ForceSend, virtualKey))
        {
            return context.HasPendingError ? HotkeyAction.ForceSend : HotkeyAction.None;
        }

        if (Matches(Bindings.Cancel, virtualKey))
        {
            return context.HasPendingError ? HotkeyAction.Cancel : HotkeyAction.None;
        }

        if (Matches(Bindings.PauseResume, virtualKey))
        {
            return HotkeyAction.PauseResume;
        }

        return HotkeyAction.None;
    }

    /// <summary>
    /// 中文：某个绑定是否命中这个虚拟键码。
    ///       未分配（null）永远不命中——这一点必须显式保证，否则一个未分配的
    ///       热键会与虚拟键码 0 相等，而 0 恰好是一个合法的按键值。
    ///       规格 §13.3 规定暂停热键默认未分配，所以这条路径是常态而非边界。
    /// English: Whether a binding matches this virtual key. Unassigned never matches, which must
    ///          be guaranteed explicitly: otherwise an unassigned hotkey would equal virtual key
    ///          0, which is itself a legitimate value. Spec §13.3 leaves the pause hotkey
    ///          unassigned by default, so this path is the norm rather than an edge case.
    /// </summary>
    private static bool Matches(ushort? binding, ushort virtualKey)
        => binding is { } assigned && assigned == virtualKey;
}
