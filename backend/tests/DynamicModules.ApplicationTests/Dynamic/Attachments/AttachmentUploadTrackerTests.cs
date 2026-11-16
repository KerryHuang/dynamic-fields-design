using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.DemoService;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DynamicModules.ApplicationTests.Dynamic.Attachments;

/// <summary>上傳清檔清單：成功後清空、重設時清空，共用 scope 時後續失敗不會刪到前一次已提交的檔；刪檔失敗不外溢。</summary>
public class AttachmentUploadTrackerTests
{
    private sealed class RecordingStore(Func<string, bool>? onDelete = null) : IAttachmentStore
    {
        public List<string> Deleted { get; } = [];
        public Task<long?> SaveAsync(string id, Stream content, long maxBytes, CancellationToken ct) => Task.FromResult<long?>(0);
        public Stream? OpenRead(string id) => null;
        public bool Delete(string id) { Deleted.Add(id); return onDelete?.Invoke(id) ?? true; }
        public IReadOnlyList<StoredAttachment> List() => [];
    }

    private static AttachmentUploadTracker Make(IAttachmentStore store) => new(store, NullLogger<AttachmentUploadTracker>.Instance);

    [Fact]
    public void SuccessThenFailure_OnlyDeletesTheFailedUploadsFile()
    {
        var store = new RecordingStore();
        var tracker = Make(store);
        tracker.Track("committed");
        tracker.Forget();
        tracker.Track("failed");

        tracker.DeleteAll();

        store.Deleted.Should().Equal("failed");
    }

    [Fact]
    public void ResetPerRequestState_ForgetsTrackedIds()
    {
        var store = new RecordingStore();
        var tracker = Make(store);
        tracker.Track("committed");

        tracker.Forget(); // DemoStructure.ResetPerRequestState 呼叫的就是這個
        tracker.DeleteAll();

        store.Deleted.Should().BeEmpty();
    }

    [Fact]
    public void ResetPerRequestState_SignatureIncludesTracker() =>
        typeof(DemoStructure).GetMethod(nameof(DemoStructure.ResetPerRequestState))!.GetParameters()
            .Select(p => p.ParameterType).Should().Contain(typeof(AttachmentUploadTracker));

    [Fact]
    public void DeleteAll_IOException_DoesNotThrow_AndKeepsDeletingTheRest()
    {
        var store = new RecordingStore(id => id == "a" ? throw new IOException("locked by scanner") : true);
        var tracker = Make(store);
        tracker.Track("a");
        tracker.Track("b");

        var act = tracker.DeleteAll;

        act.Should().NotThrow();
        store.Deleted.Should().Equal("a", "b");
    }
}
