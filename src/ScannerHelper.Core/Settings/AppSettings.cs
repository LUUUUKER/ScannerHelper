// =============================================================================
// AppSettings.cs
//
// 中文：
//   全部用户偏好的根对象（规格 §14）。
//
//   ★ 本类里**没有**当前扫描模式，也**没有**暂停状态。这不是遗漏。
//
//     规格 §3 要求启动恒为 SN、§5.7 要求启动恒为未暂停。这两条是安全要求
//     而非偏好：工人每次启动看到的必须是同一个已知状态。如果上次退出时停在
//     SKU 模式、这次启动还是 SKU，工人扫第一枪时并不知道自己处在哪个模式，
//     错误数据就是这么产生的。
//
//     "帮工人记住上次用的模式" 是一个听起来非常合理的改进，实现起来只要在
//     这里加一个字段。测试 S11、S12、S13 与 M7 一起把这条路封死了。要改这个
//     行为，得先改规格。
//
//   默认值的总体取向是"出厂即可用、出错也安全"：
//     声音默认开        工人需要听得见模式切换与错误，静音应当由他主动选择
//     记住窗口位置默认开 换班的工人希望窗口还在原处
//     主窗口置顶默认关   默认不遮挡业务软件（Compact 的置顶是强制的，
//                        与此项无关，见规格 §11.4）
//     开机启动默认关     装完就自动跑，是对使用者环境的擅自决定
//
//   SchemaVersion 从 V1 就带上。将来配置格式变化时，迁移代码需要知道手里
//   这份文件是哪个版本的产物；等到需要迁移时才加，就已经有一批没有版本号
//   的文件散落在各台机器上了。
//
//   ★ 几个**不可为空**的分组（Hotkeys、SkuParsing、SkuValidation、Diagnostics）
//     的 setter 都会把 null 折成一个默认实例，而不是原样存下。
//
//     这不是防御性编程的惯性，而是堵一个真实的洞：.NET 8 的
//     System.Text.Json **完全忽略可空性标注**（RespectNullableAnnotations 要到
//     .NET 9 才有）。因此一份内容为
//         {"SchemaVersion":1,"Hotkeys":null}
//     的配置能被正常反序列化，属性初始化器建好的实例会被 null 覆盖，
//     Load 于是返回一个"看起来正常、其实半空"的对象——而 ISettingsStore
//     承诺的是"永不失败、永远返回可用配置"。到 Phase B 才会以
//     settings.Hotkeys.ToggleMode 的空引用异常爆出来，表现为工位起不来。
//
//     按测试 S26 的既定精神（缺字段取默认值），显式的 null 与"字段不存在"
//     是同一件事，都该退回默认值。把这条不变式放在 setter 上而不是放在
//     JsonSettingsStore 里，是因为前者对**任何**赋值路径都成立——将来换一个
//     序列化器、或者有人手写 new AppSettings { Hotkeys = null! }，都无法绕开。
//
//     可空的三项（ScannerBinding、FullWindowBounds、CompactWindowBounds）
//     不做这个处理：那里的 null 是有意义的取值，表示"尚未绑定""尚无记录"。
//
// English:
//   Root object for all user preferences (spec §14).
//
//   This type deliberately has no current scan mode and no paused state.
//
//   Spec §3 requires every launch to start in SN and §5.7 un-paused. These are
//   safety requirements, not preferences: the operator must meet the same known
//   state at every launch. A session that ended in SKU mode reopening in SKU mode
//   would put the first scan of the day into an unknown mode — exactly how wrong
//   data gets written.
//
//   "Remember the operator's last mode" is a very reasonable-sounding improvement
//   that needs only one field here. Tests S11, S12, S13 and M7 close that path
//   together. Changing the behavior means changing the spec first.
//
//   Defaults lean toward "usable out of the box, safe when wrong": sounds on, window
//   position remembered, main window not topmost (Compact's topmost is mandatory and
//   unrelated, spec §11.4), and no auto-start on install.
//
//   SchemaVersion ships from V1. When the format eventually changes, migration code
//   must know which version a file came from; adding the field only once migration
//   is needed leaves a population of unversioned files already on the machines.
//
//   The four non-nullable sections (Hotkeys, SkuParsing, SkuValidation, Diagnostics)
//   fold a null assignment into a default instance rather than storing it. This is
//   not defensive habit but a real hole: .NET 8's System.Text.Json ignores
//   nullability annotations entirely (RespectNullableAnnotations arrives in .NET 9),
//   so {"SchemaVersion":1,"Hotkeys":null} deserializes happily and overwrites the
//   property initializer with null. Load would then return an object that looks fine
//   and is half empty, against ISettingsStore's promise of always returning something
//   usable — surfacing in Phase B as a null reference on settings.Hotkeys.ToggleMode,
//   which means the station does not start.
//
//   Per the established spirit of test S26 (a missing field takes its default), an
//   explicit null and an absent field are the same thing. The invariant lives on the
//   setter rather than in JsonSettingsStore because it then holds for *every*
//   assignment path — a different serializer later, or a hand-written
//   new AppSettings { Hotkeys = null! }, cannot route around it.
//
//   The three nullable members (ScannerBinding, FullWindowBounds, CompactWindowBounds)
//   are left alone: null is a meaningful value there, meaning "nothing bound yet" and
//   "nothing recorded yet".
//
// 包含的类型 / Types in this file:
//   AppSettings
// =============================================================================

