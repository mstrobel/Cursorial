namespace Cursorial.Tests.Terminal.Stdio;

// WindowsConsoleInputByteSource now reads INPUT_RECORDs via ReadConsoleInputW, not raw bytes
// via ReadFile. ReadConsoleInputW only works against real console handles, so the previous
// pipe-handle-based integration tests can't be repurposed for it. The source's correctness on
// real consoles is verified by running the demo end-to-end on Windows; the framework-level
// integration tests in Cursorial.Core.Tests that exercise the Win32 Input Mode parsing path
// (VtInputInterpreterTests, etc.) cover the byte-stream side downstream of this source.
//
// If a future change wants to add unit-level tests, the right shape is to abstract the
// read-records primitive behind an interface that tests can mock with an in-memory record
// queue. Out of scope for the current "fix the keystroke-eating bug" change.
