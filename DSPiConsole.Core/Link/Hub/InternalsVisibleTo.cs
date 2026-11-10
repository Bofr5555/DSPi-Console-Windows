using System.Runtime.CompilerServices;

// The hub's schedulers expose internal Tick() methods so the tests can drive
// them deterministically against a fake clock instead of sleeping.
[assembly: InternalsVisibleTo("DSPiConsole.Core.Tests")]
