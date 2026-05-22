using Cursorial.Rendering;

using Xunit.Sdk;

namespace Cursorial.Tests.Rendering;

public static class Extensions
{
    extension(Assert)
    {
        public static void NotEmpty(FragmentDictionary dictionary)
        {
            if (dictionary == default)
                throw new ArgumentNullException(nameof(dictionary));

            if (dictionary.Count is 0)
                throw NotEmptyException.ForNonEmptyCollection();
        }

        public static void Empty(FragmentDictionary dictionary)
        {
            if (dictionary == default)
                throw new ArgumentNullException(nameof(dictionary));

            if (dictionary.Count is not 0)
                throw EmptyException.ForNonEmptyCollection(nameof(FragmentDictionary));
        }

        public static void Single(FragmentDictionary dictionary, string? expected = null)
        {
            if (dictionary == default)
                throw new ArgumentNullException(nameof(dictionary));

            var count = dictionary.Count;

            if (count is 0) throw SingleException.Empty(expected, nameof(FragmentDictionary));
            if (count > 1) throw SingleException.MoreThanOne(count, expected, nameof(FragmentDictionary), ArraySegment<int>.Empty);
        }
    }
}