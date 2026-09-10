// =============================================================================
// ScanCommand.cs
//
// 中文：
//   命令条码：贴在工位上、扫一下就切换模式的那两张纸。
//
//   ★ 为什么要有它：键盘上没有一个键在每台电脑上都是空的。
//
//     现场反馈是"每台电脑的 F8 都可能被别的软件占用"。换成另一个固定键只是把
//     赌注挪个地方——真正的出路是干脆不用键盘。工人手里本来就握着扫码枪，
//     扫一下比腾出一只手去找按键快，而且不存在"这台机器上这个键被占了"。
//
//   ★ 前缀 #SH: 是刻意选的，而且必须是**不可能撞上的**。
//
//     撞上的后果不是报错，是**静默切模式**：工人扫了一件货，程序悄悄换了模式，
//     接下来几十枪全按错的规则发出去，而没有任何一处会提示。所以这个前缀不能是
//     "SW"、"MODE" 这种可能出现在真实条码里的词。
//
//     # 开头 + SH + 冒号这个组合，在商品条码（EAN/UPC 纯数字、Code128 的物流编码）
//     里不会出现。
//
//   ★ 认出前缀但认不出命令时，**不发出去、并且要让人看见**。
//
//     一张印错的、或者旧版本程序遇上新版本命令的条码，如果原样发进业务软件，
//     业务软件里就会凭空多出一行 #SH:XXX#——那是最难查的一类脏数据，因为
//     没有人会想到它来自一张贴在墙上的纸。既然前缀已经宣告"这是给本程序的"，
//     那就由本程序负责到底：不转发，并且报出来。
//
//   ★ 暂停期间**不认**命令条码，原样发出。
//
//     暂停是安全阀，它的承诺是"程序什么都不管，扫什么发什么"——一条没有例外的
//     规则。在这里开一个口子，这条承诺就需要附加说明，而安全阀最不该有的就是
//     附加说明。这一条在 ScanProcessor 的步骤顺序里体现：暂停判断在前。
//
// English:
//   Command barcodes: the two sheets taped to the workstation that switch the mode when scanned.
//
//   They exist because no key on the keyboard is free on every machine. The field reported that F8
//   may be taken by other software on any given PC, and moving to another fixed key only moves the
//   bet; the way out is to stop using the keyboard. The operator already holds the scanner, a scan
//   is faster than freeing a hand to find a key, and no machine can have it "already taken".
//
//   The #SH: prefix is chosen to be impossible to collide with, because a collision does not raise
//   an error — it silently switches the mode. The operator scans an item, the program quietly
//   changes mode, and the next dozens of scans go out under the wrong rule with nothing anywhere
//   saying so. That rules out words like "SW" or "MODE" that can occur in real barcodes; a leading
//   # plus SH plus a colon does not appear in product barcodes (numeric EAN/UPC, Code 128 logistics
//   codes).
//
//   A code whose prefix is recognized but whose command is not is neither forwarded nor swallowed
//   quietly. Passing a misprinted sheet — or a newer version's command reaching an older build —
//   through to the business application would put a stray #SH:XXX# line into it, the hardest kind of
//   dirty data to trace, because nobody thinks to blame a sheet of paper on the wall. The prefix has
//   already declared "this belongs to Scanner Helper", so Scanner Helper owns it to the end: it does
//   not forward it, and it says so.
//
//   While paused, command barcodes are not recognized and go out unchanged. The pause valve promises
//   that the program does nothing and sends what it reads — a rule with no exceptions. An exception
//   here would make that promise need a footnote, and a safety valve is the last place for one. The
//   ordering in ScanProcessor carries this: the paused check comes first.
//
// 包含的类型 / Types in this file:
//   ScanCommandKind
//   ScanCommand
// =============================================================================

namespace ScannerHelper.Core.Input;

/// <summary>
/// 中文：一枚条码是不是命令条码，是哪一种。
/// English: Whether a code is a command barcode, and which one.
/// </summary>
public enum ScanCommandKind
{
    /// <summary>中文：普通条码，不是命令。 English: An ordinary code, not a command.</summary>
    None,

    /// <summary>中文：切到 SN 模式。 English: Switch to SN mode.</summary>
    SwitchToSn,

    /// <summary>中文：切到 SKU 模式。 English: Switch to SKU mode.</summary>
    SwitchToSku,

    /// <summary>
    /// 中文：带本程序的前缀，但命令认不出来。见文件头：不转发、要报出来。
    /// English: Carries the prefix but the command is unrecognized. See the file header: not
    ///          forwarded, and surfaced.
    /// </summary>
    Unknown,
}

/// <summary>
/// 中文：命令条码的识别。纯函数，没有状态。
/// English: Recognizing command barcodes. Pure, stateless.
/// </summary>
public static class ScanCommand
{
    /// <summary>
    /// 中文：命令条码的前缀。见文件头关于"不可能撞上"的说明。
    /// English: The command prefix. See the file header on why it cannot collide.
    /// </summary>
    public const string Prefix = "#SH:";

    /// <summary>中文：切到 SN 的那张条码的内容。 English: The content of the SN sheet.</summary>
    public const string SwitchToSnCode = "#SH:SN#";

    /// <summary>中文：切到 SKU 的那张条码的内容。 English: The content of the SKU sheet.</summary>
    public const string SwitchToSkuCode = "#SH:SKU#";

    /// <summary>
    /// 中文：
    ///   认一枚条码。
    ///
    ///   ★ 忽略大小写。这不是宽容，是**必需**：不少扫码枪有"输出一律转大写"
    ///     的出厂设置或配置项，开着的时候我们收到的是 #SH:SKU# 还是 #sh:sku#
    ///     由那台枪决定，而不由我们决定。区分大小写会让同一张纸在 A 工位有效、
    ///     在 B 工位失效，而现场根本无从判断为什么。
    ///
    ///   ★ 去掉首尾空白。有些枪会在内容后面补一个空格再发终止符。
    /// English:
    ///   Recognizes one code.
    ///
    ///   Case is ignored, which is necessary rather than lenient: many scanners have a "force
    ///   uppercase output" setting, and while it is on, whether we receive #SH:SKU# or #sh:sku# is
    ///   decided by that scanner rather than by us. Matching case would make one printed sheet work
    ///   at one station and fail at the next, with nothing on site to explain why.
    ///
    ///   Leading and trailing whitespace is trimmed: some scanners pad the content with a space
    ///   before the terminator.
    /// </summary>
    public static ScanCommandKind Recognize(string? rawCode)
    {
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            return ScanCommandKind.None;
        }

        var code = rawCode.Trim();

        if (!code.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return ScanCommandKind.None;
        }

        if (code.Equals(SwitchToSnCode, StringComparison.OrdinalIgnoreCase))
        {
            return ScanCommandKind.SwitchToSn;
        }

        if (code.Equals(SwitchToSkuCode, StringComparison.OrdinalIgnoreCase))
        {
            return ScanCommandKind.SwitchToSku;
        }

        return ScanCommandKind.Unknown;
    }
}
