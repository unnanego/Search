#:project ../src/Search.Core/Search.Core.csproj
// Makes a Chrome Web Store package (CRX3) from a folder, signed with a key kept beside it, so a test can serve the
// store's answers itself and install, update and verify without the network.
//
//     dotnet run test/makecrx.cs -- <folder> <out.crx> <key.pem>
//
// The key is made the first time and reused after, so a second version of the same extension keeps its id. Prints the id.
using System.IO.Compression;
using System.Security.Cryptography;
using Search.Core;

if (args.Length != 3) { Console.Error.WriteLine("usage: makecrx <folder> <out.crx> <key.pem>"); return 2; }
var (folder, output, keyFile) = (args[0], args[1], args[2]);
using var rsa = RSA.Create(2048);
if (File.Exists(keyFile)) rsa.ImportFromPem(File.ReadAllText(keyFile)); else File.WriteAllText(keyFile, rsa.ExportPkcs8PrivateKeyPem());
using var zipped = new MemoryStream();
using (var archive = new ZipArchive(zipped, ZipArchiveMode.Create, leaveOpen: true))
    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        archive.CreateEntryFromFile(file, Path.GetRelativePath(folder, file).Replace('\\', '/'));
File.WriteAllBytes(output, Crx.Pack(zipped.ToArray(), rsa));
Console.WriteLine(Crx.IdOfKey(rsa.ExportSubjectPublicKeyInfo()));
return 0;
