// =============================================================================
// JsonSettingsStoreTests.cs
//
// 中文：
//   配置持久化的行为测试（对应测试清单 S14~S28）。
//
//   这是 Phase A 里唯一碰文件系统的一组。配置文件的可靠性直接关系到工人
//   能否开工——一份读不出来的配置意味着解析规则、扫码枪绑定、语言偏好
//   全部丢失，而这些是技术顾问远程配好的，工人自己配不回来。
//
//   因此本组的重点全在**坏情况**上：
//     文件不存在      正常情形（首次启动），不是错误
//     写入中途失败    原文件必须完好，绝不能既没写成新的又毁了旧的
//     文件损坏        备份原文件后用默认值继续跑，而不是拒绝启动
//     版本比程序新    同样按损坏处理（决策 D-8），不猜测未来格式
//
//   ★ 备份而不是删除，是这里最重要的一条。配置损坏时直接覆盖成默认值，
//     等于把技术顾问配了半天的规则永久销毁；备份下来，至少还能人工恢复
//     或者拿去分析为什么会坏。
//
//   测试一律使用临时目录，绝不触碰真实的用户配置目录。构造函数因此接受
//   完整文件路径，而不是自己去查 ApplicationData——那样就没法测了。
//
// English:
//   Behavior tests for settings persistence (test plan S14–S28).
//
//   The only group in Phase A that touches the file system. Configuration
//   reliability decides whether the operator can work at all: an unreadable file
//   means losing the parsing rule, the scanner binding and the language preference
//   — all configured remotely by the technical advisor and not something the
//   operator can restore themselves.
//
//   The weight therefore falls on the bad cases: a missing file (normal on first
//   launch, not an error), a failure mid-write (the previous file must survive
//   intact — never both fail to write the new one and destroy the old), a corrupt
//   file (back it up and continue on defaults rather than refusing to start), and a
//   version newer than the application (treated as corruption per D-8, never guessed
//   at).
//
//   Backing up rather than deleting is the most important rule here. Overwriting a
//   corrupt file with defaults would permanently destroy rules the advisor spent
//   time configuring; a backup leaves them recoverable by hand and available for
//   diagnosing why it broke.
//
//   Every test uses a temporary directory and never touches the real profile. The
//   constructor therefore takes a full file path rather than resolving
//   ApplicationData itself, which would make it untestable.
//
// 包含的测试 / Tests in this file:
//   Missing_file_loads_defaults_without_creating_it       S14
//   Save_creates_the_directory_when_absent                S15
//   Save_then_load_round_trips_every_field                S16
//   Successful_save_leaves_no_temporary_file              S17
//   Failure_during_save_leaves_the_previous_file_intact   S18
//   Io_error_during_save_surfaces_and_does_not_corrupt    S19
//   Default_path_is_under_the_per_user_application_data   S20
//   Corrupt_file_is_backed_up_and_defaults_are_loaded     S21 S22 S23 S24 S28
//   Repeated_corruption_does_not_overwrite_earlier_backup S25
//   Missing_field_takes_its_default                       S26
//   Unknown_fields_are_ignored                            S27
//   Failed_backup_is_reported_as_a_null_backup_path       S29
//   Explicitly_null_section_loads_as_its_default          S30
// =============================================================================

using System.Text.Json;
using ScannerHelper.Core.Domain;
using ScannerHelper.Core.Settings;

namespace ScannerHelper.Core.Tests.Settings;

public sealed class JsonSettingsStoreTests : IDisposable
{
    /// <summary>
    /// 中文：本次测试专用的临时目录。每个测试实例一个，互不干扰。
    /// English: A temporary directory dedicated to this test instance.
    /// </summary>
    private readonly string _temporaryDirectory =
        Path.Combine(Path.GetTempPath(), $"ScannerHelperTests-{Guid.NewGuid():N}");

    private string SettingsFilePath => Path.Combine(_temporaryDirectory, "settings.json");

    /// <summary>
    /// 中文：测试结束后删除临时目录。即便测试失败也会执行，不留垃圾。
    /// English: Removes the temporary directory afterwards, including after a
    ///          failure, so nothing is left behind.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private JsonSettingsStore CreateStore(ISettingsFileSystem? fileSystem = null)
        => new(SettingsFilePath, fileSystem ?? new SystemSettingsFileSystem());

    /// <summary>
    /// 中文：
    ///   S14 — 配置文件不存在时返回默认值，且**不创建文件**。
    ///   输入：无。输出：无（断言）。
    ///
    ///   文件不存在是首次启动的正常情形，不是错误，因此不应抛异常、也不应
    ///   发出诊断事件。
    ///
    ///   不创建文件这一点是刻意的：只读不写。若 Load 顺手把默认值写下来，
    ///   一次纯粹的读取就变成了写入——在只读介质、权限受限的目录，或者用户
    ///   只是想看看程序能不能起来的时候，这个副作用都是不受欢迎的。文件
    ///   应当在用户真正保存设置时才出现。
    ///
    /// English:
    ///   S14 — a missing file loads defaults and creates nothing.
    ///
    ///   A missing file is the normal first-launch case, not an error, so it must
    ///   neither throw nor raise a diagnostic.
    ///
    ///   Creating nothing is deliberate: reading stays reading. Writing defaults on
    ///   load would turn a pure read into a write, which is unwelcome on read-only
    ///   media, in a permission-restricted directory, or when someone is merely
    ///   checking whether the application starts. The file should appear when the
    ///   user actually saves settings.
    /// </summary>
    [Fact]
    public void Missing_file_loads_defaults_without_creating_it()
    {
        var store = CreateStore();
        var recoveryEvents = new List<SettingsRecoveredEventArgs>();
        store.SettingsRecovered += (_, eventArgs) => recoveryEvents.Add(eventArgs);

        var settings = store.Load();

        Assert.Equal(1, settings.SchemaVersion);
        Assert.False(settings.ModeSwitchSoundEnabled);
        Assert.False(File.Exists(SettingsFilePath));
        Assert.Empty(recoveryEvents);
    }

