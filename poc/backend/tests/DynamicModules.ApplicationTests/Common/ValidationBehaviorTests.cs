using FluentAssertions;
using FluentValidation;
using MediatR;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Behaviors;

namespace DynamicModules.ApplicationTests.Common;

public class ValidationBehaviorTests
{
    private record NameCommand(string? Name) : IRequest<ServiceResult>;

    private class NameValidator : AbstractValidator<NameCommand>
    {
        public NameValidator() => RuleFor(x => x.Name).NotEmpty().WithMessage("名稱必填");
    }

    private record PlainQuery(string? Name) : IRequest<string>;

    private class PlainValidator : AbstractValidator<PlainQuery>
    {
        public PlainValidator() => RuleFor(x => x.Name).NotEmpty();
    }

    [Fact]
    public async Task ValidationBehavior_InvalidRequest_ReturnsInvalidResultWithFieldErrors()
    {
        var behavior = new ValidationBehavior<NameCommand, ServiceResult>([new NameValidator()]);
        var called = false;

        var result = await behavior.Handle(new NameCommand(""), () =>
        {
            called = true;
            return Task.FromResult(ServiceResult.Success());
        }, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.Errors["Name"].Should().ContainSingle().Which.Should().Be("名稱必填");
        called.Should().BeFalse();
    }

    [Fact]
    public async Task ValidationBehavior_ValidRequest_CallsNext()
    {
        var behavior = new ValidationBehavior<NameCommand, ServiceResult>([new NameValidator()]);

        var result = await behavior.Handle(new NameCommand("ok"),
            () => Task.FromResult(ServiceResult.Success(1)), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ValidationBehavior_InvalidRequestWithNonServiceResponse_Throws()
    {
        var behavior = new ValidationBehavior<PlainQuery, string>([new PlainValidator()]);

        var act = () => behavior.Handle(new PlainQuery(null), () => Task.FromResult("x"), CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }
}