namespace ScannerHelper.Core.Settings;

/// <summary>
/// 中文：全部用户偏好。绝不包含当前扫描模式与暂停状态。
/// English: All user preferences. Never contains the current scan mode or the
///          paused state.
/// </summary>
public sealed class AppSettings
{
    private HotkeySettings _hotkeys = new();
    private SerialPortSettings _serialPort = new();
    private SkuParsingSettings _skuParsing = new();
    private SkuValidationSettings _skuValidation = new();
    private DiagnosticsSettings _diagnostics = new();

    /// <summary>
    /// 中文：配置结构版本，从 V1 起为 1。用于将来的格式迁移。
    ///       读到比程序更高的版本按损坏处理（决策 D-8）——猜测未来格式会
    ///       静默丢弃配置。
    /// English: Configuration schema version, 1 from V1, used for future migration.
    ///          A version newer than the application is treated as corruption
    ///          (decision D-8) — guessing at a future format silently discards
    ///          configuration.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// 中文：界面语言，null 表示**用户尚未选择过**（规格 §12）。
    ///
    ///       默认不写成具体语言，是为了区分"用户选了英文"和"用户还没选过"：
    ///       前者应当保持英文，后者应当跟随 Windows 界面语言（中文系统用中文，
    ///       其余用英文）。若默认值直接填 "en-US"，这两种情形就再也分不开了。
    /// English: UI language; null means the user has not chosen yet (spec §12).
    ///
    ///          A concrete default would make "the user chose English"
    ///          indistinguishable from "the user has not chosen" — the first must
    ///          stay English, the second must follow the Windows UI culture.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// 中文：是否随 Windows 启动，默认关闭。
    /// English: Start with Windows; off by default.
    /// </summary>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// 中文：
    ///   模式切换提示音，**默认关闭**（决策 D-28）。
    ///   切到 SN 是低音，切到 SKU 是高音，各响一声。
    ///
    ///   ★ 规格 §7 原本写的是"默认出声"，D-28 改成默认关闭。
    ///
    ///     理由来自实际使用：仓库里常年有别的声音，而一个每次切模式都响的程序，
    ///     在工人自己没要求的情况下就是噪声——被无视的提示音等于没有提示音，
    ///     还顺带让人对这个程序发出的**所有**声音都变得不敏感，包括真正要紧的
    ///     那一个（出错）。
    ///
    ///     默认安静、要用的人自己打开，比默认吵闹、嫌吵的人自己去关，更能让
    ///     "响了"这件事保持分量。
    /// English:
    ///   The mode-change sound, off by default (decision D-28). One tone: low into SN, high into
    ///   SKU.
    ///
    ///   Spec §7 originally had it on by default; D-28 turns it off. The reason comes from use: a
    ///   warehouse is never quiet, and a program that beeps on every mode change without being
    ///   asked is noise — an ignored sound is no sound at all, and it also dulls the operator to
    ///   every sound this program makes, including the one that matters (an error).
    ///
    ///   Silent by default with the people who want it turning it on keeps "it beeped" meaningful,
    ///   in a way that loud by default with the annoyed turning it off does not.
    /// </summary>
    public bool ModeSwitchSoundEnabled { get; set; }

    /// <summary>
    /// 中文：
    ///   错误提示音，**默认关闭**（决策 D-28）。
    ///
    ///   ★ 它和模式音一起默认关掉，理由是同一个：默认安静。
    ///
    ///     但两者的分量并不相同——出错时工人可能正低头看货，屏幕上的红色面板
    ///     他看不到。所以设置界面里这一条应当排在模式音后面并单独成项，让想开
    ///     声音的人可以只开这一个：**要紧的那一声，不该被一堆不要紧的淹掉。**
    ///
    ///   声音关着并不影响告知：出错时整块面板变红、带白色斜条纹、Compact 会自动
    ///   展开成 Full（规格 §11.7）。声音是辅助通道，不是唯一通道。
    /// English:
    ///   The error sound, off by default (decision D-28), for the same reason as the mode sound.
    ///
    ///   The two do not carry equal weight, though: during an error the operator may be looking at
    ///   goods and cannot see the red panel. So the settings list them separately with this one
    ///   after the mode sound, letting somebody enable only this — the sound that matters should
    ///   not be drowned out by the ones that do not.
    ///
    ///   Being off does not weaken the notification: the panel turns red with a hazard stripe and
    ///   Compact expands to Full (spec §11.7). Sound is the secondary channel, not the only one.
    /// </summary>
    public bool ErrorSoundEnabled { get; set; }

