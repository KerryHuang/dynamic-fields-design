using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Common.Behaviors;

/// <summary>
/// 請求層冪等（設計 §3.2）。在交易「內」、handler 之前，所以查詢與後面的寫入在同一把寫入鎖之下：
/// 兩個帶同一把 key 的請求同時進來，後者排隊等到前者提交後，查到紀錄，直接回放。
/// 只處理帶 Idempotency-Key 的 <see cref="IIdempotentRequest"/>（記錄寫入、任務與留言；<see cref="IIdempotentRequest.IdempotencyApplies"/>
/// 是 false 的放行）；只有成功的結果會存，失敗的請求整筆回滾、沒有紀錄。
/// MCP 通道的請求雜湊多一個通道後綴（mcp2a 判斷 5，寫死清單：安全）：畫面用過的 key 被 AI 以相同內容重送時回「key 已用在不同內容」，
/// 不回放畫面通道的結果；畫面通道的雜湊算法不變，既有紀錄照常回放。巢狀的寫入請求（交易層數大於 1）不處理。
/// 紀錄保留天數是政策 A26（maintenance.idempotencyRetentionDays）。
/// </summary>
public class IdempotencyBehavior<TReq, TRes>(
    IOperationContext operation, IOperationRepository operations, ICurrentUser user, IBusinessTime time, IUnitOfWork uow,
    IPolicyReader policies, IRequestChannel channel, AfterCommitActions afterCommit)
    : IPipelineBehavior<TReq, TRes> where TReq : notnull
{
    private const int Success = 1, Created = 2;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new LowercaseEnumConverter<FieldType>(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        // 只有最外層請求用這把 key：handler 內巢狀送出的寫入請求不另存紀錄（否則同一把 key 存兩列，主鍵衝突）
        if (request is not IIdempotentRequest { IdempotencyApplies: true } || operation.ClientKey is not { } key
            || afterCommit.TransactionDepth != 1) return await next();

        operation.Replayed = false;
        var hash = Hash(request);
        if (await operations.GetAsync(user.UserId, key, ct) is { } existing)
        {
            if (existing.RequestHash != hash)
                return (TRes)(object)ServiceResult.Unprocessable(MessageRef.Of(MessageText.IdempotencyKeyReused));
            operation.Replayed = true;
            return (TRes)(object)Replay(existing);
        }

        var response = await next();
        if (response is not ServiceResult { IsSuccess: true } result) return response;

        operations.Add(new OperationRecord
        {
            UserId = user.UserId, Key = key, RequestHash = hash,
            ResultKind = result.IsCreated ? Created : Success,
            ResultJson = result.Data is null ? "" : JsonSerializer.Serialize(result.Data, result.Data.GetType(), Json),
            CreatedTime = time.Now
        });
        var retentionDays = await policies.IntAsync(PolicyCatalog.MaintenanceIdempotencyRetentionDays, null, ct);
        await operations.PurgeOlderThanAsync(time.Now.AddDays(-retentionDays), ct);
        await uow.CommitAsync(ct);
        return response;
    }

    private const string McpSuffix = "\nmcp";

    private string Hash(TReq request)
    {
        var body = JsonSerializer.Serialize(request, request.GetType(), Json);
        var input = request.GetType().FullName + "\n" + body + (channel.Channel == RequestChannel.Mcp ? McpSuffix : "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    private static ServiceResult Replay(OperationRecord stored)
    {
        object? data = stored.ResultJson.Length == 0 ? null : JsonDocument.Parse(stored.ResultJson).RootElement.Clone();
        return stored.ResultKind == Created ? ServiceResult.Created(data!) : ServiceResult.Success(data);
    }
}
