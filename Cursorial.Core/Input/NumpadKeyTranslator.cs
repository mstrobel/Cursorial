using System.Runtime.CompilerServices;

using Cursorial.Input.Events;

namespace Cursorial.Input;

public sealed class NumpadKeyTranslator : IInputTransformer
{
    public async IAsyncEnumerable<InputEvent> TransformAsync(
        IAsyncEnumerable<InputEvent> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var textMap = new Dictionary<Key, ReadOnlyMemory<char>>();
        
        await foreach (var evt in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (evt is not KeyEvent k)
            {
                yield return evt;
                continue;
            }

            if (k.Key is Key.NumpadEnter)
            {
                yield return k with { Key = Key.Enter };
                continue;
            }

            switch (k.Kind)
            {
                case KeyEventKind.Down:
                {
                    if (k is { Text.Length: > 0, Key: >= Key.Numpad0 and <= Key.NumpadEquals })
                    {
                        textMap.TryAdd(k.Key, k.Text);
                        k = k with { Key = Key.Character };
                    }
                    break;
                }

                case KeyEventKind.Up:
                {
                    if (k is { Key: >= Key.Numpad0 and <= Key.NumpadEquals })
                    {
                        if (k.Text is { Length: > 0 })
                            k = k with { Key = Key.Character };
                        else if (textMap.TryGetValue(k.Key, out ReadOnlyMemory<char> text))
                            k = k with { Key = Key.Character, Text = text };
                    }
                    break;
                }
            }
            
            yield return k;
        }
    }
}