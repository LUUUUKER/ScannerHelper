// =============================================================================
// SkuParsingSettings.cs
//
// 中文：
//   SKU 解析规则的配置（规格 §8）。
//
//   V1 支持两种规则，同一时刻只有一种生效，由 RuleType 决定。两种规则各自
//   的字段都保留在配置里，不因为切换规则类型而清空——工人在设置页把规则类型
//   从固定位置切到正则、发现不合适再切回来时，原来的起止位置应当还在。
//   若切换即清空，每次试错都要重新输入一遍。
//
//   注意本类只**存放**配置，不校验其合法性。合法性由对应解析器的构造函数
//   负责（决策 D-2），因为那里才知道各字段的约束。设置页保存前应当先尝试
//   构造一次解析器，用是否抛异常来判断配置能否保存（规格 §13.4：非法配置
//   不能静默保存）。
//
// English:
//   Configuration for SKU parsing (spec §8).
//
//   V1 supports two rule kinds; exactly one is active, chosen by RuleType. Fields
//   for both are retained rather than cleared on switching: an operator who moves
//   from fixed position to regex, finds it unsuitable and switches back should find
//   their original positions intact. Clearing on switch would mean retyping them on
//   every experiment.
//
//   This type only *holds* configuration; it does not validate it. Validity is the
//   corresponding parser constructor's job (decision D-2), which is where the
//   constraints are known. The Settings page should attempt to construct a parser
//   before saving and use the throw as its verdict (spec §13.4: an invalid
//   configuration must not save silently).
//
// 包含的类型 / Types in this file:
//   SkuParsingRuleType   规则类型
//   SkuParsingSettings   规则配置
// =============================================================================

namespace ScannerHelper.Core.Settings;

/// <summary>
/// 中文：解析规则的类型（规格 §8）。
/// English: The kind of parsing rule (spec §8).
/// </summary>
public enum SkuParsingRuleType
{
    /// <summary>
    /// 中文：按固定位置截取（规格 §8.1）。
    /// English: Extract at a fixed position (spec §8.1).
    /// </summary>
    FixedPosition,

    /// <summary>
    /// 中文：按正则捕获组提取（规格 §8.2）。
    /// English: Extract via a regex capture group (spec §8.2).
    /// </summary>
    Regex,
}

/// <summary>
/// 中文：SKU 解析规则配置。
/// English: SKU parsing configuration.
/// </summary>
public sealed class SkuParsingSettings
{
    /// <summary>
    /// 中文：当前生效的规则类型。
    /// English: The active rule kind.
    /// </summary>
    public SkuParsingRuleType RuleType { get; set; } = SkuParsingRuleType.FixedPosition;

    /// <summary>
    /// 中文：固定位置规则的起始位置，**1-based**（规格 §8.1）。
    /// English: Fixed-position start, **1-based** (spec §8.1).
    /// </summary>
    public int StartPosition { get; set; } = 1;

    /// <summary>
    /// 中文：固定位置规则的截取长度。
    /// English: Fixed-position length.
    /// </summary>
    public int Length { get; set; } = 1;

    /// <summary>
    /// 中文：正则规则的模式。首次安装时为空，等待技术顾问提供。
    /// English: The regex pattern; empty on a fresh install, pending the advisor.
    /// </summary>
    public string? RegexPattern { get; set; }

    /// <summary>
    /// 中文：正则规则的捕获组索引。0 表示整个匹配（决策 D-3）。
    /// English: The capture group index; 0 means the whole match (decision D-3).
    /// </summary>
    public int CaptureGroupIndex { get; set; } = 1;
}
