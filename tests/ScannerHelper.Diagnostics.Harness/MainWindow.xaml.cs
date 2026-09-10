// =============================================================================
// MainWindow.xaml.cs
//
// 中文：
//   诊断工具的主窗口。只负责显示与按钮，不含任何观测逻辑——那些都在
//   ObservationSession 里。4b 要把观测换成拦截时，本文件基本不用动。
//
//   ★ 三件与 Win32 相关、必须在窗口这一层做的事：
//
//     1. **拿到窗口句柄再注册 Raw Input。** RIDEV_INPUTSINK 需要一个真实的
//        HWND，而 WPF 的 Window 在 SourceInitialized 之前是没有句柄的。
//
//     2. **在窗口过程里接住 WM_INPUT。** WPF 本身不转发这个消息，必须通过
//        HwndSource 挂一个钩子。时间戳要在认出消息的**第一时间**取——晚一步，
//        取到的就不再是"消息何时到达"而是"我们处理了多久"，而两条通道的
//        时差正是本工具唯一要测的东西。
//
//     3. **在界面线程上安装键盘钩子。** 低层键盘钩子的回调靠安装线程的消息
//        队列驱动，装在没有消息泵的线程上会一个事件都收不到，而且不报任何错。
//
//   ★ 界面刷新与钩子回调之间没有任何同步点。
//
//     回调只往无锁环形缓冲区里写，界面每 100 毫秒来取一次。因此界面再慢也
//     不可能拖住回调——而拖住回调超过 300 毫秒，Windows 会悄悄摘掉钩子
//     （规格 §19.1）。这也是为什么日志列表有条数上限：让它无限增长，界面
//     迟早会卡，虽然卡不到回调，但会让工具本身没法用。
//
// English:
//   The harness's main window. Display and buttons only; the observation logic lives in
//   ObservationSession, so switching from observation to interception in 4b barely
//   touches this file.
//
//   Three Win32 concerns must be handled at the window level. First, Raw Input
//   registration needs a real HWND, which a WPF Window does not have until
//   SourceInitialized. Second, WPF does not forward WM_INPUT, so the window procedure
//   must be hooked through HwndSource — and the timestamp must be taken the instant the
//   message is recognized, or it stops describing when the message arrived and starts
//   describing how long we took, which is the one thing this tool measures. Third, the
//   keyboard hook must be installed on the UI thread: its callback is driven by the
//   installing thread's message queue, and on a thread without a pump it receives
//   nothing and reports no error.
//
//   There is no synchronization point between the UI refresh and the hook callback. The
//   callback only writes into a lock-free ring buffer and the UI collects every 100 ms,
//   so no amount of UI slowness can stall the callback — and stalling it past 300 ms has
//   Windows silently remove the hook (spec §19.1). That is also why the log list is
//   capped: unbounded growth would eventually freeze the UI which, while it could not
//   reach the callback, would make the tool itself unusable.
//
// 包含的成员 / Members in this file:
//   OnSourceInitialized  取得窗口句柄并挂上窗口过程钩子
//   OnWindowMessage      接住 WM_INPUT
//   OnDrainTick          定时排空缓冲区并刷新界面
//   OnStartClicked / OnStopClicked / OnResetClicked / OnRefreshDevicesClicked / OnExportClicked
// =============================================================================

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ScannerHelper.Diagnostics.Harness.Observation;
using ScannerHelper.Win32;
using ScannerHelper.Win32.Observation;

namespace ScannerHelper.Diagnostics.Harness;

