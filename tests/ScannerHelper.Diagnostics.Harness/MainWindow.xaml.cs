// =============================================================================
// MainWindow.xaml.cs
//
// 中文：
//   诊断工具的主窗口。只负责显示与按钮，不含任何观测逻辑——那些都在
//   ObservationSession 里。4b 要把观测换成拦截时，本文件基本不用动。
//
//   ★ 本窗口**完全不参与捕获**，这是第一轮实测之后改的。
//
//     最初的版本把 Raw Input 注册到本窗口、在 WPF 的窗口过程里接 `WM_INPUT`、
//     并在界面线程上装钩子。结果是扫码枪的段内间隔 p99 量到了 48 毫秒——
//     扫码枪根本不可能有这种停顿，那是界面每 100 毫秒重建列表控件、把消息
//     挤到后面去造成的。`WM_INPUT` 的时间戳只能是"消息循环处理到它的时刻"，
//     界面一忙，量到的就不是硬件的性质而是"界面有多卡"的性质。
//
//     现在捕获整个搬到 InputCaptureThread：一条专用线程，一个仅消息窗口，
//     只泵消息、不碰界面。本窗口只做两件事——每隔一小段时间把环形缓冲区
//     里的事件取出来显示，以及提供几个按钮。
//
//   ★ 界面与捕获之间没有任何同步点。
//
//     捕获线程只往无锁环形缓冲区里写，界面每 100 毫秒来取一次。因此界面
//     再慢也不可能拖住钩子回调——而拖住回调超过 300 毫秒，Windows 会悄悄
//     摘掉钩子（规格 §19.1）。日志列表有条数上限也是同理：让它无限增长，
//     界面迟早会卡，虽然现在卡不到捕获，但会让工具本身没法用。
//
// English:
//   The harness's main window. Display and buttons only; the observation logic lives in
//   ObservationSession, so switching from observation to interception in 4b barely
//   touches this file.
//
//   This window takes no part in capture, which changed after the first measurement
//   run. The original version registered Raw Input to this window, caught WM_INPUT in
//   the WPF window procedure, and installed the hook on the UI thread. The result was a
//   48 ms p99 for the scanner's within-burst interval — impossible for a scanner, and
//   caused by the UI rebuilding list controls every 100 ms and pushing messages behind
//   it. A WM_INPUT timestamp can only be "when the message loop reached it", so a busy
//   UI turns the measurement from a property of the hardware into a property of how
//   sluggish the UI is.
//
//   Capture now lives entirely in InputCaptureThread: a dedicated thread with a
//   message-only window that pumps messages and touches no UI. This window only drains
//   the ring buffer periodically for display, and offers a few buttons.
//
//   There is no synchronization point between the UI and capture. The capture thread
//   only writes into a lock-free ring buffer and the UI collects every 100 ms, so no
//   amount of UI slowness can stall the hook callback — and stalling it past 300 ms has
//   Windows silently remove the hook (spec §19.1). The log cap follows the same
//   reasoning: unbounded growth would eventually freeze the UI which, while it can no
//   longer reach capture, would make the tool itself unusable.
//
// 包含的成员 / Members in this file:
//   OnDrainTick          定时排空缓冲区并刷新界面
//   OnStartClicked / OnStopClicked / OnResetClicked / OnRefreshDevicesClicked / OnExportClicked
// =============================================================================

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ScannerHelper.Diagnostics.Harness.Observation;
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

    /// <summary>
    /// 中文：统计每隔几个 tick 重算一次。tick 是 100 毫秒，5 次即约半秒——
    ///       对人来说仍然是"实时"，而重算量降到五分之一。
    /// English: How many 100 ms ticks between statistics recomputes. Five is about half a
    ///          second, still live to a person, at a fifth of the work.
    /// </summary>
    private const int StatisticsRefreshEveryNTicks = 5;

    private readonly ObservationSession _session = new();
    private readonly ObservableCollection<EventLogRow> _logRows = [];
    private readonly ObservableCollection<DeviceRow> _deviceRows = [];
    private readonly DispatcherTimer _drainTimer = new();

    /// <summary>
    /// 中文：打开着的 4b 拦截窗口，null 表示没开。留着它是为了保证同一时刻
    ///       只有一个——两个窗口就是两套钩子、两次 Raw Input 注册。
    /// English: The open 4b interception window, or null. Held so that only one can exist at a
    ///          time: two windows would mean two hooks and two raw-input registrations.
    /// </summary>
    private InterceptionWindow? _interceptionWindow;

    /// <summary>
    /// 中文：打开着的串口验证窗口，null 表示没开。同样只允许一个——两个窗口
    ///       会抢同一个串口，而后开的那个只会得到「端口被占用」。
    /// English: The open serial verification window, or null. Only one is allowed: two would
    ///          contend for the same port and the second would only ever see "port in use".
    /// </summary>
    private SerialWindow? _serialWindow;

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

    private int _ticksSinceStatisticsRefresh;

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

    private void OnStartClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            _session.Start();
            _drainTimer.Start();

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;

            // ★ 观测期间不许打开拦截窗口：两个窗口各自装一个低层键盘钩子、
            //   各自注册一次 Raw Input，事件会同时走两条链路。测出来的数字
            //   于是既不是 4a 的、也不是 4b 的，而两边看起来都很正常。
            // Interception may not be opened while observing: two windows would each install a
            // low-level hook and register raw input, sending every event down both chains. The
            // resulting numbers describe neither 4a nor 4b, and both sides look fine.
            InterceptionButton.IsEnabled = false;

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

    /// <summary>
    /// 中文：
    ///   打开 Task 4b 的拦截关卡窗口。
    ///
    ///   ★ 同一时刻只允许开一个。开两个就是两套钩子加两次 Raw Input 注册，
    ///     事件被处理两遍——而两个窗口各自看都很正常，这类错误最难被发现。
    ///
    ///   本窗口在拦截窗口开着的时候禁用「开始观测」，理由同上。
    /// English:
    ///   Opens the Task 4b hard-gate window.
    ///
    ///   Only one at a time: two would mean two hooks and two raw-input registrations with every
    ///   event handled twice — and each window would look entirely normal on its own, which is
    ///   the hardest kind of mistake to notice. Observation is likewise disabled while the
    ///   interception window is open.
    /// </summary>
    private void OnInterceptionClicked(object sender, RoutedEventArgs e)
    {
        if (_interceptionWindow is not null)
        {
            _interceptionWindow.Activate();
            return;
        }

        var window = new InterceptionWindow { Owner = this };
        _interceptionWindow = window;

        StartButton.IsEnabled = false;

        window.Closed += (_, _) =>
        {
            _interceptionWindow = null;
            StartButton.IsEnabled = true;
        };

        window.Show();
    }

    /// <summary>
    /// 中文：
    ///   打开虚拟串口验证窗口（选项 A）。
    ///
    ///   ★ 它不与观测、拦截互斥，因为它**不装钩子、不注册 Raw Input**——
    ///     串口方案的全部意义就在于此：扫码枪不再是键盘，也就没有什么需要拦。
    /// English:
    ///   Opens the virtual COM verification window (Option A).
    ///
    ///   It needs no mutual exclusion with observation or interception because it installs no
    ///   hook and registers no Raw Input — which is the whole point of the serial approach: the
    ///   scanner stops being a keyboard, so there is nothing left to intercept.
    /// </summary>
    private void OnSerialClicked(object sender, RoutedEventArgs e)
    {
        if (_serialWindow is not null)
        {
            _serialWindow.Activate();
            return;
        }

        var window = new SerialWindow { Owner = this };
        _serialWindow = window;
        window.Closed += (_, _) => _serialWindow = null;
        window.Show();
    }

    private void OnStopClicked(object sender, RoutedEventArgs e)
    {
        _drainTimer.Stop();
        _session.Stop();

        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        InterceptionButton.IsEnabled = true;
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

    /// <summary>
    /// 中文：
    ///   定时排空缓冲区并刷新界面。
    ///
    ///   日志每次都追加，统计则每 <see cref="StatisticsRefreshEveryNTicks"/> 次才重算
    ///   一遍。重算要把每台设备的全部事件重新还原成文本，样本攒到上万条之后
    ///   每 100 毫秒做一次是纯粹的浪费。
    ///
    ///   这里已经不会影响测量精度了——捕获跑在专用线程上，界面再慢也拖不到它。
    ///   降频纯粹是为了让工具本身用起来不卡。
    /// English:
    ///   Drains and refreshes on a timer. The log appends every tick while the statistics
    ///   recompute every Nth, since recomputing reconstructs every device's full event
    ///   stream into text — wasteful at 100 ms intervals once samples reach the
    ///   thousands.
    ///
    ///   This no longer affects measurement accuracy: capture runs on its own thread and
    ///   no amount of UI slowness reaches it. The throttle exists purely so the tool
    ///   itself stays responsive.
    /// </summary>
    private void OnDrainTick(object? sender, EventArgs e)
    {
        var drained = _session.Drain();
        foreach (var observedEvent in drained)
        {
            AppendLogRow(observedEvent);
        }

        if (drained.Count == 0)
        {
            return;
        }

        if (AutoScrollCheckBox.IsChecked == true && _logRows.Count > 0)
        {
            EventListView.ScrollIntoView(_logRows[^1]);
        }

        _ticksSinceStatisticsRefresh++;
        if (_ticksSinceStatisticsRefresh < StatisticsRefreshEveryNTicks)
        {
            return;
        }

        _ticksSinceStatisticsRefresh = 0;
        RefreshStatistics();
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

        var disagreements = pairing.VirtualKeyDisagreements;
        var disagreementSummary = disagreements.Count == 0
            ? "虚拟键码分歧 / VK mismatch  无 / none"
            : "虚拟键码分歧 / VK mismatch\n"
              + string.Join(
                  "\n",
                  disagreements
                      .OrderBy(entry => entry.Key)
                      .Select(entry => $"  钩子 0x{entry.Key:X2} ↔ Raw 0x{entry.Value:X2}"))
              + "\n  → 关联必须按扫描码，不能按虚拟键码";

        ValidityText.Text =
            $"缓冲区丢弃 / dropped        {dropped}\n"
            + $"钩子未配对 / hook only      {unpairedHook}\n"
            + $"Raw 未配对 / raw only       {unpairedRaw}\n"
            + "\n"
            + disagreementSummary
            + "\n\n"
            + (isClean
                ? "两条通道逐个事件对得上，数据完整。"
                : "⚠️ 数据不完整。未配对不为零可能比时序本身更值得查——\n"
                  + "先弄清是哪一类按键只出现在一条通道上。");

        ValidityBorder.Background = isClean
            ? new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6))
            : new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEA));

        RefreshDeviceStatistics();
    }

    /// <summary>
    /// 中文：
    ///   刷新设备表，并把"钩子看到的扫描内容"呈现出来。
    ///
    ///   ★ 「内容种类」这一列是当下最该盯的数字。同一个条码扫五十次，这里应当
    ///     恒为 1。一旦大于 1，说明**钩子这一侧收到的就已经不是同一个码**——
    ///     字符丢在扫码枪到 Windows 这一段，而不是丢在下游。
    ///
    ///     这个区别决定产品能不能救：丢在下游，我们吞掉重发就修好了；丢在上游，
    ///     我们只会把错码原样发进业务系统，界面还显示成功（规格 §19.1）。
    /// English:
    ///   Refreshes the device table and surfaces what the hook saw.
    ///
    ///   The "distinct" column is the number to watch. Fifty scans of one barcode should
    ///   hold it at 1. Above 1 means the hook side already received something other than
    ///   the same code — characters lost between the scanner and Windows rather than
    ///   downstream.
    ///
    ///   That distinction decides whether the product can help at all: lost downstream,
    ///   swallowing and re-emitting fixes it; lost upstream, we would emit the wrong code
    ///   and report success (spec §19.1).
    /// </summary>
    private void RefreshDeviceStatistics()
    {
        _deviceRows.Clear();
        var reconstruction = new StringBuilder();

        foreach (var device in _session.Devices.OrderByDescending(item => item.KeyDownCount))
        {
            var statistics = device.BuildIntervalStatistics();
            var scans = device.BuildReconstructedScans();
            var distinctScans = scans.Select(scan => scan.Text).Distinct().Count();

            _deviceRows.Add(new DeviceRow(
                Name: device.DisplayName,
                VendorProduct: device.VendorProduct,
                Characters: device.KeyDownCount.ToString(CultureInfo.InvariantCulture),
                Bursts: statistics.BurstCount.ToString(CultureInfo.InvariantCulture),
                LargestBurst: statistics.LargestBurstSize.ToString(CultureInfo.InvariantCulture),
                MedianInterval: statistics.MedianMilliseconds is { } median
                    ? median.ToString("0.00", CultureInfo.InvariantCulture)
                    : "—",
                DistinctScans: scans.Count == 0
                    ? "—"
                    : distinctScans > 1
                        ? $"⚠ {distinctScans}"
                        : distinctScans.ToString(CultureInfo.InvariantCulture)));

            if (scans.Count == 0)
            {
                continue;
            }

            reconstruction.AppendLine($"{device.DisplayName}  （共 {scans.Count} 次）");

            foreach (var scan in scans.TakeLast(5))
            {
                var terminator = scan.EndedWithEnter ? "⏎" : "· 无回车";
                reconstruction.AppendLine($"  {scan.Text}  {terminator}");
            }

            if (distinctScans > 1)
            {
                reconstruction.AppendLine(
                    $"  ⚠ 还原出 {distinctScans} 种不同内容——钩子这一侧收到的就已经不一致");
            }

            reconstruction.AppendLine();
        }

        var hasInconsistency = _deviceRows.Any(row => row.DistinctScans.StartsWith('⚠'));

        ReconstructionText.Text = reconstruction.Length == 0
            ? "—"
            : reconstruction.ToString().TrimEnd();

        ReconstructionBorder.Background = hasInconsistency
            ? new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEA))
            : new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6));
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
                MedianInterval: "—",
                DistinctScans: "—"));
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
    string MedianInterval,
    string DistinctScans);
