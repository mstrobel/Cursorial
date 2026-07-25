namespace Cursorial.UI.DataViews;

/// <summary>
/// Raised by <see cref="DataGrid.AutoGeneratingColumn"/> once per property while auto-generating
/// columns (WPF parity). A handler may customize the proposed <see cref="Column"/> in place — set
/// its format, alignment, header, add <c>FormatRules</c>, wire a <c>Validator</c> — or replace it
/// entirely, or set <see cref="Cancel"/> to skip the property. Fires AFTER the built-in metadata
/// (DataAnnotations + per-type default format) is applied, so a handler refines rather than rebuilds.
/// </summary>
public sealed class DataGridAutoGeneratingColumnEventArgs : EventArgs
{
    internal DataGridAutoGeneratingColumnEventArgs(string propertyName, Type propertyType, DataGridColumn column)
    {
        PropertyName = propertyName;
        PropertyType = propertyType;
        Column = column;
    }

    /// <summary>The source property's name.</summary>
    public string PropertyName { get; }

    /// <summary>The source property's type (the declared type, incl. <see cref="Nullable{T}"/>).</summary>
    public Type PropertyType { get; }

    /// <summary>The proposed column — mutate it, or assign a replacement. Never null on the way in.</summary>
    public DataGridColumn Column { get; set; }

    /// <summary>Set true to skip this property (generate no column for it).</summary>
    public bool Cancel { get; set; }
}
