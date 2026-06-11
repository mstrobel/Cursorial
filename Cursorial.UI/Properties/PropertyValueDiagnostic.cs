namespace Cursorial.UI;

/// <summary>
/// One row of the per-property value-stack enumeration
/// (<see cref="UIObject.GetValueDiagnostics"/>) — the serialization / DevTools surface the design
/// doc pairs with <c>GetValueSource</c> ("frame/local enumeration for tooling", §2.1; matrix M264).
/// Rows are listed strongest-first: the animation contribution, the raw local value, every frame
/// entry (sort-keyed, active or not), and the inherited provenance.
/// </summary>
/// <param name="Priority">The lane this contribution lives in (<see cref="BindingPriority.Animation"/>,
/// <see cref="BindingPriority.LocalValue"/>, <see cref="BindingPriority.Style"/>, or
/// <see cref="BindingPriority.Inherited"/>).</param>
/// <param name="Value">The boxed contribution: the animated effective for the Animation row, the
/// <em>raw</em> (pre-coercion) value for the local row, the entry's raw value for style rows
/// (<see langword="null"/> when valueless), and the contributing ancestor's effective value for the
/// inherited row.</param>
/// <param name="HasValue">Whether the contribution currently carries a value (a valueless frame
/// entry is listed with <see langword="false"/> — ledger A8).</param>
/// <param name="SortKey">The hosting frame's <see cref="StyleSortKey"/> for style rows;
/// <c>default</c> otherwise.</param>
/// <param name="IsActive">Whether the hosting frame is active (style rows; inactive frames are
/// listed but contribute nothing). Always <see langword="true"/> for non-style rows.</param>
/// <param name="InheritedFrom">The contributing ancestor for the inherited row; <see langword="null"/>
/// otherwise.</param>
public readonly record struct PropertyValueDiagnostic(
    BindingPriority Priority,
    object? Value,
    bool HasValue,
    StyleSortKey SortKey = default,
    bool IsActive = true,
    UIObject? InheritedFrom = null);
