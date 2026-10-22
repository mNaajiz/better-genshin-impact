using StoryPilot.Engine;

namespace StoryPilot;

public sealed class MainForm : Form
{
    private const int HotkeyId = 0x5150;

    private readonly Settings _settings = Settings.Load();
    private readonly StoryEngine _engine;

    private readonly Button _toggle = new() { Text = "Start", Width = 120, Height = 36 };
    private readonly Label _status = new() { Text = "Stopped", AutoSize = true };
    private readonly Label _stats = new() { AutoSize = true };
    private readonly ListBox _log = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 1000 };

    public MainForm()
    {
        _engine = new StoryEngine(_settings);
        Text = "StoryPilot — Genshin story auto-play";
        Width = 460;
        Height = 560;
        MinimumSize = new Size(400, 460);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        var options = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            Padding = new Padding(12, 10, 12, 4),
            WrapContents = false,
        };

        options.Controls.Add(Check("Advance dialogue (Space)", _settings.AdvanceDialogue, v => _settings.AdvanceDialogue = v));
        options.Controls.Add(Check("Auto-pick dialogue options", _settings.PickOptions, v => _settings.PickOptions = v));
        options.Controls.Add(Check("    Prefer quest options (orange text / \"!\")", _settings.PreferQuestOptions, v => _settings.PreferQuestOptions = v));

        var choice = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(18, 0, 0, 4) };
        choice.Controls.Add(new Label { Text = "Otherwise pick:", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        var choiceBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        choiceBox.Items.AddRange(["First option", "Last option"]);
        choiceBox.SelectedIndex = _settings.DefaultOption == OptionChoice.First ? 0 : 1;
        choiceBox.SelectedIndexChanged += (_, _) =>
        {
            _settings.DefaultOption = choiceBox.SelectedIndex == 0 ? OptionChoice.First : OptionChoice.Last;
            _settings.Save();
        };
        choice.Controls.Add(choiceBox);
        options.Controls.Add(choice);

        options.Controls.Add(Check("Skip cutscenes (black screens, Skip buttons)", _settings.SkipCutscenes, v => _settings.SkipCutscenes = v));
        options.Controls.Add(Check("Close pop-ups after dialogue", _settings.ClosePopups, v => _settings.ClosePopups = v));

        var delay = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        delay.Controls.Add(new Label { Text = "Wait before picking an option (ms):", AutoSize = true, Margin = new Padding(0, 6, 4, 0) });
        var delayBox = new NumericUpDown { Minimum = 0, Maximum = 10000, Increment = 250, Value = Math.Clamp(_settings.OptionDelayMs, 0, 10000), Width = 80 };
        delayBox.ValueChanged += (_, _) =>
        {
            _settings.OptionDelayMs = (int)delayBox.Value;
            _settings.Save();
        };
        delay.Controls.Add(delayBox);
        options.Controls.Add(delay);

        var controls = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        controls.Controls.Add(_toggle);
        var save = new Button { Text = "Save screenshot", Width = 130, Height = 36 };
        save.Click += (_, _) =>
        {
            var path = _engine.SaveFrame();
            AddLog(path == null ? "No game frame to save (start first)" : $"Saved {path}");
        };
        controls.Controls.Add(save);
        options.Controls.Add(controls);

        options.Controls.Add(new Label { Text = "Hotkey: Ctrl+Alt+P to start/stop", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 6, 0, 0) });
        options.Controls.Add(_status);
        options.Controls.Add(_stats);

        var logBox = new GroupBox { Text = "Activity", Dock = DockStyle.Fill, Padding = new Padding(8) };
        logBox.Controls.Add(_log);
        var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 12) };
        logHost.Controls.Add(logBox);

        Controls.Add(logHost);
        Controls.Add(options);

        _toggle.Click += (_, _) => Toggle();
        _engine.Log += AddLog;
        _engine.StatusChanged += s => BeginInvokeSafe(() => _status.Text = "Status: " + s);
        _statsTimer.Tick += (_, _) => _stats.Text = $"Advanced {_engine.Advanced} · Options {_engine.OptionsPicked} · Skips {_engine.Skipped}";
        _statsTimer.Start();
    }

    private CheckBox Check(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Text = text, Checked = value, AutoSize = true };
        box.CheckedChanged += (_, _) =>
        {
            set(box.Checked);
            _settings.Save();
        };
        return box;
    }

    private void Toggle()
    {
        if (_engine.IsRunning)
        {
            _engine.Stop();
            _toggle.Text = "Start";
            AddLog("Stopped");
        }
        else
        {
            _engine.Start();
            _toggle.Text = "Stop";
            AddLog("Started — keep the game windowed (it can be behind other windows, not minimized)");
        }
    }

    private void AddLog(string line)
    {
        BeginInvokeSafe(() =>
        {
            _log.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
            while (_log.Items.Count > 200)
            {
                _log.Items.RemoveAt(_log.Items.Count - 1);
            }
        });
    }

    private void BeginInvokeSafe(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // Form is closing.
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!Native.RegisterHotKey(Handle, HotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.P))
        {
            AddLog("Couldn't register Ctrl+Alt+P (another app uses it)");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)Native.WM_HOTKEY && m.WParam == HotkeyId)
        {
            Toggle();
        }

        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Native.UnregisterHotKey(Handle, HotkeyId);
        _statsTimer.Stop();
        _engine.Dispose();
        base.OnFormClosing(e);
    }
}
