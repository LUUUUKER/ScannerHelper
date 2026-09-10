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

        AppendEnterCheck.IsChecked = settings.AppendEnterAfterScan;
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
        => Localizer.SetLanguage(
            LanguageCombo.SelectedIndex == 1 ? UiLanguage.ChineseSimplified : UiLanguage.English);

    private void OnRuleTypeChanged(object sender, RoutedEventArgs e) => UpdateRulePanels();

    private void UpdateRulePanels()
    {
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

        TestResultText.Text = result is ParseResult.Success success
            ? Localizer.Format("SettingsTestResult", success.Sku)
            : Localizer.Format("SettingsTestFailed", lastScan);
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

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        if (BuildParsingSettings() is not { } parsing)
        {
            return;
        }

        var settings = CurrentApp.Settings;

        settings.Language = Localizer.ToPersistedValue(Localizer.Current);
        settings.SkuParsing = parsing;
        settings.AppendEnterAfterScan = AppendEnterCheck.IsChecked == true;
        settings.SerialPort.PortName = PortCombo.SelectedItem as string;
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
