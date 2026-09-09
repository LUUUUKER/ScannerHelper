// =============================================================================
// DiagnosticsSettings.cs
//
// 中文：
//   诊断日志的配置（规格 §15）。
//
//   两个设置项的默认值都在"可排查"与"少留痕"之间做了取舍，且方向不同：
//
//   条码脱敏默认**关闭**：日志里的原始条码是排查解析失败的首要线索——没有它，
//   "为什么这一枪失败了"根本无从回答。仓库条码本身不是个人信息，默认记录是
//   合理的。有合规要求的站点可以主动打开。
//
//   日志保留 14 天：规格 §15 建议 7~30 天并要求选一个保守的默认值。14 天足以
//   覆盖"上周出过一次问题、这周才有人来查"的常见场景，同时不会让日志无限
//   增长。规格明确要求防止无限增长。
//
// English:
//   Diagnostic logging configuration (spec §15).
//
//   Both defaults trade between "diagnosable" and "leaves little behind", and they
//   lean in different directions.
//
//   Barcode masking defaults **off**: the raw code in the log is the primary clue
//   when a parse fails — without it, "why did that scan fail" cannot be answered at
//   all. Warehouse barcodes are not personal data, so recording them by default is
//   reasonable, and a site with compliance requirements can turn masking on.
//
//   Retention of 14 days: spec §15 suggests 7–30 and asks for a conservative
//   default. Fourteen covers the common "it broke last week and someone is looking
//   this week" case without letting logs grow without bound, which the spec forbids.
//
// 包含的类型 / Types in this file:
//   DiagnosticsSettings
// =============================================================================

namespace ScannerHelper.Core.Settings;

/// <summary>
/// 中文：诊断日志配置。
/// English: Diagnostic logging configuration.
/// </summary>
public sealed class DiagnosticsSettings
{
    /// <summary>
    /// 中文：是否在日志中对条码内容脱敏，默认关闭（规格 §15）。
    ///       关闭时记录完整原始码——那是排查解析失败的首要线索。
    /// English: Whether to mask barcode content in logs; off by default (spec §15).
    ///          When off, the full raw code is recorded — the primary clue for
    ///          diagnosing a parse failure.
    /// </summary>
    public bool MaskBarcodeData { get; set; }

    /// <summary>
    /// 中文：日志保留天数，默认 14 天（规格 §15 建议 7~30 天）。
    ///       超期日志按日期滚动清理，防止无限增长。
    /// English: Log retention in days, default 14 (spec §15 suggests 7–30). Older
    ///          logs roll off by date so they cannot grow without bound.
    /// </summary>
    public int LogRetentionDays { get; set; } = 14;
}
