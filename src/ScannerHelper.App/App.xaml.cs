// =============================================================================
// App.xaml.cs
//
// 中文：
//   程序的启动与收尾：单实例、读配置、定语言、建会话、连串口、开窗口。
//
//   ★ 单实例是**必需**的，不是习惯。
//
//     串口是独占的：第二个实例连不上同一个端口，只会得到"端口被占用"。更糟的是
//     两个实例都在往业务软件里发内容——工人扫一枪可能收到两份，而且谁也说不清
//     是哪一个发的。所以第二个实例直接告诉工人"已经在运行了"然后退出。
//
//   ★ 连不上串口**不是**启动失败。
//
//     枪没插、端口号变了（换了个 USB 口 COM3 就可能变成 COM7）、被别的程序
//     占着——每一种在现场都会发生，而且都是工人自己能解决的。程序照常开起来，
//     以"未连接"的样子显示，让他去设置里选端口。启动直接失败等于把一件小事变成
//     一个需要找人的事故。
//
//   ★ 配置损坏也不阻止启动（规格 §14）。
//
//     JsonSettingsStore 会退回默认值并报告它做了什么；这里把那件事告诉工人，
//     而不是假装无事发生——他改过的东西没了，他有权知道。
//
// English:
//   Startup and shutdown: single instance, load settings, choose the language, build the session,
//   open the port, show the window.
//
//   Single instance is required rather than customary. A serial port is exclusive, so a second
//   instance cannot open the same one and merely reports "port in use" — and worse, two instances
//   would both be typing into the business application, so one scan could arrive twice with no way
//   to tell which sent what. The second instance therefore says so and exits.
//
//   Failing to open the port is not a failed startup. The scanner is unplugged, the number changed
//   (a different USB socket can turn COM3 into COM7), another program holds it — each happens on
//   site and each is something the operator can fix. The program opens anyway, showing itself as
//   disconnected, and lets them pick a port in Settings. Refusing to start would turn a small
//   matter into one that needs somebody called.
//
//   Damaged configuration likewise does not stop startup (spec §14): JsonSettingsStore falls back
//   to defaults and reports what it did, and that is passed on to the operator rather than
//   glossed over — their changes are gone and they are entitled to know.
//
// 包含的成员 / Members in this file:
//   OnStartup / OnExit
//   Settings / Session   全局可达的两样东西
// =============================================================================

using System.IO;
using System.Reflection;
using System.Windows;
using ScannerHelper.Core.Diagnostics;
using ScannerHelper.App.Localization;
using ScannerHelper.Core.Settings;

namespace ScannerHelper.App;

