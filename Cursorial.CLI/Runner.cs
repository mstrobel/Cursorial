using Cursorial.CLI.Commandlets;
using Cursorial.CLI.Views;
using Cursorial.CLI.Wire;
using Cursorial.Input;
using Cursorial.Terminal;
using Cursorial.UI;
using Cursorial.UI.Configuration;
using Cursorial.UI.Input;

namespace Cursorial.CLI;

/// <summary>
/// The pipeline runner (docs/cli-design.md §2): one owned <see cref="TerminalSession"/> per invocation
/// (negotiated once, signal net attached), one short-lived inline <see cref="UIApplication"/> per step
/// (sequential runs are a supported framework pattern), receipts via the runtime-assignable
/// <see cref="InlineExitBehavior"/>, results streamed to the REAL stdout between steps (the UI paints on
/// the controlling tty; stdout stays pure for results).
/// </summary>
public static class Runner
{
    private const int InlineMaxHeight = 16;

    public static async Task<int> RunAsync(string[] argv)
    {
        if (argv.Length == 0 || argv[0] is "--help" or "-h" or "help")
        {
            PrintHelp(Console.Out);
            return argv.Length == 0 ? ExitCodes.Usage : ExitCodes.Accepted;
        }

        if (argv[0] is "--version")
        {
            Console.Out.WriteLine(typeof(Runner).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
            return ExitCodes.Accepted;
        }

        IReadOnlyList<string[]> steps;

        GlobalArgs globals;
       
        try
        {
            argv = TakeGlobalOptions(argv, out globals);
            steps = PipelineParser.Split(argv);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"curio: {ex.Message}");
            return ExitCodes.Usage;
        }

        if (globals.Debug)
        {
            while (System.Diagnostics.Debugger.IsAttached is false)
                await Task.Delay(50);
        }

        // Steps that feed on piped stdin data read it BEFORE the session opens (the session owns the
        // tty; the pipe on fd 0 is read raw, never through System.Console — the framework's termios
        // warning). Null when stdin is a terminal.
        var stdinItems = StdinFeed.TryReadLines();

        // Capability cache (docs/cli-design.md §6, FW-1): warm key → seed the session, skipping the
        // identity + DECRQM handshake but still refreshing the volatile default colours (below); cold
        // key → normal negotiation, then persist the realized snapshot for next time. Kill-switches:
        // --no-caps-cache and CURIO_NO_CAPS_CACHE (checked inside CapabilityCache too — belt and braces
        // for future call sites).
        bool capsCacheEnabled = !globals.NoCapsCache && !CapabilityCache.IsDisabledByEnvironment;
        var cachedCaps = capsCacheEnabled ? CapabilityCache.TryLoad() : null;

        TerminalSession session;
        try
        {
            session = await TerminalSession.OpenAsync(new TerminalSessionOptions
            {
                CachedCapabilities = cachedCaps,
                // FW-10: refresh the volatile default colours even on a warm seed. The cache key doesn't
                // track a terminal light/dark theme flip, so a purely cached background goes stale; one
                // OSC 10/11/12 + DA1 round-trip (sub-frame — see the `bgprobe` demo) keeps light/dark
                // correct. No-op on a cold run (full negotiation always probes colours).
                Negotiation = new NegotiationOptions { RefreshColorsFromCache = true },
            });
        }
        catch (InvalidOperationException ex)
        {
            // No controlling terminal (CI/cron/ssh-without-t): the documented non-interactive policy —
            // a step with --default resolves to it (exit 0); any step without one is a usage failure.
            return RunNonInteractive(steps, stdinItems, globals.Format, ex.Message);
        }

        // Cold run: persist what negotiation just realized, post-open so it never delays first paint.
        if (capsCacheEnabled && cachedCaps is null)
            CapabilityCache.TryStore(session.Capabilities);

        var vars = new VariableBag();
        var exit = ExitCodes.Accepted;
        Variable? linesResult = null;
        string? usageError = null;
        try
        {
            foreach (var rawStep in steps)
            {
                StepArgs args;
                CommandletViewModel vm;
                Func<UIElement> viewFactory;
                var final = ReferenceEquals(rawStep, steps[^1]);
                var app = BuildStepApp(final);
                UIElement? stepRoot = null;
                try
                {
                    args = StepArgs.Parse(Interpolator.Apply(rawStep, vars));
                    (viewFactory, vm) = CreateStep(app, args, stdinItems);
                }
                catch (UsageException ex)
                {
                    await app.DisposeAsync();
                    usageError = ex.Message; // stderr can be this same RAW tty — written after the teardown
                    exit = ExitCodes.Usage;
                    break;
                }

                int code;
                try
                {
                    WireCancelKeys(app);
                    // Capture the step's root so it can be permanently torn down once the step's app is
                    // gone. curio is RootElement-hosted (not window-hosted), so the window-close teardown
                    // sweep never runs here — each stage's view is a discarded subtree whose bindings to
                    // its commandlet view-model must be released, or every stage in a pipeline accumulates
                    // a live view-model until process exit (Element-Lifecycle-and-Teardown wiki: discarding
                    // a subtree yourself).
                    code = await app.RunAsync(() => stepRoot = viewFactory());
                }
                finally
                {
                    await app.DisposeAsync(); // stdout writes are only safe after teardown
                }

                // DisposeAsync detached the root (reversible); this is the permanent sweep — same UI
                // thread, so VerifyAccess (thread-affinity only) is satisfied. Idempotent + re-entrancy
                // guarded, so a no-op on a never-built root is free.
                stepRoot?.TearDown();

                if (code == ExitCodes.Accepted)
                {
                    var variable = vm.BuildResult(args.Var ?? args.CommandletName);
                    if (args.Var is not null)
                        BindVariable(vars, variable);
                    if (globals.Format == EmitFormat.Lines && final)
                        linesResult = variable; // written AFTER the session teardown with the other formats
                    continue;
                }

                if (code == ExitCodes.Canceled && args.Optional)
                    continue; // soft cancel on an optional step: variable stays unbound, pipeline goes on

                exit = code;
                break;
            }
        }
        finally
        {
            await session.DisposeAsync();
        }

        if (usageError is not null)
            Console.Error.WriteLine($"curio: {usageError}");

        // EVERY format emits here, after the session teardown, and only on full success. Beyond the
        // buffered formats' only-on-success rule there is a terminal reason: the session holds the tty
        // RAW (output post-processing off), so a `\n` written while it is open is a bare LF — the cursor
        // keeps its column, and the shell prompt lands indented by the emitted width.
        if (exit == ExitCodes.Accepted)
        {
            if (globals.Format == EmitFormat.Lines && linesResult is not null) Emit.WriteLines(Console.Out, linesResult);
            else if (globals.Format == EmitFormat.Env) Emit.WriteEnv(Console.Out, vars);
            else if (globals.Format == EmitFormat.Json) Emit.WriteJson(Console.Out, vars);
        }

        return exit;

        UIApplication BuildStepApp(bool final)
        {
            var exitBehavior = globals.Retain switch
                               {
                                   RetainMode.All              => InlineExitBehavior.Retain,
                                   RetainMode.Final when final => InlineExitBehavior.Retain,
                                   _                           => InlineExitBehavior.Clear
                               };

            return UIApplication.CreateBuilder()
                                .WithFrameRate(60)
                                .WithUserConfiguration(new UserConfigurationOptions
                                                       {
                                                           ApplicationId = "curio",
                                                           ShowFirstRunWizard = false
                                                       })
                                .WithKeyReleaseSynthesis()
                                .WithNumpadKeyTranslation()
                                .UseInlineWithSwitching(maxHeight: InlineMaxHeight, exitBehavior)
                                .WithSession(session)
                                .ExitOnUnhandledCtrlC(false) // curio owns cancel codes: Ctrl+C is 130, not 0
                                .Build();
        }
    }

