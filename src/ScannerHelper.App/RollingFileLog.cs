// =============================================================================
// RollingFileLog.cs
//
// 中文：
//   按日期滚动的文件日志（规格 §15）。
//
//   ★ 写盘在**后台线程**上，调用方立刻返回（规格 §15 最后一句）。
//
//     扫描是在串口读取线程上处理的，那条线程一停，下一枪就要排队。磁盘偶尔会卡
//     ——杀毒软件正在扫这个目录、机械盘寻道、日志目录被重定向到了网络盘——而
//     一次几十毫秒的写入卡顿会直接变成工人手上的停顿。
//
//     所以 Write 只把一行塞进队列就回来，真正的落盘由一条自己的线程做。
//
//   ★ 队列有上限，满了**丢新的**并记一笔。
//
//     没有上限的话，磁盘写不进去（盘满、权限没了、网络盘断了）时队列会一直涨到
//     内存耗尽——程序会因为"日志写不了"而崩溃，而日志本来只是个辅助功能。
//
//     满了丢新的而不是丢旧的：出问题的**开头**通常比结尾更能说明问题，而队列
//     堵住这件事本身也会被记下来，读日志的人不会以为那段时间什么都没发生。
//
//   ★ 保留天数到期就删（规格 §15：防止无限增长）。
//
//     只在启动时和跨天时各清一次。每写一行都去扫一遍目录，等于把一个每天一次的
//     动作做上几万次。
//
//   ★ 日志写不了绝不打断工人。
//
//     整条路径上的异常全部吞掉。盘满、目录被组策略锁住、文件被杀毒软件占着——
//     每一种都可能发生，而没有一种值得让一个正在扫货的程序弹框或者停下来。
//     日志是辅助通道，扫码才是主线。
//
// English:
//   A date-rolling file log (spec §15).
//
//   Writing happens on a background thread and the caller returns immediately (spec §15's closing
//   line). Scans are processed on the serial reader thread, and stalling it queues the next scan
//   behind it; disks stall occasionally — antivirus scanning this directory, a spinning disk
//   seeking, a log directory redirected to a network share — and tens of milliseconds becomes a
//   pause in the operator's hands. So Write enqueues a line and returns, while a thread of its own
//   does the persisting.
//
//   The queue is bounded and drops the newest when full, recording that it did. Unbounded, a disk
//   that cannot be written to — full, permissions revoked, network share gone — would grow the
//   queue until memory ran out, crashing the program over a feature that only assists it. Newest
//   rather than oldest, because the beginning of a problem usually explains more than its end; and
//   the blockage itself is recorded, so a reader does not conclude that nothing happened.
//
//   Files past the retention age are deleted (spec §15: prevent unlimited growth), swept at startup
//   and at each date change — sweeping the directory on every line would turn a once-a-day action
//   into tens of thousands.
//
//   Nothing here ever interrupts the operator: every exception along the path is swallowed. A full
//   disk, a directory locked by group policy, a file held by antivirus — each can happen and none
//   justifies a dialog or a pause in a program that is scanning goods. The log assists; scanning is
//   the point.
//
// 包含的成员 / Members in this file:
//   Write     记一行（立刻返回）
//   Dispose   把队列排空再收工
// =============================================================================

using System.Collections.Concurrent;
using System.IO;
using System.Text;
using ScannerHelper.Core.Diagnostics;

namespace ScannerHelper.App;

/// <summary>
/// 中文：按日期滚动的文件日志。
/// English: A date-rolling file log.
/// </summary>
public sealed class RollingFileLog : IDiagnosticLog, IDisposable
{
    /// <summary>
    /// 中文：
    ///   队列上限。一枪一行，两万行够记很久——而到达这个数只可能是磁盘一直写不进去。
    /// English:
    ///   The queue cap. One line per scan, and twenty thousand covers a long time; reaching it can
    ///   only mean the disk has been unwritable throughout.
    /// </summary>
    private const int MaximumQueuedLines = 20_000;

    /// <summary>
    /// 中文：队列空时读取线程等多久再看一次。不用忙等，也不必太灵敏——
    ///       日志晚半秒落盘没有任何影响。
    /// English: How long the writer waits when the queue is empty. No busy-waiting is needed and no
    ///          great responsiveness either: half a second of lag in a log changes nothing.
    /// </summary>
    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(500);

    private readonly BlockingCollection<string> _queue =
        new(new ConcurrentQueue<string>(), MaximumQueuedLines);

    private readonly string _directory;
    private readonly string _applicationVersion;
    private readonly Thread _writerThread;

    private DateOnly _currentDate = DateOnly.MinValue;
    private bool _hasReportedOverflow;
    private bool _isDisposed;

