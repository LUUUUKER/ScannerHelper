// =============================================================================
// SettingsWindow.xaml.cs
//
// 中文：
//   设置窗口的代码（规格 §13）。
//
//   ★ 改动改在一份**副本**上，点保存才生效。
//
//     取消要真的能取消。工人打开设置、乱点一通、发现不对想退出去——如果改动
//     是即时生效的，那时已经没有退路了，而扫码枪的端口配错就等于程序停摆。
//     副本让"取消"这个词名副其实。
//
//     唯一的例外是**语言**：它一改就立即生效（规格 §12 明确要求），因为工人
//     多半是因为看不懂才来改它的——让他先看懂了，再决定其它几项。
//     取消时把语言改回去。
//
//   ★ 规则测试用的是**最近扫的那一枪**（规格 §8.3）。
//
//     工人改完规则最想知道的不是语法对不对，而是"它对我手上这个码管不管用"。
//     让他再想一个例子出来，是把一件具体的事变成一道抽象题。
//
// English:
//   The settings window (spec §13).
//
//   Edits are made on a copy and take effect on Save. Cancel must genuinely cancel: an operator
//   opens Settings, changes things, realizes it was wrong and wants out — with live edits there is
//   no way back by then, and a wrong scanner port stops the program dead. The copy makes the word
//   "cancel" mean what it says.
//
//   Language is the one exception, applying immediately as spec §12 requires: an operator usually
//   opens this because they cannot read the UI, and letting them read it first is the point.
//   Cancel puts the language back.
//
//   The rule test uses the most recent scan (spec §8.3). What an operator wants to know after
//   editing a rule is not whether the syntax is valid but whether it works on the code in their
//   hand; asking them to invent an example turns something concrete into an abstract puzzle.
//
// 包含的成员 / Members in this file:
//   载入与保存 / load and save
//   规则测试 / testing the rule
//   开机自启 / start with Windows
// =============================================================================

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ScannerHelper.App.Localization;
using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Parsing;
using ScannerHelper.Core.Settings;
using ScannerHelper.Core.Validation;

// ★ ValidationResult 这个名字在 WPF 里也有一个（System.Windows.Controls，用于
//   数据绑定校验），与领域里的那个同名。别名把要用的那个钉死，免得写成谁的都能
//   编译过、而含义完全不同——这类错误编译器不会拦，只会在运行时给出一个说不通
//   的结果。
// ValidationResult also exists in WPF (System.Windows.Controls, for binding validation) under the
// same name. The alias pins the one meant here, so a mix-up cannot compile into something that
// looks fine and means something else — a mistake the compiler does not catch and that only shows
// up as a result nobody can explain at runtime.
using ValidationResult = ScannerHelper.Core.Domain.ValidationResult;

namespace ScannerHelper.App;

