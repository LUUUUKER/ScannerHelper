// =============================================================================
// SettingsRecovered.cs
//
// 中文：
//   配置文件无法使用时的恢复事件（规格 §14、§15）。
//
//   配置坏了有三种可能的应对，本项目选了第三种：
//     拒绝启动    整个工位停摆。工人不会修配置文件，只能等人来
//     静默用默认值 程序能跑，但工人会发现规则莫名其妙失效了却查不出原因
//     备份后用默认值并留痕  ← 本项目
//
//   最后一种同时满足三件事：原配置被保住（技术顾问配的规则不会永久销毁）、
//   程序继续能跑（至少 SN 模式可用）、事件留下痕迹（排查时能定位到时间点
//   和原因）。
//
//   事件里带上备份路径，是为了让诊断信息可直接行动——工程师看到日志就知道
//   去哪儿找原文件，而不必自己去猜备份的命名规则。
//
// English:
//   The recovery event raised when a settings file cannot be used (spec §14, §15).
//
//   Three responses to a broken config were possible and the third was chosen:
//   refuse to start (the station halts, and the operator cannot repair a config
//   file), silently fall back to defaults (it runs, but the operator finds rules
//   inexplicably not working with no way to find out why), or back up, fall back,
//   and leave a trace.
//
//   The last satisfies all three needs at once: the original survives so the
//   advisor's rules are not destroyed, the application keeps running so SN mode is
//   at least available, and the event leaves a trace so troubleshooting can locate
//   the moment and the cause.
//
//   The backup path travels with the event so the diagnostic is directly
//   actionable: an engineer reading the log knows where the original went without
//   having to guess the naming convention.
//
// 包含的类型 / Types in this file:
//   SettingsRecoveryReason      为什么这份配置无法使用
//   SettingsRecoveredEventArgs  恢复事件的内容
// =============================================================================

namespace ScannerHelper.Core.Settings;

/// <summary>
/// 中文：配置文件无法使用的原因。两个取值指向不同的排查方向。
/// English: Why a settings file could not be used. The two values point at
///          different investigations.
/// </summary>
public enum SettingsRecoveryReason
{
    /// <summary>
    /// 中文：文件内容无法解析——非法 JSON、空文件、内容为 null，或者虽是
    ///       合法 JSON 但结构不对。指向"文件被损坏了"，可能是磁盘问题、
    ///       写入过程被强制中断，或者有人手工编辑出了错。
    /// English: The content could not be parsed — invalid JSON, an empty file, a
    ///          null document, or well-formed JSON of the wrong shape. Points at a
    ///          damaged file: a disk problem, a write cut short, or a hand edit gone
    ///          wrong.
    /// </summary>
    Malformed,

    /// <summary>
    /// 中文：文件声明的结构版本高于本程序能理解的版本（决策 D-8）。
    ///       指向"这台机器装过更新的版本又回滚了"，不是文件损坏。
    ///
    ///       按损坏处理而不是尝试解读：猜测未来格式会静默丢弃配置项，
    ///       而用户往往要过很久才发现某个设置莫名恢复了默认。备份保住原文件，
    ///       用户升回新版本后还能手工恢复。
    /// English: The file declares a schema version newer than this build understands
    ///          (decision D-8). Points at an upgrade followed by a rollback, not at
    ///          damage.
    ///
    ///          Treated as unusable rather than interpreted: guessing at a future
    ///          format silently discards settings, and the user typically notices
    ///          only much later that something reverted to its default. The backup
    ///          keeps the original recoverable once they upgrade again.
    /// </summary>
    UnsupportedSchemaVersion,
}

/// <summary>
/// 中文：一次配置恢复的详情。
/// English: The details of one settings recovery.
/// </summary>
/// <param name="BackupPath">
/// 中文：原配置被改名保存到的位置。文件内容原封不动，供人工恢复或事后分析。
/// English: Where the original was renamed to. Its content is untouched, available
///          for manual recovery or later analysis.
/// </param>
/// <param name="Reason">
/// 中文：这份配置为什么无法使用。
/// English: Why the file could not be used.
/// </param>
public sealed record SettingsRecoveredEventArgs(string BackupPath, SettingsRecoveryReason Reason);
