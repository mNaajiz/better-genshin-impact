using OpenCvSharp;
using StoryPilot.Capture;
using Xunit;

namespace StoryPilot.Tests;

public class CaptureTests
{
    /// <summary>
    /// Opens a solid-colour window, covers it with a second window, and checks that
    /// Windows Graphics Capture still returns the covered window's client area.
    /// </summary>
    [Fact]
    public void CapturesWindowWhileCovered()
    {
        Mat? frame = null;
        var thread = new Thread(() =>
        {
            using var target = new Form { Width = 640, Height = 400, BackColor = System.Drawing.Color.FromArgb(255, 0, 0), StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(100, 100), ShowInTaskbar = false };
            using var cover = new Form { Width = 800, Height = 600, BackColor = System.Drawing.Color.FromArgb(0, 0, 255), StartPosition = FormStartPosition.Manual, Location = new System.Drawing.Point(50, 50), ShowInTaskbar = false };
            target.Show();
            cover.Show();
            cover.BringToFront();
            Application.DoEvents();
            using var capture = new WgcCapture();
            capture.Start(target.Handle);
            var until = DateTime.Now.AddSeconds(5);
            while (DateTime.Now < until && frame == null)
            {
                target.Invalidate();
                Application.DoEvents();
                frame = capture.Grab(200);
            }

            capture.Stop();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.NotNull(frame);
        using (frame)
        {
            var c = frame!.At<Vec3b>(frame.Height / 2, frame.Width / 2);
            Assert.True(c.Item2 > 200 && c.Item0 < 50, $"expected red, got BGR {c.Item0},{c.Item1},{c.Item2}");
            Assert.InRange(frame.Width, 600, 660);
        }
    }
}
