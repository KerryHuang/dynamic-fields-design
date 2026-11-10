using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.OrganizationService;
using DynamicModules.Domain.ValueObjects;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>組織骨架的部門；清單登入者都可讀，增改刪的系統管理員檢查在 SystemAdminBehavior。</summary>
[ApiController]
[Route("api/v3/dynamic/departments")]
public class DepartmentsController(IMediator mediator) : ControllerBase
{
    public record DepartmentBody(string? Code, I18nText? Name, long? ParentId, string? ManagerUserId, int SortOrder, bool? Active);

    private static DepartmentInput ToInput(DepartmentBody b) =>
        new(b.Code ?? "", b.Name ?? new I18nText(), b.ParentId, b.ManagerUserId, b.SortOrder, b.Active ?? true);

    [HttpGet]
    public async Task<IActionResult> List() => (await mediator.Send(new ListDepartmentsRequest())).ToActionResult(this);

    [HttpPost]
    public async Task<IActionResult> Create(DepartmentBody body) =>
        (await mediator.Send(new CreateDepartmentRequest(ToInput(body)))).ToActionResult(this);

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, DepartmentBody body) =>
        (await mediator.Send(new UpdateDepartmentRequest(id, ToInput(body)))).ToActionResult(this);

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id) => (await mediator.Send(new DeleteDepartmentRequest(id))).ToActionResult(this);
}
