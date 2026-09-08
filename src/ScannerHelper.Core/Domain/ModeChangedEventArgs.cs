// =============================================================================
// ModeChangedEventArgs.cs
//
// 中文：
//   模式变更事件的数据载体，同时携带变更前和变更后的模式。
//
//   为什么必须带旧值、而不是只给新值：
//     - 规格 §7 要求 SN→SKU 与 SKU→SN 播放可区分的提示音，只有新值无法
//       区分这两个方向；
//     - UI 的过渡效果需要知道从哪来、到哪去；
//     - 诊断日志记录模式变更时，"从什么变成什么"才是有价值的信息。
//
// English:
//   Payload for the mode-change event, carrying both the previous and the new
//   mode.
//
//   Why the previous value is required rather than the new one alone:
//     - Spec §7 requires SN→SKU and SKU→SN to be audibly distinguishable, which
//       the new value alone cannot express;
//     - the UI transition needs to know the direction;
//     - a diagnostic log entry is only useful if it records what changed to what.
//
// 包含的类型 / Types in this file:
//   ModeChangedEventArgs
// =============================================================================

namespace ScannerHelper.Core.Domain;

/// <summary>
/// 中文：模式变更事件参数。仅在模式确实发生变化时才会被构造并发出
///       （同值重设不触发事件，见测试 M6）。
/// English: Mode-change event payload. Only constructed and raised when the mode
///          actually changed; setting the same value raises nothing (test M6).
/// </summary>
public sealed class ModeChangedEventArgs : EventArgs
{
    /// <summary>
    /// 中文：
    ///   构造模式变更事件参数。
    ///   输入：previousMode 变更前的模式；currentMode 变更后的模式。
    ///   输出：事件参数实例。
    /// English:
    ///   Creates the payload.
    ///   Input: the mode before and after the change. Output: the payload.
    /// </summary>
    public ModeChangedEventArgs(ScanMode previousMode, ScanMode currentMode)
    {
        PreviousMode = previousMode;
        CurrentMode = currentMode;
    }

    /// <summary>
    /// 中文：变更前的模式。
    /// English: The mode before the change.
    /// </summary>
    public ScanMode PreviousMode { get; }

    /// <summary>
    /// 中文：变更后的模式，与 <see cref="Modes.ModeManager.CurrentMode"/> 一致。
    /// English: The mode after the change; matches
    ///          <see cref="Modes.ModeManager.CurrentMode"/>.
    /// </summary>
    public ScanMode CurrentMode { get; }
}
