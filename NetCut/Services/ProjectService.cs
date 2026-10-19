using System.Text.Json;
using NetCut.Models;

namespace NetCut.Services;

public static class ProjectService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static async Task SaveAsync(ProjectDocument doc, string path, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, doc, JsonOptions, ct);
        File.Move(temp, path, true);
    }

    public static async Task<ProjectDocument> LoadAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ProjectDocument>(stream, JsonOptions, ct)
            ?? throw new InvalidDataException("Invalid NetCut project.");
    }
}
