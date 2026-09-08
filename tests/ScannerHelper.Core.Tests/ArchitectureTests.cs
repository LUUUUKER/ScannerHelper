// =============================================================================
// ArchitectureTests.cs
//
// 中文：
//   分层守卫测试（对应测试清单 A1~A3）。
//   ScannerHelper.Core 必须保持为纯逻辑层：目标框架 net8.0、不引用 WPF、
//   不引用 ScannerHelper.Win32、不绑定任何操作系统平台。这条边界是整个
//   Phase A 能在 macOS 上开发和测试的前提，一旦被破坏，Core 将无法在
//   非 Windows 机器上编译，最高风险组件 InputEventCorrelator 也就失去了
//   用合成事件序列做 TDD 的可能。
//
//   这三条测试在当前阶段几乎必然通过——Core 目前还没有任何依赖。它们的
//   真正价值在 Phase B：当 ScannerHelper.Win32 和 ScannerHelper.App 被加入
//   解决方案后，"顺手从 Core 引用一下 Win32" 会变成一个真实且方便的诱惑。
//   届时这三条测试会立刻变红。
//
// English:
//   Layering guard tests (test plan A1–A3).
//   ScannerHelper.Core must remain a pure logic layer: it targets net8.0,
//   references no WPF, references no ScannerHelper.Win32, and binds to no OS
//   platform. This boundary is what allows all of Phase A to be developed and
//   tested on macOS.
//
//   These tests pass trivially today because Core has no dependencies yet.
//   Their value arrives in Phase B, when referencing Win32 from Core becomes a
//   convenient temptation — at which point they turn red immediately.
//
// 包含的测试 / Tests in this file:
//   A1  Core_references_no_wpf_assemblies
//   A2  Core_references_no_win32_project
//   A3  Core_targets_no_specific_os_platform
//
// 私有辅助 / Private helper:
//   LoadCoreAssembly  加载 ScannerHelper.Core 程序集
// =============================================================================

using System.Reflection;
using System.Runtime.Versioning;

namespace ScannerHelper.Core.Tests;

public class ArchitectureTests
{
    /// <summary>
    /// 中文：ScannerHelper.Core 程序集的简单名称。测试通过名称加载而非通过
    ///       typeof(SomeType).Assembly，是为了让这组测试不依赖 Core 中任何
    ///       具体类型的存在——Task 1 阶段 Core 还是空的。
    /// English: Simple name of the Core assembly. Loading by name rather than
    ///          via typeof(...) keeps these tests independent of any particular
    ///          type existing in Core, which is still empty at Task 1.
    /// </summary>
    private const string CoreAssemblyName = "ScannerHelper.Core";

    /// <summary>
    /// 中文：被禁止出现在 Core 引用列表中的 WPF / Windows 桌面程序集。
    /// English: WPF / Windows desktop assemblies forbidden in Core's references.
    /// </summary>
    private static readonly string[] ForbiddenWpfAssemblies =
    [
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "System.Windows.Forms",
    ];

    /// <summary>
    /// 中文：
    ///   加载 ScannerHelper.Core 程序集。
    ///   输入：无。
    ///   输出：ScannerHelper.Core 的 Assembly 实例。
    ///   实现方式：按简单名称调用 Assembly.Load。因为测试项目通过
    ///   ProjectReference 引用了 Core，Core.dll 会被复制到测试输出目录，
    ///   运行时可以直接按名称解析到。
    ///   若加载失败说明项目引用配置有误，此时让异常直接抛出比返回 null
    ///   更好——测试会以清晰的原因失败，而不是在后续断言处报空引用。
    ///
    /// English:
    ///   Loads the ScannerHelper.Core assembly.
    ///   Input: none. Output: the Core assembly.
    ///   Implementation: Assembly.Load by simple name. Core.dll is copied into
    ///   the test output directory by the ProjectReference, so it resolves from
    ///   the app base. A load failure means the project reference is
    ///   misconfigured; letting it throw fails the test with a clear cause
    ///   rather than producing a null-reference further down.
    /// </summary>
    private static Assembly LoadCoreAssembly()
        => Assembly.Load(new AssemblyName(CoreAssemblyName));

