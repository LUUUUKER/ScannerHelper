// =============================================================================
// InterceptionWindow.xaml.cs
//
// 中文：
//   Task 4b 拦截关卡窗口的代码。
//
//   ★ 界面按 100 毫秒**轮询**流水线，而不是订阅它的事件。
//
//     协调器的事件是在钩子回调里触发的（终止符从钩子通道来，一枪收完这件事
//     就发生在回调内部）。界面若直接订阅，WPF 的事件处理、数据绑定、控件
//     刷新就全跑进了那个必须极快的回调里——顶穿 LowLevelHooksTimeout 一次，
//     Windows 就悄悄摘掉钩子，而那时不会有任何通知（规格 §19.1）。
//
//     轮询把两边的节奏彻底解耦：界面再卡也只是界面卡，绝不会拖累钩子。
//     这也正是 4a 第一轮踩过的坑的另一面——那次是界面拖慢了测量。
//
//   ★ 关掉窗口一定要停掉拦截。
//
//     忘了停的后果不是"泄漏一点资源"，而是钩子还挂着、消息循环还在跑，
//     而窗口已经没了——工人再也找不到那个「暂停」按钮，键盘就此归钩子管，
//     而笔记本工位没有备用键盘可插（规格假设 A4）。所以 Closing 里无条件停。
//
// English:
//   Code for the Task 4b hard-gate window.
//
//   The UI polls the pipeline every 100 ms rather than subscribing to its events. The
//   coordinator's events fire inside the hook callback — the terminator arrives on the hook
//   channel, so a scan completing happens within the callback — and a UI subscriber would drag
//   WPF event handling, data binding and control refreshes into a callback that must stay
//   extremely fast. Blow LowLevelHooksTimeout once and Windows silently removes the hook, with
//   no notification at all (spec §19.1). Polling decouples the two rhythms completely: a
//   sluggish UI stays a sluggish UI and never slows the hook. That is the other face of the
//   mistake 4a's first run made, where the UI slowed the measurement.
//
//   Closing the window must stop interception. Forgetting does not leak a little memory; it
//   leaves the hook installed and the message loop running with the window gone — the operator
//   can no longer reach the Pause button, the keyboard now belongs to the hook, and a laptop
//   workstation has no spare to plug in (assumption A4). So Closing stops unconditionally.
//
// 包含的成员 / Members in this file:
//   构造与生命周期 / construction and lifetime
//   设备枚举、绑定 / device enumeration and binding
//   启用、停止、暂停、恢复 / start, stop, pause, resume
//   刷新与导出 / refresh and export
// =============================================================================

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ScannerHelper.Core.Domain;
using ScannerHelper.Diagnostics.Harness.Interception;

namespace ScannerHelper.Diagnostics.Harness;

/// <summary>
/// 中文：Task 4b 拦截关卡窗口。
/// English: The Task 4b hard-gate window.
/// </summary>
public partial class InterceptionWindow : Window
{
    /// <summary>
    /// 中文：界面刷新间隔。100 毫秒足够人读，也远低于任何会让人觉得"没反应"的
    ///       阈值；再快没有意义——数据变化本来就来自人的动作。
    /// English: The refresh interval. A hundred milliseconds is comfortable to read and well
    ///          below anything that feels unresponsive; faster would be pointless, since the
    ///          data changes at human speed anyway.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 中文：扫描结局最多留多少条。留太多没用——判读靠的是最近这些。
    /// English: How many scan outcomes to keep. More is not useful; judgment rests on the
    ///          recent ones.
    /// </summary>
    private const int MaximumScanLogRows = 300;

    private readonly InterceptionPipeline _pipeline = new();
    private readonly GateChecklist _checklist = new();
    private readonly ObservableCollection<ScanLogRow> _scanLogRows = [];
    private readonly ObservableCollection<DeviceChoice> _deviceChoices = [];
    private readonly DispatcherTimer _refreshTimer;

    /// <summary>
    /// 中文：构造窗口。
    /// English: Creates the window.
    /// </summary>
    public InterceptionWindow()
    {
        InitializeComponent();

        GateItemsControl.ItemsSource = _checklist.Items;
        ScanLogView.ItemsSource = _scanLogRows;
        DeviceCombo.ItemsSource = _deviceChoices;
        DeviceCombo.SelectionChanged += OnDeviceSelectionChanged;

        MachineText.Text = string.Format(
            CultureInfo.InvariantCulture,
            "{0} / {1}",
            Environment.MachineName,
            Environment.OSVersion.VersionString);

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = RefreshInterval,
        };
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();

