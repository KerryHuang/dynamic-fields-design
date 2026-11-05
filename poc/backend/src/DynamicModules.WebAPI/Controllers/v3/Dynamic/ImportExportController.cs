using DynamicModules.Application.Dynamic.ExportService.Queries.ExportRecords;
using DynamicModules.Application.Dynamic.ImportService.Commands.ImportRecords;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic")]
public class ImportExportController(IMediator mediator) : ControllerBase
{
    [HttpGet("modules/{key}/export")]
    public async Task<IActionResult> Export(
        string key, [FromQuery] string format = "csv", [FromQuery] string? filter = null,
        [FromQuery(Name = "sortBy")] string[]? sortBy = null, [FromQuery(Name = "sortOrder")] string[]? sortOrder = null,
        [FromQuery] string? q = null, [FromQuery] string locale = "zh-TW")
    {
        var result = await mediator.Send(new ExportRecordsRequest(key, format, filter, sortBy ?? [], sortOrder ?? [], q, locale));
        return result.IsSuccess && result.Data is ExportFile file
            ? File(file.Content, file.ContentType, file.FileName)
            : result.ToActionResult(this);
    }

    /// <summary>
    /// 匯入 CSV（決策 6）：commit=false 預覽、true 確認寫入。1 MB 上限由 handler 檢查並回欄位錯誤；
    /// 這裡把請求上限放寬到 2 MB，讓略超過 1 MB 的檔案也能拿到「檔案不能超過 1 MB」而不是框架的 413。
    /// </summary>
    [HttpPost("modules/{key}/import")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2 * 1024 * 1024)]
    public async Task<IActionResult> Import(string key, IFormFile? file, [FromQuery] bool commit = false)
    {
        byte[] content = [];
        if (file is not null)
        {
            using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer);
            content = buffer.ToArray();
        }
        var result = commit
            ? await mediator.Send(new ImportRecordsRequest(key, content))
            : await mediator.Send(new PreviewImportRequest(key, content));
        return result.ToActionResult(this);
    }
}