    private static void BindVariable(VariableBag vars, Variable variable)
    {
        switch (variable.Kind)
        {
            case VariableKind.Selection:
                vars.BindSelection(variable.Name, variable.Values, variable.Indices);
                break;
            case VariableKind.Bool:
                vars.BindBool(variable.Name, string.Equals(variable.Values[0], "true", StringComparison.Ordinal));
                break;
            default:
                vars.BindText(variable.Name, variable.Values.Count > 0 ? variable.Values[0] : "");
                break;
        }
    }

    private static (Func<UIElement> ViewFactory, CommandletViewModel Vm) CreateStep(UIApplication app, StepArgs args, IReadOnlyList<string>? stdinItems)
    {
        switch (args.CommandletName)
        {
            case "choose":
            {
                var items = args.Positionals;
                if (items.Count == 0 && stdinItems is { Count: > 0 })
                    items = stdinItems; // `git branch | curio choose` — the piped feed
                if (items.Count == 0)
                    throw new UsageException("choose needs at least one item (curio choose <item>... or pipe items on stdin)");
                var vm = new ChooseViewModel(app, args.GetOption("prompt") ?? "Choose:", items);
                return (() => new ChooseView { DataContext = vm }, vm);
            }
            case "input":
            {
                var placeholder = args.GetOption("placeholder") ?? "";
                var prompt = args.GetOption("prompt") ?? (string.IsNullOrWhiteSpace(placeholder) ? ">" : "");
                var vm = new InputViewModel(app, prompt, args.GetOption("value") ?? "", placeholder);
                return (() => new InputView { DataContext = vm }, vm);
            }
            case "filter":
            {
                var items = args.Positionals;
                if (items.Count == 0 && stdinItems is { Count: > 0 })
                    items = stdinItems; // `git branch | curio filter` — the piped feed, same as choose
                if (items.Count == 0)
                    throw new UsageException("filter needs at least one item (curio filter <item>... or pipe items on stdin)");
                var vm = new FilterViewModel(app, args.GetOption("prompt"), items, args.GetOption("placeholder"));
                return (() => new FilterView { DataContext = vm }, vm);
            }
            case "write":
            {
                var placeholder = args.GetOption("placeholder") ?? "";
                var prompt = args.GetOption("prompt") ?? (string.IsNullOrWhiteSpace(placeholder) ? ">" : "");
                var lines = args.GetOption("lines") is {} l && int.TryParse(l, out int c) ? c : 5;
                var vm = new WriteViewModel(app, prompt, args.GetOption("value") ?? "", placeholder, lines);
                return (() => new WriteView { DataContext = vm }, vm);
            }
            case "confirm":
            {
                var defaultResponse = args.GetOption("default") switch
                                      {
                                          "y" or "Y" or "0" or "1"               => true,
                                          var s when bool.TryParse(s, out var b) => b,
                                          _                                      => false
                                      };
                var message = args.Positionals.Count > 0 ? string.Join(" ", args.Positionals) : "Proceed?";
                var vm = new ConfirmViewModel(app, message, defaultResponse);
                return (() => new ConfirmView { DataContext = vm }, vm);
            }
            default:
                throw new UsageException($"unknown commandlet '{args.CommandletName}' (try: choose, input, confirm, filter, write)");
        }
    }

