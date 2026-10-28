using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using VibeRails.Services.BertV2;
using Xunit;

namespace Tests.Services.BertV2;

public sealed class SqliteVec0PathResolverTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "sqlite vec 路径 " + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Windows", Architecture.X64, "vec0.dll")]
    [InlineData("OSX", Architecture.Arm64, "vec0.dylib")]
    [InlineData("OSX", Architecture.X64, "vec0.dylib")]
    [InlineData("Linux", Architecture.X64, "vec0.so")]
    [InlineData("Linux", Architecture.Arm64, "vec0.so")]
    public void PublishedLayout_ResolvesLibraryBesideExecutable(string platform, Architecture architecture, string filename)
    {
        var library = CreateFile(filename);

        Assert.Equal(library, SqliteVec0PathResolver.GetPath(directory, OSPlatform.Create(platform), architecture));
    }

    [Theory]
    [InlineData("Windows", Architecture.X64, "win-x64", "vec0.dll")]
    [InlineData("OSX", Architecture.Arm64, "osx-arm64", "vec0.dylib")]
    [InlineData("OSX", Architecture.X64, "osx-x64", "vec0.dylib")]
    [InlineData("Linux", Architecture.X64, "linux-x64", "vec0.so")]
    [InlineData("Linux", Architecture.Arm64, "linux-arm64", "vec0.so")]
    public void DevelopmentLayout_ResolvesLibraryForProcessArchitecture(string platform, Architecture architecture, string rid, string filename)
    {
        var library = CreateFile("runtimes", rid, "native", filename);

        Assert.Equal(library, SqliteVec0PathResolver.GetPath(directory, OSPlatform.Create(platform), architecture));
    }

    [Fact]
    public void PublishedLibrary_TakesPrecedenceOverDevelopmentLayout()
    {
        var library = CreateFile("vec0.dylib");
        CreateFile("runtimes", "osx-arm64", "native", "vec0.dylib");

        Assert.Equal(library, SqliteVec0PathResolver.GetPath(directory, OSPlatform.OSX, Architecture.Arm64));
    }

    [Fact]
    public void UnrelatedPlatformAndArchitecture_DoNotMatch()
    {
        CreateFile("vec0.dll");
        CreateFile("runtimes", "osx-arm64", "native", "vec0.dylib");

        Assert.Equal("vec0", SqliteVec0PathResolver.GetPath(directory, OSPlatform.OSX, Architecture.X64));
    }

    [Fact]
    public void MissingLibrary_PreservesSystemLookupFallback()
    {
        Assert.Equal("vec0", SqliteVec0PathResolver.GetPath(directory, OSPlatform.Linux, Architecture.X64));
    }

    [Fact]
    public void PublishedLibrary_LoadsAndCreatesVectorTable()
    {
        var platform = OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;
        var extension = OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";
        Directory.CreateDirectory(directory);
        var library = Path.Combine(directory, "vec0" + extension);
        File.Copy(SqliteVec0PathResolver.GetPath(), library);

        using var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
        connection.Open();
        connection.EnableExtensions(true);
        connection.LoadExtension(SqliteVec0PathResolver.GetPath(directory, platform, RuntimeInformation.ProcessArchitecture));
        connection.EnableExtensions(false);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE VIRTUAL TABLE vectors USING vec0(embedding FLOAT[3]);";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO vectors(rowid, embedding) VALUES(1, '[1,2,3]');";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT vec_distance_L2(embedding, '[1,2,3]') FROM vectors WHERE rowid=1;";
        Assert.Equal(0d, Convert.ToDouble(command.ExecuteScalar()));
    }

    private string CreateFile(params string[] parts)
    {
        var path = Path.Combine([directory, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