    /// <summary>
    /// 中文：
    ///   建好日志并起写入线程。
    ///   输入：directory 日志目录；applicationVersion 程序版本；
    ///         retentionDays 保留天数；isMaskingEnabled 是否遮码。
    ///
    ///   线程是后台线程：忘了 Dispose 也不会让进程退不出去。
    /// English:
    ///   Creates the log and starts its writer thread, a background thread so a missed Dispose
    ///   cannot keep the process alive.
    /// </summary>
    public RollingFileLog(
        string directory, string applicationVersion, int retentionDays, bool isMaskingEnabled)
    {
        _directory = directory;
        _applicationVersion = applicationVersion;
        RetentionDays = retentionDays;
        IsMaskingEnabled = isMaskingEnabled;

        _writerThread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "ScannerHelper diagnostics",
        };
        _writerThread.Start();
    }

    /// <summary>
    /// 中文：
    ///   是否遮码（规格 §15）。设置里可以随时改，因此不是只读的。
    /// English: Whether to mask (spec §15). Changeable from Settings at any time, hence not
    ///          read-only.
    /// </summary>
    public bool IsMaskingEnabled { get; set; }

    /// <summary>
    /// 中文：保留多少天。
    /// English: How many days of logs to keep.
    /// </summary>
    public int RetentionDays { get; set; }

    /// <summary>
    /// 中文：日志目录，供界面上"打开日志文件夹"用。
    /// English: The log directory, for the UI's "open the log folder".
    /// </summary>
    public string Directory => _directory;

    /// <inheritdoc />
    public void Write(DiagnosticEvent diagnosticEvent)
    {
        if (_isDisposed)
        {
            return;
        }

        var line = diagnosticEvent.ToLine(_applicationVersion, IsMaskingEnabled);

        // ★ TryAdd 而不是 Add：Add 在队列满时会**阻塞调用方**，而调用方可能正是
        //   串口读取线程——那样"日志写不动"就变成了"扫码卡住"，一个辅助功能拖垮
        //   了主线。
        // TryAdd rather than Add: Add blocks the caller when the queue is full, and that caller may
        // be the serial reader thread — turning "the log is stuck" into "scanning is stuck", an
        // assisting feature dragging down the point.
        if (_queue.TryAdd(line))
        {
            return;
        }

        if (_hasReportedOverflow)
        {
            return;
        }

        _hasReportedOverflow = true;

        // 队列堵住这件事本身要留一笔，否则读日志的人会以为那段时间什么都没发生。
        // The blockage itself is recorded, or a reader concludes nothing happened in that stretch.
        _queue.TryAdd(new DiagnosticEvent(
            DateTimeOffset.Now,
            DiagnosticEventKind.Fault,
            Detail: "日志队列已满，后续记录被丢弃。 The log queue filled; later entries were dropped.")
            .ToLine(_applicationVersion, IsMaskingEnabled));
    }

    /// <summary>
    /// 中文：
    ///   收工：让写入线程把队列排空再退出。
    ///
    ///   ★ 要等它排空，但**有上限**。退出时最后那几条往往正是"为什么退出"的答案，
    ///     丢掉可惜；但一个卡住的磁盘不该让程序关不掉——工人点了退出，程序就该退。
    /// English:
    ///   Lets the writer drain the queue and stop.
    ///
    ///   Draining is waited for, but with a cap. The last entries before an exit are often the
    ///   answer to why it exited and are worth keeping; a stalled disk must not stop the program
    ///   from closing, though — the operator clicked exit, and it should exit.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _queue.CompleteAdding();
        _writerThread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    /// <summary>
    /// 中文：写入线程主体。
    /// English: The writer thread's body.
    /// </summary>
    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            TryAppend(line);
        }
    }

    /// <summary>
    /// 中文：
    ///   把一行追加到今天的文件里。跨天时换文件并清理过期的。
    ///   一切异常吞掉——理由见文件头。
    /// English:
    ///   Appends one line to today's file, switching files and sweeping expired ones at a date
    ///   change. Every exception is swallowed; see the file header.
    /// </summary>
    private void TryAppend(string line)
    {
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);

            if (today != _currentDate)
            {
                _currentDate = today;
                System.IO.Directory.CreateDirectory(_directory);
                SweepExpired(today);
            }

            File.AppendAllText(PathFor(today), line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception)
        {
            // 盘满、没权限、被占用、目录被删——都不值得打断工人。见文件头。
            // Full, denied, locked, directory removed — none of it justifies interrupting the
            // operator. See the file header.
        }
    }

    private string PathFor(DateOnly date)
        => Path.Combine(_directory, $"scanner-{date:yyyy-MM-dd}.log");

    /// <summary>
    /// 中文：
    ///   删掉超过保留天数的日志（规格 §15）。
    ///
    ///   ★ 只按**文件名里的日期**判断，不看文件系统的修改时间。修改时间会被拷贝、
    ///     备份、同步软件改掉，而文件名是我们自己写的，说的就是那一天。
    ///   ★ 名字对不上格式的文件一律不动：那个目录里可能有人放了别的东西，
    ///     而一个会删掉自己不认识的文件的清理程序是危险的。
    /// English:
    ///   Deletes logs past the retention age (spec §15).
    ///
    ///   The date comes from the file name rather than the file system's timestamp, which copying,
    ///   backup and sync tools rewrite; the name is ours and says which day it is. Files whose
    ///   names do not match are left alone: somebody may have put something else in that directory,
    ///   and a cleaner that deletes what it does not recognize is dangerous.
    /// </summary>
    private void SweepExpired(DateOnly today)
    {
        var oldestKept = today.AddDays(-Math.Max(1, RetentionDays));

        foreach (var path in System.IO.Directory.GetFiles(_directory, "scanner-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(path);

            if (name.Length != "scanner-yyyy-MM-dd".Length
                || !DateOnly.TryParse(name["scanner-".Length..], out var fileDate))
            {
                continue;
            }

            if (fileDate >= oldestKept)
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // 删不掉就留着。见文件头。
                // Left in place if it cannot be deleted. See the file header.
            }
        }
    }
}
