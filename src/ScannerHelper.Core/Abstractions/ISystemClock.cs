// =============================================================================
// ISystemClock.cs
//
// 中文：
//   Core 读取时间的唯一入口（规格 §17）。
//
//   Core 中**任何**超时都必须经过本接口，绝不直接读 DateTime.UtcNow、
//   Stopwatch 或 Environment.TickCount。理由是最高风险的两个组件都靠时间
//   判断。（历史注记：这两句原先举的例子是 ScanSession 的扫描超时与
//   InputEventCorrelator
//   的关联窗口。直接读系统时钟意味着这两处只能用 Thread.Sleep 来测——
//   慢、不稳定，而且永远测不到"刚好卡在边界上"那一档。注入时间源之后，
//   测试可以精确地把时间推到 299 毫秒和 301 毫秒各跑一次。
//
//   ★ 本接口刻意提供**两种**时间，且返回类型不同，不能互相混用。
//
//     MonotonicNow  单调时刻，只增不减，原点无意义。**所有超时与时间差都用它。**
//     UtcNow        挂钟时间，只用于日志时间戳。
//
//     为什么必须分开：挂钟时间会跳。NTP 校时、管理员改系统时间，都可能让
//     UtcNow 向前或向后跃迁。若用它算超时，一次向后跳会让"上一个字符的
//     时间戳"看起来位于未来，超时判断随即失效；一次向前跳则会把一次正常的
//     扫描直接判成超时并丢弃。这类故障在仓库现场表现为"偶尔莫名其妙丢一枪"，
//     而且完全无法复现——正是最难查的那一种。
//
//     两者返回类型不同（TimeSpan 与 DateTimeOffset），因此把它们相减是编译
//     错误。这条区分不靠人记住，靠类型系统挡住。
//
//   实现放在 ScannerHelper.Win32（规格 §16 的目录结构），Core 只定义契约。
//   测试注入自己的替身，把时间当作普通输入来控制。
//
// English:
//   Core's only entry point for reading time (spec §17).
//
//   Every timeout in Core must go through this interface and never read
//   DateTime.UtcNow, Stopwatch or Environment.TickCount directly. The two
//   highest-risk components both turn on time: ScanSession's ~300 ms scan
//   inactivity timeout and InputEventCorrelator's correlation window — both retired with the
//   old architecture on 2026-09-10; see ARCHITECTURE_CHANGE_SERIAL.md. Reading the
//   system clock directly would leave both testable only via Thread.Sleep — slow,
//   flaky, and never able to hit the boundary exactly. With time injected, a test
//   can run the same case at 299 ms and at 301 ms deterministically.
//
//   Two kinds of time are offered deliberately, with different return types so they
//   cannot be mixed:
//
//     MonotonicNow  monotonic, never decreasing, with a meaningless origin. Every
//                   timeout and every time difference uses this.
//     UtcNow        wall-clock time, for log timestamps only.
//
//   Why they must be separate: wall-clock time jumps. An NTP correction or an
//   administrator changing the system time can move UtcNow forwards or backwards.
//   Computing a timeout from it means a backward jump puts "the previous
//   character's timestamp" in the future and the timeout check stops working, while
//   a forward jump condemns a perfectly normal scan as timed out and discards it.
//   On the warehouse floor that presents as "we occasionally lose a scan for no
//   reason", and it is completely irreproducible — the worst kind to chase.
//
//   Because the return types differ (TimeSpan versus DateTimeOffset), subtracting
//   one from the other does not compile. The distinction is enforced by the type
//   system rather than by remembering it.
//
//   The implementation lives in ScannerHelper.Win32 (spec §16's folder structure);
//   Core defines only the contract. Tests inject their own substitute and drive time
//   as an ordinary input.
//
// 包含的类型 / Types in this file:
//   ISystemClock
// =============================================================================

namespace ScannerHelper.Core.Abstractions;

/// <summary>
/// 中文：注入式时间源。Core 中的每一处超时都经由它读取时间。
/// English: The injected time source. Every timeout in Core reads time through it.
/// </summary>
public interface ISystemClock
{
    /// <summary>
    /// 中文：
    ///   当前的单调时刻。只增不减，原点没有意义——单独一个取值不代表任何
    ///   具体时间，只有**两次读数之差**才有意义。
    ///
    ///   全部超时判断都用它：扫描无活动超时、Raw Input 与 hook 的关联窗口。
    ///   它不受 NTP 校时和人工改表的影响，因此不会出现时间倒流导致超时判断
    ///   失效、或时间跳跃导致正常扫描被误判超时的情况。
    /// English:
    ///   The current monotonic instant. Never decreases, and its origin is
    ///   meaningless — a single reading denotes no particular time; only the
    ///   difference between two readings does.
    ///
    ///   Every timeout uses this: scan inactivity, and the Raw Input/hook
    ///   correlation window. It is immune to NTP corrections and manual clock
    ///   changes, so time can neither run backwards and break a timeout check nor
    ///   jump forwards and condemn a healthy scan.
    /// </summary>
    TimeSpan MonotonicNow { get; }

    /// <summary>
    /// 中文：
    ///   当前的世界协调时。**只用于日志时间戳**（规格 §15 要求诊断事件带上
    ///   时间戳）。
    ///
    ///   绝不要用它计算超时或两个事件的间隔——理由见文件头。
    /// English:
    ///   The current UTC wall-clock time. For log timestamps only (spec §15 requires
    ///   diagnostic events to carry one).
    ///
    ///   Never use it to compute a timeout or the interval between two events; see
    ///   the file header for why.
    /// </summary>
    DateTimeOffset UtcNow { get; }
}
