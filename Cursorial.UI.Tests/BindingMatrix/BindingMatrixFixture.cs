using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Cursorial.UI;

// ReSharper disable MemberCanBePrivate.Global

namespace Cursorial.Tests.UI.BindingMatrix;

/// <summary>
/// The §0.1 binding-matrix fixture. Static initialization makes the <c>BindWidget</c> property
/// registrations idempotent across test classes (dense property ids are process-global).
/// </summary>
public static class BindingMatrixFixture
{
    static BindingMatrixFixture()
    {
        // Forces the static constructor; touching any registered property pins it.
        _ = BindWidget.TextProperty;
    }

    /// <summary>Forces fixture registration (idempotent).</summary>
    public static void Ensure()
    {
    }
}

/// <summary>The canonical INPC viewmodel (§0.1).</summary>
public sealed class Vm : INotifyPropertyChanged
{
    private string? _name;
    private int _age;
    private bool _isDirty;
    private Vm? _sub;
    private Addr? _address;
    private PlainHolder? _holder;
    private int _readOnlyAge;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public int Age
    {
        get => _age;
        set => Set(ref _age, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        set => Set(ref _isDirty, value);
    }

    public Vm? Sub
    {
        get => _sub;
        set => Set(ref _sub, value);
    }

    public Addr? Address
    {
        get => _address;
        set => Set(ref _address, value);
    }

    /// <summary>An INPC-notified intermediate holding a non-INPC leaf — the rung-3 mid-chain row (B32).</summary>
    public PlainHolder? Holder
    {
        get => _holder;
        set => Set(ref _holder, value);
    }

    /// <summary>A property with a getter only (no setter) — the read-only-leaf row (B80).</summary>
    public int ReadOnlyAge => _readOnlyAge;

    public ObservableCollection<string> Tags { get; } = [];

    public Dictionary<string, object?> Map { get; } = [];

    public void SetReadOnlyAge(int value) => _readOnlyAge = value;

    /// <summary>Raises <c>PropertyChanged(null)</c>.</summary>
    public void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A holder exposing a non-INPC leaf (<c>Note</c>) for the parent-change-degradation chain (B32).</summary>
public sealed class PlainHolder
{
    public string? Note { get; set; }
}

/// <summary>
/// An INPC viewmodel whose <c>Leaf</c> intermediate is typed <see cref="object"/> so a swap can change
/// the leaf's declaring type (B81 — writable-vs-read-only-leaf re-evaluation on rewire). <c>Value</c> is
/// reached as <c>"Leaf.Value"</c>.
/// </summary>
public sealed class LeafSwapVm : INotifyPropertyChanged
{
    private object? _leaf;

    public event PropertyChangedEventHandler? PropertyChanged;

    public object? Leaf
    {
        get => _leaf;
        set
        {
            _leaf = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Leaf)));
        }
    }
}

/// <summary>A leaf with a read-only <c>Value</c> (no setter) — the degrade side of B81.</summary>
public sealed class ReadOnlyLeaf(int value)
{
    public int Value => value;
}

/// <summary>A leaf with a writable <c>Value</c> — the re-enable side of B81.</summary>
public sealed class WritableLeaf
{
    public int Value { get; set; }
}

/// <summary>The INPC sub-object (§0.1).</summary>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class Addr : INotifyPropertyChanged
{
    private string? _city;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? City
    {
        get => _city;
        set
        {
            if (_city == value)
                return;
            _city = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(City)));
        }
    }
}

/// <summary>
/// A CLR object with NO <c>INotifyPropertyChanged</c> (§0.1): the source-ladder rungs 2 and 3.
/// </summary>
public sealed class PlainVm
{
    private string? _title;
    private int _count;

    /// <summary>Rung 2: a convention-matched <c>[PropertyName]Changed</c> event (<c>EventHandler</c>).</summary>
    public event EventHandler? TitleChanged;

    /// <summary>Rung 2: a convention-matched <c>[PropertyName]Changed</c> event (<c>EventHandler&lt;EventArgs&gt;</c>).</summary>
    public event EventHandler<EventArgs>? CountChanged;

