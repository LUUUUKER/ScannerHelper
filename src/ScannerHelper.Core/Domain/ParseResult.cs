// =============================================================================
// ParseResult.cs
//
// 中文：
//   解析结果。成功时携带解析出的 SKU，失败时携带原因码和原始条码。
//
//   这是一个**封闭的判别式类型**，对应决策 D-7：失败结果的值必须在编译期
//   就不可达，而不是等到运行时抛异常。
//
//   实现手法：抽象基类型持有一个 private 构造函数，只有嵌套在其内部的类型
//   才能访问它。因此外部程序集无法新增第三个分支——分支集合被彻底封闭，
//   将来对 ParseResult 做模式匹配时，Success 与 Failure 就是全部可能。
//
//   为什么值不可达比抛异常好：
//     Failure 上根本没有 Sku 属性。想在失败分支里读取 SKU，代码连编译都
//     通不过。抛异常的方案要等到运行时、而且往往是在仓库现场才暴露；
//     这里的错误在开发者按下保存的那一刻就已经被拒绝了。
//
//   失败结果必须保留原始条码（测试计划 D3）。规格 §10 要求 F10 强制发送时
//   原样发出扫到的码，错误界面也要展示它。若失败时把原始码丢掉，这两件事
//   都做不到，工人就只能重扫一次。
//
// English:
//   The result of a parse: an SKU on success, a reason code plus the raw code on
//   failure.
//
//   This is a *closed* discriminated type, implementing decision D-7: the value
//   of a failed result must be unreachable at compile time rather than throwing
//   at runtime.
//
//   Mechanism: the abstract base holds a private constructor that only nested
//   types can reach, so no outside assembly can add a third case. The set of
//   cases is sealed — when pattern matching, Success and Failure are exhaustive.
//
//   Why unreachable beats throwing: Failure simply has no Sku property, so code
//   that reads an SKU from the failure branch does not compile. A throwing design
//   surfaces the same mistake at runtime, often on the warehouse floor; this one
//   is rejected the moment the developer saves the file.
//
//   A failure must retain the raw code (test plan D3). Spec §10 requires Force
//   Send to emit exactly what was scanned, and the error UI to display it.
//   Discarding it would make both impossible and force the worker to rescan.
//
// 包含的类型 / Types in this file:
//   ParseResult          抽象基类型，构造函数私有，分支集合封闭
//   ParseResult.Success  成功，携带 Sku
//   ParseResult.Failure  失败，携带 Reason 与 RawCode
// =============================================================================

namespace ScannerHelper.Core.Domain;

/// <summary>
/// 中文：解析结果。只可能是 <see cref="Success"/> 或 <see cref="Failure"/> 之一。
/// English: A parse result. Always exactly one of <see cref="Success"/> or
///          <see cref="Failure"/>.
/// </summary>
public abstract record ParseResult
{
    /// <summary>
    /// 中文：私有构造函数。仅嵌套类型可访问，从而把分支集合封闭在本文件内，
    ///       外部无法派生出第三种结果。
    /// English: Private constructor, reachable only by nested types. This seals
    ///          the set of cases to this file; no third case can be derived.
    /// </summary>
    private ParseResult()
    {
    }

    /// <summary>
    /// 中文：解析成功。
    ///       Sku 为按规则提取出的结果，尚未经过校验——校验是下一层的职责
    ///       （规格 §9）。因此这里的 Sku 可能是空串或不符合业务约束的内容，
    ///       那不是解析器该判断的事。
    /// English: A successful parse.
    ///          Sku is what the rule extracted; it has not been validated —
    ///          validation is the next layer (spec §9). The value may therefore be
    ///          empty or otherwise implausible, which is not the parser's call.
    /// </summary>
    /// <param name="Sku">中文：提取出的 SKU。 English: The extracted SKU.</param>
    public sealed record Success(string Sku) : ParseResult;

    /// <summary>
    /// 中文：解析失败。
    /// English: A failed parse.
    /// </summary>
    /// <param name="Reason">
    /// 中文：失败原因码。UI 层负责映射为对应语言的文案。
    /// English: The reason code; the UI maps it to localized text.
    /// </param>
    /// <param name="RawCode">
    /// 中文：导致失败的原始条码，供 F10 强制发送与错误界面展示使用。
    /// English: The raw code that failed, used by Force Send and the error UI.
    /// </param>
    public sealed record Failure(ParseFailureReason Reason, string RawCode) : ParseResult;
}
