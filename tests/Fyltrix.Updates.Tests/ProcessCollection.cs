namespace Fyltrix.Updates.Tests;

// These tests enumerate process names system-wide. A different test's exiting Fyltrix.exe
// probe must not race an apply guard, even though their installation directories differ.
[CollectionDefinition("Updater processes", DisableParallelization = true)]
public sealed class ProcessCollection { }
