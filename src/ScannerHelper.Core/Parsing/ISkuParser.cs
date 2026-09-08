// =============================================================================
// ISkuParser.cs
//
// 中文：
//   SKU 解析器的统一契约。V1 有两个实现：固定位置与正则（规格 §8）。
//
//   实现必须是**纯粹且确定性**的（规格 §17）：同样的输入永远得到同样的输出，
//   不碰 UI，不碰设备，不碰 SendInput，不读时钟，不写文件。这条约束使得解析
//   规则可以被完整地单元测试，也使设置页的"规则测试"区域能在不接触业务软件
//   的前提下验证一条规则（规格 §13.2）。
//
//   契约中的两条硬性约定：
//     - 对任何非 null 输入都不得抛异常。预期的坏输入必须表现为
//       ParseResult.Failure。解析发生在扫描处理链路上，一次未捕获的异常
//       意味着工人扫了码却毫无反应（规格 §19）。
//     - rawCode 为 null 时抛 ArgumentNullException（决策 D-1）。null 只可能是
//       调用方缺陷，不是预期的坏输入。
//
// English:
//   The contract for SKU parsers. V1 has two implementations: fixed position and
//   regex (spec §8).
//
//   Implementations must be pure and deterministic (spec §17): same input, same
//   output; no UI, no devices, no SendInput, no clock, no files. This is what
//   makes parsing rules fully unit-testable, and what lets the Settings page test
//   a rule without touching the business application (spec §13.2).
//
//   Two hard terms of the contract:
//     - Never throw for any non-null input. Expected bad input must appear as
//       ParseResult.Failure. Parsing sits on the scan-handling path, where an
//       uncaught exception means the worker scans and nothing happens (spec §19).
//     - Throw ArgumentNullException when rawCode is null (decision D-1). null can
//       only mean a caller defect, not expected bad input.
//
// 包含的类型 / Types in this file:
//   ISkuParser
// =============================================================================

using ScannerHelper.Core.Domain;

namespace ScannerHelper.Core.Parsing;

/// <summary>
/// 中文：从原始条码中提取 SKU。
/// English: Extracts an SKU from a raw scanned code.
/// </summary>
public interface ISkuParser
{
    /// <summary>
    /// 中文：
    ///   按本解析器配置的规则从原始条码中提取 SKU。
    ///   输入：rawCode 扫码枪送来的完整原始字符串，不得为 null。
    ///   输出：<see cref="ParseResult.Success"/> 携带提取结果，或
    ///         <see cref="ParseResult.Failure"/> 携带原因码与原始码。
    ///   不校验提取结果是否合理——那是校验层的职责（规格 §9）。
    /// English:
    ///   Extracts an SKU using this parser's configured rule.
    ///   Input: the complete raw string from the scanner; never null.
    ///   Output: Success with the extracted value, or Failure with a reason code
    ///   and the raw code. Whether the value is plausible is not checked here;
    ///   that is the validation layer's job (spec §9).
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：rawCode 为 null 时抛出。 English: Thrown when rawCode is null.
    /// </exception>
    ParseResult Parse(string rawCode);
}
