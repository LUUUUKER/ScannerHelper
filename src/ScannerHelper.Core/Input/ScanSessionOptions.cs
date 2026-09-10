// =============================================================================
// ScanSessionOptions.cs
//
// 中文：
//   一次扫描会话的配置。
//
//   三个值各自回答一个不同的问题，彼此**不能互相推导**：
//     多长的空隙算这一枪结束？        InactivityTimeout
//     什么字符表示扫描结束？          Terminator
//     多少个字符之后该判定出事了？    MaximumLength
//
//   ★ 关于 InactivityTimeout 与关联窗口是两个独立的值（决策 D-17）。
//
//     它们回答的问题完全不同：关联窗口问的是"两条通道最多能差多久"，
//     这个问的是"多长的空隙算一枪结束"。Task 4a 实测两者相差约两个数量级
//     （通道时差中位数不到 1 毫秒，人打字间隔中位数约 100 毫秒）。
//
//     规格已经就 300 毫秒这个数字提醒过一次：扫描超时与 LowLevelHooksTimeout
//     数字相同但毫无关系。这是同一个陷阱在下一层——数字相近不等于概念相同，
//     一旦有人用其中一个去推另一个，两者就再也分不开了。
//
// English:
//   Configuration for one scan session.
//
//   Three values answering three different questions, none derivable from another: how
//   long a gap ends this scan (InactivityTimeout), which character marks the end
//   (Terminator), and how many characters mean something has gone wrong (MaximumLength).
//
//   InactivityTimeout is independent of the correlation window (decision D-17). They ask
//   different questions — how long the two channels may disagree versus how long a gap
//   ends a scan — and Task 4a measured them two orders of magnitude apart (an
//   inter-channel median under a millisecond against a human typing median around
//   100 ms).
//
//   The spec already gave one warning about the number 300: the scan timeout shares it
//   with LowLevelHooksTimeout and nothing else. This is the same trap one layer down.
//   Similar numbers are not the same concept, and once someone derives one from the other
//   the two can never be separated again.
//
// 包含的类型 / Types in this file:
//   ScanSessionOptions
// =============================================================================

namespace ScannerHelper.Core.Input;