    /// <summary>
    /// 中文：是否记住窗口位置，默认开启（规格 §11.5）。
    /// English: Remember window position; on by default (spec §11.5).
    /// </summary>
    public bool RememberWindowPosition { get; set; } = true;

    /// <summary>
    /// 中文：扫描后是否自动追加回车，**默认关闭**（决策 D-10、规格 §10）。
    ///
    ///       扫码枪自带的回车始终会被吞掉，因此默认下网页收到的回车数为 0，
    ///       而不是"少了一个"。该页面是否依赖回车提交，在拿到真实页面前
    ///       无法确定。做成设置项使这个答案能在试点当天现场切换，而不必
    ///       改代码重新部署。
    /// English: Append Enter after a scan; **off by default** (decision D-10,
    ///          spec §10).
    ///
    ///          The scanner's own Enter is always swallowed, so by default the page
    ///          receives none at all rather than one fewer. Whether that page needs
    ///          one cannot be known before the pilot; a setting makes the answer
    ///          switchable on the floor instead of requiring a rebuild.
    /// </summary>
    public bool AppendEnterAfterScan { get; set; }

    /// <summary>
    /// 中文：主窗口置顶偏好，默认关闭（规格 §11.4）。
    ///       Compact 窗口的置顶是**强制**的，不受此项控制。
    /// English: Main-window always-on-top preference; off by default (spec §11.4).
    ///          Compact's topmost is mandatory and not governed by this setting.
    /// </summary>
    public bool FullWindowAlwaysOnTop { get; set; }

    /// <summary>
    /// 中文：主窗口的位置与尺寸，null 表示尚无记录（首次启动）。
    /// English: Main-window bounds; null when nothing has been recorded yet.
    /// </summary>
    public WindowBounds? FullWindowBounds { get; set; }

    /// <summary>
    /// 中文：Compact 窗口的位置与尺寸，null 表示尚无记录。
    /// English: Compact-window bounds; null when nothing has been recorded yet.
    /// </summary>
    public WindowBounds? CompactWindowBounds { get; set; }

    /// <summary>
    /// 中文：全局控制热键（规格 §13.3）。赋 null 会被折成默认实例，见文件头说明。
    /// English: The global control hotkeys (spec §13.3). Assigning null folds into a
    ///          default instance; see the file header.
    /// </summary>
    public HotkeySettings Hotkeys
    {
        get => _hotkeys;
        set => _hotkeys = value ?? new HotkeySettings();
    }

    /// <summary>
    /// 中文：已绑定的扫码枪身份，null 表示尚未绑定（规格 §6）。
    /// English: The bound scanner's identity; null when nothing is bound (spec §6).
    /// </summary>
    public ScannerBindingSettings? ScannerBinding { get; set; }

    /// <summary>
    /// 中文：
    ///   扫码枪串口的连接参数（决策 D-27）。
    ///
    ///   ★ 与 <see cref="ScannerBinding"/> 是两件事，不要合并：这里是「怎么跟它说话」
    ///     （波特率之类），那里是「它是谁」（VID/PID、序列号）。端口号会随插到哪个
    ///     USB 口而变，所以重连要靠身份去找端口，而不是记住端口号
    ///     （ARCHITECTURE_CHANGE_SERIAL.md §5.2）。
    /// English:
    ///   The scanner port's connection parameters (decision D-27).
    ///
    ///   A separate concern from <see cref="ScannerBinding"/> and not to be merged: this is how to
    ///   talk to the device (baud rate and so on), that is who the device is (VID/PID, serial).
    ///   Port numbers change with the USB socket used, so reconnection resolves the port from the
    ///   identity rather than remembering a number (ARCHITECTURE_CHANGE_SERIAL.md §5.2).
    /// </summary>
    public SerialPortSettings SerialPort
    {
        get => _serialPort;
        set => _serialPort = value ?? new SerialPortSettings();
    }

    /// <summary>
    /// 中文：SKU 解析规则（规格 §8）。赋 null 会被折成默认实例，见文件头说明。
    /// English: SKU parsing configuration (spec §8). Assigning null folds into a
    ///          default instance; see the file header.
    /// </summary>
    public SkuParsingSettings SkuParsing
    {
        get => _skuParsing;
        set => _skuParsing = value ?? new SkuParsingSettings();
    }

    /// <summary>
    /// 中文：SKU 校验规则（规格 §9）。赋 null 会被折成默认实例，见文件头说明。
    /// English: SKU validation configuration (spec §9). Assigning null folds into a
    ///          default instance; see the file header.
    /// </summary>
    public SkuValidationSettings SkuValidation
    {
        get => _skuValidation;
        set => _skuValidation = value ?? new SkuValidationSettings();
    }

    /// <summary>
    /// 中文：诊断日志配置（规格 §15）。赋 null 会被折成默认实例，见文件头说明。
    /// English: Diagnostic logging configuration (spec §15). Assigning null folds into
    ///          a default instance; see the file header.
    /// </summary>
    public DiagnosticsSettings Diagnostics
    {
        get => _diagnostics;
        set => _diagnostics = value ?? new DiagnosticsSettings();
    }
}