    // Esc = back out (canceled, region cleared); Ctrl+C = hard abort (130). Wired pre-dispatch so the
    // convention is uniform across commandlets; Shutdown is idempotent (first code wins), so the synthesized
    // key-release echo of the same gesture is harmless.
    private static void WireCancelKeys(UIApplication app)
    {
        app.InputDispatcher.PreProcessInput += (_, e) =>
        {
            if (e is not KeyEventArgs { Device.Kind: KeyEventKind.Down } key)
                return;

            if (key is { Key: Key.Character, Text.Length: > 0 } &&
                key.Modifiers.HasFlag(KeyModifiers.Control) &&
                key.Text.Span[0] is 'c' or 'C')
            {
                app.InlineExitBehavior = InlineExitBehavior.Clear;
                app.Shutdown(ExitCodes.CtrlC);
                key.Handled = true;
            }
        };

        app.InputDispatcher.PostProcessInput += (_, e) =>
        {
            if (e is not KeyEventArgs { Device.Kind: KeyEventKind.Down, Handled: false } key)
                return;

            if (key.Key == Key.Escape)
            {
                app.InlineExitBehavior = InlineExitBehavior.Clear;
                app.Shutdown(ExitCodes.Canceled);
                key.Handled = true;
            }
        };
    }

    // The leading global options (first arguments only, like --sep): --emit lines|env|json (CURIO_EMIT
    // is the environment default; buffered formats emit only on full pipeline success) and
    // --no-caps-cache (skip the capability cache for this run; CURIO_NO_CAPS_CACHE is the
    // environment kill-switch).
    internal static string[] TakeGlobalOptions(string[] argv, out GlobalArgs globals)
    {
        var format = Environment.GetEnvironmentVariable("CURIO_EMIT") switch
                     {
                         "env"  => EmitFormat.Env,
                         "json" => EmitFormat.Json,
                         _      => EmitFormat.Lines,
                     };

        var noCapsCache = false;
        var debug = false;

        // The env twin is LENIENT (unknown → the default) like CURIO_EMIT above; the FLAG is strict —
        // a typo on the command line is a usage error, a stale environment variable is not.
        var retainMode = Environment.GetEnvironmentVariable("CURIO_RETAIN")?.ToLowerInvariant() switch
                         {
                             "a" or "all"   => RetainMode.All,
                             "f" or "final" => RetainMode.Final,
                             _              => RetainMode.None,
                         };

        var index = 0;

        while (index < argv.Length)
        {
            string? value;

            if (argv[index] == "--no-caps-cache")
            {
                noCapsCache = true;
                argv = [.. argv[..index], .. argv[(index + 1)..]];
                continue;
            }

            if (argv[index] == "--debug")
            {
                debug = true;
                argv = [.. argv[..index], .. argv[(index + 1)..]];
                continue;
            }
            
            if (argv[index] == "--retain" && index + 1 < argv.Length)
            {
                retainMode = ParseRetain(argv[index + 1]);
                argv = [.. argv[..index], .. argv[(index + 2)..]];
                continue;
            }

            if (argv[index].StartsWith("--retain=", StringComparison.Ordinal))
            {
                retainMode = ParseRetain(argv[index]["--retain=".Length..]);
                argv = [.. argv[..index], .. argv[(index + 1)..]];
                continue;
            }

            if (argv[index] == "--emit" && index + 1 < argv.Length)
            {
                value = argv[index + 1];
                argv = [.. argv[..index], .. argv[(index + 2)..]];
            }
            else if (argv[index].StartsWith("--emit=", StringComparison.Ordinal))
            {
                value = argv[index]["--emit=".Length..];
                argv = [.. argv[..index], .. argv[(index + 1)..]];
            }
            else if (argv[index] is "--retain" or "--emit")
            {
                // The bare flag at the end of the leading globals: without this arm it would fall to the
                // break and surface as the step parser's baffling "Expected a commandlet name, got '--…'".
                throw new UsageException(argv[index] == "--retain"
                    ? "--retain requires a value (none, all, or final)"
                    : "--emit requires a value (lines, env, or json)");
            }
            else
            {
                break; // only leading global options are curio's; everything after belongs to steps
            }

            format = value switch
                     {
                         "lines" => EmitFormat.Lines,
                         "env"   => EmitFormat.Env,
                         "json"  => EmitFormat.Json,
                         _       => throw new UsageException($"--emit must be lines, env, or json (got '{value}')"),
                     };
        }

        globals = new GlobalArgs(debug, format, noCapsCache, retainMode);
        return argv;
    }

