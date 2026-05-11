using GlowTerm.Core.Input;
using GlowTerm.Core.Input.Parsing;

namespace GlowTerm.Core.Tests.Input.Parsing;

/// <summary>Test sink that captures every event the interpreter emits.</summary>
internal sealed class RecordingInputEventSink : IInputEventSink
{
    public List<InputEvent> Events { get; } = [];

    public void OnInputEvent(InputEvent inputEvent) => Events.Add(inputEvent);

    public T Single<T>() where T : InputEvent => Assert.IsType<T>(Assert.Single(Events));

    public T At<T>(int index) where T : InputEvent => Assert.IsType<T>(Events[index]);
}
