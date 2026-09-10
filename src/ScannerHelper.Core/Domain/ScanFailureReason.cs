// =============================================================================
// ScanFailureReason.cs
//
// 中文：
//   一次扫描未能正常结束的原因码。
//
//   与 ParseFailureReason 遵守同一条规矩：每个原因码都必须指向一个**不同的
//   排查动作**。若两种情况的处理方式完全相同，就不该拆成两个码；反之，
//   只要现场需要区别对待，就必须能区分。
//
//   这里的三个码分别指向三个不同的地方：
//     InactivityTimeout  指向硬件或环境——扫到一半断了。查扫码枪对没对准、
//                        条码有没有破损、USB 连接稳不稳。Task 4a 已经实测到
//                        这类残缺确实会发生（规格 §22.5）。
//     EmptyScan          指向捕获链路——终止符到了，前面一个字符都没有。
//                        这不是"扫到一个空条码"，条码不可能是空的；这说明
//                        字符在到达我们之前就全丢了。
//     TooLong            指向配置或误判——收到的字符数超过任何合理条码的长度。
//                        通常意味着某台设备被错认成了扫码枪，于是有人打字
//                        打成了一次"扫描"。
//
//   同样是枚举而非字符串（决策 D-5、D-6）：Core 不得产出任何面向用户的文案，
//   否则中文界面无法正确显示，而这个缺陷要等到做本地化时才会暴露（规格 §12）。
//
// English:
//   Why a scan failed to end normally.
//
//   Same rule as ParseFailureReason: every code must point at a *different* corrective
//   action. If two situations are handled identically they should not be separate codes;
//   if the shop floor must treat them differently, they must be distinguishable.
//
//   These three point in three directions. InactivityTimeout points at hardware or
//   environment — the scan broke off part-way, so check alignment, label damage and the
//   USB connection; Task 4a confirmed such damage genuinely occurs (spec §22.5).
//   EmptyScan points at the capture path — a terminator arrived with nothing before it,
//   which is not "an empty barcode" (there is no such thing) but every character being
//   lost before it reached us. TooLong points at configuration or misidentification —
//   more characters than any plausible barcode, usually meaning some device was mistaken
//   for the scanner and somebody's typing became a "scan".
//
//   An enum rather than strings (decisions D-5, D-6): Core must emit no user-facing text,
//   or the Chinese UI cannot render correctly and the defect stays hidden until
//   localization begins (spec §12).
//
// 包含的类型 / Types in this file:
//   ScanFailureReason
// =============================================================================

namespace ScannerHelper.Core.Domain;

/// <summary>
/// 中文：扫描失败的原因。
/// English: Why a scan failed.
/// </summary>
public enum ScanFailureReason
{
    /// <summary>
    /// 中文：距上一个字符太久没有新字符，这次扫描被判定为中断（规格 §5.4）。
    ///       指向硬件或环境：扫到一半断了。
    /// English: No further character arrived within the inactivity window, so the scan is
    ///          treated as broken off (spec §5.4). Points at hardware or environment.
    /// </summary>
    InactivityTimeout,

    /// <summary>
    /// 中文：终止符到达时缓冲区是空的（决策 D-15）。
    ///
    ///       指向捕获链路而非条码：条码不可能是空的，所以这说明字符在到达
    ///       我们之前就丢光了。刻意不当作"成功扫到空串"——那会让一个空值
    ///       一路走到输出，静静写进仓库系统。
    /// English: The terminator arrived with an empty buffer (decision D-15).
    ///
    ///          Points at the capture path rather than the barcode: a barcode cannot be
    ///          empty, so every character was lost before reaching us. Deliberately not
    ///          treated as a successful scan of "", which would let an empty value travel
    ///          all the way to output and quietly enter the warehouse system.
    /// </summary>
    EmptyScan,

    /// <summary>
    /// 中文：收到的字符数超过了允许的上限（决策 D-16）。
    ///       指向配置或设备误判——通常是某台设备被错认成扫码枪，于是有人
    ///       打字被当成了一次扫描。规格 §19 要求这种无法确信的情况安全失败
    ///       并留下诊断，而不是让缓冲区一直涨下去。
    /// English: More characters arrived than the configured maximum allows (decision
    ///          D-16). Points at configuration or a misidentified device — usually some
    ///          device mistaken for the scanner, turning someone's typing into a "scan".
    ///          Spec §19 requires such an unresolvable situation to fail safely and be
    ///          surfaced rather than letting a buffer grow indefinitely.
    /// </summary>
    TooLong,
}