/// <summary>
/// 中文：主窗口。
/// English: The main window.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// 中文：日志列表保留的最大行数。日志是给人扫一眼看形状用的，完整数据在
    ///       统计与报告里；让它无限增长只会让界面越来越卡。
    /// English: How many log rows are kept. The log exists for a person to eyeball the
    ///          shape of a scan; the complete data lives in the statistics and the
    ///          report. Unbounded growth only degrades the UI.
    /// </summary>
    private const int MaximumLogRows = 2000;

    private readonly ObservationSession _session = new();
    private readonly ObservableCollection<EventLogRow> _logRows = [];
    private readonly ObservableCollection<DeviceRow> _deviceRows = [];
    private readonly DispatcherTimer _drainTimer = new();

    /// <summary>
    /// 中文：第一个事件的时间戳，用于把日志里的时间显示成相对毫秒。
    ///       绝对的 Stopwatch 读数对人毫无意义，相对时间才能看出"这几个字符
    ///       是连着来的"。
    /// English: The first event's timestamp, so log times display as relative
    ///          milliseconds. An absolute Stopwatch reading means nothing to a person,
    ///          while relative time is what makes "these characters arrived together"
    ///          visible.
    /// </summary>
    private long? _firstTimestamp;

    private long _sequenceNumber;

    /// <summary>
    /// 中文：构造窗口并接上数据源。
    /// English: Builds the window and wires up its data sources.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        EventListView.ItemsSource = _logRows;
        DeviceListView.ItemsSource = _deviceRows;

        _drainTimer.Interval = ObservationSession.DrainInterval;
        _drainTimer.Tick += OnDrainTick;

        Closed += (_, _) => _session.Dispose();
    }

    /// <summary>
    /// 中文：
    ///   窗口句柄已就绪时挂上窗口过程钩子。
    ///
    ///   WPF 的 Window 在此之前没有 HWND，而 RIDEV_INPUTSINK 注册和接收
    ///   WM_INPUT 都需要一个真实的窗口句柄。所以这两件事都不能放在构造函数里。
    /// English:
    ///   Hooks the window procedure once the handle exists. A WPF Window has no HWND
    ///   before this point, and both RIDEV_INPUTSINK registration and receiving WM_INPUT
    ///   need a real one, so neither can happen in the constructor.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        source.AddHook(OnWindowMessage);
    }

    /// <summary>
    /// 中文：
    ///   窗口过程钩子，只关心 WM_INPUT。
    ///
    ///   ★ 时间戳必须是认出消息之后做的**第一件事**。本工具唯一要测的就是
    ///     两条通道的时差，那个差值可能只有几十微秒；时间戳每晚取一步，
    ///     测出来的就多混进一分我们自己代码的耗时。
    ///
    ///   不把消息标记为已处理：本工具只是旁听，WM_INPUT 该怎么走还怎么走。
    /// English:
    ///   The window procedure hook, interested only in WM_INPUT.
    ///
    ///   The timestamp must be the first thing done after recognizing the message. The
    ///   inter-channel delta this tool measures may be only tens of microseconds, and
    ///   every step taken before the timestamp folds more of our own cost into it.
    ///
    ///   The message is not marked handled: this tool listens in and lets WM_INPUT
    ///   proceed exactly as it would have.
    /// </summary>
    private IntPtr OnWindowMessage(
        IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == RawInputKeyboardListener.WindowMessage)
        {
            var timestamp = Stopwatch.GetTimestamp();
            _session.HandleRawInput(lParam, timestamp);
        }

        return IntPtr.Zero;
    }

    private void OnStartClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var windowHandle = new WindowInteropHelper(this).Handle;
            _session.Start(windowHandle);
            _drainTimer.Start();

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            StatusText.Text = $"观测中 / Observing — 开始于 {_session.StartedAt:HH:mm:ss}";

            RefreshDeviceList();
        }
        catch (Exception exception)
        {
            // 安装失败最常见的原因是权限不匹配（规格 §2.1 假设 A2）。
            // 把原始异常信息原样呈现，不要包装成"启动失败"这种毫无信息量的提示。
            // The usual cause is a privilege mismatch (spec §2.1, A2). Show the original
            // message rather than wrapping it into an uninformative "failed to start".
            MessageBox.Show(
                this,
                $"无法开始观测 / Could not start observing:\n\n{exception.Message}",
                "Scanner Helper 4a",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnStopClicked(object sender, RoutedEventArgs e)
    {
        _drainTimer.Stop();
        _session.Stop();

        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        StatusText.Text = "已停止 / Stopped";

        RefreshStatistics();
    }

    private void OnResetClicked(object sender, RoutedEventArgs e)
    {
        _session.Reset();
        _logRows.Clear();
        _deviceRows.Clear();
        _firstTimestamp = null;
        _sequenceNumber = 0;

        RefreshStatistics();
    }

    /// <summary>
    /// 中文：重新枚举当前连接的键盘类设备。
    ///       拔插扫码枪之后点一下，就能对比设备路径是否变了——这是 4a 第 4 问
    ///       的直接做法。
    /// English: Re-enumerates attached keyboard-class devices. Click after replugging
    ///          the scanner to compare device paths, which is 4a's fourth question done
    ///          directly.
    /// </summary>
    private void OnRefreshDevicesClicked(object sender, RoutedEventArgs e) => RefreshDeviceList();

    private void OnDrainTick(object? sender, EventArgs e)
    {
        var drained = _session.Drain();
        foreach (var observedEvent in drained)
        {
            AppendLogRow(observedEvent);
        }

        if (drained.Count > 0)
        {
            RefreshStatistics();

            if (AutoScrollCheckBox.IsChecked == true && _logRows.Count > 0)
            {
                EventListView.ScrollIntoView(_logRows[^1]);
            }
        }
    }

    /// <summary>
    /// 中文：
    ///   把一个观测事件追加到日志。
    ///   超过上限时从头部丢弃最旧的行，保持界面响应。
    /// English:
    ///   Appends one observed event to the log, discarding the oldest rows past the cap
    ///   so the UI stays responsive.
    /// </summary>
    private void AppendLogRow(in ObservedInputEvent observedEvent)
    {
        _firstTimestamp ??= observedEvent.Timestamp;
        _sequenceNumber++;

        var relativeMilliseconds =
            ObservationSession.TicksToMilliseconds(observedEvent.Timestamp - _firstTimestamp.Value);

        var flags = observedEvent.IsInjected ? "INJECTED" : string.Empty;

        _logRows.Add(new EventLogRow(
            RelativeMilliseconds: relativeMilliseconds.ToString("0.000", CultureInfo.InvariantCulture),
            Channel: observedEvent.Channel == InputChannel.Hook ? "HOOK" : "RAW",
            Key: DescribeKey(observedEvent.VirtualKey, observedEvent.ScanCode),
            Direction: observedEvent.IsKeyUp ? "up" : "down",
            Device: observedEvent.Channel == InputChannel.RawInput
                ? _session.ResolveDisplayName(observedEvent.DeviceHandle)
                : "—",
            Flags: flags));

        while (_logRows.Count > MaximumLogRows)
        {
            _logRows.RemoveAt(0);
        }
    }

    /// <summary>
    /// 中文：
    ///   把虚拟键码渲染成人能读的形式。
    ///
    ///   刻意同时给出名字与扫描码。修饰键、CapsLock、终止回车的行为是 4a 的
    ///   第 5 问，而那需要人去看日志——只给一个数字，读的人得一边翻表一边看。
    /// English:
    ///   Renders a virtual key readably, giving both a name and the scan code
    ///   deliberately. Question 5 — how modifiers, CapsLock and the terminating Enter
    ///   behave — is answered by a person reading this log, and a bare number would have
    ///   them consulting a table line by line.
    /// </summary>
    private static string DescribeKey(ushort virtualKey, ushort scanCode)
    {
        var name = virtualKey switch
        {
            0x0D => "Enter",
            0x09 => "Tab",
            0x10 => "Shift",
            0x11 => "Ctrl",
            0x12 => "Alt",
            0x14 => "CapsLock",
            0x1B => "Esc",
            0x20 => "Space",
            0xA0 => "LShift",
            0xA1 => "RShift",
            >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
            >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
            >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}",
            _ => $"0x{virtualKey:X2}",
        };

        return $"{name} ({scanCode:X2})";
    }

    private void RefreshStatistics()
    {
        var pairing = _session.Pairing;
        var deltas = pairing.Pairs.Select(pair => pair.DeltaMicroseconds).ToArray();
        var ordered = pairing.HookFirstCount + pairing.RawInputFirstCount;

        var verdict = ordered == 0
            ? "尚无配对样本 / no paired samples yet"
            : pairing.HookFirstCount > pairing.RawInputFirstCount
                ? "钩子先到占多数 → 决策时拿不到设备身份，需扣留-重放（规格 §5.3）"
                : "Raw Input 先到占多数 → 决策时可能已有设备身份";

        OrderingText.Text =
            $"配对 / paired      {pairing.Pairs.Count}\n"
            + $"钩子先到 / hook   {pairing.HookFirstCount}\n"
            + $"Raw 先到 / raw    {pairing.RawInputFirstCount}\n"
            + "\n"
            + $"时差 / delta (µs, 正数 = 钩子先到)\n"
            + $"  最小 / min      {FormatMicroseconds(Percentiles.Minimum(deltas))}\n"
            + $"  中位 / median   {FormatMicroseconds(Percentiles.Median(deltas))}\n"
            + $"  p99             {FormatMicroseconds(Percentiles.Percentile(deltas, 0.99))}\n"
            + $"  最大 / max      {FormatMicroseconds(Percentiles.Maximum(deltas))}\n"
            + "\n"
            + verdict;

        var dropped = _session.DroppedEventCount;
        var unpairedHook = pairing.UnpairedHookCount;
        var unpairedRaw = pairing.UnpairedRawInputCount;
        var isClean = dropped == 0 && unpairedHook == 0 && unpairedRaw == 0;

        ValidityText.Text =
            $"缓冲区丢弃 / dropped        {dropped}\n"
            + $"钩子未配对 / hook only      {unpairedHook}\n"
            + $"Raw 未配对 / raw only       {unpairedRaw}\n"
            + "\n"
            + (isClean
                ? "两条通道逐个事件对得上，数据完整。"
                : "⚠️ 数据不完整。未配对不为零可能比时序本身更值得查——\n"
                  + "先弄清是哪一类按键只出现在一条通道上。");

        ValidityBorder.Background = isClean
            ? new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6))
            : new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEA));

        RefreshDeviceStatistics();
    }

    private void RefreshDeviceStatistics()
    {
        _deviceRows.Clear();

        foreach (var device in _session.Devices.OrderByDescending(item => item.KeyDownCount))
        {
            var statistics = device.BuildIntervalStatistics();
            _deviceRows.Add(new DeviceRow(
                Name: device.DisplayName,
                VendorProduct: device.VendorProduct,
                Characters: device.KeyDownCount.ToString(CultureInfo.InvariantCulture),
                Bursts: statistics.BurstCount.ToString(CultureInfo.InvariantCulture),
                LargestBurst: statistics.LargestBurstSize.ToString(CultureInfo.InvariantCulture),
                MedianInterval: statistics.MedianMilliseconds is { } median
                    ? median.ToString("0.00", CultureInfo.InvariantCulture)
                    : "—"));
        }
    }

    /// <summary>
    /// 中文：
    ///   把当前连接的键盘设备填进设备表。
    ///
    ///   与统计表里的设备不同：这里是"系统里现在接着的全部键盘"，包括本次
    ///   一次都没按过的。笔记本内置键盘是否被 Raw Input 枚举出来、它的设备路径
    ///   长什么样（4a 第 6 问），要看的就是这一份。
    /// English:
    ///   Fills the device table with the currently attached keyboards, including those
    ///   that produced no keystroke this run. Whether the laptop's built-in keyboard is
    ///   enumerated at all and what its device path looks like — 4a's sixth question —
    ///   is read from here.
    /// </summary>
    private void RefreshDeviceList()
    {
        _deviceRows.Clear();

        foreach (var (handle, identity) in _session.EnumerateAttachedKeyboards())
        {
            var vendorProduct = identity.VendorId is null && identity.ProductId is null
                ? "—"
                : $"VID_{identity.VendorId ?? "?"} PID_{identity.ProductId ?? "?"}";

            _deviceRows.Add(new DeviceRow(
                Name: identity.FriendlyName ?? $"0x{handle:X}",
                VendorProduct: vendorProduct,
                Characters: "—",
                Bursts: "—",
                LargestBurst: "—",
                MedianInterval: "—"));
        }
    }

    /// <summary>
    /// 中文：
    ///   导出测量报告。
    ///   步骤：
    ///     1. 机器描述为空时拒绝导出——报告里没有机器信息，读者就无从判断
    ///        这批数据代表哪台硬件，而事件时序恰恰是机器的性质；
    ///     2. 渲染 Markdown；
    ///     3. 写到 docs/ 下，文件名带时间戳，避免覆盖前一次的测量。
    ///
    ///   步骤 3 带时间戳而不是固定文件名：拔插前后、重启前后要各测一轮再对比，
    ///   固定文件名会让后一次直接冲掉前一次，而那正是要对比的对象。
    /// English:
    ///   Exports the measurement report.
    ///   Steps: (1) refuse to export without a machine description — without it a reader
    ///   cannot tell which hardware the data describes, and event timing is a property of
    ///   the hardware; (2) render the Markdown; (3) write under docs/ with a timestamped
    ///   name so earlier runs are not overwritten.
    ///
    ///   Step 3 uses a timestamp rather than a fixed name because runs before and after a
    ///   replug or a reboot are meant to be compared, and a fixed name would have the
    ///   later run erase exactly what it is being compared against.
    /// </summary>
    private void OnExportClicked(object sender, RoutedEventArgs e)
    {
        // 步骤 1 / Step 1
        if (string.IsNullOrWhiteSpace(MachineTextBox.Text))
        {
            MessageBox.Show(
                this,
                "请先填写测量机器。\n\n"
                + "事件时序是机器的性质，不是代码的性质。报告里若不写清是在哪台机器上测的，"
                + "读者会默认它代表现场机器——而开发机与试点机往往不是同一台硬件。\n\n"
                + "Please state the machine first. Event timing is a property of the machine, "
                + "not of the code, and an unlabelled report will be read as if it described "
                + "the pilot hardware.",
                "Scanner Helper 4a",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            MachineTextBox.Focus();
            return;
        }

        try
        {
            // 步骤 2 / Step 2
            var markdown = MeasurementReport.Build(_session, MachineTextBox.Text, NotesTextBox.Text);

            // 步骤 3 / Step 3
            var directory = ResolveReportDirectory();
            Directory.CreateDirectory(directory);

            var path = Path.Combine(
                directory, $"TASK_4A_MEASUREMENTS_{DateTime.Now:yyyyMMdd-HHmmss}.md");

            File.WriteAllText(path, markdown);

            ExportHintText.Text = $"已导出 / Exported:\n{path}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                this,
                $"导出失败 / Export failed:\n\n{exception.Message}",
                "Scanner Helper 4a",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 中文：
    ///   找到仓库的 docs/ 目录。
    ///   从可执行文件所在目录向上找，直到找到含 docs 的那一层。找不到就退回
    ///   可执行文件旁边——导不出报告比导到别处糟得多，测量数据不该因为路径
    ///   问题就丢掉。
    /// English:
    ///   Locates the repository's docs/ directory by walking up from the executable, and
    ///   falls back to beside the executable when it cannot be found. Failing to export
    ///   at all would be far worse than exporting somewhere else; measurement data should
    ///   not be lost to a path problem.
    /// </summary>
    private static string ResolveReportDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "docs");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static string FormatMicroseconds(double? value)
        => value is null ? "—" : value.Value.ToString("0.0", CultureInfo.InvariantCulture);
}

/// <summary>
/// 中文：事件日志的一行。
/// English: One row of the event log.
/// </summary>
public sealed record EventLogRow(
    string RelativeMilliseconds,
    string Channel,
    string Key,
    string Direction,
    string Device,
    string Flags);

/// <summary>
/// 中文：设备表的一行。
/// English: One row of the device table.
/// </summary>
public sealed record DeviceRow(
    string Name,
    string VendorProduct,
    string Characters,
    string Bursts,
    string LargestBurst,
    string MedianInterval);
