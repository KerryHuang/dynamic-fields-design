using System.Linq.Expressions;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace DynamicModules.Persistence.Repositories.Dynamic;

/// <summary>
/// 把 <see cref="TaskVisibility"/> 轉成單一 EF LINQ 條件（不寫 raw SQL）。管理員不加條件；其他人看得到：參與者、
/// 部屬的任務（<see cref="SubordinateTasks"/>）、記錄在可讀模組的資料範圍內（含軟刪除的記錄，與 stub 判斷一致）。
/// </summary>
internal static class TaskVisibilityQuery
{
    public static IQueryable<TaskItem> Visible(DynamicDbContext db, TaskVisibility v)
    {
        var tasks = db.Set<TaskItem>().AsNoTracking();
        if (v.Admin) return tasks;

        var me = v.UserId;
        Expression<Func<TaskItem, bool>> participant = t => t.Participants.Any(p => p.UserId == me);
        var t = participant.Parameters[0];
        Expression body = Expression.OrElse(participant.Body, Inline(SubordinateTasks.Of(v.Subordinates), t));

        var recordClause = RecordClause(db, v);
        if (recordClause is not null) body = Expression.OrElse(body, Inline(recordClause, t));
        return tasks.Where(Expression.Lambda<Func<TaskItem, bool>>(body, t));
    }

    private static Expression Inline(Expression<Func<TaskItem, bool>> e, ParameterExpression t) =>
        ReplacingExpressionVisitor.Replace(e.Parameters[0], t, e.Body);

    /// <summary>t => EXISTS(記錄 r：r.Id == t.RecordId 且 r 在某個可讀模組的範圍內)；沒有任何可讀模組回 null。</summary>
    private static Expression<Func<TaskItem, bool>>? RecordClause(DynamicDbContext db, TaskVisibility v)
    {
        var r = Expression.Parameter(typeof(Record), "r");
        Expression? inScope = null;
        void Or(Expression<Func<Record, bool>> e)
        {
            var body = ReplacingExpressionVisitor.Replace(e.Parameters[0], r, e.Body);
            inScope = inScope is null ? body : Expression.OrElse(inScope, body);
        }

        var wholeModules = v.Modules.Where(m => m.OwnerUserIds is null).Select(m => m.ModuleId).ToArray();
        if (wholeModules.Length > 0) Or(x => wholeModules.Contains(x.ModuleId));
        foreach (var scope in v.Modules.Where(m => m.OwnerUserIds is not null))
        {
            var moduleId = scope.ModuleId;
            var owners = scope.OwnerUserIds!.ToArray();
            Or(x => x.ModuleId == moduleId && owners.Contains(x.OwnerUserId));
        }
        if (inScope is null) return null;

        var t = Expression.Parameter(typeof(TaskItem), "t");
        var sameRecord = Expression.Equal(
            Expression.Convert(Expression.Property(r, nameof(Record.Id)), typeof(long?)),
            Expression.Property(t, nameof(TaskItem.RecordId)));
        var inner = Expression.Lambda<Func<Record, bool>>(Expression.AndAlso(sameRecord, inScope), r);
        var any = Expression.Call(typeof(Queryable), nameof(Queryable.Any), [typeof(Record)],
            ((IQueryable)db.Set<Record>()).Expression, Expression.Quote(inner));
        return Expression.Lambda<Func<TaskItem, bool>>(any, t);
    }
}
