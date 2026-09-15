// =============================================================================
// UiStrings.cs
//
// 中文：
//   两种语言的全部界面文字（规格 §12：英语 en-US 与简体中文 zh-CN）。
//
//   ★ 为什么用代码里的字典，而不是 .resx。
//
//     规格 §12 写的是"全部用户可见文字必须来自资源文件，**或等价的按语言组织
//     的方式**"，并且要求"切换语言立即生效、不重启"。WPF 里 .resx 的标准绑定
//     方式是 x:Static，而 x:Static 是一次性求值的——要做到立即生效，仍然要在
//     它之上再搭一层会通知变更的东西。既然那一层无论如何都要有，就让它直接
//     持有字典，少一层间接。
//
//     真正的要求是"视图和服务里不出现硬编码的中英文"，这一点由本文件是**唯一**
//     的文字来源来保证。
//
//   ★ 两张表的键必须完全一致，由构造时的自检钉住。
//
//     少一个键的表现是界面上出现一个键名（例如 "ModeSnSubtitle"），而它多半
//     只在另一种语言下、只在某个不常走的分支里出现——现场看到的是一个莫名其妙
//     的英文单词，而开发机上一切正常。让它在启动时就炸，比让工人在货架前看到
//     一个键名要好。
//
//   ★ SN 与 SKU 在两种语言里都不翻译（规格 §12「术语」）。
//
//     它们是仓库里天天说的词，翻译过来反而没人认得。
//
// English:
//   Every UI string in both languages (spec §12: en-US and zh-CN).
//
//   Dictionaries in code rather than .resx: spec §12 asks for resource files "or equivalent
//   culture-specific organization" and requires language changes to apply immediately without a
//   restart. WPF's standard .resx binding is x:Static, which evaluates once, so achieving live
//   switching needs a change-notifying layer on top regardless — and since that layer must exist,
//   letting it hold the dictionaries directly removes an indirection. The real requirement is that
//   no Chinese or English text is hardcoded in views or services, which holds because this file is
//   the single source of it.
//
//   The two tables must carry identical keys, checked at construction. A missing key shows up as a
//   key name on screen ("ModeSnSubtitle"), usually only in one language and only down some rarely
//   taken branch — the floor sees a nonsensical English word while the development machine looks
//   fine. Failing at startup beats an operator reading a key name in front of the racks.
//
//   SN and SKU stay untranslated in both languages (spec §12, Terminology): they are what the
//   warehouse says every day, and translating them would make them unrecognizable.
//
// 包含的类型 / Types in this file:
//   UiLanguage
//   UiStrings
// =============================================================================

namespace ScannerHelper.App.Localization;

/// <summary>
/// 中文：V1 支持的两种语言（规格 §12）。
/// English: The two languages V1 supports (spec §12).
/// </summary>
public enum UiLanguage
{
    /// <summary>中文：英语。 English: English (en-US).</summary>
    English,

    /// <summary>中文：简体中文。 English: Simplified Chinese (zh-CN).</summary>
    ChineseSimplified,
}

/// <summary>
/// 中文：两种语言的文字表。
/// English: The string tables for both languages.
/// </summary>
public static class UiStrings
{
    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["AppTitle"] = "Scanner Helper",

        ["ModeHeading"] = "CURRENT MODE",
        ["ModeSn"] = "SN MODE",
        ["ModeSku"] = "SKU MODE",
        ["ModeSnSubtitle"] = "The scanned code is sent as-is",
        ["ModeSkuSubtitle"] = "The SKU is extracted from the scanned code",
        // ★ 键名是参数，不写死。切换键现在可配置（见 HotkeySettings.ToggleMode），
        //   而一句写死"按 F8"的提示在改过键之后就是一句假话。
        // The key name is a parameter rather than literal text: the toggle is now configurable (see
        // HotkeySettings.ToggleMode), and a hardcoded "press F8" becomes a false statement the
        // moment somebody changes it.
        ["SwitchModeHint"] = "{0}   Switch mode",

