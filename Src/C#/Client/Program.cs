using EasyPlayerNetSDK;

namespace WinRtspPlayer.Client;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        try
        {
            NativeDllLoader.ConfigureLibDirectory(AppContext.BaseDirectory);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to configure native EasyPlayer library directory:\n{ex.Message}",
                "Win RTSP Player Startup Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        Application.Run(new MainForm());
    }
}
