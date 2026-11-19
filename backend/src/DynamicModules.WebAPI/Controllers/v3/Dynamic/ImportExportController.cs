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
        // approval 取原始查詢值：預設 binder 會把純空白轉成 null 而繞過驗證（簽核設計 §13.4.1）
        var approval = Request.Query.TryGetValue("approval", out var raw) ? raw.ToString() : null;
        var result = await mediator.Send(new ExportRecordsRequest(key, format, filter, sortBy ?? [], sortOrder ?? [], q, locale, approval));
        return result.IsSuccess && result.Data is ExportFile file
            ? File(file.Content, file.ContentType, file.FileName)
            : result.ToActionResult(this);
    }

    /// <summary>
    /// 匯入 CSV（決策 6）：commit=false 預覽、true 確認寫入。檔案大小上限（政策 import.maxFileBytes）由 handler 檢查並回欄位錯誤；
    /// 請求上限＝兩倍檔案上限＋multipart 開銷（<see cref="ImportBodyLimitFilter"/>），讓略超過的檔案拿到欄位錯誤而不是 413。
    /// </summary>
    [HttpPost("modules/{key}/import")]
    [TypeFilter(typeof(ImportBodyLimitFilter))]
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