    private static RetainMode ParseRetain(string value) => value.ToLowerInvariant() switch
    {
        "n" or "none"  => RetainMode.None,
        "a" or "all"   => RetainMode.All,
        "f" or "final" => RetainMode.Final,
        _              => throw new UsageException($"--retain must be none, all, or final (got '{value}')"),
    };

    // The non-interactive policy (no controlling terminal): a step with --default resolves to it as an
    // accepted result; any step without one fails the run with a usage error naming the step. Emits
    // behave exactly as interactively — lines carries the FINAL step's value, buffered formats the bag.
    internal static int RunNonInteractive(IReadOnlyList<string[]> steps, IReadOnlyList<string>? stdinItems,
                                         EmitFormat emitFormat, string reason)
    {
        var vars = new VariableBag();
        foreach (var rawStep in steps)
        {
            StepArgs args;
            try
            {
                args = StepArgs.Parse(Interpolator.Apply(rawStep, vars));
            }
            catch (UsageException ex)
            {
                Console.Error.WriteLine($"curio: {ex.Message}");
                return ExitCodes.Usage;
            }

            if (args.Default is not { } fallback)
            {
                Console.Error.WriteLine(
                    $"curio: no interactive terminal ({reason}) and step '{args.CommandletName}' has no --default");
                return ExitCodes.Usage;
            }

            var variable = args.CommandletName switch
                           {
                               "confirm" => new Variable(args.Var ?? args.CommandletName, VariableKind.Bool,
                                                         [IsAffirmative(fallback) ? "true" : "false"], []),
                               "choose" or "filter" => BuildDefaultSelection(args, stdinItems, fallback),
                               _ => new Variable(args.Var ?? args.CommandletName, VariableKind.Text, [fallback], []),
                           };

            if (args.Var is not null)
                BindVariable(vars, variable);
            if (emitFormat == EmitFormat.Lines && ReferenceEquals(rawStep, steps[^1]))
                Emit.WriteLines(Console.Out, variable); // the FINAL step only — the same shape a tty run emits
        }

        if (emitFormat == EmitFormat.Env) Emit.WriteEnv(Console.Out, vars);
        else if (emitFormat == EmitFormat.Json) Emit.WriteJson(Console.Out, vars);
        return ExitCodes.Accepted;
    }