/// <summary>
/// 中文：扫描会话的配置。
/// English: Configuration for a scan session.
/// </summary>
public sealed record ScanSessionOptions
{
    /// <summary>
    /// 中文：
    ///   扫描无活动超时，默认 300 毫秒（规格 §5.4）。
    ///
    ///   Task 4a 的实测给了它充足的依据：扫码枪段内字符间隔中位数约 1.1 毫秒，
    ///   而人打字的间隔中位数约 94~122 毫秒。300 毫秒对扫码枪留了两个数量级的
    ///   余量，同时又明显小于人两次有意识按键的间隔。
    ///
    ///   ★ 两头都不能太过：
    ///     取太小 → 一枪正常的扫描会被从中间截断，工人得到半截码。而 Task 4a
    ///              已经实测到硬件本身就会产生残缺（规格 §22.5），软件再制造
    ///              一批同样形状的残缺，现场将完全无法分辨是谁造成的。
    ///     取太大 → 每次扫描结束后都要多等这么久才认定完成，直接体现为
    ///              "扫完要等一下才出结果"。
    ///
    ///   规格说这是初始默认值，要用真实硬件验证后再定稿。开发机上的实测值
    ///   不代表现场机器（规格 §22.5 同一条告诫）。
    /// English:
    ///   The scan inactivity timeout, 300 ms by default (spec §5.4).
    ///
    ///   Task 4a gives it a solid basis: the scanner's within-burst inter-character median
    ///   was about 1.1 ms while human typing sat around 94–122 ms. 300 ms leaves two orders
    ///   of magnitude of headroom over the scanner and stays well below a deliberate human
    ///   gap.
    ///
    ///   It must not err in either direction. Too small truncates a healthy scan and hands
    ///   the operator half a code — and Task 4a already found the hardware producing damage
    ///   of exactly that shape (spec §22.5), so software manufacturing more of it would
    ///   leave the site unable to tell which was responsible. Too large adds that delay
    ///   after every scan before it counts as finished, felt directly as "you have to wait
    ///   a moment after scanning".
    ///
    ///   The spec calls this an initial default to be finalized against real hardware, and
    ///   a development machine does not represent the pilot (spec §22.5's caution again).
    /// </summary>
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// 中文：
    ///   扫描终止符，默认回车（规格 §5.4）。
    ///
    ///   用**字符**而不是扫描码，是决策 D-14 的直接结果：Core 收到的是已经解码
    ///   好的字符，解码在 Win32 完成。Win32 会把 VK_RETURN 解成 '\r'，主键盘的
    ///   回车与小键盘的回车都一样，因此这里不必关心扫描码与扩展位的差别。
    ///
    ///   ★ 终止符本身**绝不进入结果**（规格 §5.4、§10）。整段原始扫描都被吞掉，
    ///     包括扫码枪自带的这个回车。程序是否另外补一个回车，是
    ///     AppendEnterAfterScan 这个独立的设置项决定的（决策 D-10），
    ///     两者不要混为一谈。
    /// English:
    ///   The scan terminator, Enter by default (spec §5.4).
    ///
    ///   A character rather than a scan code, following directly from decision D-14: Core
    ///   receives characters already decoded by Win32, which renders VK_RETURN as '\r' for
    ///   both the main and numeric-keypad Enter, so scan codes and extended flags need not
    ///   be considered here.
    ///
    ///   The terminator never appears in the result (spec §5.4, §10). The entire raw scan
    ///   is swallowed, the scanner's own Enter included. Whether the application emits an
    ///   Enter of its own is the separate AppendEnterAfterScan setting (decision D-10) and
    ///   must not be conflated with this.
    /// </summary>
    public char Terminator { get; init; } = '\r';

    /// <summary>
    /// 中文：
    ///   一次扫描允许的最大字符数，默认 512（决策 D-16）。
    ///
    ///   ★ 这**不是**业务约束，而是安全上限。
    ///
    ///     它不表达"条码不会超过 512 个字符"这种业务判断——真要限制长度，
    ///     那是 SKU 校验层的事（规格 §9.1）。它防的是完全不同的一类情况：
    ///     某台设备被错认成扫码枪，于是有人打字被当成了一次永不结束的扫描；
    ///     或者某个键卡住了。
    ///
    ///     没有上限的话，缓冲区会一直涨下去，而这一切发生在扫描处理链路上。
    ///     规格 §19 要求无法确信的情况**安全失败并留下诊断**，而不是悄悄劣化。
    ///
    ///   512 相对真实条码留了很大余量：GS1-128 常见长度在几十个字符，
    ///   本项目试点用的条码是 15 个字符。取这么大是刻意的——上限的作用是
    ///   兜住异常，不该在正常业务里被碰到，否则它就变成了一条隐形的业务规则。
    /// English:
    ///   The maximum characters one scan may hold, 512 by default (decision D-16).
    ///
    ///   This is not a business constraint but a safety bound. It makes no claim that
    ///   barcodes stay under 512 characters — genuine length limits belong to SKU
    ///   validation (spec §9.1). It guards an entirely different situation: a device
    ///   mistaken for the scanner turning somebody's typing into a scan that never ends, or
    ///   a stuck key.
    ///
    ///   Unbounded, the buffer would simply grow, and all of this sits on the scan-handling
    ///   path. Spec §19 requires an unresolvable situation to fail safely and be surfaced
    ///   rather than degrade quietly.
    ///
    ///   512 leaves a great deal of room over real barcodes — GS1-128 commonly runs to a few
    ///   dozen characters and this project's pilot barcode is 15. The generosity is
    ///   deliberate: a safety bound exists to catch anomalies and must never be reached in
    ///   normal business, or it becomes an invisible business rule.
    /// </summary>
    public int MaximumLength { get; init; } = 512;
}
