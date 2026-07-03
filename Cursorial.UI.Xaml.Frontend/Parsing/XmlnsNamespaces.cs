using System;

// ReSharper disable CheckNamespace

namespace Cursorial.UI.Xaml;

/// <summary>
/// The well-known XAML xmlns URIs and the helpers that classify a namespace string. The default
/// Cursorial namespace maps (via <c>[assembly: XmlnsDefinition]</c> declarations discovered by the
/// metadata providers) to <c>Cursorial.UI</c> + <c>Cursorial.UI.Controls</c> + <c>Cursorial.UI.Data</c>
/// + … (matrix ③); the intrinsics namespace carries the <c>x:</c> directives.
/// </summary>
internal static class XmlnsNamespaces
{
    /// <summary>The default Cursorial UI namespace URI.</summary>
    public const string CursorialUi = "https://cursorial.dev/ui";

    /// <summary>The intrinsics (<c>x:</c>) namespace URI.</summary>
    public const string Intrinsics = "https://cursorial.dev/xaml";

    /// <summary>The <c>using:</c> scheme prefix (Avalonia-style direct CLR-namespace mapping).</summary>
    public const string UsingPrefix = "using:";

    /// <summary>The <c>clr-namespace:</c> scheme prefix (WPF-style direct CLR-namespace mapping).</summary>
    public const string ClrNamespacePrefix = "clr-namespace:";

    /// <summary>True when the namespace is the intrinsics namespace.</summary>
    public static bool IsIntrinsics(string ns) => string.Equals(ns, Intrinsics, StringComparison.Ordinal);

    /// <summary>
    /// Decodes a <c>using:Ns</c> or <c>clr-namespace:Ns;assembly=Asm</c> URI into its CLR namespace
    /// and optional assembly. Returns false when <paramref name="ns"/> is neither form.
    /// </summary>
    public static bool TryDecodeClrNamespace(string ns, out string clrNamespace, out string? assemblyName)
    {
        clrNamespace = string.Empty;
        assemblyName = null;

        if (ns.StartsWith(UsingPrefix, StringComparison.Ordinal))
        {
            clrNamespace = ns.Substring(UsingPrefix.Length).Trim();
            return clrNamespace.Length > 0;
        }

        if (ns.StartsWith(ClrNamespacePrefix, StringComparison.Ordinal))
        {
            var body = ns.Substring(ClrNamespacePrefix.Length);
            int semi = body.IndexOf(';');
            if (semi < 0)
            {
                clrNamespace = body.Trim();
            }
            else
            {
                clrNamespace = body.Substring(0, semi).Trim();
                // parse the "assembly=Asm" tail (the only recognized parameter)
                var tail = body.Substring(semi + 1);
                foreach (var part in tail.Split(';'))
                {
                    var kv = part.Split(new[] { '=' }, 2);
                    if (kv.Length == 2 && string.Equals(kv[0].Trim(), "assembly", StringComparison.OrdinalIgnoreCase))
                        assemblyName = kv[1].Trim();
                }
            }
            return clrNamespace.Length > 0;
        }

        return false;
    }
}
