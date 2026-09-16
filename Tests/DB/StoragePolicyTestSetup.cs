using System.Runtime.CompilerServices;
using VibeRails.Data.Sqlite;

namespace Tests.DB;

/// <summary>
/// Ordinary fixtures adopt legacy schemas on temp files and must not need `vb --migrate`, a live
/// process probe, or a backup. Guard tests re-enable each rule inside a SchemaUpgradePolicy.Scope.
/// </summary>
internal static class StoragePolicyTestSetup
{
    [ModuleInitializer]
    internal static void Initialize() => SchemaUpgradePolicy.ConfigureForTests();
}