    /// <summary>
    /// 中文：S15 — 目录不存在时 Save 会创建它。
    ///       首次保存发生在配置目录还不存在的时候，若不创建就会失败，
    ///       工人的第一次设置保存不了。
    /// English: S15 — Save creates the directory when it does not exist. The first
    ///          save happens before the configuration directory exists; without
    ///          creating it the operator's first settings change could not be saved.
    /// </summary>
    [Fact]
    public void Save_creates_the_directory_when_absent()
    {
        Assert.False(Directory.Exists(_temporaryDirectory));

        CreateStore().Save(new AppSettings());

        Assert.True(File.Exists(SettingsFilePath));
    }

    /// <summary>
    /// 中文：
    ///   S16 — 保存后再读取，每个字段都与保存前一致。
    ///   输入：无。输出：无（断言）。
    ///
    ///   刻意把每个字段都设成**非默认值**再往返。若某个字段用的是默认值，
    ///   即便序列化时漏掉了它，读回来也会因为默认值恰好相同而看起来正确——
    ///   这种漏失只在用户改过该项时才暴露，也就是只在生产环境暴露。
    ///
    ///   嵌套对象（热键、解析、校验、绑定、窗口位置、诊断）全部覆盖，
    ///   因为漏配置某个嵌套类型的序列化是很常见的疏忽。
    ///
    /// English:
    ///   S16 — every field survives a save/load round trip.
    ///
    ///   Every field is deliberately set to a non-default value first. A field left
    ///   at its default would read back correctly even if serialization dropped it,
    ///   because the default happens to match — an omission that would surface only
    ///   once a user changed that setting, which is to say only in production.
    ///
    ///   All nested objects are covered, since forgetting to serialize one of them
    ///   is a common oversight.
    /// </summary>
    [Fact]
    public void Save_then_load_round_trips_every_field()
    {
        var saved = new AppSettings
        {
            Language = "zh-CN",
            StartWithWindows = true,
            ModeSwitchSoundEnabled = false,
            ErrorSoundEnabled = false,
            RememberWindowPosition = false,
            AppendEnterAfterScan = true,
            FullWindowAlwaysOnTop = true,
            FullWindowBounds = new WindowBounds { Left = 10.5, Top = 20.5, Width = 400.25, Height = 300.75 },
            CompactWindowBounds = new WindowBounds { Left = 1, Top = 2, Width = 200, Height = 40 },
            Hotkeys = new HotkeySettings
            {
                ToggleMode = "F7", ForceSend = "F9", Cancel = "Back", PauseResume = "F12",
            },
            ScannerBinding = new ScannerBindingSettings
            {
                DevicePath = @"\\?\HID#VID_05E0", VendorId = "05E0", ProductId = "1200",
                FriendlyName = "Symbol Scanner", SerialNumber = "SN12345",
            },
            SkuParsing = new SkuParsingSettings
            {
                RuleType = SkuParsingRuleType.Regex, StartPosition = 5, Length = 8,
                RegexPattern = @"^.{4}([A-Z0-9]{8})", CaptureGroupIndex = 1,
            },
            SkuValidation = new SkuValidationSettings
            {
                MinimumLength = 8, MaximumLength = 8,
                CharacterSet = CharacterSetPreset.LettersNumbers,
                IgnoreCase = false, ValidationRegexPattern = @"^[A-Z]{3}\d{5}$",
            },
            Diagnostics = new DiagnosticsSettings { MaskBarcodeData = true, LogRetentionDays = 30 },
        };

        var store = CreateStore();
        store.Save(saved);
        var loaded = store.Load();

        Assert.Equal(saved.Language, loaded.Language);
        Assert.Equal(saved.StartWithWindows, loaded.StartWithWindows);
        Assert.Equal(saved.ModeSwitchSoundEnabled, loaded.ModeSwitchSoundEnabled);
        Assert.Equal(saved.ErrorSoundEnabled, loaded.ErrorSoundEnabled);
        Assert.Equal(saved.RememberWindowPosition, loaded.RememberWindowPosition);
        Assert.Equal(saved.AppendEnterAfterScan, loaded.AppendEnterAfterScan);
        Assert.Equal(saved.FullWindowAlwaysOnTop, loaded.FullWindowAlwaysOnTop);

        Assert.Equal(saved.FullWindowBounds!.Left, loaded.FullWindowBounds!.Left);
        Assert.Equal(saved.FullWindowBounds.Height, loaded.FullWindowBounds.Height);
        Assert.Equal(saved.CompactWindowBounds!.Width, loaded.CompactWindowBounds!.Width);

        Assert.Equal(saved.Hotkeys.ToggleMode, loaded.Hotkeys.ToggleMode);
        Assert.Equal(saved.Hotkeys.PauseResume, loaded.Hotkeys.PauseResume);

        Assert.Equal(saved.ScannerBinding!.DevicePath, loaded.ScannerBinding!.DevicePath);
        Assert.Equal(saved.ScannerBinding.SerialNumber, loaded.ScannerBinding.SerialNumber);

        Assert.Equal(saved.SkuParsing.RuleType, loaded.SkuParsing.RuleType);
        Assert.Equal(saved.SkuParsing.RegexPattern, loaded.SkuParsing.RegexPattern);
        Assert.Equal(saved.SkuParsing.CaptureGroupIndex, loaded.SkuParsing.CaptureGroupIndex);

        Assert.Equal(saved.SkuValidation.MinimumLength, loaded.SkuValidation.MinimumLength);
        Assert.Equal(saved.SkuValidation.CharacterSet, loaded.SkuValidation.CharacterSet);
        Assert.Equal(saved.SkuValidation.IgnoreCase, loaded.SkuValidation.IgnoreCase);
        Assert.Equal(saved.SkuValidation.ValidationRegexPattern, loaded.SkuValidation.ValidationRegexPattern);

        Assert.Equal(saved.Diagnostics.MaskBarcodeData, loaded.Diagnostics.MaskBarcodeData);
        Assert.Equal(saved.Diagnostics.LogRetentionDays, loaded.Diagnostics.LogRetentionDays);
    }

