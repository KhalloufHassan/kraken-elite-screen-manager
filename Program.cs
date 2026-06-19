using Avalonia;
using KrakenEliteScreenManager;
using KrakenEliteScreenManager.Services;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Install headless Chromium for Dashboard mode (one-time).
        if (args.Length > 0 && args[0] == "playwright-install")
        {
            Environment.Exit(Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }));
            return;
        }

        // Install/enable/start the systemd user service (used by install.sh; no GUI).
        if (args.Length > 0 && args[0] == "install")
        {
            SystemdManager.ApplyAndRestart();
            Console.WriteLine($"installed, enabled and started {SystemdManager.Unit}");
            return;
        }

        // Headless daemon mode (started by systemd): keep the configured screen applied.
        if (args.Length > 0 && args[0] == "service")
        {
            Environment.Exit(ServiceRunner.RunAsync().GetAwaiter().GetResult());
            return;
        }

        // Otherwise launch the GUI config editor.
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
