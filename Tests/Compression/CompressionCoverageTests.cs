/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace QuantConnect.Tests.Compression
{
    [TestFixture]
    public class CompressionCoverageTests
    {
        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Directory.CreateTempSubdirectory("compression-coverage").FullName;
        }

        [TearDown]
        public void TearDown()
        {
            Directory.Delete(_directory, true);
        }

        private string PathFor(string name) => Path.Combine(_directory, name);

        [Test]
        public void ZipDataDictionaryRoundTrip()
        {
            var zip = PathFor("dict.zip");
            Assert.IsTrue(QuantConnect.Compression.ZipData(zip, new Dictionary<string, string> { { "a.txt", "alpha" }, { "b.txt", "beta" } }));

            var data = QuantConnect.Compression.UnzipData(File.ReadAllBytes(zip));
            Assert.AreEqual("alpha", data["a.txt"]);
            Assert.AreEqual("beta", data["b.txt"]);
            CollectionAssert.AreEquivalent(new[] { "a.txt", "b.txt" }, QuantConnect.Compression.GetZipEntryFileNames(zip));
            Assert.IsTrue(QuantConnect.Compression.ValidateZip(zip));
        }

        [Test]
        public void ZipDataBytesAndLinesRoundTrip()
        {
            var bytesZip = PathFor("bytes.zip");
            Assert.IsTrue(QuantConnect.Compression.ZipData(bytesZip, new[] { new KeyValuePair<string, byte[]>("x.bin", new byte[] { 1, 2, 3 }) }));
            var entries = QuantConnect.Compression.UnzipToFolder(File.ReadAllBytes(bytesZip), PathFor("out"));
            Assert.AreEqual(1, entries.Count);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(entries[0]));

            var linesZip = PathFor("lines.zip");
            Assert.IsTrue(QuantConnect.Compression.ZipData(linesZip, "lines.csv", new[] { "1,2", "3,4" }));
            CollectionAssert.AreEqual(new[] { "1,2", "3,4" }, QuantConnect.Compression.ReadLines(linesZip));

            var unzipped = QuantConnect.Compression.Unzip(linesZip).Single();
            Assert.AreEqual("lines.csv", unzipped.Key);
            CollectionAssert.AreEqual(new[] { "1,2", "3,4" }, unzipped.Value);
        }

        [Test]
        public void ZipCreateAppendDataAddsAndOverridesEntries()
        {
            var zip = PathFor("append.zip");
            Assert.IsTrue(QuantConnect.Compression.ZipCreateAppendData(zip, "one.txt", "1"));
            Assert.IsTrue(QuantConnect.Compression.ZipCreateAppendData(zip, "two.txt", "2"));
            Assert.IsFalse(QuantConnect.Compression.ZipCreateAppendData(zip, "two.txt", "3"));
            Assert.IsTrue(QuantConnect.Compression.ZipCreateAppendData(zip, "two.txt", "3", overrideEntry: true));

            var data = QuantConnect.Compression.UnzipData(File.ReadAllBytes(zip));
            Assert.AreEqual("1", data["one.txt"]);
            Assert.AreEqual("3", data["two.txt"]);
        }

        [Test]
        public void ZipBytesAndAsyncHelpers()
        {
            var zipped = QuantConnect.Compression.ZipBytes(Encoding.UTF8.GetBytes("payload"), "entry.txt");
            Assert.AreEqual("payload", QuantConnect.Compression.UnzipData(zipped)["entry.txt"]);

            using (var stream = new MemoryStream())
            {
                QuantConnect.Compression.ZipBytesAsync(stream, new[]
                {
                    new KeyValuePair<byte[], string>(Encoding.UTF8.GetBytes("first"), "1.txt"),
                    new KeyValuePair<byte[], string>(Encoding.UTF8.GetBytes("second"), "2.txt")
                }).Wait();
                stream.Position = 0;
                var data = QuantConnect.Compression.UnzipDataAsync(stream).Result;
                Assert.AreEqual("first", data["1.txt"]);
                Assert.AreEqual("second", data["2.txt"]);
            }

            var target = PathFor("streams.zip");
            QuantConnect.Compression.ZipStreamsAsync(target, new[]
            {
                new KeyValuePair<string, Stream>("s.txt", new MemoryStream(Encoding.UTF8.GetBytes("stream")))
            }).Wait();
            Assert.AreEqual("stream", QuantConnect.Compression.UnzipData(File.ReadAllBytes(target))["s.txt"]);
        }

        [Test]
        public void ZipTextFileAndStringOverloads()
        {
            var text = PathFor("file.txt");
            File.WriteAllText(text, "content");
            var zip = QuantConnect.Compression.Zip(text, "inner.txt", deleteOriginal: false);
            Assert.IsTrue(File.Exists(text));
            Assert.AreEqual("content", QuantConnect.Compression.UnzipData(File.ReadAllBytes(zip))["inner.txt"]);

            var zipDefault = QuantConnect.Compression.Zip(text);
            Assert.IsFalse(File.Exists(text));
            Assert.AreEqual("content", QuantConnect.Compression.UnzipData(File.ReadAllBytes(zipDefault))["file.txt"]);

            var stringZip = PathFor("string.zip");
            QuantConnect.Compression.Zip("raw data", stringZip, "raw.txt");
            Assert.AreEqual("raw data", QuantConnect.Compression.UnzipData(File.ReadAllBytes(stringZip))["raw.txt"]);
        }

        [Test]
        public void ZipDirectoryUnzipAndZipFiles()
        {
            var source = Directory.CreateDirectory(PathFor("source")).FullName;
            File.WriteAllText(Path.Combine(source, "a.txt"), "A");
            Directory.CreateDirectory(Path.Combine(source, "nested"));
            File.WriteAllText(Path.Combine(source, "nested", "b.txt"), "B");

            var zip = PathFor("dir.zip");
            Assert.IsTrue(QuantConnect.Compression.ZipDirectory(source, zip, includeRootInZip: false));
            CollectionAssert.AreEquivalent(new[] { "a.txt", "nested/b.txt" }, QuantConnect.Compression.GetZipEntryFileNames(zip));

            var destination = PathFor("dest");
            Assert.IsTrue(QuantConnect.Compression.Unzip(zip, destination));
            Assert.AreEqual("B", File.ReadAllText(Path.Combine(destination, "nested", "b.txt")));
            Assert.IsTrue(QuantConnect.Compression.Unzip(zip, destination, overwrite: true));

            Assert.IsFalse(QuantConnect.Compression.Unzip(PathFor("missing.zip"), destination));
            Assert.IsFalse(QuantConnect.Compression.ZipDirectory(PathFor("missing-dir"), PathFor("x.zip")));

            var files = PathFor("files.zip");
            QuantConnect.Compression.ZipFiles(files, new[] { Path.Combine(source, "a.txt"), Path.Combine(source, "nested", "b.txt") });
            CollectionAssert.AreEquivalent(new[] { "a.txt", "b.txt" }, QuantConnect.Compression.GetZipEntryFileNames(files));
        }

        [Test]
        public void UnzipStreamReaderByEntryName()
        {
            var zip = PathFor("entries.zip");
            QuantConnect.Compression.ZipData(zip, new Dictionary<string, string> { { "first.csv", "1" }, { "second.csv", "2" } });

            using (var reader = QuantConnect.Compression.Unzip(zip, "second.csv", out var zipFile))
            {
                Assert.AreEqual("2", reader.ReadToEnd());
                zipFile.Dispose();
            }
            using (var reader = QuantConnect.Compression.Unzip(zip, out var zipFile))
            {
                Assert.IsNotNull(reader);
                zipFile.Dispose();
            }
            using (var stream = File.OpenRead(zip))
            using (var reader = QuantConnect.Compression.UnzipStreamToStreamReader(stream))
            {
                Assert.AreEqual("1", reader.ReadToEnd());
            }
        }

        [Test]
        public void UnzipToFolderFromFileWritesNextToZip()
        {
            var zip = PathFor("folder.zip");
            QuantConnect.Compression.ZipData(zip, new Dictionary<string, string> { { "out.txt", "value" } });
            var files = QuantConnect.Compression.UnzipToFolder(zip);
            Assert.AreEqual(1, files.Count);
            Assert.AreEqual("value", File.ReadAllText(Path.Combine(_directory, "out.txt")));
        }

        [Test]
        [Ignore("Possible bug: Compression.ValidateZip throws ZipException for a non-zip file instead of returning false as documented")]
        public void ValidateZipFailsForInvalidArchive()
        {
            var path = PathFor("invalid.zip");
            File.WriteAllText(path, "not a zip");
            Assert.IsFalse(QuantConnect.Compression.ValidateZip(path));
        }

        [Test]
        public void UnGZipWritesDecompressedFile()
        {
            var gz = PathFor("data.csv.gz");
            using (var file = File.Create(gz))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            {
                gzip.Write(Encoding.UTF8.GetBytes("gz content"));
            }
            var output = QuantConnect.Compression.UnGZip(gz, Directory.CreateDirectory(PathFor("gz")).FullName);
            Assert.AreEqual("data.csv", Path.GetFileName(output));
            Assert.AreEqual("gz content", File.ReadAllText(output));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnTarReadsEntries(bool gzip)
        {
            var tarPath = PathFor(gzip ? "archive.tar.gz" : "archive.tar");
            CreateTar(tarPath, gzip);

            var entries = QuantConnect.Compression.UnTar(tarPath).ToDictionary(kvp => kvp.Key, kvp => Encoding.UTF8.GetString(kvp.Value));
            Assert.AreEqual("tar one", entries["one.txt"]);

            using (var stream = File.OpenRead(tarPath))
            {
                var fromStream = QuantConnect.Compression.UnTar(stream, gzip).ToDictionary(kvp => kvp.Key, kvp => Encoding.UTF8.GetString(kvp.Value));
                Assert.AreEqual("tar one", fromStream["one.txt"]);
            }

            var destination = Directory.CreateDirectory(PathFor("untar")).FullName;
            if (gzip)
            {
                QuantConnect.Compression.UnTarGzFiles(tarPath, destination);
            }
            else
            {
                QuantConnect.Compression.UnTarFiles(tarPath, destination);
            }
            Assert.AreEqual("tar one", File.ReadAllText(Path.Combine(destination, "one.txt")));
        }

        private static void CreateTar(string path, bool gzip)
        {
            using var file = File.Create(path);
            using Stream target = gzip ? new GZipStream(file, CompressionLevel.Fastest) : file;
            using var writer = new TarWriter(target, TarEntryFormat.Ustar);
            var entry = new UstarTarEntry(TarEntryType.RegularFile, "one.txt")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("tar one"))
            };
            writer.WriteEntry(entry);
        }
    }
}