    /// <summary>
    /// 中文：S17 — 保存成功后目录里不留临时文件。
    ///       原子保存的做法是先写临时文件再改名覆盖，改名会消耗掉临时文件。
    ///       若临时文件仍在，说明改名那一步没做，保存就不是原子的。
    /// English: S17 — a successful save leaves no temporary file. Atomic save writes
    ///          a temp file then moves it over the target, and the move consumes the
    ///          temp. A leftover temp means the move step never happened and the save
    ///          was not atomic.
    /// </summary>
    [Fact]
    public void Successful_save_leaves_no_temporary_file()
    {
        CreateStore().Save(new AppSettings());

        var leftovers = Directory.GetFiles(_temporaryDirectory)
            .Where(path => !path.EndsWith("settings.json", StringComparison.Ordinal))
            .ToArray();

        Assert.True(leftovers.Length == 0,
            $"保存成功后不应留下任何中间文件，发现：{string.Join(", ", leftovers.Select(Path.GetFileName))}");
    }

    /// <summary>
    /// 中文：
    ///   S18 — 写入中途失败时，原有配置文件完好无损。
    ///   输入：无。输出：无（断言）。
    ///   步骤：
    ///     1. 先正常保存一份配置，使文件存在且内容已知；
    ///     2. 换用一个写入必定失败的文件系统，再次保存；
    ///     3. 断言保存抛出了异常（失败要让调用方知道，不能静默）；
    ///     4. 断言原文件仍能被正常读出，内容与第 1 步一致；
    ///     5. 断言目录里没有残留的临时文件。
    ///
    ///   这是本文件最重要的一条。配置写坏的后果不是"这次设置没保存成功"，
    ///   而是"下次启动时解析规则、扫码枪绑定全没了"——而那些是技术顾问远程
    ///   配好的，工人自己配不回来。宁可这次保存失败，也绝不能既没写成新的
    ///   又毁了旧的。
    ///
    ///   先写临时文件、再改名覆盖，正是为了这个：改名在文件系统层面是接近
    ///   原子的，要么旧文件还在，要么新文件已完整就位，不存在中间态。
    ///
    /// English:
    ///   S18 — a failure mid-write leaves the previous file intact.
    ///   Steps: save normally, then save again through a file system whose write
    ///   always fails; assert it threw, assert the original still loads unchanged,
    ///   assert no temp file remains.
    ///
    ///   The most important test here. A corrupted write does not mean "this change
    ///   was not saved"; it means "the parsing rule and scanner binding are gone at
    ///   next launch" — configured remotely by the advisor and not restorable by the
    ///   operator. Better to fail the save than to both fail to write the new file
    ///   and destroy the old one.
    ///
    ///   Writing to a temp file and moving it over the target is exactly what buys
    ///   this: the move is close to atomic at the file-system level, so either the
    ///   old file is still there or the new one is completely in place, with no state
    ///   in between.
    /// </summary>
    [Fact]
    public void Failure_during_save_leaves_the_previous_file_intact()
    {
        // 步骤 1 / Step 1
        var original = new AppSettings { Language = "zh-CN", AppendEnterAfterScan = true };
        CreateStore().Save(original);

        // 步骤 2 / Step 2
        var failingStore = CreateStore(new FailingWriteFileSystem());
        var replacement = new AppSettings { Language = "en-US", AppendEnterAfterScan = false };

        // 步骤 3 / Step 3
        Assert.ThrowsAny<Exception>(() => failingStore.Save(replacement));

        // 步骤 4 / Step 4
        var reloaded = CreateStore().Load();
        Assert.Equal("zh-CN", reloaded.Language);
        Assert.True(reloaded.AppendEnterAfterScan);

        // 步骤 5 / Step 5
        var leftovers = Directory.GetFiles(_temporaryDirectory)
            .Where(path => !path.EndsWith("settings.json", StringComparison.Ordinal))
            .ToArray();
        Assert.True(leftovers.Length == 0,
            $"保存失败后应清理临时文件，发现：{string.Join(", ", leftovers.Select(Path.GetFileName))}");
    }

