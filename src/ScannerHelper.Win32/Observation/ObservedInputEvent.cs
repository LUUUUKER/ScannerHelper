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
/// <param name="VirtualKey">
/// 中文：虚拟键码。
///
///       ★ **两条通道对同一次按键给出的虚拟键码可能不同**，实测已证实：
///         按下左 Shift 时，钩子报 <c>VK_LSHIFT</c>（0xA0），Raw Input 报通用的
///         <c>VK_SHIFT</c>（0x10）；而两者的扫描码都是 0x2A。
///
///         因此**虚拟键码不能用来把两条通道的事件对应起来**，扫描码才行。
///         第一轮测量正是栽在这里：按虚拟键码配对，所有修饰键全部配不上，
///         报出四百多个"未配对"事件。
/// English: The virtual key code.
///
///          The two channels can report *different* virtual keys for one keystroke, as
///          measurement confirmed: pressing left Shift has the hook report VK_LSHIFT
///          (0xA0) and Raw Input report the generic VK_SHIFT (0x10), while both report
///          scan code 0x2A.
///
///          Virtual keys therefore cannot be used to match events across the channels;
///          scan codes can. The first measurement run failed exactly here, pairing by
///          virtual key and reporting four hundred-odd "unpaired" modifier events.
/// </param>
/// <param name="ScanCode">
/// 中文：硬件扫描码。两条通道对同一次按键报的是**同一个**扫描码，因此它加上
///       扩展位与按下/弹起方向，才是跨通道配对的正确依据。
/// English: The hardware scan code. Both channels report the same one for a given
///          keystroke, so it — together with the extended flag and the up/down
///          direction — is the correct basis for cross-channel pairing.
/// </param>
/// <param name="IsExtended">
/// 中文：是否为扩展键。扫描码本身会重复（例如右 Ctrl 与左 Ctrl 的扫描码相同），
///       靠这一位区分。配对时必须连它一起比，否则左右修饰键会互相配错。
/// English: Whether this is an extended key. Scan codes repeat — right Ctrl shares
///          left Ctrl's — and this bit separates them. Pairing must compare it too, or
///          left and right modifiers pair with each other.
/// </param>
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
    bool IsExtended,
    bool IsKeyUp,
    bool IsInjected,
    nint DeviceHandle)
{
    /// <summary>
    /// 中文：跨通道配对用的按键身份：扫描码 + 扩展位 + 按下/弹起方向。
    ///
    ///       ★ 刻意**不含**虚拟键码。两条通道对修饰键给出的虚拟键码不同
    ///         （钩子给 VK_LSHIFT，Raw Input 给 VK_SHIFT），把它算进身份
    ///         会让所有修饰键都配不上——第一轮测量就是这么坏掉的。
    /// English: The key identity used for cross-channel pairing: scan code, extended
    ///          flag and direction.
    ///
    ///          The virtual key is deliberately excluded. The channels disagree about it
    ///          for modifiers — the hook says VK_LSHIFT where Raw Input says VK_SHIFT —
    ///          and including it leaves every modifier unpaired, which is precisely how
    ///          the first measurement run broke.
    /// </summary>
    public (ushort ScanCode, bool IsExtended, bool IsKeyUp) PairingIdentity
        => (ScanCode, IsExtended, IsKeyUp);
}
