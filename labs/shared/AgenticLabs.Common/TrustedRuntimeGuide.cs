using System.Text;

namespace WorkIqFiles;

internal static class TrustedRuntimeGuide
{
    internal static IReadOnlyDictionary<string, string> Load(string resource, string version, string prefix,
        string[] names, int maxBytes, System.Reflection.Assembly assembly)
    {
        using Stream stream = assembly.GetManifestResourceStream(resource) ??
            throw new LabException("Trusted runtime guide resource missing; rebuild this lab.");
        if (stream.Length > maxBytes) throw new LabException("Trusted runtime guide exceeds its build-owned byte budget.");
        using StreamReader reader = new(stream, new UTF8Encoding(false, true));
        return Parse(reader.ReadToEnd(), version, prefix, names, maxBytes);
    }
    internal static IReadOnlyDictionary<string, string> Parse(string document, string version, string prefix,
        string[] names, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(document) > maxBytes)
            throw new LabException("Trusted runtime guide exceeds its build-owned byte budget.");
        document = document.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!document.StartsWith(version + "\n", StringComparison.Ordinal))
            throw new LabException("Trusted runtime guide version invalid; rebuild with the supported version.");
        Dictionary<string, string> sections = new(StringComparer.Ordinal);
        foreach (string part in document.Split(prefix, StringSplitOptions.None).Skip(1))
        {
            int end = part.IndexOf(" -->", StringComparison.Ordinal);
            if (end < 0) throw new LabException("Trusted runtime guide section marker invalid.");
            string key = part[..end], text = part[(end + 4)..].Trim();
            if (!names.Contains(key, StringComparer.Ordinal) || text.Length == 0 || !sections.TryAdd(key, text))
                throw new LabException("Trusted runtime guide has an unknown, empty or duplicate section.");
        }
        if (sections.Count != names.Length) throw new LabException("Trusted runtime guide section missing; no partial guide.");
        return sections;
    }
}
