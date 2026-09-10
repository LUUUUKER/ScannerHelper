// =============================================================================
// Code128.cs
//
// 中文：
//   把一段文字编成 Code 128 条码的模块序列（黑白条的宽度）。
//
//   ★ 为什么自己写，而不是引一个库。
//
//     规格 §22.1 说仓库 IT 按哈希把可执行文件加进白名单，依赖越少发布件越容易
//     解释。而这里需要的只是 Code 128 里最基础的一条路径——B 字符集、印两张
//     固定内容的纸——大约一张表加二十行代码，换一个第三方依赖不划算。
//
//   ★ 为什么放在 Core 而不是界面层。
//
//     校验位算错的条码不会报错，它只是**扫不出来**——而"扫不出来"在现场会被
//     归因到枪、到光线、到纸张，唯独不会有人怀疑是校验位。这种错必须由单元
//     测试钉住，而 Core 是唯一能在任何机器上跑测试的地方。
//
//   ★ 只做 B 字符集。
//
//     命令条码的内容是我们自己定的（#SH:SN# / #SH:SKU#），全部落在可打印
//     ASCII 里。C 字符集能把纯数字压缩一半，但这里没有纯数字；A 字符集要处理
//     控制字符，这里也没有。多做的每一种都是没有使用者的代码。
//
// English:
//   Encodes text into a Code 128 barcode's module sequence (the widths of the bars and spaces).
//
//   Written here rather than pulled from a library because spec §22.1 has warehouse IT allow-listing
//   the executable by hash, and fewer dependencies make the artifact easier to explain — while what
//   is needed is Code 128's most basic path: character set B, printing two sheets of fixed content.
//   That is one table and about twenty lines, a poor trade for a third-party dependency.
//
//   It lives in Core rather than the UI layer because a wrong check digit raises no error; the
//   barcode simply does not scan, and on site that gets blamed on the scanner, the lighting or the
//   paper, never on a check digit. That kind of mistake has to be held by a unit test, and Core is
//   the only place tests run on any machine.
//
//   Only character set B is implemented. The command barcodes' content is ours (#SH:SN# / #SH:SKU#)
//   and is entirely printable ASCII. Set C halves the width of pure digits, of which there are none
//   here, and set A handles control characters, of which there are likewise none; each addition
//   would be code with no consumer.
//
// 包含的类型 / Types in this file:
//   Code128
// =============================================================================

namespace ScannerHelper.Core.Printing;

/// <summary>
/// 中文：Code 128（B 字符集）编码。
/// English: Code 128 encoding, character set B.
/// </summary>
public static class Code128
{
    /// <summary>
    /// 中文：
    ///   107 个符号各自的模块宽度。每个字符串从**黑条**开始，黑白交替。
    ///   索引 0~102 是数据符号，103/104/105 是三种起始符，106 是终止符。
    /// English:
    ///   The module widths of all 107 symbols. Each string starts with a bar and alternates.
    ///   Indices 0-102 are data symbols, 103/104/105 the three start symbols, 106 the stop symbol.
    /// </summary>
    private static readonly string[] Patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312",
        "132212", "221213", "221312", "231212", "112232", "122132", "122231", "113222",
        "123122", "123221", "223211", "221132", "221231", "213212", "223112", "312131",
        "311222", "321122", "321221", "312212", "322112", "322211", "212123", "212321",
        "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121",
        "313121", "211331", "231131", "213113", "213311", "213131", "311123", "311321",
        "331121", "312113", "312311", "332111", "314111", "221411", "431111", "111224",
        "111422", "121124", "121421", "141122", "141221", "112214", "112412", "122114",
        "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112",
        "421211", "212141", "214121", "412121", "111143", "111341", "131141", "114113",
        "114311", "411113", "411311", "113141", "114131", "311141", "411131", "211412",
        "211214", "211232", "2331112",
    ];

    /// <summary>中文：B 字符集的起始符。 English: The start symbol for set B.</summary>
    private const int StartB = 104;

    /// <summary>中文：终止符。 English: The stop symbol.</summary>
    private const int Stop = 106;

    /// <summary>
    /// 中文：
    ///   编码一段文字，返回模块宽度序列。序列从黑条开始，黑白交替。
    ///   输入：可打印 ASCII（空格到 ~）。
    ///
    ///   ★ 校验位是 Code 128 强制的，且算法是**加权**的：起始符的值加上每个
    ///     数据符号乘以它的位置，再对 103 取模。不是简单求和——写成求和的话，
    ///     交换两个字符的位置会得到同一个校验位，而那种条码在扫描时才会暴露。
    /// English:
    ///   Encodes text into a sequence of module widths, starting with a bar and alternating.
    ///   Input must be printable ASCII (space through ~).
    ///
    ///   The check symbol is mandatory in Code 128 and its algorithm is weighted: the start symbol's
    ///   value plus each data symbol times its position, modulo 103. Not a plain sum — written as
    ///   one, swapping two characters would yield the same check symbol, and such a barcode reveals
    ///   itself only when someone tries to scan it.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// 中文：文字里有 B 字符集表示不了的字符。
    /// English: The text contains a character set B cannot represent.
    /// </exception>
    public static IReadOnlyList<int> Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var symbols = new List<int> { StartB };
        var checksum = StartB;

        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];

            if (character < ' ' || character > '~')
            {
                throw new ArgumentException(
                    $"Code 128 B 表示不了字符 U+{(int)character:X4}。 " +
                    $"Code 128 set B cannot represent U+{(int)character:X4}.",
                    nameof(text));
            }

            var value = character - ' ';
            symbols.Add(value);
            checksum += value * (i + 1);
        }

        symbols.Add(checksum % 103);
        symbols.Add(Stop);

        var modules = new List<int>();

        foreach (var symbol in symbols)
        {
            foreach (var width in Patterns[symbol])
            {
                modules.Add(width - '0');
            }
        }

        return modules;
    }
}
