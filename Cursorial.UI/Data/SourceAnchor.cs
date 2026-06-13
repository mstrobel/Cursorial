namespace Cursorial.UI.Data;

/// <summary>How an expression's root source is anchored (design doc §6.4).</summary>
internal enum AnchorKind : byte
{
    /// <summary>The target's inherited <c>DataContext</c> (default; rebind on change — BD3).</summary>
    DataContext,

    /// <summary>The logical parent's <c>DataContext</c> — the DataContext-as-target special case (BD2).</summary>
    ParentDataContext,

    /// <summary>An explicit fixed <see cref="AnchoredBinding.Source"/> (never re-resolved — BD3).</summary>
    Source,

    /// <summary>A named element via <c>NameScope.FindEnclosing</c> (B1).</summary>
    ElementName,

    /// <summary>The target element itself (<c>RelativeSource.Self</c>).</summary>
    Self,

    /// <summary>The target's <c>TemplatedParent</c>.</summary>
    TemplatedParent,

    /// <summary>The nth assignable logical ancestor (<c>FindAncestor</c>).</summary>
    FindAncestor,
}
