// =============================================================================
// RawInputEvent.cs
//
// 中文：
//   Raw Input 通道观测到的一次按键。
//
//   它与 <see cref="KeyEvent"/> 描述的是**同一次物理按键**，只是从另一条通道
//   看过去。两者各缺一半：
//
//     KeyEvent（钩子）        能拦截，但不知道是哪台设备按的
//     RawInputEvent           知道是哪台设备，但拦不住
//
//   关联器要做的就是把这两半拼起来，而拼的依据是 <see cref="Identity"/>
//   ——两条通道唯一一致的东西是扫描码（决策 D-11）。
//
//   ★ 设备用 long 表示，而不是原生句柄类型。
//
//     Core 不该在业务逻辑里流通操作系统的句柄类型。这里的 DeviceId 是一个
//     **不透明的会话内标识**：Core 只拿它做相等比较，从不解释它的含义，
//     由 Win32 负责把 Raw Input 的设备句柄转换过来。
//
//     必须强调"会话内"：设备句柄拔插一次就会变，因此它**绝不能被持久化**
//     成绑定依据。跨会话的身份是设备路径（规格 §6，Task 4a 第 6 节已实测
//     记录）。这里用 long 而不是 ScannerDeviceIdentity，也是这个原因——
//     关联发生在每一次按键上，逐次比较一组字符串既慢又没有必要。
//
//   ★ 本类型不带字符。
//
//     解码是 Win32 在钩子那一侧做的（决策 D-14），结果放在 KeyEvent 上。
//     Raw Input 这条通道的全部价值就是"是谁按的"，重复解码一遍既多余，
//     又会引入两条通道对同一次按键给出不同字符的可能——而那种不一致
//     排查起来极其痛苦。
//
// English:
//   One keystroke as observed on the Raw Input channel.
//
//   It describes the same physical keystroke as <see cref="KeyEvent"/>, seen from the
//   other channel, and each has half the answer: the hook can intercept but does not
//   know the device; Raw Input knows the device but cannot intercept. The correlator
//   joins the halves through Identity, the scan code being the only thing the two
//   channels agree on (decision D-11).
//
//   The device is a long rather than a native handle type. Core should not traffic in
//   operating-system handles: DeviceId is an opaque, session-scoped token that Core only
//   ever compares for equality and never interprets, with Win32 converting the Raw Input
//   handle across.
//
//   "Session-scoped" matters: a device handle changes across a replug and must never be
//   persisted as a binding. The cross-session identity is the device path (spec §6,
//   recorded empirically in Task 4a §6). That is also why this is a long rather than a
//   ScannerDeviceIdentity — correlation happens on every keystroke, and comparing a
//   group of strings each time would be both slow and pointless.
//
//   This type carries no character. Decoding happens in Win32 on the hook side (D-14)
//   and the result lives on KeyEvent. Decoding again here would be redundant and would
//   admit the possibility of the two channels disagreeing about a keystroke's character
//   — an inconsistency that is miserable to diagnose.
//
// 包含的类型 / Types in this file:
//   RawInputEvent
// =============================================================================

namespace ScannerHelper.Core.Domain;

/// <summary>
/// 中文：Raw Input 通道观测到的一次按键。不可变。
/// English: One keystroke observed on the Raw Input channel. Immutable.
/// </summary>
/// <param name="Timestamp">
/// 中文：单调时刻，取自 <see cref="Abstractions.ISystemClock.MonotonicNow"/>，
///       应在窗口过程认出 <c>WM_INPUT</c> 的第一时间取得。
/// English: A monotonic instant from
///          <see cref="Abstractions.ISystemClock.MonotonicNow"/>, taken the moment the
///          window procedure recognizes <c>WM_INPUT</c>.
/// </param>
/// <param name="ScanCode">中文：硬件扫描码。 English: The hardware scan code.</param>
/// <param name="IsExtended">
/// 中文：扫描码是否带 E0 前缀。 English: Whether the scan code carries an E0 prefix.
/// </param>
/// <param name="IsKeyUp">中文：弹起为 true。 English: True for key up.</param>
/// <param name="DeviceId">
/// 中文：产生该输入的设备的不透明标识，**仅本次会话内有效**。绝不可持久化——
///       跨会话的绑定身份是设备路径（规格 §6）。
/// English: An opaque, session-scoped identifier for the device that produced the input.
///          Never persist it; the cross-session binding identity is the device path
///          (spec §6).
/// </param>
public readonly record struct RawInputEvent(
    TimeSpan Timestamp,
    ushort ScanCode,
    bool IsExtended,
    bool IsKeyUp,
    long DeviceId)
{
    /// <summary>
    /// 中文：跨通道关联用的身份，与 <see cref="KeyEvent.Identity"/> 可直接比较。
    /// English: The correlation identity, directly comparable with
    ///          <see cref="KeyEvent.Identity"/>.
    /// </summary>
    public KeyIdentity Identity => new(ScanCode, IsExtended, IsKeyUp);
}
