using System.Net;
using System.Text.Json;
using DynamicModules.Application.Dynamic.RecordService.Data;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests;

public class ConcurrencyExceptionTests
{
    private sealed class ThrowingMediator : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default) =>
            throw new DbUpdateConcurrencyException("race");
        public Task Send<TRequest>(TRequest request, CancellationToken ct = default) where TRequest : IRequest =>
            throw new DbUpdateConcurrencyException("race");
        public Task<object?> Send(object request, CancellationToken ct = default) =>
            throw new DbUpdateConcurrencyException("race");
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default)
            where TNotification : INotification => throw new NotSupportedException();
    }

    [Fact]
    public async Task DbUpdateConcurrencyException_Returns409WithTraceIdAndMessage()
    {
        using var app = new TestApp();
        using var client = app.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddScoped<IMediator, ThrowingMediator>())).CreateClient();
        client.DefaultRequestHeaders.Add("X-User", "admin");

        var res = await client.GetAsync("/api/v3/dynamic/modules");

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("message").GetString().Should().Be(RecordMapper.Stale);
        doc.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrEmpty();
    }
}
