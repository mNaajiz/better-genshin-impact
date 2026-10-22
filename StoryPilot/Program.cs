namespace StoryPilot;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var single = new Mutex(true, "StoryPilot.SingleInstance", out var first);
        if (!first)
        {
            MessageBox.Show("StoryPilot is already running.", "StoryPilot");
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
