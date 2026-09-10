// =============================================================================
// App.xaml.cs
//
// 中文：
//   诊断工具的应用入口。
//
//   本类刻意保持为空壳。所有观测逻辑都在 ObservationSession 里，窗口只负责
//   显示——4b 要把观测模式换成拦截模式时，改的是那一个类。
//
// English:
//   The harness's application entry point.
//
//   Deliberately an empty shell. All observation logic lives in ObservationSession and
//   the window only displays it, so switching from observation to interception in 4b
//   changes that one class.
//
// 包含的类型 / Types in this file:
//   App
// =============================================================================

using System.Windows;

namespace ScannerHelper.Diagnostics.Harness;

/// <summary>
/// 中文：应用入口。
/// English: The application entry point.
/// </summary>
public partial class App : Application
{
}
