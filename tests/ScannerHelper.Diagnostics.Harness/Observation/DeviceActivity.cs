// =============================================================================
// DeviceActivity.cs
//
// 中文：
//   单台设备的按键活动统计（Task 4a 的第 3、5 个问题）。
//
//   要回答的是"扫码枪的字符间隔是多少，与人打字差多少"。这个数字直接决定
//   Task 6 里扫描无活动超时该取多大：取小了会把一枪正常的扫描从中间截断，
//   取大了则每次扫描后都要多等那么久才认定结束。
//
//   ★ "连发段"（burst）的概念是这里的核心。
//
//     一次扫码是几十个字符在极短时间内连续到达；人打字则是零散的、间隔大得多。
//     若把所有按键间隔混在一起统计，两枪扫描之间那段几秒钟的空闲会被算成一个
//     "间隔"，中位数立刻失去意义。因此先按空隙把事件切成一段一段，只统计
//     **段内**的间隔——那才是"扫码枪吐字有多快"。
//
//     切分阈值取 200 毫秒，明显大于任何扫码枪的字符间隔，又明显小于人两次
//     有意识按键的最小间隔。它只用于**分析**，与 Task 6 里那个待定的扫描超时
//     没有关系，不要把两者混为一谈。
//
//   只记按下、不记弹起：一次按键必然产生一对按下与弹起，两者都统计等于把
//   同一件事数两遍，而且"按住多久"与"两个字符隔多久"是不同的问题。
//
// English:
//   Per-device keystroke statistics (4a's third and fifth questions).
//
//   The question is what a scanner's inter-character interval is and how it compares
//   to human typing. That figure directly sets the scan inactivity timeout in Task 6:
//   too small truncates a healthy scan mid-way, too large adds that much delay after
//   every scan before it is considered finished.
//
//   The notion of a *burst* is central here. One scan is dozens of characters arriving
//   in a very short window; typing is sparse and far slower. Pooling every interval
//   together would count the several idle seconds between two scans as an "interval"
//   and destroy the median. So events are first cut into runs at the gaps, and only
//   *within-run* intervals are measured — which is what "how fast the scanner emits
//   characters" actually means.
//
//   The split threshold is 200 ms: clearly above any scanner's inter-character
//   interval and clearly below the shortest gap between two deliberate human
//   keystrokes. It is used for *analysis* only and has nothing to do with the
//   still-undecided scan timeout in Task 6; the two must not be conflated.
//
//   Only key-down events are recorded. A keypress always produces a down and an up, so
//   counting both would count one thing twice — and "how long a key was held" is a
//   different question from "how far apart two characters were".
//
// 包含的成员 / Members in this file:
//   Handle / Identity        设备句柄与身份
//   TotalEventCount          观测到的事件总数（含弹起）
//   KeyDownCount             按下事件数
//   RecordKeyDown / RecordEvent  记录
//   BuildIntervalStatistics  按连发段统计字符间隔
// =============================================================================

using System.Diagnostics;
using ScannerHelper.Core.Domain;

namespace ScannerHelper.Diagnostics.Harness.Observation;

/// <summary>
/// 中文：一台设备的活动记录。
/// English: One device's activity record.
/// </summary>
public sealed class DeviceActivity
{
    /// <summary>
    /// 中文：把事件切成连发段的空隙阈值。仅用于分析，见文件头说明。
    /// English: The gap that separates one burst from the next. Analysis only; see the
    ///          file header.
    /// </summary>
    public static readonly TimeSpan BurstGap = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 中文：保留的按下时间戳上限。超过后丢弃最旧的。
    ///       统计中位数与百分位需要保留原始样本，但一次测量不该无限增长内存——
    ///       两万个样本足以覆盖数百次扫描，早已远超得出稳定分布所需。
    /// English: How many key-down timestamps are retained before the oldest are
    ///          discarded. Medians and percentiles need the raw samples, but a
    ///          measurement session must not grow without bound; twenty thousand covers
    ///          several hundred scans, far past what a stable distribution needs.
    /// </summary>
    private const int MaximumRetainedSamples = 20_000;

