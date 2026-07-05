using Cursorial.Rendering;
using Cursorial.UI.Controls;
using Cursorial.UI.Data;

namespace Cursorial.UI.Dialogs;

internal class CommandLink : Button
{
    private TaskDialogButton? _boundDefinition;

    public static readonly StyledProperty<TaskDialogButton?> ButtonDefinitionProperty =
        UIProperty.Register<CommandLink, TaskDialogButton?>(nameof(ButtonDefinition),
                                                            changed: OnButtonDefinitionChanged);

    public static readonly StyledProperty<object?> ExplanationProperty =
        UIProperty.Register<CommandLink, object?>(nameof(Explanation));

    public static readonly StyledProperty<bool> ExplanationContainsMarkupProperty =
        UIProperty.Register<CommandLink, bool>(nameof(ExplanationContainsMarkup));

    static CommandLink()
    {
        PaddingProperty.OverrideDefaultValue<CommandLink>(new Margins(1, 0));
    }

    public TaskDialogButton? ButtonDefinition
    {
        get => GetValue(ButtonDefinitionProperty);
        set => SetValue(ButtonDefinitionProperty, value);
    }

    public object? Explanation => GetValue(ExplanationProperty);

    public bool ExplanationContainsMarkup => GetValue(ExplanationContainsMarkupProperty);

    /// <inheritdoc/>
    protected override void OnAttachedToTree(in TreeAttachmentEventArgs e)
    {
        base.OnAttachedToTree(in e);
        UpdateButtonDefinitionBindings();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromTree(in TreeAttachmentEventArgs e)
    {
        RemoveButtonDefinitionBindings();
        base.OnDetachedFromTree(in e);
    }

    protected void UpdateButtonDefinitionBindings()
    {
        var newDefinition = ButtonDefinition;

        if (_boundDefinition is {} oldDefinition)
        {
            if (Equals(oldDefinition, newDefinition))
                return;

            RemoveButtonDefinitionBindings();
        }

        if (newDefinition is null)
            return;

        _boundDefinition = newDefinition;

        BindingOperations.SetBinding(this,
                                     ContentProperty,
                                     new Binding(nameof(ButtonDefinition.Label))
                                     {
                                         Source = newDefinition
                                     });

        BindingOperations.SetBinding(this,
                                     ExplanationProperty,
                                     new Binding(nameof(ButtonDefinition.Explanation))
                                     {
                                         Source = newDefinition
                                     });

        BindingOperations.SetBinding(this,
                                     ExplanationContainsMarkupProperty,
                                     new Binding(nameof(ButtonDefinition.ExplanationContainsMarkup))
                                     {
                                         Source = newDefinition
                                     });
    }

    protected void RemoveButtonDefinitionBindings()
    {
        BindingOperations.ClearBinding(this, ContentProperty);
        BindingOperations.ClearBinding(this, ExplanationProperty);
        BindingOperations.ClearBinding(this, ExplanationContainsMarkupProperty);
    }


    private static void OnButtonDefinitionChanged(UIObject sender,
                                                  TaskDialogButton? oldValue,
                                                  TaskDialogButton? newValue)
    {
        if (sender is CommandLink commandLink)
            commandLink.UpdateButtonDefinitionBindings();
    }
}