/// <summary>
/// 中文：应用程序。
/// English: The application.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 中文：
    ///   单实例互斥体的名字。带 Local\ 前缀，作用域是当前登录会话——
    ///   同一台机器上两个工人各自登录、各用各的扫码枪是合理的，不该互相挡住。
    /// English:
    ///   The single-instance mutex name. The Local\ prefix scopes it to the logon session: two
    ///   operators logged in on one machine with their own scanners is reasonable and must not
    ///   block one another.
    /// </summary>
    private const string SingleInstanceMutexName = @"Local\ScannerHelper.SingleInstance";

    private Mutex? _singleInstanceMutex;
    private RollingFileLog? _log;
    private JsonSettingsStore? _settingsStore;
    private SettingsRecoveredEventArgs? _recovery;

    /// <summary>
    /// 中文：当前配置。窗口通过它读写设置。
    /// English: The current settings, read and written by the windows.
    /// </summary>
    public AppSettings Settings { get; private set; } = new();

    /// <summary>
    /// 中文：当前会话。
    /// English: The current session.
    /// </summary>
    public ScannerSession? Session { get; private set; }

    /// <summary>
    /// 中文：诊断日志（规格 §15）。设置界面用它开关遮码、打开日志目录。
    /// English: The diagnostic log (spec §15); Settings uses it to toggle masking and open the
    ///          folder.
    /// </summary>
    public RollingFileLog? Log => _log;

    /// <summary>
    /// 中文：把配置存回磁盘。设置窗口保存之后调用。
    /// English: Writes the settings back to disk; called after the settings window saves.
    /// </summary>
    public void SaveSettings() => _settingsStore?.Save(Settings);

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // 步骤 0 —— 界面线程上的漏网异常 / Step 0 — unhandled exceptions on the UI thread
        DispatcherUnhandledException += OnUnhandledException;

        // ★ 打包用的旁路：--sheets <路径> 只写出条码页然后退出。
        //
        //   放在**最前面**，在单实例互斥体和串口之前。打包时工位上很可能正开着
        //   一个实例——那时去抢互斥体会弹"已经在运行"的框，去开串口会失败，
        //   而这件事和它们都没有关系：它只是把一份 HTML 写到磁盘上。
        //
        // A packaging bypass: --sheets <path> writes the sheet and exits. It comes first, before the
        // single-instance mutex and the serial port, because an instance is likely already running
        // when a package is built — taking the mutex would raise "already running" and opening the
        // port would fail, neither of which has anything to do with writing one HTML file to disk.
        if (e.Args is ["--sheets", var sheetPath])
        {
            var sheetFailure = CommandSheet.Write(sheetPath);
            Shutdown(sheetFailure is null ? 0 : 1);
            return;
        }

        // 步骤 1 —— 单实例 / Step 1 — single instance
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirst);

        if (!isFirst)
        {
            // 语言还没定，这里用系统语言选一次，好让这条提示至少是工人看得懂的。
            // The language is not settled yet; deriving it from the system at least makes this one
            // message readable to the operator.
            Localizer.Initialize(persistedLanguage: null);

            MessageBox.Show(
                Localizer.Get("AlreadyRunningMessage"),
                Localizer.Get("AlreadyRunningTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown();
            return;
        }

        // 步骤 2 —— 读配置（损坏就退回默认并记下，规格 §14）
        // Step 2 — load settings, falling back to defaults on damage and recording it (spec §14)
        _settingsStore = new JsonSettingsStore(JsonSettingsStore.DefaultSettingsFilePath);
        _settingsStore.SettingsRecovered += (_, args) => _recovery = args;
        Settings = _settingsStore.Load();

        // 步骤 3 —— 定语言（规格 §12）
        // Step 3 — settle the language (spec §12)
        Localizer.Initialize(Settings.Language);

        // 步骤 3.5 —— 建日志。**在会话之前**，因为会话从第一次连接起就要记东西。
        //
        // ★ 日志放在配置文件旁边而不是程序目录下：程序可能装在 Program Files
        //   这种普通权限写不进去的地方，而本程序绝不提权（规格 §2.1 假设 A2）。
        //   写不进去的日志等于没有日志，而且失败得毫无声息。
        // Step 3.5 — the log, before the session, which records from its first connection onward.
        // It lives beside the settings file rather than beside the executable: the program may be
        // installed under Program Files, which is not writable without elevation, and this program
        // never elevates (spec §2.1, assumption A2). A log that cannot be written is no log at all,
        // and it fails silently.
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

        _log = new RollingFileLog(
            Path.Combine(
                Path.GetDirectoryName(JsonSettingsStore.DefaultSettingsFilePath)
                    ?? Path.GetTempPath(),
                "logs"),
            version,
            Settings.Diagnostics.LogRetentionDays,
            Settings.Diagnostics.MaskBarcodeData);

        _log.Write(new DiagnosticEvent(
            DateTimeOffset.Now, DiagnosticEventKind.Started, Detail: version));

        // 步骤 4 —— 建会话并尝试连接
        // Step 4 — build the session and try to connect
        Session = new ScannerSession(Settings, _log);

        var failure = string.IsNullOrWhiteSpace(Settings.SerialPort.PortName)
            ? null
            : Session.Connect();

        // 步骤 5 —— 开窗口
        // Step 5 — show the window
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // 步骤 6 —— 有话要说的，等窗口出来之后再说
        //
        // ★ 顺序有意为之：先让主窗口出现，再弹提示。反过来的话，工人先看到一个
        //   没有上下文的对话框，关掉之后才看到程序本身——"这是什么东西"是他会
        //   问的第一个问题，而那本可以避免。
        // Step 6 — anything to report is reported after the window exists. Deliberately in that
        // order: otherwise the operator meets a dialog with no context and only sees the program
        // after dismissing it, and "what is this" becomes their first question when it need not be.
        if (_recovery is { } recovery)
        {
            ReportRecovery(window, recovery);
        }

        // ★ 启动时连不上**不弹对话框**。
        //
        //   有了自动重连之后，这件事三秒之内多半自己就好了：枪还没插稳、系统刚
        //   唤醒设备还没枚举完、端口号从 COM3 变成了 COM7——每一种都会被重连
        //   那一轮收拾掉。为一件自愈的事竖一个必须点掉的模态框，是在替工人制造
        //   工作，而且是他一天里要遇到很多次的那种。
        //
        //   界面本来就已经把这件事说清楚了：整块面板变灰、带红色顶栏、写着
        //   "扫码枪未连接"。真连不上时那个状态会一直留在那儿，比一个被点掉就
        //   再也看不见的对话框更持久、也更诚实。
        //
        //   对话框留给**工人主动发起**的操作：在设置里保存、点"连接它"——
        //   那时他正等着一个答复，沉默才是错的。
        // A failed connect at startup raises no dialog. With reconnection in place it usually fixes
        // itself within seconds — the scanner is not seated yet, the system just woke and devices
        // are still enumerating, the number moved from COM3 to COM7 — and every one of those is
        // handled by the next reconnect pass. Putting a modal box in front of something that heals
        // itself manufactures work for the operator, of a kind they would meet many times a day.
        //
        // The UI already says it: the panel turns slate with a red top bar reading "scanner not
        // connected". If it genuinely cannot connect, that state stays, which is more durable and
        // more honest than a dialog that disappears once dismissed.
        //
        // Dialogs are for actions the operator initiated — saving settings, clicking "connect to
        // it" — where they are waiting for an answer and silence would be the wrong reply.
        _ = failure;
    }

    /// <summary>
    /// 中文：
    ///   告诉工人配置被恢复过了（规格 §14）。
    ///   备份路径存在时一并给出——他改过的东西还在那个文件里，这句话决定了他是
    ///   "重新配一遍"还是"找回来"。
    /// English:
    ///   Tells the operator the configuration was recovered (spec §14), naming the backup when one
    ///   exists: their changes are still in that file, and this sentence decides whether they
    ///   reconfigure from scratch or recover.
    /// </summary>
    private static void ReportRecovery(Window owner, SettingsRecoveredEventArgs recovery)
    {
        var message = recovery.BackupPath is { } backupPath
            ? $"{recovery.Reason}\n\n{backupPath}"
            : recovery.Reason.ToString();

        MessageBox.Show(
            owner, message, Localizer.Get("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// 中文：
    ///   界面线程上没人接的异常。
    ///
    ///   ★ 这道网是 2026-09-15 一次真实崩溃换来的：小窗的 × 触发了一处非法的
    ///     重入关闭，WPF 抛异常、没人接，进程当场死掉。而那次崩溃**在诊断日志里
    ///     没有留下任何痕迹**——记录停在 Connected 那一行就没有了。事后看日志的
    ///     人会以为程序还在正常运行，这正是规格 §15 要回答却答不上来的情形。
    ///
    ///   ★ 先记日志，再告诉工人，最后**干净地退出**。
    ///
    ///     不吞掉异常继续跑：出了未知异常之后程序的状态是未定义的，而这个程序
    ///     负责往业务软件里打字，带病运行可能把错的东西发进仓库系统——比停下来
    ///     坏得多。
    ///
    ///     也不放任它崩：放任的话 Windows 错误报告会让窗口僵在那里几十秒收集转储，
    ///     工人看到的是一个没有任何解释的死窗口。主动退出让这件事在一秒内结束，
    ///     并且给出一句人话。
    /// English:
    ///   An exception nobody caught on the UI thread.
    ///
    ///   This net was paid for by a real crash on 2026-09-15: the compact window's close button
    ///   triggered an illegal re-entrant Close, WPF threw, nothing caught it, and the process died —
    ///   leaving no trace in the diagnostic log, whose record simply stopped after Connected. Anyone
    ///   reading it afterwards would assume the program was still running, exactly the situation
    ///   spec §15 exists to explain.
    ///
    ///   The handler logs, tells the operator, and exits cleanly. It does not swallow and continue:
    ///   after an unknown exception the program's state is undefined, and this program types into
    ///   the business application — carrying on could send wrong data into the warehouse system,
    ///   which is worse than stopping. Nor does it let the crash stand: Windows Error Reporting then
    ///   freezes the window for tens of seconds collecting a dump, leaving the operator with a dead
    ///   window and no explanation. Exiting deliberately ends it in a second, with a sentence a
    ///   person can read.
    /// </summary>
    private void OnUnhandledException(
        object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        try
        {
            _log?.Write(new DiagnosticEvent(
                DateTimeOffset.Now,
                DiagnosticEventKind.Fault,
                Detail: e.Exception.ToString()));
        }
        catch (Exception)
        {
            // 记不下来也要继续走完退出流程。见下面那句：现在最要紧的是干净地停下来。
            // Even if it cannot be recorded, the shutdown must still run: stopping cleanly is what
            // matters now.
        }

        try
        {
            MessageBox.Show(
                Localizer.Get("FaultMessage"),
                Localizer.Get("FaultTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception)
        {
            // 连提示框都弹不出来，就别再折腾了，直接退。
            // If even the dialog fails, stop trying and just exit.
        }

        Shutdown();
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _log?.Write(new DiagnosticEvent(DateTimeOffset.Now, DiagnosticEventKind.Stopped));

        Session?.Dispose();

        // ★ 退出时存一次配置：窗口位置、语言、模式以外的一切都在这里落盘。
        //   模式**不存**（规格 §5.2：启动恒为 SN），暂停状态也不存（规格 §5.7）。
        // Settings are written once on exit: window positions, language, everything except the
        // mode, which is never persisted (spec §5.2: every launch starts in SN), and the paused
        // state, which is likewise never persisted (spec §5.7).
        if (_settingsStore is not null)
        {
            try
            {
                _settingsStore.Save(Settings);
            }
            catch (IOException)
            {
                // 退出时存不下配置，没有任何补救办法，也没有人在看提示框。
                // 记住的是"下次启动用默认值"，而那不足以打断关机。
                // Nothing can be done about a failed write on exit and nobody is watching a dialog.
                // The consequence is defaults next launch, which does not justify blocking shutdown.
            }
        }

        // ★ 日志最后释放：上面每一步都可能还要记东西，而 Dispose 会等队列排空。
        // The log is disposed last: every step above may still write, and Dispose drains the queue.
        _log?.Dispose();

        _singleInstanceMutex?.Dispose();
    }
}
