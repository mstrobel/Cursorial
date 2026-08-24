using Cursorial.CLI;

try
{
    return await Runner.RunAsync(args);
}
finally
{
    // System.Console smkx repair for SESSION-LESS paths (help / version / usage errors): the
    // Unix runtime's first Console WRITE runs a one-time terminal init that emits terminfo smkx
    // (application cursor-key/keypad mode) and never restores it, which silently breaks
    // mode-gated terminal keymaps (kitty `send_text normal`) at the shell until `reset`.
    // Session-running paths are already repaired by the session's restore (rmkx pair); this
    // covers every run that printed through Console without ever opening a session. tty-gated
    // so piped output (`curio --json | jq`) never sees escape bytes; a doubled rmkx after a
    // session run is an idempotent no-op.
    if (!Console.IsOutputRedirected)
    {
        Console.Out.Write("\x1b[?1l\x1b>");
        Console.Out.Flush();
    }
}