        // ★ 键被别的程序占着时用这一句。界面绝不能一边说"按这个键"一边知道
        //   按了没用——规格 §19.1 不许显示没有验证过的东西，那既包括运行状态，
        //   也包括一句操作提示。
        // Used when another program holds the key. The UI must never say "press this" while knowing
        // that pressing it does nothing: spec §19.1 forbids presenting anything unverified, and that
        // covers instructions as much as operational state.
        ["SwitchModeHintUnavailable"] = "{0} is taken by another program — use the button",

        // 命令条码（见 ScanCommand）扫过之后，"最近一枪"旁边显示的说明。
        // Shown next to "most recent scan" after a command barcode (see ScanCommand).
        ["NoticeSwitchedToSn"] = "(switched to SN mode)",
        ["NoticeSwitchedToSku"] = "(switched to SKU mode)",
        ["NoticeUnknownCommand"] = "(unrecognized command sheet — nothing was sent)",
        ["SwitchMode"] = "Switch mode",

        ["Connected"] = "✓  Scanner connected",
        ["Disconnected"] = "⛔  Scanner not connected",
        ["ConnectedOn"] = "✓  Scanner connected on {0}",
        ["Connect"] = "Connect",
        ["Disconnect"] = "Disconnect",

        ["ReconnectSuggestion"] =
            "A device matching the bound scanner appeared on {0} ({1}). Only the vendor and product"
            + " ID match, which many adapters share — confirm it is your scanner.",
        ["ReconnectConnect"] = "Connect to it",
        ["LastScan"] = "Last scan",
        ["NoScanYet"] = "Nothing scanned yet",

        ["Pause"] = "Pause",
        ["Resume"] = "Resume",
        ["PausedHeading"] = "PAUSED",

        // ★ 措辞必须匹配决策 D-26 的新语义，不能沿用规格 §11.1 里那句
        //   "passes through untouched"——那描述的是旧架构（钩子放行一切）。
        //   现在暂停的意思是"跳过规则、原样发出"，两者对工人的含义不同。
        // The wording must match decision D-26's new semantics rather than spec §11.1's "passes
        // through untouched", which described the old architecture where the hook let everything
        // by. Pausing now means "skip the rules and send it unchanged", which means something
        // different to the operator.
        ["PausedSubtitle"] = "Scans are sent unchanged, without parsing or validation",
        ["ModeOnResume"] = "Mode on resume",

        ["ErrorHeading"] = "⚠  SCAN NOT SENT",
        ["ErrorParse"] = "The SKU rule did not match this code",
        ["ErrorValidation"] = "The extracted SKU failed validation",
        // ★ 这句曾经是"按原样发出去，还是丢弃？"——描述的是 F10 / Esc 那两个出口。
        //   它们在 2026-09-15 去掉了（见 docs/CHANGE_ERROR_HANDLING.md），而一句
        //   还在描述已删除功能的提示比没有提示更坏：工人会去找那两个按钮，找不到
        //   就以为程序坏了。
        // This used to read "send it as scanned, or discard it?", describing the F10 / Escape exits.
        // Those were removed on 2026-09-15 (see docs/CHANGE_ERROR_HANDLING.md), and a prompt still
        // describing a deleted feature is worse than none: the operator looks for those buttons and
        // concludes the program is broken when they are not there.
        ["ErrorPrompt"] = "Pick the right mode below, then scan again",

        ["Settings"] = "Settings",
        ["Pin"] = "Keep on top",
        ["Unpin"] = "Stop keeping on top",
        ["Compact"] = "Compact",
        ["Expand"] = "Expand",
        ["Close"] = "Close",

        ["ExitTitle"] = "Exit Scanner Helper?",
        ["ExitMessage"] =
            "Scans will no longer be processed after this. "
            + "To hide the window instead, use Compact.",
        ["ExitConfirm"] = "Exit",
        ["ExitCancel"] = "Keep running",

        ["LinkSilent"] =
            "No scan for {0} minutes. Check that the scanner is still in virtual COM mode — "
            + "if it was switched back to keyboard mode it types directly into the business "
            + "application and nothing here reports an error.",

