namespace Cursorial.UI.Themes;

public sealed record ThemeClass
{
    private ThemeClass(string className)
    {
        ArgumentNullException.ThrowIfNull(className);
        ClassName = className;
    }

    /// <summary>
    /// The <c>.accent</c> class indicates an element should be shown in an accent color.
    /// Accent colors are intended for <em>primary actions</em>.
    /// </summary>
    public static readonly ThemeClass Accent = new("accent");

    /// <summary>
    /// The <c>.cool</c> class indicates an element should be shown in a cool color.
    /// Cool colors are intended for <em>navigation</em>.
    /// </summary>
    public static readonly ThemeClass Cool = new("cool");

    /// <summary>
    /// The <c>.info</c> class indicates an element is presenting informational content.
    /// </summary>
    public static readonly ThemeClass Info = new("info");

    /// <summary>
    /// The <c>.success</c> class indicates an element is indicating a successful action
    /// or an "OK" state.
    /// </summary>
    public static readonly ThemeClass Success = new("success");
    
    /// <summary>
    /// The <c>.warning</c> class indicates an element is indicating a warningful action
    /// or an "OK" state.
    /// </summary>
    public static readonly ThemeClass Warning = new("warning");
    
    /// <summary>
    /// The <c>.danger</c> class indicates an element should be shown in a danger color.
    /// Danger colors are intended for <em>potentially destructive</em> actions.
    /// </summary>
    public static readonly ThemeClass Danger = new("danger");

    public string ClassName { get; init; }

    public void Deconstruct(out string className)
    {
        className = this.ClassName;
    }
}