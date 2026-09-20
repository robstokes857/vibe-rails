using System.Runtime.CompilerServices;
using VibeRails.Data.Sqlite;

namespace Tests.DB;

/// <summary>
/// Ordinary temporary fixtures skip backup I/O; backup tests enable it in SchemaUpgradePolicy.Scope.
/// Tests and production use the same automatic migration behavior, without an opt-in override.
/// </summary>
internal static class StoragePolicyTestSetup
{
    [ModuleInitializer]
    internal static void Initialize() => SchemaUpgradePolicy.ConfigureForTests();
}