        ["SettingsTitle"] = "Settings",
        ["SettingsLanguage"] = "Language",
        ["SettingsScanner"] = "Scanner",
        ["SettingsPort"] = "Port",
        ["SettingsBaud"] = "Baud rate",
        ["SettingsRefreshPorts"] = "Refresh",
        ["SettingsSkuRule"] = "SKU rule",
        ["SettingsRuleFixed"] = "Fixed position",
        ["SettingsRuleRegex"] = "Regular expression",
        ["SettingsStartPosition"] = "Start position",
        ["SettingsLength"] = "Length",
        ["SettingsRegexPattern"] = "Pattern",
        ["SettingsCaptureGroup"] = "Capture group",
        ["SettingsValidation"] = "SKU validation",
        ["SettingsValidationHint"] =
            "Every check you turn on must pass. Leave a field empty to turn that check off.",
        ["SettingsMinLength"] = "Min length",
        ["SettingsMaxLength"] = "Max length",
        ["SettingsCharacterSet"] = "Allowed characters",
        ["SettingsCharsAny"] = "No restriction",
        ["SettingsCharsNumbers"] = "Numbers only",
        ["SettingsCharsLetters"] = "Letters only",
        ["SettingsCharsLettersNumbers"] = "Letters and numbers",
        ["SettingsCharsLettersNumbersDashUnderscore"] = "Letters, numbers, - and _",
        ["SettingsIgnoreCase"] = "Ignore letter case",
        ["SettingsIgnoreCaseHint"] =
            "On by default. Rejecting an otherwise valid barcode purely for a lowercase letter is a"
            + " worse failure than accepting one. If uppercase is genuinely required, say so in the"
            + " validation pattern (^[A-Z]+$), which this toggle does not affect.",
        ["SettingsValidationRegex"] = "Validation pattern",
        ["SettingsValidationRegexHint"] =
            "The extracted SKU must match this. Leave empty to skip the check.",
        ["SettingsTestValid"] = "Result: {0}  ✓ passes validation",
        ["SettingsTestInvalid"] = "Result: {0}  ✗ {1}",
        ["SettingsOutput"] = "Output",
        ["SettingsAppendEnter"] = "Press Enter after each scan",
        ["SettingsAppendEnterHint"] =
            "Off by default. Turn it on only if the page needs Enter to submit or move on.",
        ["SettingsSound"] = "Sound",
        ["SettingsModeSound"] = "Beep when the mode changes",
        ["SettingsModeSoundHint"] =
            "One tone: low for SN, high for SKU. Off by default — a warehouse is never quiet, and a"
            + " sound nobody asked for is noise.",
        ["SettingsErrorSound"] = "Beep when a scan is not sent",
        ["SettingsErrorSoundHint"] =
            "The one worth hearing: during an error you may be looking at the goods rather than the"
            + " screen.",
        ["SettingsDiagnostics"] = "Diagnostics",
        // 热键一节（现场反馈：F8 在不同电脑上被不同软件占用）。
        // The hotkey section (field report: F8 is taken by different software on different PCs).
        // 漏网异常时的提示。写清楚两件事：扫码停了，以及日志在哪儿——
        // 工人唯一能做的就是重开程序，而开发者需要的是那个文件。
        // Shown for an unhandled exception. It states the two things that matter: scanning has
        // stopped, and where the log is — restarting is all the operator can do, and that file is
        // what the developer will need.
        ["FaultTitle"] = "Scanner Helper hit an error",
        ["FaultMessage"] =
            "Scanner Helper ran into an unexpected error and has to close.\n\n"
            + "Scans are no longer being processed. Start it again to carry on.\n\n"
            + "The details were written to the log folder (Settings -> Open the log folder).",

        // 出错界面。{0} 是出错时所处的模式——最常见的病因就是模式不对。
        // The error screen. {0} is the mode the failure happened in; the wrong mode is the
        // commonest cause.
        ["ErrorInMode"] = "Failed in {0}",
        ["SwitchToSn"] = "Switch to SN",
        ["SwitchToSku"] = "Switch to SKU",