    private readonly List<long> _keyDownTimestamps = [];

    /// <summary>
    /// 中文：
    ///   构造设备活动记录。
    ///   输入：handle 设备句柄；identity 已解析的设备身份。
    /// English:
    ///   Creates the record from a device handle and its resolved identity.
    /// </summary>
    public DeviceActivity(nint handle, ScannerDeviceIdentity identity)
    {
        Handle = handle;
        Identity = identity;
    }

    /// <summary>
    /// 中文：设备句柄。**仅本次会话内有效**，重新插拔会变，绝不能用于持久化绑定。
    /// English: The device handle. Valid for this session only — it changes across a
    ///          replug and must never be persisted as a binding.
    /// </summary>
    public nint Handle { get; }

    /// <summary>
    /// 中文：设备身份。设备路径才是可跨会话持久化的那一项（规格 §6）。
    /// English: The device identity. The device path is the part that persists across
    ///          sessions (spec §6).
    /// </summary>
    public ScannerDeviceIdentity Identity { get; }

    /// <summary>
    /// 中文：观测到的事件总数，含按下与弹起。
    /// English: Total observed events, including key-ups.
    /// </summary>
    public int TotalEventCount { get; private set; }

    /// <summary>
    /// 中文：按下事件数，即字符数。
    /// English: Key-down events, that is, characters.
    /// </summary>
    public int KeyDownCount => _keyDownTimestamps.Count;

    /// <summary>
    /// 中文：显示用的名字：有友好名就用友好名，否则退回设备路径的末段，
    ///       再不行才用句柄。
    /// English: A display name: the friendly name when present, else the tail of the
    ///          device path, else the handle.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Identity.FriendlyName))
            {
                return Identity.FriendlyName;
            }

            if (!string.IsNullOrWhiteSpace(Identity.DevicePath))
            {
                var lastSeparator = Identity.DevicePath.LastIndexOf('#');
                return lastSeparator >= 0 && lastSeparator < Identity.DevicePath.Length - 1
                    ? Identity.DevicePath[..lastSeparator]
                    : Identity.DevicePath;
            }

