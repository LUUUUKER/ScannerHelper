// =============================================================================
// StopwatchSystemClock.cs
//
// 中文：
//   ISystemClock 的生产实现，也是**唯一**定义本产品单调时间基准的地方。
//
//   ★ 它为什么必须和钩子回调里那个 Stopwatch.GetTimestamp() 是同一个基准。
//
//     Core 的关联器把两件事放在一起比较：
//       - KeyEvent.Timestamp / RawInputEvent.Timestamp —— 由 Win32 层在钩子
//         回调和窗口过程里取得，用的是 Stopwatch.GetTimestamp()；
//       - ISystemClock.MonotonicNow —— 关联器自己在 Advance() 里读，用来判断
//         某个被扣留的事件是不是等超时了。
//
//     两者若不同源，比较出来的差值就毫无意义。举一个具体的坏法：时间戳用
//     Stopwatch（开机以来的计数），而 MonotonicNow 用 Environment.TickCount64
//     （同样是开机以来，但起点与分辨率都不同）。两个数都"单调"、都"合理"，
//     相减却是一个与真实间隔无关的常数偏移——偏大则每一个被扣留的按键立刻
//     判超时、全部走重放，扫码枪的字符原封不动漏进业务软件；偏小则永远不
//     超时，被扣留的按键再也放不出来，笔记本键盘当场失灵（规格假设 A4：
//     现场没有备用键盘可插）。
//
//     两种结果都很严重，而且都不会有任何异常或日志——数字看起来完全正常。
//     所以这个转换只在这一个文件里写一次，Win32 层的时间戳一律经
//     ToMonotonic 换算，谁也不再自己乘一遍系数。
//
//   ★ MonotonicNow 与 UtcNow 的返回类型刻意不同（TimeSpan / DateTimeOffset），
//     见 ISystemClock：类型不同，就不可能把两者混着用。
//
// English:
//   The production ISystemClock, and the single place defining this product's monotonic
//   time base.
//
//   It must share a base with the Stopwatch.GetTimestamp() reading taken in the hook
//   callback, because Core's correlator compares the two directly: KeyEvent.Timestamp and
//   RawInputEvent.Timestamp come from the Win32 layer's Stopwatch readings, while
//   ISystemClock.MonotonicNow is what the correlator reads in Advance() to decide whether a
//   withheld event has waited too long.
//
//   From different sources the difference between them is meaningless. A concrete way to get
//   it wrong: timestamps from Stopwatch, MonotonicNow from Environment.TickCount64 — both
//   monotonic, both plausible, but their difference is a constant offset unrelated to any real
//   interval. Too large and every withheld keystroke expires at once and is replayed, letting
//   scanner characters through into the business application untouched; too small and nothing
//   ever expires, withheld keystrokes are never released, and the laptop's keyboard stops
//   working then and there (assumption A4: no spare keyboard on site).
//
//   Both are severe and neither raises an exception or writes a log — the numbers look
//   entirely normal. So the conversion is written once, here, and every Win32-layer timestamp
//   goes through ToMonotonic rather than each site applying the scale factor itself.
//
//   MonotonicNow and UtcNow deliberately return different types (TimeSpan versus
//   DateTimeOffset); see ISystemClock — differing types make mixing them impossible.
//
// 包含的成员 / Members in this file:
//   Instance      共享实例
//   MonotonicNow  单调时刻
//   UtcNow        挂钟时刻
//   ToMonotonic   把 Stopwatch.GetTimestamp() 的读数换算成同一基准的 TimeSpan
// =============================================================================

using System.Diagnostics;
using ScannerHelper.Core.Abstractions;

namespace ScannerHelper.Win32;

/// <summary>
/// 中文：以 <see cref="Stopwatch"/> 为基准的系统时钟。
/// English: The system clock, based on <see cref="Stopwatch"/>.
/// </summary>
public sealed class StopwatchSystemClock : ISystemClock
{
    /// <summary>
    /// 中文：一个 Stopwatch 计数换算成多少个 TimeSpan 刻度。
    ///       在绝大多数 Windows 机器上 <see cref="Stopwatch.Frequency"/> 正好是
    ///       一千万，此时该系数为 1。
    /// English: How many TimeSpan ticks one Stopwatch count is worth. On most Windows
    ///          machines <see cref="Stopwatch.Frequency"/> is exactly ten million and the
    ///          factor is 1.
    /// </summary>
    private static readonly double TicksPerCount =
        (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;

    /// <summary>
    /// 中文：共享实例。本类没有任何状态，也就没有理由到处 new。
    /// English: The shared instance. The class holds no state, so there is no reason to
    ///          create more than one.
    /// </summary>
    public static readonly StopwatchSystemClock Instance = new();

    /// <inheritdoc />
    public TimeSpan MonotonicNow => ToMonotonic(Stopwatch.GetTimestamp());

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <summary>
    /// 中文：
    ///   把一次 <see cref="Stopwatch.GetTimestamp"/> 的读数换算成与
    ///   <see cref="MonotonicNow"/> 同一基准的 TimeSpan。
    ///   输入：stopwatchTimestamp 计数读数。输出：对应的单调时刻。
    ///
    ///   钩子回调与窗口过程都必须先取原始计数（那是它们能做的最快的事），
    ///   再由本方法换算——而不是在那两处直接读 <see cref="MonotonicNow"/>。
    ///   属性访问要多绕一层，而 Task 4a 测出的两条通道时差最小只有 110 微秒，
    ///   多绕的那一层就直接掺进了测量对象里。
    /// English:
    ///   Converts one <see cref="Stopwatch.GetTimestamp"/> reading into a TimeSpan on the
    ///   same base as <see cref="MonotonicNow"/>.
    ///
    ///   The hook callback and the window procedure both take the raw count first — the
    ///   fastest thing they can do — and convert here, rather than reading
    ///   <see cref="MonotonicNow"/> directly at those sites. The property access costs an
    ///   extra indirection, and Task 4a measured the inter-channel delta as low as 110 µs,
    ///   so that indirection lands straight inside what is being measured.
    /// </summary>
    public static TimeSpan ToMonotonic(long stopwatchTimestamp)
        => TimeSpan.FromTicks((long)(stopwatchTimestamp * TicksPerCount));
}