        ["SettingsHotkeyGroup"] = "Mode switch key",
        ["SettingsToggleKey"] = "Key",

        // ★ 这两句是这一节存在的理由。RegisterHotKey 的成败是**这台机器上的**
        //   确定答案，比任何一份"哪些键通常空着"的猜测都可靠——所以直接把它
        //   显示出来，让装机的人当场换一个，而不是装完才发现按了没反应。
        // These two lines are why the section exists. Whether RegisterHotKey succeeds is a definite
        // answer about this machine, more reliable than any guess at which keys are usually free —
        // so it is shown outright, letting whoever installs it change the key on the spot rather
        // than discovering after the fact that pressing it does nothing.
        ["HotkeyAvailable"] = "✓  Available on this PC",
        ["HotkeyTaken"] = "✗  Taken by another program on this PC",
        ["SettingsCommandSheets"] = "Print the mode-switch sheets",
        ["SettingsCommandSheetsHint"] =
            "Tape them to the workstation and scan one to switch — no key needed",

        ["SettingsMaskBarcodes"] = "Mask barcode content in the log",
        ["SettingsMaskBarcodesHint"] =
            "Keeps the first and last two characters and the length: AB************89. Enough to"
            + " investigate, without leaving a full record of goods on disk.",
        ["SettingsRetention"] = "Keep logs for (days)",
        ["SettingsOpenLogFolder"] = "Open the log folder",
        ["SettingsStartWithWindows"] = "Start with Windows",
        ["SettingsSave"] = "Save",
        ["SettingsCancel"] = "Cancel",
        ["SettingsTest"] = "Test with the last scan",
        ["SettingsTestNoScan"] = "Scan something first.",
        ["SettingsTestResult"] = "Result: {0}",
        ["SettingsTestFailed"] = "This rule does not match: {0}",

        ["AlreadyRunningTitle"] = "Scanner Helper is already running",
        ["AlreadyRunningMessage"] =
            "Only one copy can run at a time, because only one can hold the scanner's port.",