    private static bool IsAffirmative(string value)
        => value is "y" or "Y" or "1" || (bool.TryParse(value, out var b) && b);

    private static Variable BuildDefaultSelection(StepArgs args, IReadOnlyList<string>? stdinItems, string fallback)
    {
        var items = args.Positionals.Count > 0 ? args.Positionals : stdinItems ?? [];
        var index = 0;
        for (; index < items.Count && !string.Equals(items[index], fallback, StringComparison.Ordinal); index++) { }
        return new Variable(args.Var ?? args.CommandletName, VariableKind.Selection,
                            [fallback], [index < items.Count ? index : 0]);
    }

    internal readonly record struct GlobalArgs(bool Debug, EmitFormat Format, bool NoCapsCache, RetainMode Retain);

    internal enum RetainMode
    {
        None,
        Final,
        All
    }

    private static void PrintHelp(TextWriter writer)
    {
        writer.WriteLine("curio — Cursorial commandlets for shell scripts");
        writer.WriteLine();
        writer.WriteLine("usage: curio <commandlet> [options] [--] [args]  [++ <commandlet> ...]");
        writer.WriteLine();
        writer.WriteLine("commandlets:");
        writer.WriteLine("  choose <item>...      pick an item (arrows + Enter); --prompt <text>");
        writer.WriteLine("  filter <item>...      fuzzy-pick an item (type to narrow, arrows + Enter); --prompt <text>,");
        writer.WriteLine("                        --placeholder <text>");
        writer.WriteLine("  input                 line prompt; --prompt <text>, --value <initial>");
        writer.WriteLine("  write                 multiline prompt (Enter = newline, Ctrl+D accepts);");
        writer.WriteLine("                        --prompt <text>, --value <initial>, --placeholder <text>, --lines <n>");
        writer.WriteLine("  confirm [message]     yes/no ([y]/Enter accepts, [n]/Esc declines)");
        writer.WriteLine();
        writer.WriteLine("pipeline: steps separated by ++ run in ONE process against one terminal session.");
        writer.WriteLine("  --var NAME            capture a step's result; later steps interpolate {NAME} / {NAME.index}");
        writer.WriteLine("  --optional            a canceled step unbinds its variable and the pipeline continues");
        writer.WriteLine("  --sep TOK             use TOK instead of ++ (first argument only)");
        writer.WriteLine("  --emit lines|env|json wire format (default lines; all emit after the final step, on success; CURIO_EMIT)");
        writer.WriteLine("  --retain none|all|final keep inline receipts on screen: no step (default), every accepted");
        writer.WriteLine("                        step, or only the final one (CURIO_RETAIN)");
        writer.WriteLine("  --no-caps-cache       skip the terminal capability cache for this run (CURIO_NO_CAPS_CACHE)");
        writer.WriteLine("  --default VALUE       non-interactive fallback (no tty): the step resolves to VALUE");
        writer.WriteLine();
        writer.WriteLine("stdin: pipe items to choose/filter (git branch | curio filter); keys always read from the tty");
        writer.WriteLine();
        writer.WriteLine("env emit: a child process cannot set your shell's variables — eval the output:");
        writer.WriteLine("  eval \"$(curio --emit env choose --var branch main develop)\"    # then: $BRANCH, $BRANCH_INDEX");
        writer.WriteLine();
        writer.WriteLine("exit codes: 0 accepted · 1 canceled/declined · 2 usage · 130 Ctrl+C");
    }
}
