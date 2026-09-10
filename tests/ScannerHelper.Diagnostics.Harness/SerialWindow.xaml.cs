// =============================================================================
// SerialWindow.xaml.cs
//
// 中文：
//   虚拟串口验证窗口的代码。
//
//   ★ 它要证明的三件事，缺一不可：
//     1. 扫码枪切到串口模式之后，**不再往业务软件里打字**（在记事本里扫，
//        什么都不出现）；
//     2. 我们能**完整、正确**地收到条码内容；
//     3. 连续扫很多枪，内容**一直**正确——上一套方案就是在这一条上垮的。
//
//   ★ 读取事件在读取线程上触发，界面按 100 毫秒轮询队列。
//     理由和 4b 那个窗口一样：不让界面的工作跑进别人的时间预算里。这里虽然
//     没有钩子回调那么苛刻的预算，但把「谁在哪条线程上」这件事保持一致，
//     比每处各想一遍要可靠。
//
// English:
//   Code for the virtual COM verification window.
//
//   It must prove three things: that a scanner switched to serial mode no longer types into the
//   business application (scanning into Notepad produces nothing); that the barcode arrives
//   complete and correct; and that it stays correct over many consecutive scans — the last being
//   where the previous approach collapsed.
//
//   Read events are raised on the reader thread and the UI polls a queue every 100 ms, as in the
//   4b window. The budget here is not as tight as a hook callback's, but keeping "who runs on
//   which thread" uniform is more reliable than reasoning about it afresh in each place.
//
// 包含的成员 / Members in this file:
//   打开、关闭、清空 / open, close, clear
//   刷新与核对 / refresh and verification
// =============================================================================

using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ScannerHelper.Win32.Serial;

namespace ScannerHelper.Diagnostics.Harness;

