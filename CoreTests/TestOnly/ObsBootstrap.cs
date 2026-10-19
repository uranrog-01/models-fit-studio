using System.Runtime.CompilerServices;

namespace CoreTests.TestOnly;

public static class ObsBootstrap
{
    /// <summary>
    /// Reset once at assembly load so the observed.log reflects the whole run no
    /// matter which xUnit collection happens to execute first.
    /// </summary>
    [ModuleInitializer]
    public static void Init() => Obs.Reset();
}
