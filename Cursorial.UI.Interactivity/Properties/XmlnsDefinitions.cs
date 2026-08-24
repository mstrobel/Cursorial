using Cursorial.Markup;

// The CLR namespace this assembly exposes under the default Cursorial UI xmlns (https://cursorial.dev/ui),
// discovered by both XAML metadata providers. The interactivity design doc claims default-map contribution
// (interactivity-design.md §xmlns) — the designer-completion review (2026-08-24) found the assembly never
// delivered it, leaving <Interaction.Behaviors>/<EventTrigger> reachable only via clr-namespace:.
[assembly: XmlnsDefinition("https://cursorial.dev/ui", "Cursorial.UI.Interactivity")]