/// <summary>
/// 中文：虚拟串口验证窗口。
/// English: The virtual COM verification window.
/// </summary>
public partial class SerialWindow : Window
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 中文：原始字节区最多留多少字符。留太多既没用又拖慢界面。
    /// English: How many characters of raw bytes to keep; more is useless and slows the UI.
    /// </summary>
    private const int MaximumRawCharacters = 8000;

    private readonly SerialScannerReader _reader = new();
    private readonly ObservableCollection<FrameRow> _frameRows = [];
    private readonly ConcurrentQueue<SerialFrameEventArgs> _pendingFrames = new();
    private readonly ConcurrentQueue<byte[]> _pendingChunks = new();
    private readonly StringBuilder _rawBuffer = new();
    private readonly DispatcherTimer _refreshTimer;

    private Exception? _lastFault;
    private int _frameIndex;
    private int _mismatchCount;

    /// <summary>
    /// 中文：构造窗口。
    /// English: Creates the window.
    /// </summary>
    public SerialWindow()
    {
        InitializeComponent();

        FrameListView.ItemsSource = _frameRows;

        _reader.FrameReceived += (_, args) => _pendingFrames.Enqueue(args);
        _reader.ChunkReceived += (_, args) => _pendingChunks.Enqueue(args.Bytes);
        _reader.Faulted += (_, exception) => _lastFault = exception;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = RefreshInterval,
        };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();

        RefreshPorts();
        Refresh();
    }

    private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _refreshTimer.Stop();
        _reader.Dispose();
    }

    /// <summary>
    /// 中文：
    ///   重新列出串口。
    ///
    ///   ★ 找不到扫码枪那个端口时，最可靠的办法是：先列一遍，把枪拔掉再列一遍，
    ///     少掉的那个就是它。猜端口号很容易打开一个别的设备，而那时"打开成功
    ///     但什么都收不到"看起来和"串口方案行不通"一模一样。
    /// English:
    ///   Re-lists the COM ports. When the scanner's port is not obvious, list once, unplug it and
    ///   list again: the one that disappeared is it. Guessing the number easily opens some other
    ///   device, and "opened fine but nothing arrives" then looks exactly like "the serial
    ///   approach does not work".
    /// </summary>
    private void RefreshPorts()
    {
        var previous = PortCombo.SelectedItem as string;

        PortCombo.ItemsSource = SerialScannerReader.ListPorts();

        if (previous is not null && PortCombo.Items.Contains(previous))
        {
            PortCombo.SelectedItem = previous;
        }
        else if (PortCombo.Items.Count > 0)
        {
            PortCombo.SelectedIndex = 0;
        }
    }

    private void OnRefreshPortsClicked(object sender, RoutedEventArgs e) => RefreshPorts();

    /// <summary>
    /// 中文：
    ///   打开端口。
    ///   失败时把原始异常原样显示：最常见的两种是"端口被别的程序占用"和
    ///   "端口不存在"，而这两句话本身就是最有用的线索，包装成"打开失败"
    ///   只会把它盖掉。
    /// English:
    ///   Opens the port, showing the original exception on failure. The two usual causes — the
    ///   port is held by another program, or it does not exist — are themselves the most useful
    ///   clue, and wrapping them into "failed to open" would hide it.
    /// </summary>
    private void OnOpenClicked(object sender, RoutedEventArgs e)
    {
        if (PortCombo.SelectedItem is not string portName)
        {
            MessageBox.Show(
                this,
                "先选一个端口。切到虚拟串口模式之后，把扫码枪插拔一次再点「重新列出」，"
                + "新出现的那个端口就是它。"
                + "\n\nSelect a port first. After switching to virtual COM mode, replug the scanner"
                + " and click Refresh; the port that appears is the one.",
                "Scanner Helper",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var baudRate = int.Parse(
            ((System.Windows.Controls.ComboBoxItem)BaudCombo.SelectedItem).Content.ToString()!,
            CultureInfo.InvariantCulture);

        try
        {
            _lastFault = null;
            _reader.Open(new SerialScannerOptions(portName, baudRate));
        }
        catch (Exception openException)
        {
            MessageBox.Show(
                this,
                "打开端口失败 / Could not open the port:\n\n" + openException.Message,
                "Scanner Helper",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        Refresh();
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        _reader.Close();
        Refresh();
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        _frameRows.Clear();
        _rawBuffer.Clear();
        RawText.Clear();
        _frameIndex = 0;
        _mismatchCount = 0;
        Refresh();
    }

    private void OnExpectedChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => Refresh();

    /// <summary>
    /// 中文：
    ///   把读取线程攒下的东西搬到界面上。
    ///   步骤：
    ///     1. 取走新的帧，逐条核对；
    ///     2. 取走新的原始字节，转成可读形式；
    ///     3. 刷新统计与结论。
    /// English:
    ///   Moves what the reader thread accumulated onto the UI: take new frames and check each,
    ///   take new raw bytes and render them readably, then refresh the counters and verdict.
    /// </summary>
    private void Refresh()
    {
        // 步骤 1 / Step 1
        var expected = ExpectedText.Text;

        while (_pendingFrames.TryDequeue(out var frame))
        {
            _frameIndex++;

            var matches = expected.Length == 0 || frame.Text == expected;
            if (!matches)
            {
                _mismatchCount++;
            }

            _frameRows.Insert(0, new FrameRow(
                Index: _frameIndex,
                Time: DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                Text: frame.Text,
                Length: frame.Text.Length,
                Terminator: frame.Terminator,
                Match: expected.Length == 0 ? "—" : matches ? "✅" : "❌"));
        }

        while (_frameRows.Count > 500)
        {
            _frameRows.RemoveAt(_frameRows.Count - 1);
        }

        // 步骤 2 / Step 2
        var appended = false;
        while (_pendingChunks.TryDequeue(out var chunk))
        {
            _rawBuffer.Append(Describe(chunk));
            appended = true;
        }

        if (appended)
        {
            if (_rawBuffer.Length > MaximumRawCharacters)
            {
                _rawBuffer.Remove(0, _rawBuffer.Length - MaximumRawCharacters);
            }

            RawText.Text = _rawBuffer.ToString();
            RawText.ScrollToEnd();
        }

        // 步骤 3 / Step 3
        UpdateCounters();
        UpdateVerdict(expected);

        OpenButton.IsEnabled = !_reader.IsOpen;
        CloseButton.IsEnabled = _reader.IsOpen;
        PortCombo.IsEnabled = !_reader.IsOpen;
        BaudCombo.IsEnabled = !_reader.IsOpen;

        StatusText.Text = _reader.IsOpen
            ? "已打开，正在读 / Open, reading"
            : "未打开 / Not open";
    }

    /// <summary>
    /// 中文：
    ///   把原始字节转成人能读的形式：可打印字符原样显示，其余用尖括号标出。
    ///
    ///   ★ 这一块是判断波特率对不对的唯一依据。波特率填错**不会报任何错**，
    ///     只会收到一串看起来像随机字节的东西——不把原始字节摆出来，人会以为
    ///     是扫码枪坏了或者串口方案行不通。
    /// English:
    ///   Renders raw bytes readably: printable characters as themselves, everything else in
    ///   angle brackets.
    ///
    ///   This panel is the only way to tell whether the baud rate is right. A wrong baud rate
    ///   raises no error at all and simply yields what look like random bytes; without showing
    ///   them, a person concludes the scanner is broken or the serial approach does not work.
    /// </summary>
    private static string Describe(byte[] chunk)
    {
        var text = new StringBuilder(chunk.Length * 2);

        foreach (var value in chunk)
        {
            switch (value)
            {
                case (byte)'\r':
                    text.Append("<CR>");
                    break;

                case (byte)'\n':
                    text.Append("<LF>\n");
                    break;

                case >= 0x20 and < 0x7F:
                    text.Append((char)value);
                    break;

                default:
                    text.Append(CultureInfo.InvariantCulture, $"<{value:X2}>");
                    break;
            }
        }

        return text.ToString();
    }

    private void UpdateCounters()
    {
        var counters = new StringBuilder();
        counters.AppendLine(CultureInfo.InvariantCulture, $"帧 / Frames    {_reader.FrameCount}");
        counters.AppendLine(CultureInfo.InvariantCulture, $"字节 / Bytes   {_reader.ByteCount}");
        counters.AppendLine(CultureInfo.InvariantCulture, $"错误 / Errors  {_reader.ErrorCount}");
        counters.Append(CultureInfo.InvariantCulture, $"不符 / Mismatch {_mismatchCount}");

        if (_lastFault is { } fault)
        {
            counters.AppendLine();
            counters.Append(CultureInfo.InvariantCulture, $"最近异常 / Last fault: {fault.Message}");
        }

        CountersText.Text = counters.ToString();
    }

    /// <summary>
    /// 中文：
    ///   给出结论。
    ///   没填期望内容就不下结论——工具不该替人猜"正确"是什么样子。
    /// English:
    ///   States the verdict, and states none until an expected value is given: the tool should
    ///   not guess what "correct" looks like on a person's behalf.
    /// </summary>
    private void UpdateVerdict(string expected)
    {
        if (expected.Length == 0)
        {
            VerdictText.Text = "填入条码内容后开始逐帧核对 / Enter the barcode to check each frame";
            VerdictText.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            return;
        }

        if (_frameRows.Count == 0)
        {
            VerdictText.Text = "还没收到任何一帧 / No frames yet";
            VerdictText.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            return;
        }

        var total = _frameRows.Count;

        VerdictText.Text = _mismatchCount == 0
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0} 枪，全部正确 / {0} scans, all correct",
                total)
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0} 枪，其中 {1} 枪不符 / {0} scans, {1} mismatched",
                total,
                _mismatchCount);

        VerdictText.Foreground = _mismatchCount == 0
            ? new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x20))
            : new SolidColorBrush(Color.FromRgb(0xB0, 0x00, 0x20));
    }

    /// <summary>
    /// 中文：帧列表里的一行。
    /// English: One row in the frame list.
    /// </summary>
    private sealed record FrameRow(
        int Index, string Time, string Text, int Length, string Terminator, string Match);
}
