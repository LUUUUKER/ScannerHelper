// =============================================================================
// ModeManager.cs
//
// 中文：
//   持有当前扫描模式（SN / SKU），并提供安全的切换。
//
//   职责被刻意限制得很窄。它不知道 WPF，不知道扫码枪，不知道热键，也不知道
//   配置文件的存在——它只回答"现在是哪个模式"，并在模式变化时发出通知。
//   谁有权触发切换（规格 §7：只有非扫码枪的物理键盘）是 HotkeyCoordinator
//   的职责，不属于这里。
//
//   两条不可协商的约束：
//     1. 启动恒为 SN（规格 §3、§7）。工人每次启动看到的都是同一个已知状态。
//     2. 绝不持久化当前模式（规格 §14）。本类因此不接受任何配置或持久化
//        依赖——不是靠约定，而是让"存起来"这件事在编译期就无从下手
//        （守卫见测试 M7）。
//
//   线程模型：假定切换调用是串行的。规格 §4.2 已确认工作场景为单工位、
//   单扫码枪、单操作员，F8 不存在并发按下的情况。因此 SetMode 中的
//   "比较后赋值"没有加锁。若将来出现并发触发路径（例如自动化测试夹具
//   或多设备支持），必须重新审视这里，而不是想当然地认为它是安全的。
//
// English:
//   Holds the current scan mode (SN / SKU) and offers a safe toggle.
//
//   Its responsibility is deliberately narrow. It knows nothing of WPF, of
//   scanners, of hotkeys, or of configuration — it only answers "which mode is
//   active" and announces changes. Deciding *who* may trigger a toggle (spec §7:
//   only a non-scanner physical keyboard) belongs to HotkeyCoordinator.
//
//   Two non-negotiable constraints:
//     1. Startup is always SN (spec §3, §7).
//     2. The current mode is never persisted (spec §14). This type therefore
//        accepts no settings or persistence dependency — enforced at compile
//        time rather than by convention (guarded by test M7).
//
//   Threading: toggle calls are assumed to be serialized. Spec §4.2 confirms a
//   single workstation, single scanner, single operator, so F8 is never pressed
//   concurrently. The compare-then-assign in SetMode is therefore unlocked. If a
//   concurrent path ever appears, revisit this rather than assuming it is safe.
//
// 包含的成员 / Members in this file:
//   CurrentMode  当前模式
//   ModeChanged  模式变更事件
//   Toggle       在 SN 与 SKU 之间切换
//   SetMode      设为指定模式；同值不触发事件
// =============================================================================

using ScannerHelper.Core.Domain;

namespace ScannerHelper.Core.Modes;

/// <summary>
/// 中文：扫描模式的唯一持有者。
/// English: The single owner of the current scan mode.
/// </summary>
public sealed class ModeManager
{
    /// <summary>
    /// 中文：当前模式的存储字段。初始值即为启动模式，必须是 SN（规格 §3）。
    ///       此处的初始化就是"启动恒为 SN"这条规则的全部实现——不读配置、
    ///       不查历史、没有任何其他来源。
    /// English: Backing field. Its initializer *is* the entire implementation of
    ///          "startup is always SN" (spec §3) — no config is read, no history
    ///          is consulted, there is no other source.
    /// </summary>
    private ScanMode _currentMode = ScanMode.Sn;

    /// <summary>
    /// 中文：当前扫描模式。
    /// English: The current scan mode.
    /// </summary>
    public ScanMode CurrentMode => _currentMode;

    /// <summary>
    /// 中文：模式发生实际变化时触发。同值重设不会触发（见 <see cref="SetMode"/>）。
    /// English: Raised when the mode actually changes. Setting the same value
    ///          raises nothing (see <see cref="SetMode"/>).
    /// </summary>
    public event EventHandler<ModeChangedEventArgs>? ModeChanged;

    /// <summary>
    /// 中文：
    ///   在 SN 与 SKU 之间切换。
    ///   输入：无。输出：无。副作用：模式改变并触发 <see cref="ModeChanged"/>。
    ///   实现：把"另一个模式"算出来后交给 <see cref="SetMode"/>，切换与通知
    ///   两条逻辑因此只有一份实现。
    ///
    ///   由于只有两个模式，切换必然改变当前值，本方法总会触发事件。这也是
    ///   测试 M4（切换两次回到原点）要锁定的性质：模式集合恒为二元，F8 永远
    ///   不会变成三态轮转，工人可以凭肌肉记忆预测按下去的结果。
    ///
    /// English:
    ///   Toggles between SN and SKU.
    ///   Implementation: computes the other mode and delegates to
    ///   <see cref="SetMode"/>, so switching and notification have one
    ///   implementation between them.
    ///
    ///   With exactly two modes a toggle always changes the value, so this
    ///   always raises the event. That two-ness is what test M4 pins down: F8
    ///   never becomes a three-state rotation the worker cannot predict.
    /// </summary>
    public void Toggle()
        => SetMode(_currentMode == ScanMode.Sn ? ScanMode.Sku : ScanMode.Sn);

    /// <summary>
    /// 中文：
    ///   将当前模式设为指定值。
    ///   输入：mode 目标模式。输出：无。
    ///   步骤：
    ///     1. 若目标模式与当前模式相同，直接返回，不触发任何事件；
    ///     2. 记下旧值；
    ///     3. 写入新值；
    ///     4. 触发 <see cref="ModeChanged"/>，携带旧值和新值。
    ///
    ///   步骤 1 不是性能优化，而是行为要求（测试 M6）：模式变更会重绘 UI
    ///   并播放提示音，若同值重设也算变更，工人会听到一声"切换成功"的提示
    ///   却发现什么都没变。
    ///
    ///   步骤 3 先于步骤 4：事件处理器在被调用时读取 <see cref="CurrentMode"/>，
    ///   拿到的必须已经是新值，否则订阅方会看到自相矛盾的状态。
    ///
    /// English:
    ///   Sets the current mode.
    ///   Steps: (1) return early if unchanged; (2) capture the previous value;
    ///   (3) store the new value; (4) raise <see cref="ModeChanged"/> with both.
    ///
    ///   Step 1 is a behavioral requirement, not an optimization (test M6): a
    ///   change repaints the UI and plays a sound, so a no-op treated as a change
    ///   would sound a "mode switched" cue when nothing switched.
    ///
    ///   Step 3 precedes step 4 so that a handler reading
    ///   <see cref="CurrentMode"/> sees the new value; otherwise subscribers
    ///   would observe a self-contradictory state.
    /// </summary>
    public void SetMode(ScanMode mode)
    {
        // 步骤 1 / Step 1
        if (mode == _currentMode)
        {
            return;
        }

        // 步骤 2 / Step 2
        var previousMode = _currentMode;

        // 步骤 3 / Step 3
        _currentMode = mode;

        // 步骤 4 / Step 4
        ModeChanged?.Invoke(this, new ModeChangedEventArgs(previousMode, mode));
    }
}
