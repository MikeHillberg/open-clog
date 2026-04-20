using Microsoft.UI.Xaml;

namespace open_clog
{
    public partial class App : Application
    {
        private Window? _window;

        public static bool TestInstall { get; private set; }

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            var cmdArgs = System.Environment.GetCommandLineArgs();
            for (int i = 1; i < cmdArgs.Length - 1; i++)
            {
                if (cmdArgs[i] == "--test" && cmdArgs[i + 1] == "install")
                {
                    TestInstall = true;
                    break;
                }
            }

            _window = new MainWindow();
            _window.Activate();
        }
    }
}
