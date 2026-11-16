using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Common.Behaviors;

/// <summary>
/// 請求層冪等（設計 §3.2）。在交易「內」、handler 之前，所以查詢與後面的寫入在同一把寫入鎖之下：
/// 兩個帶同一把 key 的請求同時進來，後者排隊等到前者提交後，查到紀錄，直接回放。
/// 只處理帶 Idempotency-Key 的 <see cref="IRecordWriteRequest"/>；只有成功的結果會存，失敗的請求整筆回滾、沒有紀錄。
/// </summary>
public class IdempotencyBehavior<TReq, TRes>(
    IOperationContext operation, IOperationRepository operations, ICurrentUser user, IBusinessTime time, IUnitOfWork uow)
    : IPipelineBehavior<TReq, TRes> where TReq : notnull
{
    public const int RetentionDays = 7;
    private const int Success = 1, Created = 2;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new LowercaseEnumConverter<FieldType>(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        if (request is not IRecordWriteRequest || operation.ClientKey is not { } key) return await next();

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
        await operations.PurgeOlderThanAsync(time.Now.AddDays(-RetentionDays), ct);
        await uow.CommitAsync(ct);
        return response;
    }

    private static string Hash(TReq request)
    {
        var body = JsonSerializer.Serialize(request, request.GetType(), Json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.GetType().FullName + "\n" + body)));
    }

    private static ServiceResult Replay(OperationRecord stored)
    {
        object? data = stored.ResultJson.Length == 0 ? null : JsonDocument.Parse(stored.ResultJson).RootElement.Clone();
        return stored.ResultKind == Created ? ServiceResult.Created(data!) : ServiceResult.Success(data);
    }
}
