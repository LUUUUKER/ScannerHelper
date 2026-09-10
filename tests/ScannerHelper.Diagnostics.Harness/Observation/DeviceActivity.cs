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

    /// <summary>
    /// 中文：本设备的全部按键记录，含弹起。
    ///
    ///       之前只存按下的时间戳，够算间隔，却不够**把扫描内容还原出来**——
    ///       还原需要 Shift 的按下与弹起来维护大小写状态。而"钩子看到的码是否
    ///       完整"正是当下最要紧的问题，所以这里改成留全量。
    /// English:
    ///   Every keystroke recorded for this device, key-ups included.
    ///
    ///   Storing only key-down timestamps sufficed for intervals but not for
    ///   reconstructing the scan text, which needs Shift downs and ups to track case.
    ///   Whether the hook saw a complete code is the pressing question, so the full
    ///   stream is retained.
    /// </summary>
    private readonly List<RecordedKey> _keys = [];

    private int _keyDownCount;
    private int _outOfOrderCount;

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
    /// 中文：按下事件数。注意含修饰键，因此不等于还原出的字符数——一个大写
    ///       字母是两次按下（Shift 加字母）换一个字符。
    /// English: Key-down events. Modifiers included, so this is not the number of
    ///          reconstructed characters: an uppercase letter is two key-downs, Shift
    ///          plus the letter, for one character.
    /// </summary>
    public int KeyDownCount => _keyDownCount;

    /// <summary>
    /// 中文：时间戳出现倒退的次数。
    ///
    ///       ★ 正常情况下恒为零。不为零说明**配对配错了**——某个事件被安到了
    ///         错误的设备上，或者跟一个很久以前的事件配成了对。第二轮实测中
    ///         PS/2 键盘的"最小段内间隔"报出负 24 秒，就是这么来的。
    ///
    ///         这个数字必须被看见而不是被吸收进统计。规格 §19.1 的原则同样适用：
    ///         程序绝不能显示一个自己没验证过的状态——一份掺着错配数据的分布，
    ///         看起来和真的一模一样。
    /// English:
    ///   How many times a timestamp went backwards. Always zero when things are right; a
    ///   non-zero value means mis-pairing — an event attributed to the wrong device, or
    ///   paired with one from long ago. The second measurement run reported a minimum
    ///   within-burst interval of minus 24 seconds for exactly this reason.
    ///
    ///   The count must be visible rather than absorbed into the statistics. Spec §19.1's
    ///   principle applies: never display a state that has not been verified, and a
    ///   distribution containing mis-paired data looks exactly like a sound one.
    /// </summary>
    public int OutOfOrderCount => _outOfOrderCount;

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
    ///   输入：timestamp 钩子那一侧的时间戳；virtualKey 虚拟键码；
    ///         isKeyUp 是否为弹起。
    ///   输出：无。
    ///   步骤：
    ///     1. 时间戳早于上一条时计入乱序计数——那说明配对出了问题，
    ///        但**照常记录**，不丢弃；
    ///     2. 超出保留上限时丢弃最旧的一条；
    ///     3. 追加。
    ///
    ///   步骤 1 只计数、不丢弃，是因为丢弃会掩盖问题：统计看起来会变正常，
    ///   而错配依然存在。计数让它留在明面上（规格 §19.1 的同一条原则）。
    /// English:
    ///   Records one event.
    ///   Steps: (1) count a timestamp earlier than the previous one as out-of-order —
    ///   evidence of a pairing problem — while still recording it; (2) drop the oldest
    ///   past the retention cap; (3) append.
    ///
    ///   Step 1 counts without discarding, because discarding would hide the problem:
    ///   the statistics would look healthy while the mis-pairing persisted. Counting
    ///   keeps it in plain sight — spec §19.1's principle again.
    /// </summary>
    public void Record(long timestamp, ushort virtualKey, bool isKeyUp)
    {
        TotalEventCount++;

        // 步骤 1 / Step 1
        if (_keys.Count > 0 && timestamp < _keys[^1].Timestamp)
        {
            _outOfOrderCount++;
        }

        // 步骤 2 / Step 2
        if (_keys.Count >= MaximumRetainedSamples)
        {
            if (!_keys[0].IsKeyUp)
            {
                _keyDownCount--;
            }

            _keys.RemoveAt(0);
        }

        // 步骤 3 / Step 3
        _keys.Add(new RecordedKey(timestamp, virtualKey, isKeyUp));

        if (!isKeyUp)
        {
            _keyDownCount++;
        }
    }

    /// <summary>
    /// 中文：
    ///   把本设备的事件流还原成一段一段的扫描内容。
    ///
    ///   ★ 这是当下最要紧的对照材料：把这里还原出来的内容，跟应用（记事本、
    ///     业务网页）里实际收到的内容逐条比对。
    ///
    ///       两边都完整   → 那次扫描没问题
    ///       这边完整、应用那边残缺
    ///                    → 字符丢在**钩子之后**。我们的架构能修好它：吞掉原始
    ///                      按键，自己按可控节奏重发。
    ///       这边也残缺   → 字符丢在**钩子之前**，我们会捕获到一个错码再原样
    ///                      发出去，而界面显示一切正常。这是规格 §19.1 的
    ///                      "静默的错误数据"，4b 的硬性关卡必须拦下。
    /// English:
    ///   Reconstructs this device's event stream into individual scans.
    ///
    ///   This is the comparison that matters right now: match each reconstruction against
    ///   what the application actually received. Both complete means that scan was fine.
    ///   Complete here but damaged in the application means characters are lost *after*
    ///   the hook, which this architecture repairs by swallowing the raw keystrokes and
    ///   re-emitting at a controlled pace. Damaged here too means they are lost *before*
    ///   the hook, so we would capture a wrong code and emit it while reporting success —
    ///   spec §19.1's silently wrong data, which 4b's hard gate must catch.
    /// </summary>
    public IReadOnlyList<ReconstructedScan> BuildReconstructedScans()
        => ScanTextReconstruction.Split(
            _keys, (long)(BurstGap.TotalSeconds * Stopwatch.Frequency));

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
        var keyDownTimestamps = _keys
            .Where(key => !key.IsKeyUp)
            .Select(key => key.Timestamp)
            .ToArray();

        var withinBurstIntervals = new List<double>();
        var burstCount = keyDownTimestamps.Length > 0 ? 1 : 0;
        var currentBurstSize = keyDownTimestamps.Length > 0 ? 1 : 0;
        var largestBurstSize = currentBurstSize;

        for (var index = 1; index < keyDownTimestamps.Length; index++)
        {
            var elapsedTicks = keyDownTimestamps[index] - keyDownTimestamps[index - 1];

            // 负间隔说明时间戳倒退了，那是配对错误的产物，不是输入的性质。
            // 把它算进分布会污染整份统计，因此排除——它已经被 OutOfOrderCount
            // 单独计数，不会被藏起来。
            // A negative interval means the timestamp went backwards, which is an
            // artifact of mis-pairing rather than a property of the input. Including it
            // would poison the distribution, so it is excluded — and it is not hidden,
            // being counted separately in OutOfOrderCount.
            if (elapsedTicks < 0)
            {
                continue;
            }

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
