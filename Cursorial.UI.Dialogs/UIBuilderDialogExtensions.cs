using Cursorial.UI.Dialogs;

// ReSharper disable once CheckNamespace
namespace Cursorial.UI;

public static class UIBuilderDialogExtensions
{
    extension(UIApplicationBuilder builder)
    {
        public UIApplicationBuilder WithDialogServices()
        {
            return builder.WithService<IFileDialogService>(app => new FileDialogService(app))
                          .WithService<ITaskDialogService>(app => new TaskDialogService(app));
        }
    }
}