    /// <summary>
    /// 中文：S19 — 保存时的 IO 错误以异常形式暴露，不被吞掉。
    ///       静默失败比抛异常糟得多：工人以为设置保存好了，下次启动才发现
    ///       全没变，而那时已经无从判断是哪一步出的问题。
    /// English: S19 — an IO error during save surfaces as an exception rather than
    ///          being swallowed. Silent failure is far worse: the operator believes
    ///          the settings were saved and discovers otherwise at the next launch,
    ///          by which point there is no way to tell which step failed.
    /// </summary>
    [Fact]
    public void Io_error_during_save_surfaces_and_does_not_corrupt()
    {
        var store = CreateStore(new FailingWriteFileSystem());

        Assert.ThrowsAny<Exception>(() => store.Save(new AppSettings()));

        Assert.False(File.Exists(SettingsFilePath));
    }

    /// <summary>
    /// 中文：
    ///   S20 — 默认配置路径位于**每用户**的应用数据目录下。
    ///   输入：无。输出：无（断言）。
    ///
    ///   规格 §14 要求配置存放在合适的 Windows 应用数据目录，而不是可执行
    ///   文件旁边。放在程序目录会有两个问题：Program Files 通常不可写，
    ///   以及同一台机器上多个工人会共用同一份配置。
    ///
    ///   本条只断言路径的构成，不进行任何读写——绝不能在测试里碰真实的
    ///   用户配置目录。
    ///
    /// English:
    ///   S20 — the default path lives under the per-user application data directory.
    ///   Spec §14 requires this rather than storing beside the executable, which
    ///   would be unwritable under Program Files and would share one configuration
    ///   between every operator on the machine.
    ///
    ///   Only the composition of the path is asserted; nothing is read or written,
    ///   because a test must never touch the real profile.
    /// </summary>
    [Fact]
    public void Default_path_is_under_the_per_user_application_data()
    {
        var applicationDataDirectory =
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.StartsWith(applicationDataDirectory, JsonSettingsStore.DefaultSettingsFilePath);
        Assert.Contains("ScannerHelper", JsonSettingsStore.DefaultSettingsFilePath);
        Assert.EndsWith(".json", JsonSettingsStore.DefaultSettingsFilePath);
    }

    /// <summary>
    /// 中文：
    ///   S21~S24、S28 — 无法使用的配置文件被备份后，以默认值继续运行。
    ///   输入：caseId 用例编号；fileContent 文件内容；expectedReason 期望的
    ///         恢复原因。输出：无（断言）。
    ///
    ///   覆盖点：
    ///     S21  非法 JSON
    ///     S22  空文件
    ///     S23  内容为 null
    ///     S24  合法 JSON 但结构不对（数组而非对象）
    ///     S28  版本号高于本程序（决策 D-8）
    ///
    ///   ★ 三件事必须同时发生，缺一不可：
    ///     1. 原文件被**改名备份**，不是被覆盖或删除。技术顾问配了半天的规则
    ///        不能因为文件坏了就永久销毁——备份下来至少还能人工恢复，也能
    ///        拿去分析为什么会坏。
    ///     2. 返回默认值，让程序**继续能跑**。配置坏了就拒绝启动，会让整个
    ///        工位停摆；用默认值跑起来，工人至少还能用 SN 模式干活。
    ///     3. 发出诊断事件，让这件事**留下痕迹**。悄悄用默认值继续，工人会
    ///        发现规则莫名其妙失效了却找不到原因。
    ///
    ///   S28 与前四条归为一类，是决策 D-8 的体现：版本号比程序新，说明这份
    ///   文件是更高版本的程序写的（用户装了新版又回滚）。猜测未来格式会静默
    ///   丢弃配置，按损坏处理反而更诚实——备份保住了原文件，用户升回新版本
    ///   后还能手工恢复。
    ///
    /// English:
    ///   S21–S24, S28 — an unusable file is backed up and defaults are loaded.
    ///
    ///   Three things must happen together: the original is *renamed*, not
    ///   overwritten or deleted (rules the advisor spent time on must not be
    ///   destroyed because a file broke); defaults are returned so the application
    ///   still runs (refusing to start would halt the station, whereas defaults at
    ///   least leave SN mode working); and a diagnostic is raised so the event leaves
    ///   a trace, since silently continuing would leave the operator with rules that
    ///   inexplicably stopped working.
    ///
    ///   S28 belongs to this group by decision D-8: a newer version means the file
    ///   was written by a later build, typically after an upgrade and rollback.
    ///   Guessing at a future format would silently discard configuration; treating
    ///   it as corruption is more honest, and the backup means the user can recover
    ///   by hand after upgrading again.
    /// </summary>
    [Theory]
    [InlineData("S21", "{{{", SettingsRecoveryReason.Malformed)]
    [InlineData("S22", "", SettingsRecoveryReason.Malformed)]
    [InlineData("S23", "null", SettingsRecoveryReason.Malformed)]
    [InlineData("S24", "[1,2,3]", SettingsRecoveryReason.Malformed)]
    [InlineData("S28", """{"SchemaVersion":999}""", SettingsRecoveryReason.UnsupportedSchemaVersion)]
    public void Corrupt_file_is_backed_up_and_defaults_are_loaded(
        string caseId, string fileContent, SettingsRecoveryReason expectedReason)
    {
        Directory.CreateDirectory(_temporaryDirectory);
        File.WriteAllText(SettingsFilePath, fileContent);

        var store = CreateStore();
        var recoveryEvents = new List<SettingsRecoveredEventArgs>();
        store.SettingsRecovered += (_, eventArgs) => recoveryEvents.Add(eventArgs);

        var settings = store.Load();

        // 返回默认值 / defaults returned
        // ★ 这里**故意不用** ModeSwitchSoundEnabled 来证明"默认值生效了"。
        //   决策 D-28 把它的默认值改成了 false，而 false 同时也是 bool 的零值——
        //   断言 false 无法区分"默认值被应用了"和"这个字段压根没被赋值"，
        //   那样的断言看着还在，其实已经什么都不证明了。
        //   改用默认值不等于零值的字段：波特率默认 9600（决策 D-27）。
        // ModeSwitchSoundEnabled is deliberately not used to prove "defaults were applied":
        // decision D-28 made its default false, which is also bool's zero value, so asserting false
        // cannot distinguish "the default was applied" from "the field was never set at all" — an
        // assertion that still looks present while proving nothing. A field whose default differs
        // from its zero value is used instead: the baud rate defaults to 9600 (decision D-27).
        Assert.True(
            settings.SerialPort.BaudRate == 9600,
            $"{caseId}: 应返回默认值 / should fall back to the defaults");
        Assert.Equal(1, settings.SchemaVersion);

        // 发出诊断事件 / diagnostic raised
        var recovery = Assert.Single(recoveryEvents);
        Assert.True(recovery.Reason == expectedReason,
            $"{caseId}: 恢复原因应为 {expectedReason}，实际为 {recovery.Reason}");

        // 原文件被改名备份，内容原封不动 / original renamed, contents untouched
        Assert.NotNull(recovery.BackupPath);
        Assert.True(File.Exists(recovery.BackupPath), $"{caseId}: 原文件必须被备份而非销毁");
        Assert.Equal(fileContent, File.ReadAllText(recovery.BackupPath));
        Assert.False(File.Exists(SettingsFilePath), $"{caseId}: 损坏的文件应被移走");
    }

