using System.Text.RegularExpressions;
using DynamicModules.Application.Dynamic.AttachmentService;

namespace DynamicModules.Persistence.Files;

/// <summary>
/// 本機檔案儲存。磁碟上的名稱只由系統產生的 32 碼十六進位 id 決定（再分兩層目錄打散），
/// 呼叫端給的任何字串都不會進入路徑；id 格式不對一律當作不存在。
/// </summary>
public sealed partial class LocalAttachmentStore : IAttachmentStore
{
    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex IdPattern();

    private readonly string _root;

    public LocalAttachmentStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    private string? PathOf(string id) =>
        IdPattern().IsMatch(id) ? Path.Combine(_root, id[..2], id[2..4], id) : null;

    public async Task<long?> SaveAsync(string id, Stream content, long maxBytes, CancellationToken ct)
    {
        var path = PathOf(id) ?? throw new ArgumentException("附件 id 格式錯誤", nameof(id));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        long total = 0;
        try
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > maxBytes) { file.Close(); File.Delete(path); return null; }
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            return total;
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    public Stream? OpenRead(string id) =>
        PathOf(id) is { } path && File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;

    public bool Delete(string id)
    {
        if (PathOf(id) is not { } path || !File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public IReadOnlyList<StoredAttachment> List() =>
        !Directory.Exists(_root) ? [] :
        [.. Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetFileName(f))
            .Where(n => IdPattern().IsMatch(n))
            .Select(n => new StoredAttachment(n, File.GetLastWriteTimeUtc(PathOf(n)!)))];
}