    /// <summary>
    /// 中文：
    ///   A1 — 断言 Core 没有引用任何 WPF 程序集。
    ///   输入：无。输出：无（断言）。
    ///   步骤：
    ///     1. 加载 Core 程序集；
    ///     2. 取出它在元数据中记录的全部引用程序集名称；
    ///     3. 与禁止列表求交集；
    ///     4. 交集必须为空。
    ///   注意：GetReferencedAssemblies 反映的是**实际被使用**的引用。若某个
    ///   程序集被声明引用但未使用任何其中的类型，编译器不会将其写入元数据，
    ///   因此这条测试抓不到"声明了但没用"的情况。那种情况由构建检查 B3
    ///   （csproj 中不得出现 ProjectReference）覆盖，且一旦真正开始使用就会
    ///   立刻在此变红。
    ///
    /// English:
    ///   A1 — asserts Core references no WPF assembly.
    ///   Steps: load Core, read its referenced assembly names, intersect with
    ///   the forbidden list, require the intersection to be empty.
    ///   Note: GetReferencedAssemblies reflects references actually *used*. A
    ///   declared-but-unused reference is not written to metadata, so this test
    ///   cannot catch that case; build check B3 covers the declaration side, and
    ///   the moment such a reference is actually used this test turns red.
    /// </summary>
    [Fact]
    public void A1_Core_references_no_wpf_assemblies()
    {
        // 步骤 1、2 / Steps 1–2
        var referencedAssemblyNames = LoadCoreAssembly()
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        // 步骤 3 / Step 3
        var forbiddenReferencesFound = referencedAssemblyNames
            .Intersect(ForbiddenWpfAssemblies, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // 步骤 4 / Step 4
        Assert.True(
            forbiddenReferencesFound.Length == 0,
            $"ScannerHelper.Core 必须保持为纯逻辑层，不得引用 WPF。"
            + $" 发现的违规引用 / Forbidden references found: "
            + $"{string.Join(", ", forbiddenReferencesFound)}");
    }

    /// <summary>
    /// 中文：
    ///   A2 — 断言 Core 没有引用 ScannerHelper.Win32。
    ///   输入：无。输出：无（断言）。
    ///   步骤：
    ///     1. 加载 Core 程序集；
    ///     2. 检查引用列表中是否存在名称以 "ScannerHelper.Win32" 开头的项。
    ///   依赖方向必须始终向内：Core 定义接口，Win32 实现接口，App 负责组装。
    ///   一旦 Core 引用了 Win32，Core 的目标框架就被迫变成 net8.0-windows，
    ///   Phase A 的全部工作立即失去承载平台。
    ///
    /// English:
    ///   A2 — asserts Core does not reference ScannerHelper.Win32.
    ///   Dependencies must always point inward: Core defines interfaces, Win32
    ///   implements them, App composes. If Core referenced Win32, Core's target
    ///   framework would be forced to net8.0-windows and all Phase A work would
    ///   lose the platform it runs on.
    /// </summary>
    [Fact]
    public void A2_Core_references_no_win32_project()
    {
        // 步骤 1、2 / Steps 1–2
        var win32ReferencesFound = LoadCoreAssembly()
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null
                && name.StartsWith("ScannerHelper.Win32", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            win32ReferencesFound.Length == 0,
            "依赖方向必须向内：Core 定义接口，Win32 实现接口。"
            + " Core 不得引用 ScannerHelper.Win32。"
            + $" 发现 / Found: {string.Join(", ", win32ReferencesFound)}");
    }

    /// <summary>
    /// 中文：
    ///   A3 — 断言 Core 没有绑定任何特定操作系统平台。
    ///   输入：无。输出：无（断言）。
    ///   步骤：
    ///     1. 加载 Core 程序集；
    ///     2. 读取程序集级别的 TargetPlatformAttribute；
    ///     3. 该特性必须不存在。
    ///   原理：当目标框架为 net8.0 时，编译器不会写入 TargetPlatformAttribute；
    ///   一旦被改成 net8.0-windows，编译器会写入 "Windows7.0" 之类的值。
    ///   因此这条测试直接盯住"把 Core 改成 Windows 专用"这个具体动作，
    ///   比检查 Microsoft.WindowsDesktop.App 更有效——后者是 framework
    ///   reference，根本不会出现在 GetReferencedAssemblies 的结果中。
    ///
    /// English:
    ///   A3 — asserts Core binds to no specific OS platform.
    ///   Under net8.0 the compiler emits no TargetPlatformAttribute; under
    ///   net8.0-windows it emits one such as "Windows7.0". This test therefore
    ///   watches the exact action worth preventing — retargeting Core to be
    ///   Windows-only. It is strictly more effective than checking for
    ///   Microsoft.WindowsDesktop.App, which is a framework reference and never
    ///   appears in GetReferencedAssemblies at all.
    /// </summary>
    [Fact]
    public void A3_Core_targets_no_specific_os_platform()
    {
        // 步骤 1、2 / Steps 1–2
        var targetPlatform = LoadCoreAssembly()
            .GetCustomAttribute<TargetPlatformAttribute>();

        // 步骤 3 / Step 3
        Assert.True(
            targetPlatform is null,
            "ScannerHelper.Core 的目标框架必须是 net8.0（跨平台），不得是"
            + " net8.0-windows。否则 Phase A 无法在 macOS 上编译和测试。"
            + $" 实际检测到的平台 / Detected platform: {targetPlatform?.PlatformName}");
    }
}
