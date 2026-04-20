using Microsoft.UI.Xaml;
using System.Threading.Tasks;

namespace BlenderRenderStudio;

public partial class App : Application
{
    private Window? _window;

<<<<<<< HEAD
    /// <summary>当前主窗口实例（供 FileOpenPicker 等需要 hwnd 的组件使用）</summary>
    public static Window CurrentWindow { get; private set; } = null!;

=======
>>>>>>> 24b10e2407b584065c0922a9cd8684aebb0d1adc
    public App()
    {
        InitializeComponent();
        // 全局兜底：防止 fire-and-forget Task 的未观察异常导致 0xC000027B 崩溃
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
        UnhandledException += (_, e) => e.Handled = true;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
<<<<<<< HEAD
        CurrentWindow = _window;
=======
>>>>>>> 24b10e2407b584065c0922a9cd8684aebb0d1adc
        _window.Activate();
    }
}
