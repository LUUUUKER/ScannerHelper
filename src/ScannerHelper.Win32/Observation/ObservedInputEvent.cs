// =============================================================================
// ObservedInputEvent.cs
//
// 中文：
//   两条输入通道中任意一条上观测到的一个按键事件（Task 4a）。
//
//   同一次物理按键会产生**两个**本类型的实例：一个来自低层钩子，一个来自
//   Raw Input。4a 的全部工作就是把它们配对，然后回答"哪一个先到、差多少"。
//
//   ★ 必须是 struct，而且字段全部是值类型。
//
//     低层钩子回调里绝不能有内存分配（规格 §19）。用 class 的话每个事件都要
//     new 一次，扫一枪就是几十次分配，几百枪之后触发一次 GC——而 GC 暂停
//     恰好可能让回调超过 LowLevelHooksTimeout，于是 Windows 悄悄把钩子摘掉。
//     换句话说，在这里用引用类型，会亲手制造出规格 §19.1 要检测的那个故障。
//
//     值类型配合预分配的环形缓冲区，回调路径上的分配次数是零。
//
//   时间戳用 Stopwatch.GetTimestamp 而不是 DateTime，也不是钩子结构体自带的
//   time 字段：后者来自 GetTickCount 时基，分辨率只有 10~16 毫秒，而我们要测的
//   两条通道时差很可能远小于此。两条通道读同一个高分辨率时钟，差值才有意义。
//
// English:
//   One key event as observed on either of the two input channels (Task 4a).
//
//   A single physical keypress produces *two* of these — one from the low-level hook,
//   one from Raw Input. All of 4a consists of pairing them up and answering which
//   arrived first and by how much.
//
//   It must be a struct with value-typed fields only. The hook callback must not
//   allocate (spec §19): a class would mean a new object per event, dozens per scan,
//   and a GC after a few hundred scans — and a GC pause is precisely what can push the
//   callback past LowLevelHooksTimeout, at which point Windows silently removes the
//   hook. Using a reference type here would manufacture the very failure spec §19.1
//   exists to detect. A value type in a preallocated ring buffer allocates nothing on
//   the callback path.
//
//   Timestamps come from Stopwatch.GetTimestamp rather than DateTime or the hook
//   struct's own time field: the latter sits on GetTickCount's 10–16 ms base, while
//   the delta being measured is likely far smaller. Both channels read one
//   high-resolution clock, which is what makes the difference meaningful.
//
// 包含的类型 / Types in this file:
//   InputChannel        事件来自哪条通道
//   ObservedInputEvent  一次观测
// =============================================================================

namespace ScannerHelper.Win32.Observation;

/// <summary>
/// 中文：事件来自哪条通道。
/// English: Which channel an event came from.
/// </summary>
public enum InputChannel
{
    /// <summary>
    /// 中文：低层键盘钩子。同步回调，能拦截，但不知道设备身份。
    /// English: The low-level keyboard hook. A synchronous callback that can
    ///          intercept but does not know the device.
    /// </summary>
    Hook,

    /// <summary>
    /// 中文：Raw Input 的 WM_INPUT 消息。知道设备身份，但拦不住。
    /// English: Raw Input's WM_INPUT message. Knows the device but cannot intercept.
    /// </summary>
    RawInput,
}

/// <summary>
/// 中文：一次按键观测。不可变值类型。
/// English: One observed keystroke. An immutable value type.
/// </summary>
/// <param name="Channel">中文：来自哪条通道。 English: Which channel.</param>
/// <param name="Timestamp">
/// 中文：Stopwatch.GetTimestamp 读数。两条通道共用同一个时钟，因此可以直接相减。
/// English: A Stopwatch.GetTimestamp reading. Both channels share one clock, so
///          readings may be subtracted directly.
/// </param>
/// <param name="VirtualKey">中文：虚拟键码。 English: The virtual key code.</param>
/// <param name="ScanCode">中文：硬件扫描码。 English: The hardware scan code.</param>
/// <param name="IsKeyUp">中文：true 为弹起，false 为按下。 English: True for key up.</param>
/// <param name="IsInjected">
/// 中文：是否为程序合成的事件。只有钩子通道能判断——Raw Input 看到的是真实
///       硬件输入，合成事件根本不经过它。这本身就是一条值得在 4a 里确认的事实。
/// English: Whether the event was synthesized by software. Only the hook channel can
///          tell — Raw Input reports genuine hardware input and synthesized events do
///          not pass through it at all. That is itself a fact worth confirming in 4a.
/// </param>
/// <param name="DeviceHandle">
/// 中文：产生该输入的物理设备句柄。**仅 Raw Input 通道有值**，钩子通道恒为 0。
///       这个"恒为 0"不是实现偷懒，而是整个 4a 要研究的问题本身。
/// English: The physical device that produced the input. Populated on the Raw Input
///          channel only; always zero on the hook channel. That zero is not an
///          implementation shortcut — it is the very problem 4a exists to study.
/// </param>
public readonly record struct ObservedInputEvent(
    InputChannel Channel,
    long Timestamp,
    ushort VirtualKey,
    ushort ScanCode,
    bool IsKeyUp,
    bool IsInjected,
    nint DeviceHandle);
