namespace Cursorial.Text;

public static class TextExtensions
{
    extension(string target)
    {
        public GraphemeEnumerator GetGraphemeEnumerator() => new(target, 0);
    }
}