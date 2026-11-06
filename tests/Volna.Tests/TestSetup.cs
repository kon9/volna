using System.Runtime.CompilerServices;
using Volna.Core;
using Xunit;

// The data folder is one setting for the whole process, and a TestRoom replaces it for its test. Run in parallel, one
// test would find the folder of another, deleted under it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Volna.Tests;

static class TestSetup
{
    /// <summary>Where tests without a TestRoom keep data, so no test ever touches the real data folder.</summary>
    public static readonly string Data = Path.Combine(Path.GetTempPath(), "volna-tests-" + Guid.NewGuid().ToString("N"));

#pragma warning disable CA2255 // a test assembly is the place this is meant for
    [ModuleInitializer]
    internal static void Start()
    {
        AppPaths.Override = Data;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Data, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }
#pragma warning restore CA2255
}
