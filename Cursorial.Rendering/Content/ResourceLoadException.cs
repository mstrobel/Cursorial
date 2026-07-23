using Cursorial.Markup;

namespace Cursorial.Rendering.Content;

/// <summary>
/// The exception that is thrown when a resource could not be loaded.
/// </summary>
public class ResourceLoadException : InvalidOperationException
{
    public ResourceLoadException(Uri resourceUri, Exception? innerException = null) 
        : base(BuildMessage(resourceUri), innerException) {}

    public ResourceLoadException(string message, Exception? innerException = null)
        : base(message, innerException) {}

    private static string BuildMessage(Uri uri)
    {
        if (CursorialUri.IsCursorialUri(uri) &&
            CursorialUri.GetOriginalAuthority(uri) is {} assemblyName &&
            uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped) is {} path)
        {
            return $"Resource '{path}' is missing from the assembly. " +
                   "This may indicate a packaging error in " + assemblyName + ".";
        }

        return $"Resource could not be loaded: <{uri.OriginalString}>";
    }
}