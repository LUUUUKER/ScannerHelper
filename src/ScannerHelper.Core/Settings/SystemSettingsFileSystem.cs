// =============================================================================
// SystemSettingsFileSystem.cs
//
// 中文：
//   <see cref="ISettingsFileSystem"/> 的真实实现，直接调用 System.IO。
//
//   本类刻意保持为纯粹的转发，一行业务判断都没有。所有关于"什么时候写、
//   写到哪、失败了怎么办"的决策都在 JsonSettingsStore 里；这里只负责把
//   动作落到磁盘上。这样测试注入假实现时，被替换掉的只是 IO，业务逻辑
//   仍然是被真实执行的那一份。
//
//   System.IO 在 macOS 上同样可用，因此本类不破坏 Core 的跨平台性——
//   它没有引入任何 Windows 专有依赖。
//
// English:
//   The real <see cref="ISettingsFileSystem"/>, delegating straight to System.IO.
//
//   Deliberately pure forwarding, with no business decisions. Everything about when
//   to write, where, and what to do on failure lives in JsonSettingsStore; this only
//   puts the action on disk. So when a test injects a fake, only the IO is replaced
//   and the logic under test is still the real one.
//
//   System.IO works on macOS too, so this does not compromise Core's
//   cross-platform property: nothing Windows-specific is introduced.
//
// 包含的类型 / Types in this file:
//   SystemSettingsFileSystem
// =============================================================================

namespace ScannerHelper.Core.Settings;

/// <summary>
/// 中文：基于 System.IO 的文件操作实现。
/// English: A System.IO-backed implementation.
/// </summary>
public sealed class SystemSettingsFileSystem : ISettingsFileSystem
{
    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public string ReadAllText(string path) => File.ReadAllText(path);

    /// <inheritdoc />
    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);

    /// <inheritdoc />
    public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
        => File.Move(sourcePath, destinationPath, overwrite);

    /// <inheritdoc />
    public void CreateDirectory(string directoryPath) => Directory.CreateDirectory(directoryPath);

    /// <inheritdoc />
    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