    /// <summary>
    /// 中文：
    ///   S25 — 再次损坏时备份到新编号，不覆盖上一次的备份。
    ///   输入：无。输出：无（断言）。
    ///
    ///   若固定用同一个备份名，第二次损坏就会把第一次的备份冲掉。而真正
    ///   有价值的往往是**最早那一份**——它是损坏前最后一次完整配置，后面
    ///   几次可能已经是在坏文件基础上继续劣化的结果。
    ///
    /// English:
    ///   S25 — repeated corruption backs up under a new number instead of
    ///   overwriting the earlier backup. A fixed backup name would let the second
    ///   corruption erase the first — and the earliest copy is usually the valuable
    ///   one, being the last complete configuration before things went wrong.
    /// </summary>
    [Fact]
    public void Repeated_corruption_does_not_overwrite_earlier_backup()
    {
        Directory.CreateDirectory(_temporaryDirectory);

        File.WriteAllText(SettingsFilePath, "first corruption");
        var firstBackup = LoadAndCaptureBackupPath();

        File.WriteAllText(SettingsFilePath, "second corruption");
        var secondBackup = LoadAndCaptureBackupPath();

        Assert.NotEqual(firstBackup, secondBackup);
        Assert.Equal("first corruption", File.ReadAllText(firstBackup));
        Assert.Equal("second corruption", File.ReadAllText(secondBackup));
    }

    /// <summary>
    /// 中文：
    ///   S26 — 旧版本写的配置缺少新增字段时，该字段取默认值，其余照常读入。
    ///   输入：无。输出：无（断言）。
    ///
    ///   这是升级路径上最常见的情形：新版本加了一个设置项，而机器上的配置
    ///   文件是旧版本写的。若因为缺字段就判定文件损坏，每次升级都会让所有
    ///   工位的配置被备份清空——那显然不可接受。
    ///
    /// English:
    ///   S26 — a file written by an older version, missing a newer field, takes the
    ///   default for that field and loads everything else normally. This is the
    ///   ordinary upgrade path; treating a missing field as corruption would wipe
    ///   every station's configuration on every release.
    /// </summary>
    [Fact]
    public void Missing_field_takes_its_default()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        File.WriteAllText(SettingsFilePath,
            """{"SchemaVersion":1,"Language":"zh-CN","SkuValidation":{"MinimumLength":8}}""");

        var store = CreateStore();
        var recoveryEvents = new List<SettingsRecoveredEventArgs>();
        store.SettingsRecovered += (_, eventArgs) => recoveryEvents.Add(eventArgs);

        var settings = store.Load();

        Assert.Empty(recoveryEvents);
        Assert.Equal("zh-CN", settings.Language);
        Assert.Equal(8, settings.SkuValidation.MinimumLength);
        Assert.True(settings.SkuValidation.IgnoreCase);      // 缺失的字段取默认值

