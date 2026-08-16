using Cursorial.Input;
using Cursorial.UI;
using Cursorial.UI.Controls;
using Cursorial.UI.Input;
using Cursorial.UI.Themes;

// ReSharper disable CheckNamespace

// Inline-presentation showcase: the application renders in a region BELOW the shell prompt (no alt
// screen, no clear-screen) that grows and shrinks with its content, live against the real terminal:
//   + / =            add a task row (the region grows; at the terminal bottom it scrolls the
//                    shell history up to make room),
//   -                remove a task row (the region shrinks; the vacated rows are wiped),
//   Space            toggle the focused task's check,
//   r                toggle the exit behavior LIVE between Clear (the region vanishes and the
//                    prompt resumes where the demo started) and Retain (the last frame stays in
//                    the scrollback and the prompt resumes below it),
//   q / Esc / Ctrl+C exit.
// The status line shows the current row count and exit behavior. Run it from a prompt part-way
// down the screen to see the region root there; run it near the bottom to see the growth scroll.
internal sealed class InlineDemo : IDemo
{
    public string Name => "inline";
    public IReadOnlyList<string> Aliases => ["il"];
    public string Description => "Inline-presentation showcase (renders below the prompt, sizes to content; +/- grow/shrink, r toggles Clear/Retain exit, q quits).";

    public async Task RunAsync(string argument)
    {
        Console.WriteLine("Inline demo: the UI renders below this line — no alt screen. '+'/'-' grow/shrink, " +
                          "Space toggles, 'r' flips the exit behavior (Clear/Retain), q / Esc / Ctrl+C exits.");

        var app = UIApplication.DefaultBuilder().UseInline(maxHeight: 12).Build();
        var controller = new Controller(app);

        try
        {
            await app.RunAsync(controller.BuildTree);
        }
        finally
        {
            await app.DisposeAsync(); // clears the thread-local Current for the next demo run
        }

        Console.WriteLine($"Back in the shell (exit behavior was {app.InlineExitBehavior}).");
    }

    private sealed class Controller(UIApplication app)
    {
        private readonly List<CheckBox> _tasks = [];
        private StackPanel _list = null!;
        private TextBlock _status = null!;
        private int _created;

        public UIElement BuildTree()
        {
            var root = new StackPanel();
            root.SetResourceReference(Panel.BackgroundProperty, ThemeKeys.WindowBackground);
            root.AddHandler(UIElement.KeyDownEvent, OnRootKeyDown);

            root.Children.Add(new TextBlock { Text = " Inline task list — the region below the prompt sizes to this content" });

            _list = new StackPanel();
            root.Children.Add(_list);

            _status = new TextBlock();
            root.Children.Add(_status);

            AddTask();
            AddTask();
            AddTask();
            UpdateStatus();
            return root;
        }

        private void AddTask()
        {
            var task = new CheckBox { Content = new TextBlock { Text = $" Task #{++_created}" } };
            task.Click += (_, _) => UpdateStatus();
            _tasks.Add(task);
            _list.Children.Add(task);
        }

        private void RemoveTask()
        {
            if (_tasks.Count == 0)
                return;

            var last = _tasks[^1];
            _tasks.RemoveAt(_tasks.Count - 1);
            _list.Children.Remove(last);
        }

        private void UpdateStatus()
        {
            var done = _tasks.Count(t => t.IsChecked == true);
            _status.Text = $" {_tasks.Count} task(s), {done} done · exit: {app.InlineExitBehavior} · +/- grow/shrink · r toggle exit · q quit";
        }

        private void OnRootKeyDown(object? sender, KeyEventArgs e)
        {
            if ((e.Modifiers & KeyModifiers.Control) != 0)
                return; // leave Ctrl-modified keys (the Ctrl+C exit gesture) alone

            switch (e.Key)
            {
                case Key.Escape:
                    app.Shutdown();
                    e.Handled = true;
                    return;

                case Key.Character when e.Text.Length > 0:
                    switch (char.ToLowerInvariant(e.Text.Span[0]))
                    {
                        case '+' or '=':
                            AddTask();
                            break;

                        case '-':
                            RemoveTask();
                            break;

                        case 'r':
                            app.InlineExitBehavior = app.InlineExitBehavior == InlineExitBehavior.Clear
                                                         ? InlineExitBehavior.Retain
                                                         : InlineExitBehavior.Clear;
                            break;

                        case 'q':
                            app.Shutdown();
                            e.Handled = true;
                            return;

                        default:
                            return;
                    }

                    UpdateStatus();
                    e.Handled = true;
                    return;
            }
        }
    }
}
