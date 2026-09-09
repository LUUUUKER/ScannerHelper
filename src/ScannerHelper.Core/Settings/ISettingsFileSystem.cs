// =============================================================================
// ISettingsFileSystem.cs
//
// 中文：
//   配置持久化所需的最小文件操作集合。
//
//   为什么要有这层抽象——只有一个理由：**让"写入中途失败"变得可测**。
//
//   配置写坏的后果不是"这次设置没保存成功"，而是"下次启动时解析规则、
//   扫码枪绑定全没了"，而那些是技术顾问远程配好的。既然这是最需要保证的
//   行为，就必须有对应的测试；而制造真实的写失败要么改目录权限、要么填满
//   磁盘，既慢又难清理，在不同机器上表现还不一致。注入一个只让写入抛异常
//   的假实现，几毫秒就能覆盖这条路径。
//
//   接口刻意只有六个方法，不做成通用的文件系统门面。多一个方法就多一处
//   将来可能被滥用的入口，而这里唯一的用途就是存取一份 JSON。
//
// English:
//   The minimal set of file operations settings persistence needs.
//
//   This abstraction exists for exactly one reason: to make "failure mid-write"
//   testable.
//
//   A corrupted write does not mean "this change was not saved"; it means the
//   parsing rule and scanner binding are gone at next launch — and those were
//   configured remotely by the technical advisor. Since that is the behavior most
//   worth guaranteeing, it needs a test; and producing a genuine write failure
//   means altering directory permissions or filling a disk, which is slow, awkward
//   to clean up, and inconsistent across machines. Injecting a fake whose writes
//   throw covers the path in milliseconds.
//
//   Six methods deliberately, rather than a general-purpose file-system facade.
//   Every extra method is another entry point that could later be misused, and the
//   only job here is reading and writing one JSON file.
//
// 包含的类型 / Types in this file:
//   ISettingsFileSystem
// =============================================================================

namespace ScannerHelper.Core.Settings;

/// <summary>
/// 中文：配置读写所需的文件操作。生产环境使用
///       <see cref="SystemSettingsFileSystem"/>，测试可注入替身。
/// English: File operations needed for settings persistence. Production uses
///          <see cref="SystemSettingsFileSystem"/>; tests inject a substitute.
/// </summary>
public interface ISettingsFileSystem
{
    /// <summary>
    /// 中文：文件是否存在。 English: Whether the file exists.
    /// </summary>
    bool FileExists(string path);

    /// <summary>
    /// 中文：读取文件全部文本内容。 English: Reads the file's entire text content.
    /// </summary>
    string ReadAllText(string path);

    /// <summary>
    /// 中文：把文本写入文件，已存在则覆盖。
    /// English: Writes text to a file, overwriting if it exists.
    /// </summary>
    void WriteAllText(string path, string contents);

    /// <summary>
    /// 中文：移动文件。原子保存的关键一步——先写临时文件，再用一次移动
    ///       把它换到目标位置，从而不存在"写了一半的配置文件"这种中间态。
    /// English: Moves a file. The key step in an atomic save: write a temp file,
    ///          then move it into place, so a half-written settings file never
    ///          exists.
    /// </summary>
    void MoveFile(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>
    /// 中文：创建目录，已存在则不做任何事。
    /// English: Creates a directory; does nothing if it already exists.
    /// </summary>
    void CreateDirectory(string directoryPath);

    /// <summary>
    /// 中文：删除文件，不存在则不做任何事。用于保存失败后清理临时文件。
    /// English: Deletes a file; does nothing if absent. Used to clean up the temp
    ///          file after a failed save.
    /// </summary>
    void DeleteFile(string path);
}
