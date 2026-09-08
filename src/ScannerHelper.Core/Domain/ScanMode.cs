// =============================================================================
// ScanMode.cs
//
// 中文：
//   扫描模式枚举。这是整个产品最核心的领域概念——同一次扫码的输出内容，
//   完全由当前处于哪个模式决定。
//
//   刻意不给成员指定显式数值。规格 §14 要求当前模式绝不持久化，因此不存在
//   "旧配置文件里存着数字 1，新版本必须还认得它"这类兼容性负担。一旦有人
//   在这里补上 = 0 / = 1，往往意味着他正打算把模式序列化出去——那是规格
//   明确禁止的（相应的守卫见测试 M7）。
//
// English:
//   The scan mode. This is the product's central domain concept: what a single
//   scan emits is determined entirely by which mode is active.
//
//   Explicit numeric values are deliberately omitted. Spec §14 forbids
//   persisting the current mode, so there is no stored number that a future
//   version must keep recognizing. Someone adding = 0 / = 1 here is usually
//   about to serialize the mode — which the spec prohibits (guarded by test M7).
//
// 包含的类型 / Types in this file:
//   ScanMode  SN / SKU 两个模式
// =============================================================================

namespace ScannerHelper.Core.Domain;

/// <summary>
/// 中文：扫描模式。启动恒为 <see cref="Sn"/>，且绝不跨重启保留（规格 §3、§7、§14）。
/// English: The scan mode. Always starts as <see cref="Sn"/> and is never
///          preserved across restarts (spec §3, §7, §14).
/// </summary>
public enum ScanMode
{
    /// <summary>
    /// 中文：SN 模式。原样输出扫描到的完整原始条码，不做任何提取或改写。
    /// English: SN mode. Emits the complete raw scanned code unchanged.
    /// </summary>
    Sn,

    /// <summary>
    /// 中文：SKU 模式。对同一段原始条码先解析出 SKU，再校验，最后输出。
    /// English: SKU mode. Parses an SKU out of the same raw code, validates it,
    ///          then emits it.
    /// </summary>
    Sku,
}
