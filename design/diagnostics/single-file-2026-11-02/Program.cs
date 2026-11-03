using System.Security.Cryptography;
using System.Text.Json;

// Read the .NET 6+ bundle format without executing a Windows binary.
// Specification: dotnet/runtime v10.0.0, Microsoft.NET.HostModel/Bundle/{Manifest,FileEntry,Bundler}.cs.
// Diagnostic only; not a product dependency or a Windows execution test.
if (args.Length == 0 || args.Length % 3 != 0)
    throw new ArgumentException("Usage: InspectBundle EXE BUILD_OUTPUT PRE_BUNDLE_HOST [EXE BUILD_OUTPUT PRE_BUNDLE_HOST ...]");
var reports = new List<object>();
for (var i = 0; i < args.Length; i += 3)
    reports.Add(Inspect(Path.GetFullPath(args[i]), Path.GetFullPath(args[i + 1]), Path.GetFullPath(args[i + 2])));
Console.WriteLine(JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));

static object Inspect(string path, string buildOutput, string hostPath)
{
    var adjacent = Directory.GetFiles(Path.GetDirectoryName(path)!, "*", SearchOption.AllDirectories);
    Check(adjacent.Length == 1 && adjacent[0] == path, "Publish directory must contain exactly one EXE");
    using var stream = File.OpenRead(path);
    using var reader = new BinaryReader(stream);
    var prefix = reader.ReadBytes((int)Math.Min(stream.Length, 1024 * 1024));
    Check(prefix[0] == 'M' && prefix[1] == 'Z', "Missing MZ header");
    var pe = BitConverter.ToInt32(prefix, 0x3c);
    Check(BitConverter.ToUInt32(prefix, pe) == 0x00004550 && BitConverter.ToUInt16(prefix, pe + 4) == 0x8664,
        "Host must be a Windows x64 PE");
    var signature = Convert.FromHexString("8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae");
    var marker = FindSignature(stream, signature);
    Check(marker >= 8, "Missing .NET bundle marker");
    stream.Position = marker - 8;
    var header = reader.ReadInt64();
    Check(header > 0 && header < stream.Length, "Invalid bundle header offset");
    // On Windows, the SDK's singlefilehost statically includes CoreCLR/JIT/host libraries.
    // Those components are part of the native host, not individual manifest entries.
    var hostBytes = File.ReadAllBytes(hostPath);
    stream.Position = 0;
    var actualHost = reader.ReadBytes(hostBytes.Length);
    Check(marker < hostBytes.Length && BitConverter.ToInt64(hostBytes, (int)marker - 8) == 0,
        "Invalid pre-bundle host marker");
    hostBytes.AsSpan((int)marker - 8, 8).CopyTo(actualHost.AsSpan((int)marker - 8, 8));
    Check(actualHost.AsSpan().SequenceEqual(hostBytes), "Native host differs beyond the bundle offset");
    stream.Position = header;
    var major = reader.ReadUInt32(); var minor = reader.ReadUInt32(); var count = reader.ReadInt32();
    Check(major == 6 && minor == 0 && count > 0 && count < 10_000, "Unsupported bundle manifest");
    var id = reader.ReadString();
    var depsOffset = reader.ReadInt64(); var depsSize = reader.ReadInt64();
    var configOffset = reader.ReadInt64(); var configSize = reader.ReadInt64();
    Check(reader.ReadUInt64() == 0, "Unexpected full-content extraction mode");
    var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    for (var j = 0; j < count; j++)
    {
        var offset = reader.ReadInt64(); var size = reader.ReadInt64(); var compressed = reader.ReadInt64();
        var type = reader.ReadByte(); var name = reader.ReadString();
        Check(offset > 0 && size > 0 && size <= header - offset && compressed == 0,
            "Invalid range or unexpected compression: " + name);
        Check(!Path.IsPathRooted(name) && !name.Split('/').Contains(".."), "Invalid entry path");
        Check(!name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase), "Loose symbol in bundle");
        Check(entries.TryAdd(name, new(offset, size, type)), "Duplicate bundle entry");
    }
    var app = Path.GetFileNameWithoutExtension(path);
    var configEntry = entries[app + ".runtimeconfig.json"];
    var depsEntry = entries[app + ".deps.json"];
    Check(configEntry.Offset == configOffset && configEntry.Size == configSize &&
        depsEntry.Offset == depsOffset && depsEntry.Size == depsSize, "Header configuration ranges differ");
    stream.Position = configOffset;
    using var config = JsonDocument.Parse(reader.ReadBytes(checked((int)configSize)));
    var frameworks = config.RootElement.GetProperty("runtimeOptions").GetProperty("includedFrameworks");
    Check(frameworks.GetArrayLength() > 0, "Missing self-contained runtime metadata");
    foreach (var required in new[] { app + ".dll", "Gpss.Core.dll", "Gpss.Application.dll",
        "Gpss.Input.Abstractions.dll", "Gpss.Input.HidDirect.dll", "Gpss.Input.XInput.dll",
        "Gpss.Input.Simulation.dll", "System.Private.CoreLib.dll" })
        Check(entries.ContainsKey(required), "Missing runtime dependency: " + required);
    if (app == "Gpss.Desktop")
        foreach (var required in new[] { "PresentationFramework.dll", "PresentationCore.dll", "WindowsBase.dll", "wpfgfx_cor3.dll" })
            Check(entries.ContainsKey(required), "Missing WPF dependency: " + required);
    if (app == "Gpss.Cli") Check(entries.ContainsKey("Gpss.Recording.dll"), "Missing recording dependency");

    var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
    foreach (var (name, entry) in entries)
    {
        // Publishing rewrites deps.json for the bundled/static-host runtime layout.
        var original = name == app + ".deps.json"
            ? Path.Combine(Path.GetDirectoryName(hostPath)!, name)
            : Path.Combine(buildOutput, name);
        Check(File.Exists(original), "Missing pre-bundle comparison file: " + name);
        stream.Position = entry.Offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024]; var remaining = entry.Size;
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            Check(read > 0, "Unexpected EOF"); hash.AppendData(buffer, 0, read); remaining -= read;
        }
        var actual = hash.GetHashAndReset();
        using var source = File.OpenRead(original);
        Check(actual.AsSpan().SequenceEqual(SHA256.HashData(source)), "Embedded payload differs: " + name);
        hashes.Add(name, Convert.ToHexStringLower(actual));
    }
    stream.Position = 0;
    return new { executable = Path.GetFileName(path), bytes = stream.Length,
        sha256 = Convert.ToHexStringLower(SHA256.HashData(stream)), bundleVersion = $"{major}.{minor}",
        bundleId = id, embeddedFiles = count, nativeHostBytesVerified = hostBytes.Length,
        includedFrameworks = frameworks.Clone(),
        payloadHashes = hashes, windowsExecutionTested = false };
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

static long FindSignature(FileStream stream, byte[] signature)
{
    stream.Position = 0;
    var buffer = new byte[64 * 1024]; var retained = 0; long start = 0;
    while (true)
    {
        var read = stream.Read(buffer, retained, buffer.Length - retained);
        if (read == 0) return -1;
        var count = retained + read;
        var index = buffer.AsSpan(0, count).IndexOf(signature);
        if (index >= 0) return start + index;
        retained = Math.Min(signature.Length - 1, count);
        buffer.AsSpan(count - retained, retained).CopyTo(buffer);
        start += count - retained;
    }
}

internal sealed record Entry(long Offset, long Size, byte Type);