/// <summary>
/// 中文：设置窗口。
/// English: The settings window.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>
    /// 中文：开机自启写在这里。HKCU 而不是 HKLM——写 HKLM 要管理员权限，
    ///       而本程序绝不提权（规格 §2.1 假设 A2）。
    /// English: Start-with-Windows lives here. HKCU rather than HKLM: writing HKLM needs
    ///          administrator rights and this program never elevates (spec §2.1, assumption A2).
    /// </summary>
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string RunValueName = "ScannerHelper";

    private readonly UiLanguage _languageOnOpen;

    /// <summary>
    /// 中文：正在把设置填进控件。填的过程中控件会抛出各种事件，那些都不是工人的
    ///       操作，不该产生任何副作用。
    /// English: True while the controls are being filled. They raise events as they are filled, and
    ///          none of those are the operator's doing, so none may have side effects.
    /// </summary>
    private bool _isLoading;

    /// <summary>
    /// 中文：构造窗口并把当前设置载进来。
    /// English: Creates the window and loads the current settings into it.
    /// </summary>
    public SettingsWindow()
    {
        InitializeComponent();

        _languageOnOpen = Localizer.Current;
        Load();
    }

    private static App CurrentApp => (App)Application.Current;

    /// <summary>
    /// 中文：把当前设置填进控件。
    /// English: Fills the controls from the current settings.
    /// </summary>
    private void Load()
    {
        _isLoading = true;

        try
        {
            LoadCore();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void LoadCore()
    {
        var settings = CurrentApp.Settings;

        LanguageCombo.SelectedIndex =
            Localizer.Current == UiLanguage.ChineseSimplified ? 1 : 0;

        RefreshPorts();
        SelectBaud(settings.SerialPort.BaudRate);

        var parsing = settings.SkuParsing;
        FixedRuleRadio.IsChecked = parsing.RuleType == SkuParsingRuleType.FixedPosition;
        RegexRuleRadio.IsChecked = parsing.RuleType == SkuParsingRuleType.Regex;

        StartPositionBox.Text = parsing.StartPosition.ToString(CultureInfo.InvariantCulture);
        LengthBox.Text = parsing.Length.ToString(CultureInfo.InvariantCulture);
        RegexBox.Text = parsing.RegexPattern ?? string.Empty;
        CaptureGroupBox.Text = parsing.CaptureGroupIndex.ToString(CultureInfo.InvariantCulture);

        var validation = settings.SkuValidation;
        MinLengthBox.Text = FormatOptionalLength(validation.MinimumLength);
        MaxLengthBox.Text = FormatOptionalLength(validation.MaximumLength);
        CharacterSetCombo.SelectedIndex = validation.CharacterSet switch
        {
            CharacterSetPreset.Numbers => 1,
            CharacterSetPreset.Letters => 2,
            CharacterSetPreset.LettersNumbers => 3,
            CharacterSetPreset.LettersNumbersDashUnderscore => 4,
            _ => 0,
        };
        IgnoreCaseCheck.IsChecked = validation.IgnoreCase;
        ValidationRegexBox.Text = validation.ValidationRegexPattern ?? string.Empty;

        MaskBarcodesCheck.IsChecked = settings.Diagnostics.MaskBarcodeData;
        RetentionBox.Text = settings.Diagnostics.LogRetentionDays.ToString(CultureInfo.InvariantCulture);

        AppendEnterCheck.IsChecked = settings.AppendEnterAfterScan;
        ModeSoundCheck.IsChecked = settings.ModeSwitchSoundEnabled;
        ErrorSoundCheck.IsChecked = settings.ErrorSoundEnabled;
        StartWithWindowsCheck.IsChecked = settings.StartWithWindows;

        UpdateRulePanels();
    }

    private void RefreshPorts()
    {
        var ports = ScannerSession.ListPorts();
        PortCombo.ItemsSource = ports;

        var current = CurrentApp.Settings.SerialPort.PortName;

        if (current is not null && ports.Contains(current))
        {
            PortCombo.SelectedItem = current;
        }
        else if (ports.Count > 0)
        {
            PortCombo.SelectedIndex = 0;
        }
    }

    private void SelectBaud(int baudRate)
    {
        var wanted = baudRate.ToString(CultureInfo.InvariantCulture);

        foreach (ComboBoxItem item in BaudCombo.Items)
        {
            if ((string?)item.Content == wanted)
            {
                BaudCombo.SelectedItem = item;
                return;
            }
        }

        BaudCombo.SelectedIndex = 0;
    }

    private void OnRefreshPortsClicked(object sender, RoutedEventArgs e) => RefreshPorts();

    /// <summary>
    /// 中文：语言立即生效（规格 §12）。取消时会改回去。
    /// English: The language applies immediately (spec §12); Cancel puts it back.
    /// </summary>
    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        // ★ 同样的道理：SelectionChanged 也可能在构造途中触发。
        //   载入期间不要动全局语言——那会在工人还没选之前就先切一次。
        // The same hazard: SelectionChanged can fire mid-construction. The global language must not
        // move while loading, which would switch it before the operator chose anything.
        if (_isLoading)
        {
            return;
        }

        Localizer.SetLanguage(
            LanguageCombo.SelectedIndex == 1 ? UiLanguage.ChineseSimplified : UiLanguage.English);
    }

    private void OnRuleTypeChanged(object sender, RoutedEventArgs e) => UpdateRulePanels();

    /// <summary>
    /// 中文：
    ///   按当前选中的规则类型显示对应的那组输入框。
    ///
    ///   ★ 开头那个 null 检查不是防御性代码，是 WPF 的事实：控件的事件可以在
    ///     InitializeComponent **进行中**触发，而那时后面才声明的 x:Name 字段
    ///     仍然是 null。少了这一层，"点一下设置"就是一个从构造函数里抛出来、
    ///     没人接住的空引用——现场看到的是程序卡死。
    /// English:
    ///   Shows the input group matching the selected rule type.
    ///
    ///   The null check is not defensive coding but a fact of WPF: a control's events can fire
    ///   *during* InitializeComponent, while x:Name fields declared later in the XAML are still
    ///   null. Without it, clicking Settings raises a NullReferenceException out of the
    ///   constructor with nobody to catch it, and what the floor sees is a frozen program.
    /// </summary>
    private void UpdateRulePanels()
    {
        if (FixedRulePanel is null || RegexRulePanel is null)
        {
            return;
        }

        var isFixed = FixedRuleRadio.IsChecked == true;
        FixedRulePanel.Visibility = isFixed ? Visibility.Visible : Visibility.Collapsed;
        RegexRulePanel.Visibility = isFixed ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 中文：
    ///   用最近扫的那一枪试一下当前填的规则（规格 §8.3）。
    ///
    ///   ★ 试的是**界面上现在填的**规则，不是已保存的那条——工人正是在改的过程中
    ///     想知道结果，等他保存了再试就晚了。
    /// English:
    ///   Tests the rule as currently typed against the most recent scan (spec §8.3) — as typed
    ///   rather than as saved, because the operator wants the answer while editing, and testing
    ///   only after saving comes too late.
    /// </summary>
    private void OnTestRuleClicked(object sender, RoutedEventArgs e)
    {
        var lastScan = CurrentApp.Session?.Snapshot().LastScanRawCode;

        if (string.IsNullOrEmpty(lastScan))
        {
            TestResultText.Text = Localizer.Get("SettingsTestNoScan");
            return;
        }

        if (BuildParsingSettings() is not { } parsing)
        {
            return;
        }

        var result = SkuParserFactory.Create(parsing).Parse(lastScan);

        if (result is not ParseResult.Success success)
        {
            TestResultText.Text = Localizer.Format("SettingsTestFailed", lastScan);
            return;
        }

        // ★ 解析成功之后接着跑校验。工人改完规则想知道的是"这一枪到底能不能过"，
        //   而不是"截出来的那一段长什么样"——只报解析结果等于把问题回答了一半，
        //   而剩下那一半正是他下一秒会撞上的。
        // Validation runs straight after a successful parse. What the operator wants to know after
        // editing rules is whether this scan gets through, not what the extracted slice looks like;
        // reporting only the parse answers half the question, and the other half is what they are
        // about to hit.
        if (BuildValidationSettings() is not { } validationSettings)
        {
            return;
        }

        var validation = SkuValidatorFactory.Create(validationSettings).Validate(success.Sku);

        TestResultText.Text = validation is ValidationResult.Invalid invalid
            ? Localizer.Format("SettingsTestInvalid", success.Sku, Describe(invalid))
            : Localizer.Format("SettingsTestValid", success.Sku);
    }

    /// <summary>
    /// 中文：
    ///   从界面上读出解析设置。数字填得不对就提示并返回 null。
    ///
    ///   ★ 不静默纠正。把"起始位 0"悄悄改成 1 看起来体贴，实际是让工人以为自己
    ///     填对了，而结果与他预期的差一位——那种错他一辈子也查不出来。
    /// English:
    ///   Reads the parsing settings from the UI, returning null after a prompt when a number is
    ///   wrong. Nothing is silently corrected: quietly turning "start position 0" into 1 looks
    ///   considerate but leaves the operator believing they typed what they meant while the result
    ///   is off by one — a mistake they will never find.
    /// </summary>
    private SkuParsingSettings? BuildParsingSettings()
    {
        if (FixedRuleRadio.IsChecked == true)
        {
            if (!int.TryParse(StartPositionBox.Text, out var startPosition)
                || !int.TryParse(LengthBox.Text, out var length))
            {
                TestResultText.Text = Localizer.Format("SettingsTestFailed", StartPositionBox.Text);
                return null;
            }

            return new SkuParsingSettings
            {
                RuleType = SkuParsingRuleType.FixedPosition,
                StartPosition = startPosition,
                Length = length,
            };
        }

        if (!int.TryParse(CaptureGroupBox.Text, out var captureGroup))
        {
            TestResultText.Text = Localizer.Format("SettingsTestFailed", CaptureGroupBox.Text);
            return null;
        }

        return new SkuParsingSettings
        {
            RuleType = SkuParsingRuleType.Regex,
            RegexPattern = RegexBox.Text,
            CaptureGroupIndex = captureGroup,
        };
    }

    /// <summary>
    /// 中文：
    ///   把长度输入框读成可空整数。空 = 不启用这一项。
    ///   输出：解析成功返回 (true, 值)；填了但不是合法数字返回 (false, null)。
    ///
    ///   ★ "空"和"填错了"必须区分开。两者都当成"不启用"看着省事，实际是把工人的
    ///     手误静静吞掉：他明明填了 8，因为多打了一个字符就变成"不检查长度"，
    ///     而界面上什么都没说。
    /// English:
    ///   Reads a length box as a nullable integer — empty meaning the check is off — and reports
    ///   separately when something was typed that is not a valid number.
    ///
    ///   Empty and mistyped must not be conflated. Treating both as "off" looks convenient and
    ///   quietly swallows a typo: they typed 8, one stray character turned it into "do not check
    ///   the length", and nothing on screen said so.
    /// </summary>
    private static (bool IsValid, int? Value) ReadOptionalLength(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (true, null);
        }

        return int.TryParse(text.Trim(), out var value) && value >= 0
            ? (true, value)
            : (false, null);
    }

    private static string FormatOptionalLength(int? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// 中文：
    ///   从界面上读出校验设置（规格 §9）。数字填错就提示并返回 null。
    ///
    ///   ★ 正则不在这里试着编译。规格 §9.3 要求正则始终带有限超时，而那件事由
    ///     SkuValidatorFactory 统一负责；在这里再编一次，等于给"这条正则合不合法"
    ///     制造第二个判断点，两处一旦不一致，工人保存得下去却用不了。
    ///     要看它管不管用，用「用最近一枪试一下」——那条路走的是真正在跑的那套。
    /// English:
    ///   Reads the validation settings from the UI (spec §9), prompting and returning null when a
    ///   number is wrong.
    ///
    ///   The pattern is not compiled here. Spec §9.3 requires a finite timeout on every regex and
    ///   SkuValidatorFactory owns that; compiling again here would create a second place deciding
    ///   whether a pattern is legal, and once the two disagree the operator saves something that
    ///   then does not work. "Test with the last scan" answers that through the code that runs.
    /// </summary>
    private SkuValidationSettings? BuildValidationSettings()
    {
        var (minimumIsValid, minimum) = ReadOptionalLength(MinLengthBox.Text);
        var (maximumIsValid, maximum) = ReadOptionalLength(MaxLengthBox.Text);

        if (!minimumIsValid || !maximumIsValid)
        {
            TestResultText.Text = Localizer.Format(
                "SettingsTestFailed", minimumIsValid ? MaxLengthBox.Text : MinLengthBox.Text);
            return null;
        }

        return new SkuValidationSettings
        {
            MinimumLength = minimum,
            MaximumLength = maximum,
            CharacterSet = CharacterSetCombo.SelectedIndex switch
            {
                1 => CharacterSetPreset.Numbers,
                2 => CharacterSetPreset.Letters,
                3 => CharacterSetPreset.LettersNumbers,
                4 => CharacterSetPreset.LettersNumbersDashUnderscore,
                _ => null,
            },
            IgnoreCase = IgnoreCaseCheck.IsChecked == true,
            ValidationRegexPattern = string.IsNullOrWhiteSpace(ValidationRegexBox.Text)
                ? null
                : ValidationRegexBox.Text,
        };
    }

    /// <summary>
    /// 中文：
    ///   把校验失败的原因说成人话。
    ///
    ///   ★ 说的是"哪一条没过"，而不是"没过"。工人看到「12 < 16」能立刻判断是码
    ///     不对还是规则配错了；只看到"校验失败"，他两种都判断不了，而这正是他
    ///     打开设置要解决的问题。
    /// English:
    ///   Renders validation failures readably, naming which check failed rather than only that one
    ///   did. "12 < 16" lets the operator tell a wrong code from a wrong rule instantly;
    ///   "validation failed" lets them tell neither — and telling them apart is why they opened
    ///   Settings.
    /// </summary>
    private static string Describe(ValidationResult.Invalid invalid)
        => string.Join("; ", invalid.Failures.Select(failure => failure switch
        {
            ValidationFailure.TooShort tooShort => string.Format(
                CultureInfo.InvariantCulture,
                "{0} < {1}", tooShort.ActualLength, tooShort.MinimumLength),
            ValidationFailure.TooLong tooLong => string.Format(
                CultureInfo.InvariantCulture,
                "{0} > {1}", tooLong.ActualLength, tooLong.MaximumLength),
            ValidationFailure.IllegalCharacter illegal => string.Format(
                CultureInfo.InvariantCulture,
                "'{0}' @{1}", illegal.Character, illegal.Position),
            _ => failure.ToString() ?? string.Empty,
        }));

    /// <summary>
    /// 中文：
    ///   在资源管理器里打开日志文件夹。
    ///
    ///   ★ 有这个按钮，是因为日志在 %AppData% 下——那是一个工人根本不知道怎么
    ///     找到的地方。而"出问题时把日志发过来"这件事，如果第一步就是"先找到
    ///     一个隐藏目录"，那多半就不会发生了。
    /// English:
    ///   Opens the log folder in Explorer. The button exists because the logs live under %AppData%,
    ///   a place an operator has no idea how to reach — and "send me the log when it goes wrong"
    ///   mostly does not happen if its first step is finding a hidden directory.
    /// </summary>
    private void OnOpenLogFolderClicked(object sender, RoutedEventArgs e)
    {
        if (CurrentApp.Log is not { } log)
        {
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(log.Directory);
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(log.Directory) { UseShellExecute = true });
        }
        catch (Exception openException)
        {
            MessageBox.Show(
                this, openException.Message, Localizer.Get("SettingsTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (BuildParsingSettings() is not { } parsing)
        {
            return;
        }

        var settings = CurrentApp.Settings;

        settings.Language = Localizer.ToPersistedValue(Localizer.Current);
        if (BuildValidationSettings() is not { } validation)
        {
            return;
        }

        settings.SkuParsing = parsing;
        settings.SkuValidation = validation;
        settings.AppendEnterAfterScan = AppendEnterCheck.IsChecked == true;
        settings.Diagnostics.MaskBarcodeData = MaskBarcodesCheck.IsChecked == true;

        if (int.TryParse(RetentionBox.Text, out var retentionDays) && retentionDays >= 1)
        {
            settings.Diagnostics.LogRetentionDays = retentionDays;
        }

        // 立刻生效：遮码是工人**刚刚**做的决定，等下次启动才生效等于这一次没生效。
        // Applied immediately: masking is a decision just made, and taking effect only at the next
        // launch means it did not take effect.
        if (CurrentApp.Log is { } log)
        {
            log.IsMaskingEnabled = settings.Diagnostics.MaskBarcodeData;
            log.RetentionDays = settings.Diagnostics.LogRetentionDays;
        }

        settings.ModeSwitchSoundEnabled = ModeSoundCheck.IsChecked == true;
        settings.ErrorSoundEnabled = ErrorSoundCheck.IsChecked == true;
        settings.SerialPort.PortName = PortCombo.SelectedItem as string;

        // ★ 选定端口的同时把它背后的**设备身份**记下来（规格 §6）。
        //   只记端口号是不够的：换一个 USB 口 COM3 就可能变成 COM7，而工人不会
        //   知道这件事——他只知道"昨天还好好的"。记了身份，重连时才能拿身份去
        //   找现在的端口。
        // Record the identity behind the port as well as the port itself (spec §6). The number
        // alone is not enough: another USB socket can turn COM3 into COM7, which the operator will
        // never know — they only know it worked yesterday. With the identity recorded, reconnection
        // can resolve the current port from it.
        if (settings.SerialPort.PortName is { } chosenPort)
        {
            CurrentApp.Session?.RecordBinding(chosenPort);
        }
        settings.SerialPort.BaudRate = int.Parse(
            (string)((ComboBoxItem)BaudCombo.SelectedItem).Content, CultureInfo.InvariantCulture);

        settings.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        ApplyStartWithWindows(settings.StartWithWindows);

        CurrentApp.SaveSettings();

        // 设置生效可能要重连串口；连不上就把原始原因原样告诉工人。
        // Applying may reconnect the port; a failure reports its original cause unchanged.
        if (CurrentApp.Session?.ApplySettings(settings) is { } failure)
        {
            MessageBox.Show(
                this,
                failure.Failure.Message,
                Localizer.Get(failure.TitleKey),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        DialogResult = true;
        Close();
    }

    /// <summary>
    /// 中文：取消：把语言改回打开设置时的那个，其它什么都没动过。
    /// English: Cancel: put the language back to what it was on opening; nothing else was touched.
    /// </summary>
    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        if (Localizer.Current != _languageOnOpen)
        {
            Localizer.SetLanguage(_languageOnOpen);
        }

        Close();
    }

    /// <summary>
    /// 中文：
    ///   写入或删除开机自启（规格 §13）。
    ///
    ///   ★ 失败不弹错。注册表这一项被组策略锁住是仓库里常见的事，而它失败并不
    ///     影响程序当下能不能用——为一件"下次开机才生效"的事打断工人，代价大于
    ///     收益。复选框的状态照实存进配置，下次打开设置时他看到的仍是自己勾的那样。
    /// English:
    ///   Writes or removes the start-with-Windows entry (spec §13).
    ///
    ///   A failure raises no dialog. Group policy locking this key is common in warehouses, and
    ///   failing does not affect whether the program works right now — interrupting the operator
    ///   over something that would only matter at the next boot costs more than it gains. The
    ///   checkbox state is persisted as chosen, so reopening Settings shows what they picked.
    /// </summary>
    private static void ApplyStartWithWindows(bool enabled)
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);

            if (runKey is null)
            {
                return;
            }

            if (enabled)
            {
                var executablePath = Environment.ProcessPath;

                if (!string.IsNullOrEmpty(executablePath))
                {
                    runKey.SetValue(RunValueName, $"\"{executablePath}\"");
                }
            }
            else
            {
                runKey.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // 被组策略或权限挡住。见上面的说明。
            // Blocked by policy or permissions; see the note above.
        }
        catch (System.Security.SecurityException)
        {
            // 同上。 As above.
        }
    }
}
