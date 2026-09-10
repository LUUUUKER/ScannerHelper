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
using System.Windows;
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
    /// 中文：把配置存回磁盘。设置窗口保存之后调用。
    /// English: Writes the settings back to disk; called after the settings window saves.
    /// </summary>
    public void SaveSettings() => _settingsStore?.Save(Settings);

    private void OnStartup(object sender, StartupEventArgs e)
    {
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

        // 步骤 4 —— 建会话并尝试连接
        // Step 4 — build the session and try to connect
        Session = new ScannerSession(Settings);

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

        if (failure is { } connectFailure)
        {
            MessageBox.Show(
                window,
                connectFailure.Failure.Message,
                Localizer.Get(connectFailure.TitleKey),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
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

    private void OnExit(object sender, ExitEventArgs e)
    {
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

        _singleInstanceMutex?.Dispose();
    }
}
