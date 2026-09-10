// =============================================================================
// BarcodeMask.cs
//
// 中文：
//   把条码内容遮掉一部分再写进日志（规格 §15）。
//
//   ★ 遮码要在"能查问题"和"不落一份完整清单"之间取平衡，两头都不能倒。
//
//     日志存在的意义是现场出了事能查。全部遮掉（写成 ****）等于把日志变成一行
//     "有一枪失败了"——而查问题时最需要知道的恰恰是"失败的那些码有什么共同点"。
//     完全不遮又意味着程序在磁盘上留下一份完整的货品流水，那是仓库不一定愿意
//     交出去的东西，而且日志会被拷来拷去。
//
//     所以留两头、遮中间，并且**保留长度**：
//       ABCDEFG123456789  →  AB************89
//
//     这样仍然能回答查问题时的大部分疑问：长度对不对（长度校验失败就靠它）、
//     是不是同一批货（前缀相同）、是不是同一枚（后缀不同）。
//
//   ★ 保留长度这一条尤其要紧。
//
//     "SKU 太短"这类失败，日志里若连长度都看不出来，那条记录就等于没写。
//     而长度本身几乎不泄露什么——同一类条码长度本来就都一样。
//
//   ★ 短码整个遮掉。
//
//     四位以内留两头就等于原文，"遮了"只是一种错觉——而错觉比不遮更坏：
//     打开遮码开关的人会以为自己已经保护了数据。
//
// English:
//   Masks part of a barcode before it goes into the log (spec §15).
//
//   Masking has to balance "can still diagnose" against "does not leave a complete list on disk",
//   and must not fall off either side. A log exists so that trouble on site can be investigated,
//   and masking everything to **** reduces an entry to "a scan failed" — while what an
//   investigation most needs is what the failing codes have in common. Masking nothing leaves a
//   full record of goods movement on disk, which a warehouse may not be willing to hand over, and
//   logs get copied around.
//
//   So the ends stay, the middle goes, and the length is preserved:
//   ABCDEFG123456789 becomes AB************89. That still answers most of what an investigation
//   asks: whether the length is right (which is what a length failure hinges on), whether these are
//   the same batch (same prefix), whether they are distinct items (different suffix).
//
//   Preserving the length matters especially: for a "SKU too short" failure, an entry that does not
//   even show the length may as well not have been written — and the length itself reveals almost
//   nothing, since barcodes of one kind all share it.
//
//   Short codes are masked entirely: keeping both ends of a four-character code returns the code
//   itself, and "masked" would then be an illusion — worse than not masking, because whoever turned
//   the switch on believes the data is protected.
//
// 包含的类型 / Types in this file:
//   BarcodeMask
// =============================================================================

namespace ScannerHelper.Core.Diagnostics;

/// <summary>
/// 中文：条码遮码（规格 §15）。
/// English: Barcode masking (spec §15).
/// </summary>
public static class BarcodeMask
{
    /// <summary>
    /// 中文：两头各留几位。
    /// English: How many characters are kept at each end.
    /// </summary>
    private const int VisibleAtEachEnd = 2;

    /// <summary>
    /// 中文：短于这个长度就整个遮掉——留两头等于没遮。
    /// English: Anything shorter than this is masked entirely; keeping both ends would mask
    ///          nothing.
    /// </summary>
    private const int MinimumLengthForPartialMask = (VisibleAtEachEnd * 2) + 1;

    /// <summary>
    /// 中文：
    ///   按设置决定要不要遮，并返回写进日志的那个值。
    ///   输入：rawCode 原始内容；isMaskingEnabled 设置里是否开启遮码。
    ///   输出：可以直接写进日志的字符串。
    ///
    ///   ★ 把"要不要遮"也放进来，而不是让调用方自己判断。调用方有十几处
    ///     （每一种日志事件一处），判断散在十几处，迟早有一处忘了——而那一处
    ///     会把明文写进一个使用者以为已经遮过的日志里，且没有任何迹象。
    /// English:
    ///   Applies the setting and returns the value to log.
    ///
    ///   The decision lives here rather than at the call sites: there are a dozen of those, one per
    ///   event kind, and a decision repeated a dozen times is eventually forgotten in one of them —
    ///   which then writes plaintext into a log its reader believes is masked, with nothing to show
    ///   for it.
    /// </summary>
    public static string Apply(string? rawCode, bool isMaskingEnabled)
    {
        if (string.IsNullOrEmpty(rawCode))
        {
            return "—";
        }

        if (!isMaskingEnabled)
        {
            return rawCode;
        }

        if (rawCode.Length < MinimumLengthForPartialMask)
        {
            return new string('*', rawCode.Length);
        }

        var hidden = rawCode.Length - (VisibleAtEachEnd * 2);

        return string.Concat(
            rawCode.AsSpan(0, VisibleAtEachEnd),
            new string('*', hidden),
            rawCode.AsSpan(rawCode.Length - VisibleAtEachEnd));
    }
}
