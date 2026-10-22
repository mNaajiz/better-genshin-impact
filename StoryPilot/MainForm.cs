using StoryPilot.Engine;

namespace StoryPilot;

/// <summary>
/// One big Start/Stop button, a few switches, and a "More settings" section that stays hidden until needed.
/// </summary>
public sealed class MainForm : Form
{
    private const int HotkeyId = 0x5150;
    private const int WalkHotkeyId = 0x5151;

    private static readonly Color StartColor = Color.FromArgb(46, 125, 50);
    private static readonly Color StopColor = Color.FromArgb(198, 40, 40);

    private readonly Settings _settings = Settings.Load();
    private readonly StoryEngine _engine;

    private readonly Button _toggle = new()
    {
        Text = "Start",
        Dock = DockStyle.Top,
        Height = 56,
        FlatStyle = FlatStyle.Flat,
        ForeColor = Color.White,
        BackColor = StartColor,
        Font = new Font("Segoe UI Semibold", 14f),
        Cursor = Cursors.Hand,
    };

    private readonly Label _status = new() { Text = "Stopped", AutoSize = true, Margin = new Padding(0, 8, 0, 0), Font = new Font("Segoe UI", 10f) };
    private readonly Label _stats = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _lastAction = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(380, 0) };
    private readonly FlowLayoutPanel _more = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Visible = false, Margin = new Padding(16, 0, 0, 0) };
    private readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 1000 };

    public MainForm()
    {
        _engine = new StoryEngine(_settings);
        Text = "StoryPilot";
        ClientSize = new Size(400, 380);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _toggle.FlatAppearance.BorderSize = 0;

        var body = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Padding = new Padding(16, 12, 16, 16),
        };

        body.Controls.Add(Check("Skip through dialogue", _settings.AdvanceDialogue, v => _settings.AdvanceDialogue = v));
        body.Controls.Add(Check("Choose dialogue options", _settings.PickOptions, v => _settings.PickOptions = v));
        body.Controls.Add(Check("Skip cutscenes", _settings.SkipCutscenes, v => _settings.SkipCutscenes = v));
        body.Controls.Add(Check("Close pop-ups", _settings.ClosePopups, v => _settings.ClosePopups = v));
        body.Controls.Add(Check("Teleport to next quest", _settings.TeleportToNextQuest, v => _settings.TeleportToNextQuest = v));
        body.Controls.Add(Check("Auto-teleport when I pick a waypoint", _settings.AutoTeleportOnWaypoint, v => _settings.AutoTeleportOnWaypoint = v));
        body.Controls.Add(Check("Walk to quest marker after a story", _settings.WalkToQuestMarker, v => _settings.WalkToQuestMarker = v));

        body.Controls.Add(_status);
        body.Controls.Add(_stats);
        body.Controls.Add(_lastAction);

        var moreLink = new LinkLabel { Text = "More settings ▸", AutoSize = true, Margin = new Padding(0, 12, 0, 4) };
        moreLink.LinkClicked += (_, _) =>
        {
            _more.Visible = !_more.Visible;
            moreLink.Text = _more.Visible ? "More settings ▾" : "More settings ▸";
        };
        body.Controls.Add(moreLink);
        BuildMoreSettings();
        body.Controls.Add(_more);

        body.Controls.Add(new Label
        {
            Text = "Keep the game windowed. It can be behind other windows, but not minimized. Ctrl+Alt+P starts/stops. Ctrl+Alt+W walks to the quest marker (game in front).",
            AutoSize = true,
            MaximumSize = new Size(370, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 12, 0, 0),
        });

        Controls.Add(body);
        Controls.Add(_toggle);

        _toggle.Click += (_, _) => Toggle();
        _engine.Log += line => BeginInvokeSafe(() => _lastAction.Text = $"Last: {line} ({DateTime.Now:HH:mm:ss})");
        _engine.StatusChanged += s => BeginInvokeSafe(() => _status.Text = s);
        _statsTimer.Tick += (_, _) => _stats.Text = _engine.IsRunning
            ? $"Lines skipped {_engine.Advanced} · Options chosen {_engine.OptionsPicked} · Cutscenes/pop-ups {_engine.Skipped}"
            : "";
        _statsTimer.Start();
    }

    private void BuildMoreSettings()
    {
        _more.Controls.Add(Check("Prefer quest options (orange text or \"!\")", _settings.PreferQuestOptions, v => _settings.PreferQuestOptions = v));

        var choice = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        choice.Controls.Add(new Label { Text = "Otherwise choose", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        var choiceBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        choiceBox.Items.AddRange(["the first option", "the last option"]);
        choiceBox.SelectedIndex = _settings.DefaultOption == OptionChoice.First ? 0 : 1;
        choiceBox.SelectedIndexChanged += (_, _) =>
        {
            _settings.DefaultOption = choiceBox.SelectedIndex == 0 ? OptionChoice.First : OptionChoice.Last;
            _settings.Save();
        };
        choice.Controls.Add(choiceBox);
        _more.Controls.Add(choice);

        var delay = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        delay.Controls.Add(new Label { Text = "Wait before choosing", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        var delayBox = new NumericUpDown { Minimum = 0, Maximum = 10, DecimalPlaces = 1, Increment = 0.5m, Value = Math.Clamp(_settings.OptionDelayMs / 1000m, 0, 10), Width = 60 };
        delayBox.ValueChanged += (_, _) =>
        {
            _settings.OptionDelayMs = (int)(delayBox.Value * 1000);
            _settings.Save();
        };
        delay.Controls.Add(delayBox);
        delay.Controls.Add(new Label { Text = "seconds", AutoSize = true, Margin = new Padding(4, 6, 0, 0) });
        _more.Controls.Add(delay);

        var far = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        far.Controls.Add(new Label { Text = "Teleport only if the quest is over", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        var farBox = new NumericUpDown { Minimum = 50, Maximum = 5000, Increment = 50, Value = Math.Clamp(_settings.TeleportMinDistanceMetres, 50, 5000), Width = 70 };
        farBox.ValueChanged += (_, _) =>
        {
            _settings.TeleportMinDistanceMetres = (int)farBox.Value;
            _settings.Save();
        };
        far.Controls.Add(farBox);
        far.Controls.Add(new Label { Text = "m away", AutoSize = true, Margin = new Padding(4, 6, 0, 0) });
        _more.Controls.Add(far);

        var tpDelay = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
        tpDelay.Controls.Add(new Label { Text = "Auto-teleport wait", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
        var tpDelayBox = new NumericUpDown { Minimum = 0, Maximum = 2000, Increment = 50, Value = Math.Clamp(_settings.AutoTeleportDelayMs, 0, 2000), Width = 70 };
        tpDelayBox.ValueChanged += (_, _) =>
        {
            _settings.AutoTeleportDelayMs = (int)tpDelayBox.Value;
            _settings.Save();
        };
        tpDelay.Controls.Add(tpDelayBox);
        tpDelay.Controls.Add(new Label { Text = "ms", AutoSize = true, Margin = new Padding(4, 6, 0, 0) });
        _more.Controls.Add(tpDelay);
        _more.Controls.Add(Check("Sprint while walking to the quest", _settings.WalkSprint, v => _settings.WalkSprint = v));

        var save = new Button { Text = "Save screenshot of the game", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        save.Click += (_, _) =>
        {
            var path = _engine.SaveFrame();
            _lastAction.Text = path == null ? "Press Start first, then save a screenshot." : $"Saved to {path}";
        };
        _more.Controls.Add(save);
    }

    private CheckBox Check(string text, bool value, Action<bool> set)
    {
        var box = new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(0, 3, 0, 3) };
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
            _toggle.BackColor = StartColor;
        }
        else
        {
            _engine.Start();
            _toggle.Text = "Stop";
            _toggle.BackColor = StopColor;
        }
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
            _lastAction.Text = "Ctrl+Alt+P is used by another app; use the button instead.";
        }

        if (!Native.RegisterHotKey(Handle, WalkHotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.W))
        {
            _lastAction.Text = "Ctrl+Alt+W is used by another app.";
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == (int)Native.WM_HOTKEY && m.WParam == HotkeyId)
        {
            Toggle();
        }
        else if (m.Msg == (int)Native.WM_HOTKEY && m.WParam == WalkHotkeyId)
        {
            if (_engine.IsRunning)
            {
                _engine.ToggleWalk();
            }
            else
            {
                _lastAction.Text = "Press Start first, then Ctrl+Alt+W to walk to the quest marker.";
            }
        }

        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Native.UnregisterHotKey(Handle, HotkeyId);
        Native.UnregisterHotKey(Handle, WalkHotkeyId);
        _statsTimer.Stop();
        _engine.Dispose();
        base.OnFormClosing(e);
    }
}