        RefreshDevices();
        Refresh();
    }

    /// <summary>
    /// 中文：
    ///   关窗时无条件停掉拦截并释放。
    ///   见文件头：窗口没了而钩子还在，等于把键盘交给一个再也点不到的程序。
    /// English:
    ///   Stops and disposes unconditionally when closing. See this file's header: the window
    ///   gone with the hook still installed hands the keyboard to a program nobody can click.
    /// </summary>
    private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _refreshTimer.Stop();
        _pipeline.Stop();
        _pipeline.Dispose();
    }

    /// <summary>
    /// 中文：
    ///   重新枚举键盘类设备。
    ///
    ///   ★ 笔记本内置键盘也会出现在这张表里，而且很多机器上它没有 VID/PID
    ///     （规格假设 A4）。选错了就是把自己的键盘当成扫码枪拦下来——所以
    ///     列表里把设备路径也显示出来，那是唯一能区分个体的东西（规格 §6）。
    /// English:
    ///   Re-enumerates keyboard-class devices.
    ///
    ///   The laptop's built-in keyboard appears here too, and on many machines it exposes no
    ///   VID/PID (assumption A4). Choosing wrong means intercepting your own keyboard as though
    ///   it were the scanner, so the list shows the device path as well — the only thing that
    ///   identifies an individual device (spec §6).
    /// </summary>
    private void RefreshDevices()
    {
        var previouslySelected = (DeviceCombo.SelectedItem as DeviceChoice)?.Handle;

        _deviceChoices.Clear();

        foreach (var (handle, identity) in _pipeline.EnumerateKeyboards())
        {
            _deviceChoices.Add(new DeviceChoice(handle, DescribeDevice(identity)));
        }

        if (previouslySelected is { } handleToRestore)
        {
            DeviceCombo.SelectedItem =
                _deviceChoices.FirstOrDefault(choice => choice.Handle == handleToRestore);
        }
    }

    /// <summary>
    /// 中文：把设备身份压成一行标签。设备路径太长，只留末尾能辨认的一段。
    /// English: Flattens a device identity into one label. Device paths are long, so only the
    ///          recognizable tail is kept.
    /// </summary>
    private static string DescribeDevice(ScannerDeviceIdentity identity)
    {
        var label = new StringBuilder();

        label.Append(string.IsNullOrWhiteSpace(identity.FriendlyName)
            ? "（无名称 / unnamed）"
            : identity.FriendlyName);

        if (identity.VendorId is { } vendorId && identity.ProductId is { } productId)
        {
            label.Append(CultureInfo.InvariantCulture, $"  VID_{vendorId}&PID_{productId}");
        }
        else
        {
            label.Append("  （无 VID/PID）");
        }

        if (identity.DevicePath is { Length: > 0 } path)
        {
            var tail = path.Length <= 40 ? path : string.Concat("…", path.AsSpan(path.Length - 40));
            label.Append(CultureInfo.InvariantCulture, $"  {tail}");
        }

        return label.ToString();
    }

    private void OnRefreshDevicesClicked(object sender, RoutedEventArgs e) => RefreshDevices();

    private void OnDeviceSelectionChanged(object? sender, SelectionChangedEventArgs e) => Refresh();

    private void OnAcknowledgeChanged(object sender, RoutedEventArgs e) => Refresh();

    /// <summary>
    /// 中文：
    ///   启用拦截。
    ///
    ///   ★ 启用之后**还没有绑定扫码枪**，因此这一刻什么都不会被吞掉。
    ///     绑定是单独一步，需要再点一次「绑定」。把两件事分开，是为了让人
    ///     有机会先确认钩子装得上（权限、UIPI），再去承担吞按键的风险。
    /// English:
    ///   Starts interception.
    ///
    ///   No scanner is bound at this point, so nothing is swallowed yet. Binding is a separate
    ///   click. Splitting the two lets a person confirm the hook installs at all — privileges,
    ///   UIPI — before taking on the risk of swallowing keystrokes.
    /// </summary>
    private void OnStartClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            _pipeline.Start();
        }
        catch (Exception startupException)
        {
            MessageBox.Show(
                this,
                "启用拦截失败。 Failed to start interception.\n\n" + startupException.Message,
                "Scanner Helper",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        Refresh();
    }

    private void OnStopClicked(object sender, RoutedEventArgs e)
    {
        _pipeline.Stop();
        Refresh();
    }

    private void OnBindClicked(object sender, RoutedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is not DeviceChoice choice)
        {
            return;
        }

        _pipeline.BindScanner(choice.Handle);
        Refresh();
    }

    private void OnUnbindClicked(object sender, RoutedEventArgs e)
    {
        _pipeline.UnbindScanner();
        Refresh();
    }

    private void OnPauseClicked(object sender, RoutedEventArgs e)
    {
        _pipeline.Pause();
        Refresh();
    }

    private void OnResumeClicked(object sender, RoutedEventArgs e)
    {
        _pipeline.Resume();
        Refresh();
    }

    private void OnToggleModeClicked(object sender, RoutedEventArgs e)
    {
        _pipeline.ToggleMode();
        Refresh();
    }

    private void OnAppendEnterChanged(object sender, RoutedEventArgs e)
        => _pipeline.AppendEnterAfterScan = AppendEnterCheck.IsChecked == true;

    private void OnForceSendClicked(object sender, RoutedEventArgs e)
    {
        _pipeline.ForceSend();
        Refresh();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        _pipeline.Cancel();
        Refresh();
    }

    private void OnClearInputClicked(object sender, RoutedEventArgs e)
    {
        TestInput.Clear();
        TestInput.Focus();
    }

    private void OnRefreshTick(object? sender, EventArgs e) => Refresh();

    /// <summary>
    /// 中文：
    ///   把流水线的状态搬到界面上。
    ///   步骤：
    ///     1. 取一份快照（一次取齐，避免逐个属性读到互相矛盾的数字）；
    ///     2. 取走新的扫描结局；
    ///     3. 刷新文字与按钮可用性；
    ///     4. 刷新关卡结论。
    /// English:
    ///   Moves the pipeline's state onto the UI.
    ///   Steps: (1) take one snapshot, so properties read one by one cannot disagree; (2) drain
    ///   new scan outcomes; (3) refresh the text and which buttons are enabled; (4) refresh the
    ///   gate verdict.
    /// </summary>
    private void Refresh()
    {
        // 步骤 1 / Step 1
        var snapshot = _pipeline.TakeSnapshot();

        // 步骤 2 / Step 2
        foreach (var entry in _pipeline.DrainScanLog())
        {
            _scanLogRows.Insert(0, new ScanLogRow(
                Time: entry.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                Mode: entry.Mode == ScanMode.Sn ? "SN" : "SKU",
                RawCode: entry.RawCode,
                Result: entry.Result));
        }

        while (_scanLogRows.Count > MaximumScanLogRows)
        {
            _scanLogRows.RemoveAt(_scanLogRows.Count - 1);
        }

        // 步骤 3 / Step 3
        UpdateStateText(snapshot);
        UpdateCounters(snapshot);
        UpdateButtons(snapshot);

        // 步骤 4 / Step 4
        UpdateGateVerdict(snapshot);
    }

    /// <summary>
    /// 中文：
    ///   刷新状态区。
    ///
    ///   ★ 底色是刻意区分的：暂停用黄色、未绑定用灰色、正在拦截用绿色。
    ///     规格 §19.1 要求界面绝不显示一个它没有验证过的、笃定的运行状态——
    ///     "正在拦截"是最容易让人放心的那句话，所以只有在真的既运行又绑定
    ///     的时候才显示它。
    /// English:
    ///   Refreshes the state panel.
    ///
    ///   The background colors are deliberate: yellow while paused, grey while unbound, green
    ///   while intercepting. Spec §19.1 forbids the UI from showing a confident operational
    ///   state it has not verified, and "intercepting" is the most reassuring thing it can say —
    ///   so it says it only when genuinely both running and bound.
    /// </summary>
    private void UpdateStateText(PipelineSnapshot snapshot)
    {
        var state = new StringBuilder();

        state.AppendLine(snapshot.IsRunning
            ? snapshot.IsPaused
                ? "已暂停 —— 一切原样放行 / PAUSED — everything passes through"
                : snapshot.IsBound
                    ? "正在拦截 / Intercepting"
                    : "已启用，但未绑定扫码枪 —— 什么都不会被吞掉"
                      + " / Running, no scanner bound — nothing is swallowed"
            : "未启用 / Not running");

        state.AppendLine(CultureInfo.InvariantCulture, $"流水线状态 / Pipeline state: {snapshot.State}");
        state.AppendLine(CultureInfo.InvariantCulture, $"模式 / Mode: {(snapshot.Mode == ScanMode.Sn ? "SN" : "SKU")}");
        state.Append(snapshot.PendingErrorRawCode is { } pending
            ? string.Format(
                CultureInfo.InvariantCulture,
                "待决错误 / Pending error: {0}  →  F10 强制发送，Esc 取消",
                pending)
            : "无待决错误 / No pending error");

        StateText.Text = state.ToString();

        StateBorder.Background = !snapshot.IsRunning
            ? new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6))
            : snapshot.IsPaused
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xE5))
                : snapshot.IsBound
                    ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF5, 0xE9))
                    : new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6));

        ModeText.Text = snapshot.Mode == ScanMode.Sn ? "SN" : "SKU";
    }

    /// <summary>
    /// 中文：刷新计数、回调耗时与异常三块。
    /// English: Refreshes the counters, callback cost and faults panels.
    /// </summary>
    private void UpdateCounters(PipelineSnapshot snapshot)
    {
        var counters = new StringBuilder();
        counters.AppendLine(CultureInfo.InvariantCulture, $"吞掉 / Swallowed     {snapshot.SwallowedCount}");
        counters.AppendLine(CultureInfo.InvariantCulture, $"放行 / Passed        {snapshot.PassedThroughCount}");
        counters.AppendLine(CultureInfo.InvariantCulture, $"补发 / Replayed      {snapshot.ReplayedCount}");
        counters.AppendLine(CultureInfo.InvariantCulture, $"Raw Input           {snapshot.RawInputCount}");
        counters.Append(CultureInfo.InvariantCulture, $"扫描 / Scans         {snapshot.ScanCount}");
        CountersText.Text = counters.ToString();

        var budget = new StringBuilder();
        budget.AppendLine(CultureInfo.InvariantCulture,
            $"最长 / Longest       {snapshot.MaximumHookCallbackDuration.TotalMilliseconds:0.000} ms");
        budget.AppendLine(CultureInfo.InvariantCulture,
            $"预算 / Budget        {Win32.Win32ScannerInputSource.HookCallbackBudget.TotalMilliseconds:0} ms");
        budget.Append(CultureInfo.InvariantCulture,
            $"超预算 / Over budget {snapshot.HookCallbackBudgetExceededCount}");
        BudgetText.Text = budget.ToString();

        BudgetBorder.Background = snapshot.HookCallbackBudgetExceededCount > 0
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xE5))
            : new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6));

        FaultText.Text = snapshot.FaultCount == 0
            ? "0"
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0} 次 / faults\n最近 / last: {1}",
                snapshot.FaultCount,
                snapshot.LastFault);

        FaultBorder.Background = snapshot.FaultCount > 0
            ? new SolidColorBrush(Color.FromRgb(0xFD, 0xE7, 0xE9))
            : new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6));
    }

    /// <summary>
    /// 中文：
    ///   刷新按钮可用性。
    ///
    ///   ★ 「停止」与「暂停」在拦截运行期间**永远可点**。它们是逃生通道，
    ///     不能因为界面处在某个状态就被禁用——真需要它们的时候，正是别的
    ///     一切都不对劲的时候。
    /// English:
    ///   Refreshes which buttons are enabled.
    ///
    ///   Stop and Pause stay clickable for as long as interception runs. They are the escape
    ///   route and must not be disabled because the UI is in some state — the moment they are
    ///   needed is precisely the moment everything else is wrong.
    /// </summary>
    private void UpdateButtons(PipelineSnapshot snapshot)
    {
        var acknowledged = AcknowledgeCheck.IsChecked == true;

        StartButton.IsEnabled = acknowledged && !snapshot.IsRunning;
        StopButton.IsEnabled = snapshot.IsRunning;

        PauseButton.IsEnabled = snapshot.IsRunning && !snapshot.IsPaused;
        ResumeButton.IsEnabled = snapshot.IsRunning && snapshot.IsPaused;

        BindButton.IsEnabled =
            snapshot.IsRunning && DeviceCombo.SelectedItem is DeviceChoice;
        UnbindButton.IsEnabled = snapshot.IsBound;

        ForceSendButton.IsEnabled = snapshot.PendingErrorRawCode is not null;
        CancelButton.IsEnabled = snapshot.PendingErrorRawCode is not null;
    }

    /// <summary>
    /// <para>
    /// 中文：
    ///   刷新关卡结论。红字是常态——第 2~6 条全部明确通过，**并且**运行时计数
    ///   不与之矛盾，才转绿。
    ///
    ///   ★ 只看人打的钩是不够的，这是实测教训：第一次导出的报告写着「关卡通过」，
    ///     而同一份报告里处理完成的扫描是 0 枪、回调最长 3379 毫秒。人看到的是
    ///     屏幕上的字符，而「吞掉到底生没生效」屏幕上根本看不出来——回调超时后
    ///     Windows 会无视我们的返回值照常投递，看起来和主动放行一模一样。
    ///     矛盾的详情放在提示气泡里，鼠标停上去就能看到。
    /// English:
    ///   Refreshes the gate verdict. Red is the normal state; it turns green only when every one
    ///   of items 2–6 is explicitly passed and the runtime counters do not contradict them.
    ///
    ///   Ticked boxes alone are not enough, which is a measured lesson: the first exported report
    ///   said the gate had passed while that same report showed zero scans processed and a
    ///   longest callback of 3379 ms. A person sees characters on a screen, and whether the
    ///   swallow took effect is not something a screen shows — after a timeout Windows disregards
    ///   our return value and delivers the key anyway, looking exactly like a deliberate pass.
    ///   The details sit in the tooltip.
    /// </para>
    /// </summary>
    private void UpdateGateVerdict(PipelineSnapshot snapshot)
    {
        var contradictions = _checklist.FindContradictions(snapshot);
        var passed = _checklist.HasPassedHardGate && contradictions.Count == 0;

        GateVerdictText.Text = passed
            ? "硬性关卡：通过 / Hard gate: passed"
            : contradictions.Count > 0 && _checklist.HasPassedHardGate
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "硬性关卡：打钩与计数矛盾（{0} 条）/ contradicted by counters",
                    contradictions.Count)
                : "硬性关卡：未通过 / Hard gate: not passed";

        GateVerdictText.ToolTip = contradictions.Count == 0
            ? null
            : string.Join(Environment.NewLine + Environment.NewLine, contradictions);

        GateVerdictText.Foreground = passed
            ? new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x20))
            : new SolidColorBrush(Color.FromRgb(0xB0, 0x00, 0x20));
    }

    /// <summary>
    /// 中文：
    ///   导出关卡报告到 docs/。
    ///   文件名带时间戳，因此重复导出不会互相覆盖——两次测量本来就该都留着，
    ///   尤其是失败的那一次。
    /// English:
    ///   Exports the gate report into docs/. The filename carries a timestamp so repeated
    ///   exports do not overwrite one another: both runs deserve to survive, the failed one
    ///   especially.
    /// </summary>
    private void OnExportClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = _checklist.BuildReport(MachineText.Text, _pipeline.TakeSnapshot());

            var fileName = string.Format(
                CultureInfo.InvariantCulture,
                "TASK_4B_GATE_{0:yyyyMMdd-HHmmss}.md",
                DateTimeOffset.Now);

            var path = Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath) ?? Environment.CurrentDirectory,
                fileName);

            File.WriteAllText(path, report, Encoding.UTF8);

            MessageBox.Show(
                this,
                "已导出 / Exported:\n" + path,
                "Scanner Helper",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exportException)
        {
            MessageBox.Show(
                this,
                "导出失败。 Export failed.\n\n" + exportException.Message,
                "Scanner Helper",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 中文：下拉里的一个设备。
    /// English: One device in the dropdown.
    /// </summary>
    private sealed record DeviceChoice(nint Handle, string Label);

    /// <summary>
    /// 中文：扫描结局列表里的一行。
    /// English: One row in the scan outcome list.
    /// </summary>
    private sealed record ScanLogRow(string Time, string Mode, string RawCode, string Result);
}
