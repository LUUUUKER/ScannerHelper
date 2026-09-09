// =============================================================================
// ISettingsStore.cs
//
// 中文：
//   配置的读写契约（规格 §14）。
//
//   两条硬性约定：
//     Load 永不抛异常，也永不返回 null。配置读不出来不该让程序起不来——
//     工人面对一个打不开的程序毫无办法，而用默认值至少 SN 模式还能干活。
//     无法使用的文件会被备份，并通过 SettingsRecovered 事件留下痕迹。
//
//     Save 失败时**必须抛异常**。这里和 Load 的取向相反，因为后果不同：
//     读失败可以退回默认值继续跑，写失败若被吞掉，工人会以为设置保存好了，
//     直到下次启动才发现全没变——而那时已经无从判断是哪一步出的问题。
//
// English:
//   The contract for reading and writing settings (spec §14).
//
//   Two hard terms:
//
//   Load never throws and never returns null. An unreadable config must not prevent
//   startup — an operator faced with an application that will not open has no
//   recourse, whereas defaults at least leave SN mode working. An unusable file is
//   backed up and reported through SettingsRecovered.
//
//   Save must throw on failure. The opposite stance, because the consequence
//   differs: a failed read can fall back and carry on, but a swallowed write failure
//   leaves the operator believing their settings were saved until the next launch
//   proves otherwise — by which point there is no way to tell which step failed.
//
// 包含的类型 / Types in this file:
//   ISettingsStore
// =============================================================================

namespace ScannerHelper.Core.Settings;

/// <summary>
/// 中文：读写用户配置。
/// English: Reads and writes user settings.
/// </summary>
public interface ISettingsStore
{
    /// <summary>
    /// 中文：当一份无法使用的配置被备份、并以默认值替代时触发（规格 §15）。
    ///       正常读取不会触发本事件。
    /// English: Raised when an unusable file has been backed up and replaced by
    ///          defaults (spec §15). A normal load raises nothing.
    /// </summary>
    event EventHandler<SettingsRecoveredEventArgs>? SettingsRecovered;

    /// <summary>
    /// 中文：
    ///   读取配置。
    ///   输入：无。输出：一份可用的配置，永不为 null。
    ///   文件不存在时返回默认值，且**不创建文件**——首次启动是正常情形，
    ///   不是错误，读取也不该产生写入副作用。
    ///   文件无法使用时备份原文件、返回默认值，并触发
    ///   <see cref="SettingsRecovered"/>。
    /// English:
    ///   Loads settings, always returning something usable and never null.
    ///   A missing file yields defaults and creates nothing — a first launch is
    ///   normal rather than an error, and reading should not cause a write.
    ///   An unusable file is backed up, defaults are returned, and
    ///   <see cref="SettingsRecovered"/> is raised.
    /// </summary>
    AppSettings Load();

    /// <summary>
    /// 中文：
    ///   保存配置。
    ///   输入：settings 待保存的配置，不得为 null。输出：无。
    ///   采用原子语义：先写临时文件，再移动覆盖目标。因此保存失败时，
    ///   原有配置文件完好无损，绝不会出现"既没写成新的又毁了旧的"。
    ///   失败以异常形式抛出，不静默。
    /// English:
    ///   Saves settings with atomic semantics: write a temp file, then move it over
    ///   the target. A failed save therefore leaves the previous file intact and
    ///   never both fails to write the new one and destroys the old. Failures throw
    ///   rather than pass silently.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// 中文：settings 为 null。 English: settings is null.
    /// </exception>
    void Save(AppSettings settings);
}
