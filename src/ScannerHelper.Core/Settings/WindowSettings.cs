// =============================================================================
// WindowSettings.cs
//
// 中文：
//   窗口位置与尺寸（规格 §11.5）。
//
//   用 double 而非 int：WPF 的坐标系是设备无关单位（DIP），在 125%、150%
//   缩放下会出现非整数值。存成 int 会在每次保存时截断，多次开关后窗口位置
//   会缓慢漂移。
//
//   ★ 本类不校验坐标是否落在可见屏幕内。规格 §11.5 要求"启动时校验存储的
//     坐标，避免显示器变化后窗口开到屏幕外"，但那需要知道当前有哪些显示器
//     ——那是 Windows 专有信息，属于 Phase B 的 App 层。Core 只负责如实存取，
//     不假装自己知道屏幕布局。
//
// English:
//   Window position and size (spec §11.5).
//
//   double rather than int: WPF coordinates are device-independent units, which are
//   non-integral at 125% and 150% scaling. Storing them as int would truncate on
//   every save and let the window drift slowly across sessions.
//
//   This type does not check whether the coordinates land on a visible screen.
//   Spec §11.5 requires validating stored coordinates at launch so a window cannot
//   reopen off-screen after a monitor change — but that needs to know which
//   monitors exist, which is Windows-specific and belongs to the App layer in
//   Phase B. Core stores and returns faithfully rather than pretending to know the
//   display layout.
//
// 包含的类型 / Types in this file:
//   WindowBounds
// =============================================================================

namespace ScannerHelper.Core.Settings;

/// <summary>
/// 中文：一个窗口的位置与尺寸。
/// English: One window's position and size.
/// </summary>
public sealed class WindowBounds
{
    /// <summary>
    /// 中文：左边距。 English: Distance from the left edge.
    /// </summary>
    public double Left { get; set; }

    /// <summary>
    /// 中文：上边距。 English: Distance from the top edge.
    /// </summary>
    public double Top { get; set; }

    /// <summary>
    /// 中文：宽度。 English: Width.
    /// </summary>
    public double Width { get; set; }

    /// <summary>
    /// 中文：高度。 English: Height.
    /// </summary>
    public double Height { get; set; }
}
