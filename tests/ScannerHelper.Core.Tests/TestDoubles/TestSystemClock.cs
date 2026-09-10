// =============================================================================
// TestSystemClock.cs
//
// 中文：
//   可以由测试任意推进的时间源。
//
//   ★ 它存在的意义：让超时行为**确定地**可测，而不是靠 Thread.Sleep 去赌。
//
//     用 Thread.Sleep 测超时有三个问题，每一个都足以让测试变成负担：
//       慢     每测一次超时就要真的等那么久。50 毫秒的关联窗口测十个分支
//              就是半秒，攒到几十个用例整套测试就没法频繁跑了。
//       不稳   Sleep 只保证"至少睡这么久"。机器一忙就可能睡过头，
//              测"没到超时"的用例随机变红。而随机变红的测试很快就会被
//              加上重试、被忽略、最后被删掉。
//       测不到 边界根本测不了。"恰好差一个刻度"这种情形，Sleep 无法表达。
//
//     注入时间之后，这三件事一次解决：时间成了普通输入，想让它是多少就是多少。
//
//   ★ 单调时刻与挂钟时间分开推进，刻意不联动。
//
//     生产实现里两者本来就可能各走各的：NTP 校时会让挂钟跳变，单调时刻不受
//     影响。测试里保持这个可能性，才能把"用错时间源"这类缺陷暴露出来——
//     若两者在假时钟里被绑成一体，一段错用挂钟算超时的代码在测试里会表现得
//     完全正常，到现场才以"偶尔莫名其妙丢一枪"的形式发作。
//
// English:
//   A time source tests advance at will.
//
//   Its purpose is to make timeout behavior deterministically testable instead of gambling
//   with Thread.Sleep, which fails in three ways, each enough to make a test suite a
//   burden. It is slow: every timeout test really waits, and ten branches of a 50 ms
//   window is half a second, which stops the suite from being run often. It is flaky:
//   Sleep guarantees only "at least this long", so a busy machine oversleeps and the
//   "not yet timed out" cases go red at random — and randomly red tests acquire retries,
//   then get ignored, then get deleted. And it cannot reach the boundary at all: "exactly
//   one tick short" is not expressible with Sleep.
//
//   Injecting time solves all three at once by making time an ordinary input.
//
//   The monotonic instant and the wall clock advance separately and deliberately do not
//   move together. In the real implementation they genuinely can diverge — an NTP
//   correction jumps the wall clock while the monotonic instant is unaffected — and
//   preserving that possibility is what exposes code that reads the wrong one. Tied
//   together in the fake, a timeout mistakenly computed from the wall clock would behave
//   perfectly in tests and only show up on site as "we occasionally lose a scan for no
//   reason".
//
// 包含的类型 / Types in this file:
//   TestSystemClock
// =============================================================================

using ScannerHelper.Core.Abstractions;

namespace ScannerHelper.Core.Tests.TestDoubles;

/// <summary>
/// 中文：测试用时间源。时间只在测试显式推进时才前进。
/// English: A test time source. Time advances only when a test says so.
/// </summary>
public sealed class TestSystemClock : ISystemClock
{
    /// <inheritdoc />
    public TimeSpan MonotonicNow { get; private set; } = TimeSpan.Zero;

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; private set; }
        = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// 中文：把单调时刻向前推进。挂钟**不动**——理由见文件头。
    /// English: Advances the monotonic instant. The wall clock does not move; see the
    ///          file header.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 中文：推进量为负。单调时刻按定义只增不减，倒退会掩盖"用错时间源"这类缺陷。
    /// English: A negative advance. The monotonic instant never decreases by definition,
    ///          and allowing it to would mask exactly the defects this type exists to expose.
    /// </exception>
    public void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount,
                "单调时刻只增不减，不能倒退。"
                + " The monotonic instant never decreases and cannot be moved backwards.");
        }

        MonotonicNow += amount;
    }

    /// <summary>
    /// 中文：单独推进挂钟。用于验证某段代码**没有**拿挂钟去算超时。
    /// English: Advances the wall clock alone, to verify that some piece of code is *not*
    ///          computing a timeout from it.
    /// </summary>
    public void AdvanceWallClock(TimeSpan amount) => UtcNow += amount;
}