            return $"设备 / device 0x{Handle:X}";
        }
    }

    /// <summary>
    /// 中文：VID/PID 的显示形式。内置键盘走 ACPI，没有这两项，显示为"—"
    ///       ——这不是缺陷，正是规格假设 A4 描述的情形。
    /// English: VID/PID for display. A built-in keyboard arrives over ACPI and has
    ///          neither, shown as an em dash — not a defect but exactly the situation
    ///          spec assumption A4 describes.
    /// </summary>
    public string VendorProduct =>
        Identity.VendorId is null && Identity.ProductId is null
            ? "—"
            : $"VID_{Identity.VendorId ?? "?"} PID_{Identity.ProductId ?? "?"}";

    /// <summary>
    /// 中文：
    ///   记录一个事件。
    ///   输入：timestamp 事件时间戳；isKeyUp 是否为弹起。
    ///   输出：无。
    ///   弹起只计入总数；按下额外保留时间戳用于间隔统计。
    /// English:
    ///   Records one event. Key-ups only increment the total; key-downs additionally
    ///   retain their timestamp for interval statistics.
    /// </summary>
    public void Record(long timestamp, bool isKeyUp)
    {
        TotalEventCount++;

        if (isKeyUp)
        {
            return;
        }

        if (_keyDownTimestamps.Count >= MaximumRetainedSamples)
        {
            _keyDownTimestamps.RemoveAt(0);
        }

        _keyDownTimestamps.Add(timestamp);
    }

    /// <summary>
    /// 中文：
    ///   按连发段统计字符间隔。
    ///   输入：无。
    ///   输出：段数、最大段的字符数，以及段内间隔的最小值/中位数/p99/最大值
    ///         （毫秒）。样本不足时各统计量为 null。
    ///   步骤：
    ///     1. 相邻按下时间戳求差；
    ///     2. 差值大于 <see cref="BurstGap"/> 的视为段与段之间的空隙，不计入间隔；
    ///     3. 其余差值即段内间隔，据此求统计量。
    ///
    ///   ★ 步骤 2 是这份统计有没有意义的关键。不做切分的话，两枪扫描之间
    ///     几秒钟的空闲会被当成一个"字符间隔"混进样本，中位数被彻底带偏，
    ///     而结果看上去仍然像一个正常数字——这类错误不会报错，只会安静地
    ///     给出错误结论。
    /// English:
    ///   Interval statistics computed per burst: the number of bursts, the largest
    ///   burst's character count, and the minimum, median, p99 and maximum
    ///   within-burst interval in milliseconds, or nulls when there is too little data.
    ///   Steps: (1) difference consecutive key-down timestamps; (2) treat a difference
    ///   above <see cref="BurstGap"/> as the space between bursts and exclude it;
    ///   (3) the rest are within-burst intervals.
    ///
    ///   Step 2 decides whether the statistic means anything. Without the split, the
    ///   idle seconds between two scans enter the sample as an "inter-character
    ///   interval", dragging the median off entirely — while the result still looks
    ///   like a plausible number. This class of error never raises anything; it just
    ///   quietly produces a wrong conclusion.
    /// </summary>
    public IntervalStatistics BuildIntervalStatistics()
    {
        var burstGapTicks = (long)(BurstGap.TotalSeconds * Stopwatch.Frequency);

        var withinBurstIntervals = new List<double>();
        var burstCount = _keyDownTimestamps.Count > 0 ? 1 : 0;
        var currentBurstSize = _keyDownTimestamps.Count > 0 ? 1 : 0;
        var largestBurstSize = currentBurstSize;

        for (var index = 1; index < _keyDownTimestamps.Count; index++)
        {
            var elapsedTicks = _keyDownTimestamps[index] - _keyDownTimestamps[index - 1];

            if (elapsedTicks > burstGapTicks)
            {
                burstCount++;
                largestBurstSize = Math.Max(largestBurstSize, currentBurstSize);
                currentBurstSize = 1;
                continue;
            }

            currentBurstSize++;
            withinBurstIntervals.Add(elapsedTicks * 1000.0 / Stopwatch.Frequency);
        }

        largestBurstSize = Math.Max(largestBurstSize, currentBurstSize);

        return new IntervalStatistics(
            BurstCount: burstCount,
            LargestBurstSize: largestBurstSize,
            SampleCount: withinBurstIntervals.Count,
            MinimumMilliseconds: Percentiles.Minimum(withinBurstIntervals),
            MedianMilliseconds: Percentiles.Median(withinBurstIntervals),
            NinetyNinthMilliseconds: Percentiles.Percentile(withinBurstIntervals, 0.99),
            MaximumMilliseconds: Percentiles.Maximum(withinBurstIntervals));
    }
}

/// <summary>
/// 中文：一台设备的字符间隔统计结果。
/// English: One device's inter-character interval statistics.
/// </summary>
/// <param name="BurstCount">中文：连发段数量，约等于扫描次数。 English: Burst count, roughly the number of scans.</param>
/// <param name="LargestBurstSize">中文：最大一段的字符数，约等于最长条码的长度。 English: The largest burst's character count, roughly the longest barcode.</param>
/// <param name="SampleCount">中文：段内间隔样本数。 English: Number of within-burst interval samples.</param>
/// <param name="MinimumMilliseconds">中文：最小间隔。 English: Smallest interval.</param>
/// <param name="MedianMilliseconds">中文：中位间隔。 English: Median interval.</param>
/// <param name="NinetyNinthMilliseconds">中文：p99 间隔。 English: 99th percentile interval.</param>
/// <param name="MaximumMilliseconds">中文：最大间隔。 English: Largest interval.</param>
public readonly record struct IntervalStatistics(
    int BurstCount,
    int LargestBurstSize,
    int SampleCount,
    double? MinimumMilliseconds,
    double? MedianMilliseconds,
    double? NinetyNinthMilliseconds,
    double? MaximumMilliseconds);
