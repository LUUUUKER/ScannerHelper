// =============================================================================
// JsonSettingsStore.cs
//
// 中文：
//   把 AppSettings 读写为一份 JSON 文件（规格 §14）。
//
//   ★ 保存是原子的：先写临时文件，再用一次移动把它换到目标位置。
//
//     配置写坏的后果不是"这次设置没保存成功"，而是"下次启动时解析规则、
//     扫码枪绑定全没了"——那些是技术顾问远程配好的，工人自己配不回来。
//     直接往目标文件写，一旦中途断电或磁盘满，留下的就是半份 JSON：
//     旧配置毁了，新配置也没成。先写临时文件再移动，则要么旧文件还在，
//     要么新文件已完整就位，不存在中间态。
//
//   ★ 读取永不失败：无法使用的文件被**改名备份**后，以默认值继续。
//
//     三种应对里选了这一种：拒绝启动会让整个工位停摆；静默用默认值会让
//     工人发现规则莫名失效却查不出原因；备份 + 默认值 + 诊断事件同时满足
//     "原配置保住了""程序还能跑""这件事留下了痕迹"。
//
//     备份而不是覆盖，是因为技术顾问配了半天的规则不能因为文件坏了就永久
//     销毁——留着至少能人工恢复，也能拿去分析为什么会坏。
//
//   枚举序列化为**字符串**而非数字。配置文件要被技术顾问远程查看，
//   "LettersNumbers" 一眼能懂，而 2 需要查表。代价是新增枚举成员时不能
//   随意改名，这个约束是合理的。
//
// English:
//   Reads and writes AppSettings as a JSON file (spec §14).
//
//   Saving is atomic: write a temp file, then move it into place. A corrupted write
//   does not mean "this change was not saved"; it means the parsing rule and
//   scanner binding are gone at next launch, and those were configured remotely by
//   the advisor. Writing straight to the target would leave half a JSON document on
//   power loss or a full disk — old config destroyed, new one not written. The
//   temp-then-move sequence guarantees either the old file is still there or the new
//   one is completely in place.
//
//   Loading never fails: an unusable file is renamed aside and defaults are used.
//   Refusing to start would halt the station; silently defaulting would leave the
//   operator with rules that inexplicably stopped working. Backing up, defaulting,
//   and raising a diagnostic satisfies all three needs at once.
//
//   Enums serialize as strings rather than numbers. The config file gets read
//   remotely by the technical advisor, and "LettersNumbers" is self-explanatory
//   where 2 needs a lookup. The cost is that enum members cannot be freely renamed,
//   which is a reasonable constraint.
//
// 包含的成员 / Members in this file:
//   DefaultSettingsFilePath  默认配置路径
//   SettingsRecovered        恢复事件
//   Load                     读取配置
//   Save                     原子保存配置
//   RecoverFrom              备份无法使用的文件并返回默认值
//   BuildBackupPath          生成不覆盖已有备份的备份路径
// =============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScannerHelper.Core.Settings;

