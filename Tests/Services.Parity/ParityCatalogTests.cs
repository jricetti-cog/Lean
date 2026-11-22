using NUnit.Framework;

namespace QuantConnect.Tests.Services.Parity;

/// <summary>Stack-free checks of the regression set the harness runs (these run in CI without a stack).</summary>
[TestFixture, Category("ServicesParity")]
public class ParityCatalogTests
{
    [Test]
    public void DiscoversBothLanguagesAndExplainsEverySkip()
    {
        var all = ParityCatalog.All;
        TestContext.Out.WriteLine($"{all.Count} cases: " + string.Join(", ", all.GroupBy(c => (c.Language, c.SkipReason == null))
            .Select(g => $"{g.Key.Language} {(g.Key.Item2 ? "runnable" : "skipped")} {g.Count()}")));
        if (ParitySettings.Only.Count == 0)
        {
            Assert.That(all.Where(c => c.SkipReason == null).Select(c => c.Language).Distinct(), Is.EquivalentTo(ParitySettings.Languages));
        }
        Assert.That(all.Where(c => c.SkipReason != null), Has.All.Matches<ParityCase>(c => c.SkipReason!.Length > 10));
        Assert.That(all.Select(c => c.Name), Is.Unique);
    }

    [Test]
    public void PythonPackageCarriesImportedSiblingModules()
    {
        var zip = PythonPackage.Build("CustomDataRegressionAlgorithm", out var modules);
        Assert.That(zip, Is.Not.Empty);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        Assert.That(archive.Entries.Select(e => e.FullName), Does.Contain("main.py"));
        Assert.That(archive.Entries.Count, Is.EqualTo(modules.Count + 1));
    }
}