        // ★ 这里**故意不用** ModeSwitchSoundEnabled 来证明"默认值生效了"。
        //   决策 D-28 把它的默认值改成了 false，而 false 同时也是 bool 的零值——
        //   断言 false 无法区分"默认值被应用了"和"这个字段压根没被赋值"，
        //   那样的断言看着还在，其实已经什么都不证明了。
        //   改用默认值不等于零值的字段：波特率默认 9600（决策 D-27）。
        // ModeSwitchSoundEnabled is deliberately not used to prove "defaults were applied":
        // decision D-28 made its default false, which is also bool's zero value, so asserting false
        // cannot distinguish "the default was applied" from "the field was never set at all" — an
        // assertion that still looks present while proving nothing. A field whose default differs
        // from its zero value is used instead: the baud rate defaults to 9600 (decision D-27).
        Assert.Equal(9600, settings.SerialPort.BaudRate);
    }

    /// <summary>
    /// 中文：
    ///   S27 — 未来版本写入的未知字段被忽略，不导致读取失败。
    ///   输入：无。输出：无（断言）。
    ///
    ///   与 S28 并不矛盾：S28 拒绝的是**版本号声明**自己更高的文件，那是
    ///   明确的"我是新格式"信号；本条处理的是版本号相同、只是多了几个字段
    ///   的情况——那通常是同版本内的小幅扩展，忽略即可，没有理由让整份配置
    ///   作废。
    ///
    /// English:
    ///   S27 — unknown fields written by a future version are ignored rather than
    ///   failing the load. Not in conflict with S28: that one rejects a file whose
    ///   *declared version* is higher, an explicit "this is a newer format" signal.
    ///   Here the version matches and there are merely extra fields — an in-version
    ///   extension, with no reason to discard the whole configuration.
    /// </summary>
    [Fact]
    public void Unknown_fields_are_ignored()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        File.WriteAllText(SettingsFilePath,
            """{"SchemaVersion":1,"Language":"en-US","SomeFutureSetting":true,"Another":{"Nested":1}}""");

        var store = CreateStore();
        var recoveryEvents = new List<SettingsRecoveredEventArgs>();
        store.SettingsRecovered += (_, eventArgs) => recoveryEvents.Add(eventArgs);

        var settings = store.Load();

        Assert.Empty(recoveryEvents);
        Assert.Equal("en-US", settings.Language);
    }

    /// <summary>
    /// 中文：
    ///   S29 — 备份没做成时，恢复事件里的备份路径必须为 null。
    ///   输入：无。输出：无（断言）。
    ///   步骤：
    ///     1. 放一份损坏的配置；
    ///     2. 换用一个改名必定失败的文件系统（模拟目录只读）执行 Load；
    ///     3. 断言 Load 没有抛异常，仍然返回默认值——备份失败不该让程序起不来；
    ///     4. 断言恢复事件仍然发出，原因码正确；
    ///     5. 断言事件里的 BackupPath 为 **null**。
    ///
    ///   ★ 第 5 步是本条存在的全部理由。备份失败被有意吞掉（第 3 步的取舍是
    ///     对的：为一次备份失败让整个工位起不来显然更糟），但它绝不能被悄悄
    ///     抹平。若无论成败都报一个路径，日志上就会写着"原文件已备份到 X"，
    ///     而 X 根本不存在——工程师照着去找白跑一趟，还会误以为原配置还留着，
    ///     从而放弃去别处找回它。
    ///
    ///     规格 §19.1 那条设计规则"程序绝不能显示一个自己没验证过的状态"
    ///     写的是 hook，但诊断信息同样适用。用可空类型表达之后，读取方根本
    ///     拿不到一个不存在的路径。
    ///
    /// English:
    ///   S29 — when the backup did not happen, the recovery event carries a null path.
    ///   Steps: write a corrupt file; Load through a file system whose rename always
    ///   fails (a read-only directory); assert Load still returns defaults without
    ///   throwing, still raises the event with the right reason, and reports a null
    ///   backup path.
    ///
    ///   Step 5 is the whole reason this test exists. The failed backup is swallowed
    ///   deliberately — halting the station because a *backup* failed would plainly be
    ///   worse — but it must not be papered over. Reporting a path regardless would
    ///   write "the original was backed up to X" into the log for an X that does not
    ///   exist, sending an engineer on a wasted search and leaving them believing the
    ///   original survived, so they stop looking for it elsewhere.
    ///
    ///   Spec §19.1's rule — never display a state that has not been verified — is
    ///   written about the hook but applies to diagnostics just as much. As a
    ///   nullable, a reader simply cannot obtain a path that is not there.
    /// </summary>
    [Fact]
    public void Failed_backup_is_reported_as_a_null_backup_path()
    {
        // 步骤 1 / Step 1
        Directory.CreateDirectory(_temporaryDirectory);
        File.WriteAllText(SettingsFilePath, "{{{");

        // 步骤 2 / Step 2
        var store = CreateStore(new FailingMoveFileSystem());
        var recoveryEvents = new List<SettingsRecoveredEventArgs>();
        store.SettingsRecovered += (_, eventArgs) => recoveryEvents.Add(eventArgs);

        var settings = store.Load();

        // 步骤 3 / Step 3
        // ★ 这里**故意不用** ModeSwitchSoundEnabled 来证明"默认值生效了"。
        //   决策 D-28 把它的默认值改成了 false，而 false 同时也是 bool 的零值——
        //   断言 false 无法区分"默认值被应用了"和"这个字段压根没被赋值"，
        //   那样的断言看着还在，其实已经什么都不证明了。
        //   改用默认值不等于零值的字段：波特率默认 9600（决策 D-27）。
        // ModeSwitchSoundEnabled is deliberately not used to prove "defaults were applied":
        // decision D-28 made its default false, which is also bool's zero value, so asserting false
        // cannot distinguish "the default was applied" from "the field was never set at all" — an
        // assertion that still looks present while proving nothing. A field whose default differs
        // from its zero value is used instead: the baud rate defaults to 9600 (decision D-27).
        Assert.Equal(9600, settings.SerialPort.BaudRate);

        // 步骤 4 / Step 4
        var recovery = Assert.Single(recoveryEvents);
        Assert.Equal(SettingsRecoveryReason.Malformed, recovery.Reason);

        // 步骤 5 / Step 5
        Assert.True(recovery.BackupPath is null,
            "备份失败时不得报告一个不存在的备份路径，否则日志会声称原配置已保留。"
            + $" 实际报告：{recovery.BackupPath}");
    }

    /// <summary>
    /// 中文：
    ///   S30 — 配置文件中把某个分组显式写成 null 时，读回来仍是默认实例。
    ///   输入：sectionName 被置空的分组名。输出：无（断言）。
    ///
    ///   ★ 这条堵的是一个真实存在的洞，不是假想的边界情况。
    ///
    ///     .NET 8 的 System.Text.Json **完全忽略可空性标注**
    ///     （RespectNullableAnnotations 要到 .NET 9 才有）。因此
    ///         {"SchemaVersion":1,"Hotkeys":null}
    ///     能被正常反序列化，AppSettings 里属性初始化器建好的实例会被 null
    ///     覆盖掉。Load 于是返回一个"看起来正常、其实半空"的对象，而
    ///     ISettingsStore 承诺的是"永不失败、永远返回可用配置"。
    ///
    ///     这个洞不会在 Phase A 暴露——Core 里没人访问这些分组。它会在
    ///     Phase B 以 settings.Hotkeys.ToggleMode 的空引用异常爆出来，
    ///     表现为工位起不来，而配置文件看上去完全正常。
    ///
    ///   按 S26 的既定精神，显式 null 与"字段不存在"是同一件事：都表示这一段
    ///   没有有效内容，都该退回默认值。两条测试合起来覆盖了缺字段的两种写法。
    ///
    ///   四个分组逐个测而不是只测一个：漏掉其中某一个的属性写法，与四个全漏
    ///   在故障现场看起来完全一样，但只有逐个断言才能定位是哪一个。
    ///
    /// English:
    ///   S30 — a section written as an explicit null still loads as a default instance.
    ///
    ///   This closes a real hole rather than a hypothetical edge case. .NET 8's
    ///   System.Text.Json ignores nullability annotations entirely
    ///   (RespectNullableAnnotations arrives in .NET 9), so
    ///   {"SchemaVersion":1,"Hotkeys":null} deserializes happily and overwrites the
    ///   property initializer with null. Load then returns an object that looks fine
    ///   and is half empty, against ISettingsStore's promise to always return
    ///   something usable.
    ///
    ///   The hole cannot surface in Phase A, where nothing in Core reads these
    ///   sections. It surfaces in Phase B as a null reference on
    ///   settings.Hotkeys.ToggleMode — the station fails to start while the
    ///   configuration file looks perfectly normal.
    ///
    ///   Per the established spirit of S26, an explicit null and an absent field are
    ///   the same thing: neither carries usable content, and both take the default.
    ///   Together the two tests cover both spellings of "this field is missing".
    ///
    ///   All four sections are tested individually rather than just one: missing the
    ///   fix on a single section looks identical in the field to missing all four, and
    ///   only per-section assertions say which.
    /// </summary>
    [Theory]
    [InlineData("Hotkeys")]
    [InlineData("SkuParsing")]
    [InlineData("SkuValidation")]
    [InlineData("Diagnostics")]
    public void Explicitly_null_section_loads_as_its_default(string sectionName)
    {
        Directory.CreateDirectory(_temporaryDirectory);
        File.WriteAllText(SettingsFilePath, $$"""{"SchemaVersion":1,"{{sectionName}}":null}""");

        var store = CreateStore();
        var recoveryEvents = new List<SettingsRecoveredEventArgs>();
        store.SettingsRecovered += (_, eventArgs) => recoveryEvents.Add(eventArgs);

        var settings = store.Load();

        // 不算损坏：一个分组为 null 与该分组缺失等价，不该丢掉整份配置
        // Not corruption: a null section equals an absent one and must not discard
        // the whole configuration
        Assert.Empty(recoveryEvents);

        // 四个分组一律非空，且取到各自的默认值
        // Every section is non-null and carries its own defaults
        Assert.NotNull(settings.Hotkeys);
        Assert.NotNull(settings.SkuParsing);
        Assert.NotNull(settings.SkuValidation);
        Assert.NotNull(settings.Diagnostics);

        Assert.Equal("F8", settings.Hotkeys.ToggleMode);
        Assert.Equal(SkuParsingRuleType.FixedPosition, settings.SkuParsing.RuleType);
        Assert.True(settings.SkuValidation.IgnoreCase);
        Assert.Equal(14, settings.Diagnostics.LogRetentionDays);
    }

    /// <summary>
    /// 中文：执行一次 Load，捕获它发出的恢复事件，并返回其中的备份路径，供 S25 复用。
    ///       备份路径为可空——null 表示备份没做成（见 S29）。本辅助方法断言它非空，
    ///       因为 S25 的前提就是两次备份都成功了。
    /// English: Runs one Load, captures the recovery event, and returns its backup
    ///          path, for S25. The path is nullable — null means the backup did not
    ///          happen (see S29) — and this helper asserts it is not, since S25's
    ///          premise is that both backups succeeded.
    /// </summary>
    private string LoadAndCaptureBackupPath()
    {
        var store = CreateStore();
        SettingsRecoveredEventArgs? captured = null;
        store.SettingsRecovered += (_, eventArgs) => captured = eventArgs;

        store.Load();

        Assert.NotNull(captured);
        Assert.NotNull(captured.BackupPath);
        return captured.BackupPath;
    }

    /// <summary>
    /// 中文：
    ///   模拟"写到一半失败"的文件系统。其余操作照常委托给真实实现。
    ///
    ///   ★ 关键在于它**先落下一半内容、再抛异常**，而不是一上来就抛。
    ///
    ///     最初的版本是直接抛异常，结果这条测试无法区分原子写与非原子写：
    ///     既然一个字节都没写出去，即便实现直接往目标文件写，原文件也毫发
    ///     无伤。测试通过了，但通过的原因是假实现太温和，而不是实现真的
    ///     正确。已通过把实现改成非原子写来验证：修正之前测试照样全绿，
    ///     修正之后立刻变红。
    ///
    ///     真实的磁盘满、断电、进程被杀，都是**写了一部分才中断**。假实现
    ///     必须复现这一点，否则它守不住任何东西。
    ///
    ///   用假实现而不是真的去制造写失败的环境：制造真实写失败需要改目录
    ///   权限或填满磁盘，既慢又难清理，在不同机器上表现还不一致。
    ///
    /// English:
    ///   A file system that simulates failing *part-way through* a write.
    ///
    ///   The critical detail is that it writes half the content before throwing,
    ///   rather than throwing immediately.
    ///
    ///   The first version threw straight away, and the test then could not tell an
    ///   atomic save from a non-atomic one: with no bytes written, even an
    ///   implementation writing directly to the target left the original untouched.
    ///   The test passed, but because the fake was too gentle rather than because the
    ///   implementation was right. Confirmed by switching the implementation to a
    ///   non-atomic write: it stayed green before this fix and turns red after it.
    ///
    ///   A real full disk, power loss or killed process all interrupt a write
    ///   part-way. The fake has to reproduce that or it guards nothing.
    ///
    ///   A fake rather than a genuinely failing environment: producing a real write
    ///   failure means changing directory permissions or filling a disk — slow,
    ///   awkward to clean up, and inconsistent across machines.
    /// </summary>
    private sealed class FailingWriteFileSystem : ISettingsFileSystem
    {
        private readonly SystemSettingsFileSystem _real = new();

        public bool FileExists(string path) => _real.FileExists(path);

        public string ReadAllText(string path) => _real.ReadAllText(path);

        public void CreateDirectory(string directoryPath) => _real.CreateDirectory(directoryPath);

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
            => _real.MoveFile(sourcePath, destinationPath, overwrite);

        public void DeleteFile(string path) => _real.DeleteFile(path);

        public void WriteAllText(string path, string contents)
        {
            // 先写一半，模拟真实的中途中断 / write half, as a real interruption would
            _real.WriteAllText(path, contents[..(contents.Length / 2)]);

            throw new IOException("模拟写到一半失败 / simulated failure part-way through a write");
        }
    }

    /// <summary>
    /// 中文：
    ///   模拟"改名失败"的文件系统，其余操作照常委托给真实实现。供 S29 使用。
    ///
    ///   对应的真实场景是配置目录只读，或原文件被别的进程占用——此时损坏的
    ///   配置备份不下来。程序仍应正常启动（用默认值），但绝不能报告一个不存在
    ///   的备份路径。
    ///
    ///   与 FailingWriteFileSystem 分成两个类而不是合成一个可配置的假实现：
    ///   两者模拟的是不同的故障，各自只失败一个动作，读起来一眼就知道在测什么。
    ///
    /// English:
    ///   A file system whose rename always fails, delegating everything else to the
    ///   real one. Used by S29.
    ///
    ///   The real-world case is a read-only configuration directory, or the original
    ///   file held open by another process, so a corrupt config cannot be backed up.
    ///   The application must still start on defaults — but must never report a backup
    ///   path that does not exist.
    ///
    ///   Kept separate from FailingWriteFileSystem rather than merged into one
    ///   configurable fake: they simulate different faults, each fails exactly one
    ///   operation, and which one is under test is visible at a glance.
    /// </summary>
    private sealed class FailingMoveFileSystem : ISettingsFileSystem
    {
        private readonly SystemSettingsFileSystem _real = new();

        public bool FileExists(string path) => _real.FileExists(path);

        public string ReadAllText(string path) => _real.ReadAllText(path);

        public void WriteAllText(string path, string contents) => _real.WriteAllText(path, contents);

        public void CreateDirectory(string directoryPath) => _real.CreateDirectory(directoryPath);

        public void DeleteFile(string path) => _real.DeleteFile(path);

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
            => throw new UnauthorizedAccessException(
                "模拟目录只读导致改名失败 / simulated rename failure on a read-only directory");
    }
}
