using System.Windows;

namespace ScreenFind
{
    public partial class App : Application
    {
        protected override void OnExit(ExitEventArgs e)
        {
            PaddleOcrEngineManager.Shutdown();
            base.OnExit(e);
        }
    }
}
