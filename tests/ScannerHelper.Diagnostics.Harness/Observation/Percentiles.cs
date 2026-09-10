// =============================================================================
// Percentiles.cs
//
// 中文：
//   分布统计的小工具。
//
//   ★ 为什么报告里必须给分布，而不是只给平均值。
//
//     规格 §4.3 明确要求"两条通道时差的分布（最小值 / 中位数 / p99）"。
//     平均值在这里会骗人：假设一千次按键里九百九十九次时差是 50 微秒、
//     一次是 200 毫秒，平均值大约 250 微秒——看上去毫无问题，而那唯一的
//     一次恰恰是整个架构会出事的地方。
//
//     Task 4b 的"扣留-重放"要设一个等待窗口，这个窗口必须覆盖 p99 甚至更极端
//     的情况，否则每一百次按键就有一次判断错源。要选这个值，看的是尾部，
//     不是中间。
//
//   样本为空时一律返回 null，而不是 0。0 是一个**看起来完全正常的测量结果**，
//   会被直接抄进报告；null 逼着调用方显式处理"根本没有数据"这件事。
//
// English:
//   Small helpers for distribution statistics.
//
//   Why the report must give a distribution rather than an average: spec §4.3 asks for
//   the inter-channel delta's min, median and p99 specifically. An average lies here.
//   If 999 of 1000 keystrokes differ by 50 µs and one by 200 ms, the average is about
//   250 µs — entirely unalarming, while that single outlier is exactly where the
//   architecture breaks.
//
//   Task 4b's withhold-and-replay needs a wait window, and that window must cover p99
//   and beyond or one keystroke in a hundred is attributed to the wrong device.
//   Choosing it means looking at the tail, not the middle.
//
//   An empty sample returns null rather than 0. Zero is a measurement result that
//   looks perfectly normal and would be copied straight into the report; null forces
//   the caller to deal with "there is no data" explicitly.
//
// 包含的成员 / Members in this file:
//   Minimum / Maximum / Median / Percentile
// =============================================================================

namespace ScannerHelper.Diagnostics.Harness.Observation;

/// <summary>
/// 中文：分布统计辅助。
/// English: Distribution statistics helpers.
/// </summary>
public static class Percentiles
{
    /// <summary>
    /// 中文：最小值；样本为空时返回 null。
    /// English: The minimum, or null for an empty sample.
    /// </summary>
    public static double? Minimum(IReadOnlyCollection<double> samples)
        => samples.Count == 0 ? null : samples.Min();

    /// <summary>
    /// 中文：最大值；样本为空时返回 null。
    /// English: The maximum, or null for an empty sample.
    /// </summary>
    public static double? Maximum(IReadOnlyCollection<double> samples)
        => samples.Count == 0 ? null : samples.Max();

    /// <summary>
    /// 中文：中位数；样本为空时返回 null。
    /// English: The median, or null for an empty sample.
    /// </summary>
    public static double? Median(IReadOnlyCollection<double> samples)
        => Percentile(samples, 0.5);

    /// <summary>
    /// 中文：
    ///   求分位数。
    ///   输入：samples 样本；fraction 分位，0 到 1 之间。
    ///   输出：该分位上的值；样本为空时返回 null。
    ///   实现：排序后取最近的秩次（nearest-rank）。
    ///
    ///   刻意不做线性插值。插值出来的数字在样本里**并不存在**，而这里的样本是
    ///   实测到的时延——报告里写一个"从未真实发生过的时延"，会让读者以为
    ///   那是观测事实。取最近秩次意味着报告的每一个数字都对应着一次真实按键。
    /// English:
    ///   The value at the given fraction (0 to 1), or null for an empty sample, using
    ///   the nearest-rank method after sorting.
    ///
    ///   Interpolation is deliberately avoided. An interpolated figure does not exist
    ///   in the sample, and these samples are measured latencies — printing a latency
    ///   that never actually occurred invites the reader to treat it as an observation.
    ///   Nearest-rank means every number in the report corresponds to a real keystroke.
    /// </summary>
    public static double? Percentile(IReadOnlyCollection<double> samples, double fraction)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        var ordered = samples.OrderBy(sample => sample).ToArray();
        var rank = (int)Math.Ceiling(fraction * ordered.Length) - 1;
        return ordered[Math.Clamp(rank, 0, ordered.Length - 1)];
    }
}
