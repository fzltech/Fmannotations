using System.IO.Compression;
using Xunit;

namespace Fmannotations.Tests;

public class PackageDistributionTests
{
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "fmverify.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"No fmverify.slnx above {AppContext.BaseDirectory}.");
    }

    private static string PackageVersion()
    {
        var repoRoot = FindRepoRoot();
        return File.ReadAllText(Path.Combine(repoRoot, "VERSION")).Trim();
    }

    [Fact]
    public void Annotations_project_packs_for_expected_target_frameworks()
    {
        var repoRoot = FindRepoRoot();
        var projectPath = Path.Combine(repoRoot, "src", "Fmverify.Annotations", "Fmverify.Annotations.csproj");
        var outputDir = Path.Combine(Path.GetTempPath(), "fmverify-pack-test-" + Guid.NewGuid().ToString("N"));

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"pack \"{projectPath}\" -c Release -o \"{outputDir}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            var proc = System.Diagnostics.Process.Start(psi)!;
            proc.WaitForExit();
            Assert.Equal(0, proc.ExitCode);

            var version = PackageVersion();
            var nupkg = Path.Combine(outputDir, $"Fmverify.Annotations.{version}.nupkg");
            Assert.True(File.Exists(nupkg), "Expected package was not produced");

            using var zip = ZipFile.OpenRead(nupkg);
            var libEntries = zip.Entries.Where(e => e.FullName.StartsWith("lib/")).Select(e => e.FullName).ToList();

            Assert.Contains(libEntries, e => e.Contains("netstandard2.0/Fmverify.Annotations.dll"));
            Assert.Contains(libEntries, e => e.Contains("net8.0/Fmverify.Annotations.dll"));
            Assert.Contains(libEntries, e => e.Contains("net9.0/Fmverify.Annotations.dll"));
            Assert.Contains(libEntries, e => e.Contains("net10.0/Fmverify.Annotations.dll"));
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        }
    }
}
