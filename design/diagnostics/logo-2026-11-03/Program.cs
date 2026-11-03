using System.Buffers.Binary;
using System.Collections;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Security.Cryptography;
using System.Text.Json;

// Portable artifact inspection. Does not load WPF or execute Windows binaries.
if (args.Length != 4) throw new ArgumentException("Usage: VerifyBranding ICO DESKTOP_DLL DESKTOP_EXE CLI_EXE");
var ico = File.ReadAllBytes(args[0]);
Check(U16(ico, 0) == 0 && U16(ico, 2) == 1, "Invalid ICO header");
var expected = new Dictionary<(int Width, int Height), byte[]>();
for (var i = 0; i < U16(ico, 4); i++)
{
    var p = 6 + i * 16;
    var width = ico[p] == 0 ? 256 : ico[p]; var height = ico[p + 1] == 0 ? 256 : ico[p + 1];
    Check(U16(ico, p + 4) <= 1 && U16(ico, p + 6) == 32, "Expected 32bit ICO frame");
    var size = checked((int)U32(ico, p + 8)); var offset = checked((int)U32(ico, p + 12));
    expected.Add((width, height), ico.AsSpan(offset, size).ToArray());
}
Check(expected.Keys.Select(k => k.Width).Order().SequenceEqual(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }), "Missing DPI frame");
using (var stream = File.OpenRead(args[1]))
using (var pe = new PEReader(stream))
{
    var metadata = pe.GetMetadataReader();
    var resources = pe.GetSectionData(pe.PEHeaders.CorHeader!.ResourcesDirectory.RelativeVirtualAddress).GetContent().ToArray();
    var found = false;
    foreach (var handle in metadata.ManifestResources)
    {
        var resource = metadata.GetManifestResource(handle);
        if (!metadata.GetString(resource.Name).EndsWith(".g.resources", StringComparison.Ordinal)) continue;
        var offset = checked((int)resource.Offset); var size = checked((int)U32(resources, offset));
        using var content = new MemoryStream(resources, offset + 4, size, writable: false);
        using var reader = new ResourceReader(content);
        foreach (DictionaryEntry entry in reader)
        {
            if (!string.Equals((string)entry.Key, "assets/gpss.ico", StringComparison.Ordinal)) continue;
            using var image = (Stream)entry.Value!; using var copy = new MemoryStream(); image.CopyTo(copy);
            Check(copy.ToArray().AsSpan().SequenceEqual(ico), "WPF embedded ICO differs"); found = true;
        }
    }
    Check(found, "WPF pack URI resource is missing");
}
var reports = args.Skip(2).Select(InspectExecutable).ToArray();
Console.WriteLine(JsonSerializer.Serialize(new { wpfEmbeddedIconMatches = true, frameSizes = expected.Keys.Select(k => k.Width).Order(), executables = reports, windowsExecuted = false }, new JsonSerializerOptions { WriteIndented = true }));

object InspectExecutable(string path)
{
    using var stream = File.OpenRead(path); using var pe = new PEReader(stream);
    Check(pe.PEHeaders.CoffHeader.Machine == Machine.Amd64, "Expected Windows x64 host");
    var rva = pe.PEHeaders.PEHeader!.ResourceTableDirectory.RelativeVirtualAddress;
    Check(rva > 0, "No native resources");
    var section = pe.GetSectionData(rva).GetContent().ToArray();
    var leaves = new List<(int Type, int Id, int Language, byte[] Data)>();
    Walk(0, []);
    void Walk(int offset, int[] ancestors)
    {
        var count = U16(section, offset + 12) + U16(section, offset + 14);
        for (var i = 0; i < count; i++)
        {
            var entry = offset + 16 + 8 * i;
            var name = U32(section, entry); if ((name & 0x80000000) != 0) continue;
            var target = U32(section, entry + 4); var keys = ancestors.Append(checked((int)name)).ToArray();
            if ((target & 0x80000000) != 0) Walk(checked((int)(target & 0x7fffffff)), keys);
            else
            {
                Check(keys.Length == 3, "Unexpected resource depth");
                var dataEntry = checked((int)target);
                var dataRva = checked((int)U32(section, dataEntry)); var size = checked((int)U32(section, dataEntry + 4));
                leaves.Add((keys[0], keys[1], keys[2], pe.GetSectionData(dataRva).GetContent(0, size).ToArray()));
            }
        }
    }
    var groups = leaves.Where(r => r.Type == 14).ToArray();
    Check(groups.Length == 1, "Expected exactly one native icon group");
    foreach (var group in groups)
    {
        Check(U16(group.Data, 0) == 0 && U16(group.Data, 2) == 1 && U16(group.Data, 4) == expected.Count, "Invalid native icon group");
        var seen = new HashSet<(int, int)>();
        for (var i = 0; i < expected.Count; i++)
        {
            var p = 6 + 14 * i;
            var width = group.Data[p] == 0 ? 256 : group.Data[p]; var height = group.Data[p + 1] == 0 ? 256 : group.Data[p + 1];
            var id = U16(group.Data, p + 12);
            var image = leaves.Single(r => r.Type == 3 && r.Id == id && r.Language == group.Language);
            Check(image.Data.Length == U32(group.Data, p + 8), "Native icon size differs");
            Check(expected.TryGetValue((width, height), out var source) && image.Data.AsSpan().SequenceEqual(source), "Native icon pixels differ");
            Check(seen.Add((width, height)), "Duplicate native size");
        }
    }
    return new { file = Path.GetFileName(path), nativeIconFrames = expected.Count, nativeGroupCount = groups.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) };
}
static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
static void Check(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
