// =============================================================================
// CharacterSetPreset.cs
//
// 中文：
//   字符集校验的预设（规格 §9.2）。
//
//   预设决定**允许哪几类字符**；字母的大小写算不算数由独立的「忽略大小写」
//   开关决定，不在本枚举内。两者刻意分开，使"允许什么"和"大小写敏不敏感"
//   可以各自变化，而不必为每种组合再造一个预设。
//
//   全部预设都只接受 **ASCII** 字符。这一点必须由实现显式保证，不能依赖
//   char.IsLetter / char.IsDigit——那两个方法会放行 Unicode 字母和数字，
//   例如汉字 '中' 会被 char.IsLetter 判为字母、阿拉伯数字 '٣' 会被
//   char.IsDigit 判为数字。仓库条码是 ASCII 的，非 ASCII 字符出现在 SKU 里
//   本身就是异常信号，必须被校验层拦下而不是悄悄放行。
//
//   本枚举没有 None 成员。"未启用" 由可空类型表达（CharacterSetPreset?），
//   与长度校验器用 int? 表达未启用保持一致，避免在枚举里塞一个语义上
//   "什么都不做" 的值。
//
// English:
//   Presets for character-set validation (spec §9.2).
//
//   A preset decides which *kinds* of character are allowed; whether letter case
//   matters is the separate "ignore case" toggle and is deliberately not encoded
//   here, so the two can vary independently without a preset per combination.
//
//   Every preset accepts ASCII only, and the implementation must enforce that
//   explicitly rather than relying on char.IsLetter / char.IsDigit — those admit
//   Unicode letters and digits, so '中' counts as a letter and '٣' as a digit.
//   Warehouse barcodes are ASCII, and a non-ASCII character in an SKU is itself
//   an anomaly the validation layer must catch rather than quietly accept.
//
//   There is no None member: "not enabled" is expressed by a nullable
//   CharacterSetPreset?, matching how the length validator uses int?, rather than
//   adding an enum value that means "do nothing".
//
// 包含的类型 / Types in this file:
//   CharacterSetPreset
// =============================================================================

namespace ScannerHelper.Core.Domain;

/// <summary>
/// 中文：允许的字符类别。所有预设均限定为 ASCII。
/// English: Which kinds of character are allowed. All presets are ASCII-only.
/// </summary>
public enum CharacterSetPreset
{
    /// <summary>
    /// 中文：仅数字 <c>0-9</c>。
    /// English: Digits <c>0-9</c> only.
    /// </summary>
    Numbers,

    /// <summary>
    /// 中文：仅字母。忽略大小写时为 <c>A-Z</c> 与 <c>a-z</c>，
    ///       区分大小写时仅 <c>A-Z</c>。
    /// English: Letters only — <c>A-Z</c> and <c>a-z</c> when case is ignored,
    ///          <c>A-Z</c> alone when it is not.
    /// </summary>
    Letters,

    /// <summary>
    /// 中文：字母加数字。
    /// English: Letters and digits.
    /// </summary>
    LettersNumbers,

    /// <summary>
    /// 中文：字母、数字，以及连字符 <c>-</c> 和下划线 <c>_</c>。
    /// English: Letters, digits, hyphen <c>-</c> and underscore <c>_</c>.
    /// </summary>
    LettersNumbersDashUnderscore,
}