/// <summary>
/// 中文：基于 JSON 文件的配置存储。
/// English: A JSON-file-backed settings store.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    /// <summary>
    /// 中文：本程序能理解的最高结构版本。文件声明的版本高于此值时按无法使用
    ///       处理（决策 D-8）。
    /// English: The highest schema version this build understands. A file declaring
    ///          more is treated as unusable (decision D-8).
    /// </summary>
    private const int SupportedSchemaVersion = 1;

    /// <summary>
    /// 中文：序列化选项。
    ///       缩进：配置文件要给人看，也可能被人手工编辑。
    ///       枚举转字符串：见文件头说明。
    /// English: Serializer options. Indented because the file is read and sometimes
    ///          hand-edited by people; enums as strings for the reason in the header.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _settingsFilePath;
    private readonly ISettingsFileSystem _fileSystem;

    /// <summary>
    /// 中文：
    ///   构造配置存储。
    ///   输入：settingsFilePath 配置文件的完整路径，不得为 null 或空白；
    ///         fileSystem 文件操作实现，null 时使用真实文件系统。
    ///   输出：存储实例。
    ///
    ///   接受完整路径而不是自己去查 ApplicationData：那样测试就只能写进真实的
    ///   用户配置目录，既污染开发机器，又让多个测试互相干扰。默认路径由
    ///   <see cref="DefaultSettingsFilePath"/> 单独提供，由组装代码决定是否使用。
    /// English:
    ///   Creates the store. Takes a full path rather than resolving ApplicationData
    ///   itself: doing so would force tests to write into the real profile, polluting
    ///   the development machine and letting tests interfere with one another. The
    ///   default path is offered separately as <see cref="DefaultSettingsFilePath"/>
    ///   for composition code to use.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// 中文：路径为 null 或空白。 English: The path is null or blank.
    /// </exception>
    public JsonSettingsStore(string settingsFilePath, ISettingsFileSystem? fileSystem = null)
    {
        if (string.IsNullOrWhiteSpace(settingsFilePath))
        {
            throw new ArgumentException(
                "配置文件路径不能为空。 The settings file path must not be blank.",
                nameof(settingsFilePath));
        }

        _settingsFilePath = settingsFilePath;
        _fileSystem = fileSystem ?? new SystemSettingsFileSystem();
    }

    /// <summary>
    /// 中文：默认配置路径，位于每用户的应用数据目录下（规格 §14）。
    ///
    ///       放在用户目录而不是可执行文件旁边，有两个原因：程序目录
    ///       （Program Files）通常不可写；以及同一台机器上多个工人若共用一份
    ///       配置，一个人改绑定会影响到另一个人。
    /// English: The default path, under the per-user application data directory
    ///          (spec §14).
    ///
    ///          Stored in the user profile rather than beside the executable because
    ///          the program directory is usually unwritable, and because a shared
    ///          file would let one operator's rebinding affect another's session.
    /// </summary>
    public static string DefaultSettingsFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ScannerHelper",
        "settings.json");

    /// <inheritdoc />
    public event EventHandler<SettingsRecoveredEventArgs>? SettingsRecovered;

    /// <summary>
    /// 中文：
    ///   读取配置。
    ///   输入：无。输出：一份可用的配置，永不为 null。
    ///   步骤：
    ///     1. 文件不存在时直接返回默认值——首次启动的正常情形，不创建文件；
    ///     2. 读取文本；读取本身失败（权限、被占用）按无法使用处理；
    ///     3. 反序列化；抛异常或得到 null 均按无法使用处理；
    ///     4. 结构版本高于本程序时按无法使用处理（决策 D-8）；
    ///     5. 返回读到的配置。
    ///
    ///   步骤 1 不创建文件是刻意的：Load 顺手写默认值会让一次纯粹的读取
    ///   变成写入，在只读介质或权限受限目录下都会造成意外失败。文件应当在
    ///   用户真正保存设置时才出现。
    ///
    ///   步骤 3 里 null 需要单独判断：内容为字面量 "null" 时反序列化不会抛
    ///   异常，而是返回 null 引用。若不判断，调用方会拿到一个 null 配置，
    ///   在毫不相关的地方炸掉。
    ///
    /// English:
    ///   Loads settings, always returning something usable.
    ///   Steps: (1) missing file yields defaults and creates nothing; (2) read the
    ///   text, treating a read failure as unusable; (3) deserialize, treating a throw
    ///   or a null result as unusable; (4) treat a newer schema version as unusable
    ///   (D-8); (5) return what was read.
    ///
    ///   Step 1 creates nothing deliberately: writing defaults on load turns a pure
    ///   read into a write and fails unexpectedly on read-only media or in a
    ///   permission-restricted directory. The file should appear when the user
    ///   actually saves.
    ///
    ///   Step 3 checks for null separately: the literal "null" deserializes without
    ///   throwing and yields a null reference. Unchecked, the caller would receive
    ///   null settings and fail somewhere unrelated.
    /// </summary>
    public AppSettings Load()
    {
        // 步骤 1 / Step 1
        if (!_fileSystem.FileExists(_settingsFilePath))
        {
            return new AppSettings();
        }

        // 步骤 2、3 / Steps 2–3
        AppSettings? loaded;
        try
        {
            var fileContent = _fileSystem.ReadAllText(_settingsFilePath);
            loaded = JsonSerializer.Deserialize<AppSettings>(fileContent, SerializerOptions);
        }
        catch (Exception exception) when (exception is JsonException or IOException
                                              or UnauthorizedAccessException or NotSupportedException)
        {
            return RecoverFrom(SettingsRecoveryReason.Malformed);
        }

        if (loaded is null)
        {
            return RecoverFrom(SettingsRecoveryReason.Malformed);
        }

        // 步骤 4 / Step 4
        if (loaded.SchemaVersion > SupportedSchemaVersion)
        {
            return RecoverFrom(SettingsRecoveryReason.UnsupportedSchemaVersion);
        }

        // 步骤 5 / Step 5
        return loaded;
    }

    /// <summary>
    /// 中文：
    ///   原子地保存配置。
    ///   输入：settings 待保存的配置，不得为 null。输出：无。
    ///   步骤：
    ///     1. settings 为 null 时抛 ArgumentNullException；
    ///     2. 确保目标目录存在；
    ///     3. 序列化为 JSON；
    ///     4. 写入同目录下的临时文件；
    ///     5. 把临时文件移动覆盖到目标路径；
    ///     6. 步骤 4 或 5 失败时清理临时文件，然后把异常原样抛出。
    ///
    ///   临时文件放在**同一个目录**下，而不是系统临时目录：跨卷移动不是原子
    ///   操作，会退化成"复制 + 删除"，中途失败照样可能留下半份文件。同目录
    ///   内的移动才是文件系统层面的改名。
    ///
    ///   步骤 6 清理后重新抛出而不是吞掉：写失败若被静默处理，工人会以为设置
    ///   保存好了，直到下次启动才发现全没变，那时已无从判断问题出在哪一步。
    ///
    /// English:
    ///   Saves atomically.
    ///   Steps: (1) reject null; (2) ensure the directory exists; (3) serialize;
    ///   (4) write a temp file beside the target; (5) move it over the target;
    ///   (6) on failure, clean up the temp file and rethrow.
    ///
    ///   The temp file sits in the *same directory*, not the system temp folder: a
    ///   cross-volume move is not atomic and degrades into copy-then-delete, which
    ///   can still leave a partial file. Only a same-directory move is a
    ///   file-system rename.
    ///
    ///   Step 6 rethrows rather than swallowing: a silently failed write leaves the
    ///   operator believing their settings were saved until the next launch proves
    ///   otherwise, with no way left to tell which step failed.
    /// </summary>
    public void Save(AppSettings settings)
    {
        // 步骤 1 / Step 1
        ArgumentNullException.ThrowIfNull(settings);

        // 步骤 2 / Step 2
        var directoryPath = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrEmpty(directoryPath))
        {
            _fileSystem.CreateDirectory(directoryPath);
        }

        // 步骤 3 / Step 3
        var json = JsonSerializer.Serialize(settings, SerializerOptions);

        // 步骤 4、5 / Steps 4–5
        var temporaryPath = _settingsFilePath + ".tmp";
        try
        {
            _fileSystem.WriteAllText(temporaryPath, json);
            _fileSystem.MoveFile(temporaryPath, _settingsFilePath, overwrite: true);
        }
        catch
        {
            // 步骤 6 / Step 6 —— 清理失败不应掩盖原始异常
            // Cleanup failure must not mask the original exception
            try
            {
                _fileSystem.DeleteFile(temporaryPath);
            }
            catch
            {
                // 有意忽略 / intentionally ignored
            }

            throw;
        }
    }

    /// <summary>
    /// 中文：
    ///   备份无法使用的配置文件，并返回一份默认配置。
    ///   输入：reason 该文件为何无法使用。输出：默认配置。
    ///   步骤：
    ///     1. 生成一个不会覆盖已有备份的备份路径；
    ///     2. 把原文件改名过去，并记下是否成功；
    ///     3. 触发 SettingsRecovered 事件，**只在备份确实成功时**带上路径；
    ///     4. 返回默认配置。
    ///
    ///   步骤 2 若失败（例如目录只读），不再抛异常，而是继续返回默认值。
    ///   此时程序仍能启动，只是原文件没能备份成——比起因为"备份失败"而让
    ///   整个工位起不来，这个取舍是明确的。
    ///
    ///   步骤 3 在失败时传 null 而不是照样传路径：否则日志会写下"原文件已备份
    ///   到 X"，而 X 根本不存在，工程师照着去找只会白跑一趟，还会误以为原配置
    ///   还留着。规格 §19.1 那条"绝不显示未经验证的状态"同样适用于诊断信息。
    ///
    /// English:
    ///   Backs up an unusable file and returns defaults.
    ///   Steps: (1) build a backup path that will not overwrite an existing one;
    ///   (2) rename the original there, recording whether it worked; (3) raise
    ///   SettingsRecovered, carrying the path *only if the backup actually
    ///   succeeded*; (4) return defaults.
    ///
    ///   If step 2 fails — a read-only directory, say — nothing is thrown and defaults
    ///   are still returned. The application starts, merely without having preserved
    ///   the original. Halting the whole station because a *backup* failed would be the
    ///   worse trade.
    ///
    ///   Step 3 passes null on failure rather than the path anyway: otherwise the log
    ///   would record "the original was backed up to X" for an X that does not exist,
    ///   sending an engineer on a wasted search and leaving them believing the original
    ///   configuration survived. Spec §19.1's "never display an unverified state"
    ///   applies to diagnostics too.
    /// </summary>
    private AppSettings RecoverFrom(SettingsRecoveryReason reason)
    {
        // 步骤 1 / Step 1
        var backupPath = BuildBackupPath();

        // 步骤 2 / Step 2
        var backupSucceeded = false;
        try
        {
            _fileSystem.MoveFile(_settingsFilePath, backupPath, overwrite: false);
            backupSucceeded = true;
        }
        catch
        {
            // 备份失败不应阻止程序启动 / a failed backup must not prevent startup
        }

        // 步骤 3 / Step 3
        SettingsRecovered?.Invoke(
            this,
            new SettingsRecoveredEventArgs(backupSucceeded ? backupPath : null, reason));

        // 步骤 4 / Step 4
        return new AppSettings();
    }

    /// <summary>
    /// 中文：
    ///   生成备份路径，形如 settings.corrupt-1.json；若已存在则递增编号。
    ///   输入：无。输出：一个当前尚不存在的备份路径。
    ///
    ///   递增而不是固定用一个名字：第二次损坏若覆盖第一次的备份，最有价值的
    ///   那一份就没了——最早的备份是损坏前最后一次完整配置，后面几次很可能
    ///   已经是在坏文件基础上继续劣化的结果。
    ///
    ///   编号设了上限。极端情况下（例如目录只读导致改名一直失败，而每次启动
    ///   都重试）循环必须能终止；到达上限后复用最后一个编号，宁可覆盖也不要
    ///   让程序卡在这里。
    ///
    /// English:
    ///   Builds a backup path such as settings.corrupt-1.json, incrementing if one
    ///   already exists.
    ///
    ///   Incrementing rather than reusing one name: letting a second corruption
    ///   overwrite the first backup would lose the valuable copy, since the earliest
    ///   is the last complete configuration before things went wrong.
    ///
    ///   The number is capped. In a pathological case — a read-only directory making
    ///   every rename fail while each launch retries — the loop must still terminate;
    ///   past the cap the last number is reused, since overwriting is better than
    ///   hanging.
    /// </summary>
    private string BuildBackupPath()
    {
        const int maximumBackupCount = 100;

        var directoryPath = Path.GetDirectoryName(_settingsFilePath) ?? string.Empty;
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(_settingsFilePath);
        var extension = Path.GetExtension(_settingsFilePath);

        for (var backupNumber = 1; backupNumber < maximumBackupCount; backupNumber++)
        {
            var candidate = Path.Combine(
                directoryPath, $"{fileNameWithoutExtension}.corrupt-{backupNumber}{extension}");

            if (!_fileSystem.FileExists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(
            directoryPath, $"{fileNameWithoutExtension}.corrupt-{maximumBackupCount}{extension}");
    }
}