    public string? Title
    {
        get => _title;
        set
        {
            _title = value;
            TitleChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int Count
    {
        get => _count;
        set
        {
            _count = value;
            CountChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Rung 3: neither INPC nor <c>[Name]Changed</c> (the parent-change-degradation hop).</summary>
    public string? Note { get; set; }

    public void RaiseTitleChanged() => TitleChanged?.Invoke(this, EventArgs.Empty);

    public void RaiseCountChanged() => CountChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// A source that implements BOTH <c>INotifyPropertyChanged</c> and a <c>NameChanged</c> event for
/// the same property (B33 — INPC wins).
/// </summary>
public sealed class DualVm : INotifyPropertyChanged
{
    private string? _name;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? NameChanged;

    public string? Name
    {
        get => _name;
        set
        {
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public void RaiseNameChanged() => NameChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>The <c>BindWidget : UIElement</c> test target (§0.1).</summary>
public sealed class BindWidget : UIElement
{
    private int _dir;

    /// <summary><c>Text</c> = <c>StyledProperty&lt;string?&gt;</c> default null.</summary>
    public static readonly StyledProperty<string?> TextProperty =
        UIProperty.Register<BindWidget, string?>(nameof(Text));

    /// <summary><c>Num</c> = <c>StyledProperty&lt;int&gt;</c> default 0.</summary>
    public static readonly StyledProperty<int> NumProperty =
        UIProperty.Register<BindWidget, int>(nameof(Num));

    /// <summary><c>Flag</c> = <c>StyledProperty&lt;bool&gt;</c>, <c>BindsTwoWayByDefault</c>.</summary>
    public static readonly StyledProperty<bool> FlagProperty =
        UIProperty.Register<BindWidget, bool>(nameof(Flag), metadata: new PropertyMetadata<bool>(false));

    /// <summary><c>Off</c> = <c>StyledProperty&lt;int&gt;</c> <c>AffectsComposite</c> (the compiled-lane hot property).</summary>
    public static readonly StyledProperty<int> OffProperty =
        UIProperty.Register<BindWidget, int>(nameof(Off));

    /// <summary><c>NDB</c> = <c>StyledProperty&lt;int&gt;</c> metadata <c>NotDataBindable</c>.</summary>
    public static readonly StyledProperty<int> NdbProperty =
        UIProperty.Register<BindWidget, int>("Ndb");

    /// <summary><c>Dir</c> = <c>DirectProperty&lt;BindWidget,int&gt;</c>.</summary>
    public static readonly DirectProperty<BindWidget, int> DirProperty =
        UIProperty.RegisterDirect<BindWidget, int>("Dir", static w => w._dir, static (w, v) => w._dir = v);

    private static readonly UIPropertyKey<int> RoKey =
        UIProperty.RegisterReadOnly<BindWidget, int>("Ro");

    /// <summary><c>Ro</c> = read-only <c>StyledProperty&lt;int&gt;</c>.</summary>
    public static readonly StyledProperty<int> RoProperty = RoKey.Property;

    /// <summary><c>Cmp</c> = <c>StyledProperty&lt;string?&gt;</c> with metadata <c>Comparer = OrdinalIgnoreCase</c> (matrix §0.1).</summary>
    public static readonly StyledProperty<string?> CmpProperty =
        UIProperty.Register<BindWidget, string?>(
            nameof(Cmp), metadata: new PropertyMetadata<string?>(Comparer: StringComparer.OrdinalIgnoreCase));

    static BindWidget()
    {
        AffectsComposite<BindWidget>(OffProperty);
        BindsTwoWayByDefault<BindWidget>(FlagProperty);
        NotDataBindable<BindWidget>(NdbProperty);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public int Num
    {
        get => GetValue(NumProperty);
        set => SetValue(NumProperty, value);
    }

    public bool Flag
    {
        get => GetValue(FlagProperty);
        set => SetValue(FlagProperty, value);
    }

    public int Off
    {
        get => GetValue(OffProperty);
        set => SetValue(OffProperty, value);
    }

    public int Dir
    {
        get => _dir;
        set => SetAndRaise(DirProperty, ref _dir, value);
    }

    public int Ro => GetValue(RoProperty);

    public void SetRo(int value) => SetValue(RoKey, value);

    public string? Cmp
    {
        get => GetValue(CmpProperty);
        set => SetValue(CmpProperty, value);
    }

    /// <summary>Adds a logical child for tree-shaped rows.</summary>
    public void AddChild(UIElement child) => AddLogicalChild(child);

    /// <summary>Stamps the templated parent (template-part rows).</summary>
    public void StampTemplatedParent(UIElement? parent) => SetTemplatedParent(parent);
}