        ["PortBusyTitle"] = "The port is in use",
        ["PortMissingTitle"] = "The port was not found",
    };

    private static readonly Dictionary<string, string> ChineseSimplified = new(StringComparer.Ordinal)
    {
        ["AppTitle"] = "扫码助手",

        ["ModeHeading"] = "当前模式",
        ["ModeSn"] = "SN MODE",
        ["ModeSku"] = "SKU MODE",
        ["ModeSnSubtitle"] = "扫到的码原样发出",
        ["ModeSkuSubtitle"] = "从扫到的码里提取 SKU",
        ["SwitchModeHint"] = "{0}   切换模式",
        ["SwitchModeHintUnavailable"] = "{0} 被别的程序占用了，请用下面的按钮",
        ["NoticeSwitchedToSn"] = "（已切到 SN 模式）",
        ["NoticeSwitchedToSku"] = "（已切到 SKU 模式）",
        ["NoticeUnknownCommand"] = "（认不出的命令条码，什么都没发出去）",
        ["SwitchMode"] = "切换模式",

        ["Connected"] = "✓  扫码枪已连接",
        ["Disconnected"] = "⛔  扫码枪未连接",
        ["ConnectedOn"] = "✓  扫码枪已连接（{0}）",
        ["Connect"] = "连接",
        ["Disconnect"] = "断开",

        ["ReconnectSuggestion"] =
            "{0} 上出现了一个和绑定的扫码枪相符的设备（{1}）。只有厂商与产品编号对得上，"
            + "而很多转接头共用同一个编号——请确认它是你的枪。",
        ["ReconnectConnect"] = "连接它",
        ["LastScan"] = "最近一枪",
        ["NoScanYet"] = "还没有扫过",

        ["Pause"] = "暂停",
        ["Resume"] = "恢复",
        ["PausedHeading"] = "已暂停",
        ["PausedSubtitle"] = "扫到的码原样发出，不做解析与校验",
        ["ModeOnResume"] = "恢复后的模式",

        ["ErrorHeading"] = "⚠  这一枪没有发出",
        ["ErrorParse"] = "SKU 规则匹配不上这个码",
        ["ErrorValidation"] = "提取出来的 SKU 没通过校验",
        ["ErrorPrompt"] = "在下面选对模式，然后重扫一次",

        ["Settings"] = "设置",
        ["Pin"] = "始终置顶",
        ["Unpin"] = "取消置顶",
        ["Compact"] = "收起",
        ["Expand"] = "展开",
        ["Close"] = "关闭",

        ["ExitTitle"] = "要退出扫码助手吗？",
        ["ExitMessage"] = "退出之后扫码将不再被处理。只是想让窗口小一点的话，请用「收起」。",
        ["ExitConfirm"] = "退出",
        ["ExitCancel"] = "继续运行",

        ["LinkSilent"] =
            "已经 {0} 分钟没有收到扫码了。请确认扫码枪还在虚拟串口模式——"
            + "若它被切回了键盘模式，它会直接往业务软件里打字，而这里不会报任何错。",

        ["SettingsTitle"] = "设置",
        ["SettingsLanguage"] = "语言",
        ["SettingsScanner"] = "扫码枪",
        ["SettingsPort"] = "端口",
        ["SettingsBaud"] = "波特率",
        ["SettingsRefreshPorts"] = "重新列出",
        ["SettingsSkuRule"] = "SKU 规则",
        ["SettingsRuleFixed"] = "固定位置",
        ["SettingsRuleRegex"] = "正则表达式",
        ["SettingsStartPosition"] = "起始位",
        ["SettingsLength"] = "长度",
        ["SettingsRegexPattern"] = "表达式",
        ["SettingsCaptureGroup"] = "捕获组",
        ["SettingsValidation"] = "SKU 校验",
        ["SettingsValidationHint"] = "打开的每一项都必须通过。留空就是不启用这一项。",
        ["SettingsMinLength"] = "最短",
        ["SettingsMaxLength"] = "最长",
        ["SettingsCharacterSet"] = "允许的字符",
        ["SettingsCharsAny"] = "不限",
        ["SettingsCharsNumbers"] = "只允许数字",
        ["SettingsCharsLetters"] = "只允许字母",
        ["SettingsCharsLettersNumbers"] = "字母和数字",
        ["SettingsCharsLettersNumbersDashUnderscore"] = "字母、数字、- 和 _",
        ["SettingsIgnoreCase"] = "忽略字母大小写",
        ["SettingsIgnoreCaseHint"] =
            "默认开启。仅仅因为有个小写字母就把一个本来正确的条码拒掉，比放过它更糟。"
            + "如果确实要求全大写，请写在校验正则里（^[A-Z]+$），那条不受这个开关影响。",
        ["SettingsValidationRegex"] = "校验正则",
        ["SettingsValidationRegexHint"] = "提取出来的 SKU 必须匹配它。留空就是不做这项检查。",
        ["SettingsTestValid"] = "结果：{0}  ✓ 校验通过",
        ["SettingsTestInvalid"] = "结果：{0}  ✗ {1}",
        ["SettingsOutput"] = "输出",
        ["SettingsAppendEnter"] = "每枪之后按一次回车",
        ["SettingsAppendEnterHint"] = "默认关闭。只有当网页需要回车才提交或跳到下一格时才打开。",
        ["SettingsSound"] = "声音",
        ["SettingsModeSound"] = "切换模式时响一声",
        ["SettingsModeSoundHint"] = "一声：SN 低音、SKU 高音。默认关闭——仓库本来就不安静，没人要求的提示音就是噪声。",
        ["SettingsErrorSound"] = "一枪没发出去时响一声",
        ["SettingsErrorSoundHint"] = "这一声最值得开：出错时你可能正低头看货，看不到屏幕上的红色。",
        ["SettingsDiagnostics"] = "诊断日志",
        ["FaultTitle"] = "扫码助手出错了",
        ["FaultMessage"] =
            "扫码助手遇到一个意料之外的错误，必须关闭。\n\n"
            + "扫码已经停止处理了，重新打开程序即可继续。\n\n"
            + "详细情况已经写进日志文件夹（设置 → 打开日志文件夹）。",

        ["ErrorInMode"] = "在 {0} 下没发出去",
        ["SwitchToSn"] = "切到 SN",
        ["SwitchToSku"] = "切到 SKU",

        ["SettingsHotkeyGroup"] = "模式切换按键",
        ["SettingsToggleKey"] = "按键",
        ["HotkeyAvailable"] = "✓  这台电脑上可用",
        ["HotkeyTaken"] = "✗  这台电脑上被别的程序占用了",
        ["SettingsCommandSheets"] = "打印模式切换条码",
        ["SettingsCommandSheetsHint"] = "贴在工位上，扫一下就切换，完全不用碰键盘",

        ["SettingsMaskBarcodes"] = "日志里遮住条码内容",
        ["SettingsMaskBarcodesHint"] =
            "保留首尾各两位和长度：AB************89。够用来查问题，又不会在磁盘上留下一份完整的货品流水。",
        ["SettingsRetention"] = "日志保留天数",
        ["SettingsOpenLogFolder"] = "打开日志文件夹",
        ["SettingsStartWithWindows"] = "开机自动启动",
        ["SettingsSave"] = "保存",
        ["SettingsCancel"] = "取消",
        ["SettingsTest"] = "用最近一枪试一下",
        ["SettingsTestNoScan"] = "先扫一枪。",
        ["SettingsTestResult"] = "结果：{0}",
        ["SettingsTestFailed"] = "这条规则匹配不上：{0}",

        ["AlreadyRunningTitle"] = "扫码助手已经在运行",
        ["AlreadyRunningMessage"] = "同一时刻只能开一个，因为扫码枪的串口只能被一个程序占用。",

        ["PortBusyTitle"] = "端口被占用",
        ["PortMissingTitle"] = "找不到这个端口",
    };

    /// <summary>
    /// 中文：
    ///   静态构造：核对两张表的键完全一致。
    ///   不一致就在启动时抛异常——理由见文件头。
    /// English:
    ///   Static construction checks that both tables carry identical keys, throwing at startup if
    ///   not; see the file header for why.
    /// </summary>
    static UiStrings()
    {
        var onlyInEnglish = English.Keys.Except(ChineseSimplified.Keys).ToArray();
        var onlyInChinese = ChineseSimplified.Keys.Except(English.Keys).ToArray();

        if (onlyInEnglish.Length > 0 || onlyInChinese.Length > 0)
        {
            throw new InvalidOperationException(
                "两种语言的文字表键不一致。 The two language tables have different keys."
                + $" 只在英文表里 / English only: [{string.Join(", ", onlyInEnglish)}]"
                + $" 只在中文表里 / Chinese only: [{string.Join(", ", onlyInChinese)}]");
        }
    }

    /// <summary>
    /// 中文：
    ///   取一条文字。
    ///   输入：language 语言；key 键。
    ///   输出：对应的文字；键不存在时返回键名本身。
    ///
    ///   ★ 键不存在时返回键名，而不是抛异常或返回空串。界面上出现一个键名很难看，
    ///     但它**指名道姓地告诉你缺了哪一条**；空串会让那处控件看起来只是空着，
    ///     而抛异常会因为一句文案让整个程序倒下——工人正在扫货，程序不该为一句
    ///     文案罢工。
    /// English:
    ///   Returns one string, or the key itself when it is missing.
    ///
    ///   The key rather than an exception or an empty string: a key on screen is ugly but it names
    ///   exactly what is missing, an empty string makes the control merely look blank, and throwing
    ///   would take the whole program down over a caption while the operator is scanning goods.
    /// </summary>
    public static string Get(UiLanguage language, string key)
    {
        var table = language == UiLanguage.ChineseSimplified ? ChineseSimplified : English;
        return table.TryGetValue(key, out var value) ? value : key;
    }
}
