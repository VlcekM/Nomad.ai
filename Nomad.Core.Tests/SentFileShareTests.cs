using Nomad.Core.Hub;
using Nomad.Hub.Contracts;

namespace Nomad.Core.Tests;

/// <summary>A PC sending a phone a file a chat sent the user: named at once, sent after, and sent only once while the hub has it.</summary>
public sealed class SentFileShareTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nomad-sent-files-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeHub
    {
        public List<(string Slot, byte[] Content, string Name, long Length)> Uploads { get; } = [];

        public TaskCompletionSource Gate { get; set; } = new();

        public bool Fail { get; set; }

        public async Task<ChatFile> UploadAsync(string slot, Stream content, string name, long length, CancellationToken ct)
        {
            await Gate.Task;
            if (Fail)
            {
                throw new HttpRequestException("The hub is away.");
            }

            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, ct);
            lock (Uploads)
            {
                Uploads.Add((slot, copy.ToArray(), name, length));
            }

            return new ChatFile(slot, name, length);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task AFileIsNamedAtOnceAndSentAfter()
    {
        var path = Path.Combine(_dir, "shot.png");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
        var hub = new FakeHub();
        var share = new SentFileShare(hub.UploadAsync);

        // The answer doesn't wait for the upload, which waits on the gate.
        var offered = share.Offer(path, "slot1");
        Assert.Equal(new ChatFile("slot1", "shot.png", 4), offered);
        Assert.Empty(hub.Uploads);

        hub.Gate.SetResult();
        await share.UploadOf("slot1")!;
        var upload = Assert.Single(hub.Uploads);
        Assert.Equal(("slot1", "shot.png", 4L), (upload.Slot, upload.Name, upload.Length));
        Assert.Equal([1, 2, 3, 4], upload.Content);
    }

    [Fact]
    public async Task TheSameFileIsNamedAgainUntilItChangesOrTheHubWouldHaveDroppedIt()
    {
        var path = Path.Combine(_dir, "report.pdf");
        await File.WriteAllBytesAsync(path, [9, 9]);
        var hub = new FakeHub();
        hub.Gate.SetResult();
        var clock = new Clock();
        var share = new SentFileShare(hub.UploadAsync, clock);

        share.Offer(path, "slot1");
        await share.UploadOf("slot1")!;
        Assert.Equal("slot1", share.Offer(path, "slot2").Id); // Already there: slot2 goes unused.

        // Changed since: sent again.
        await File.WriteAllBytesAsync(path, [9, 9, 9]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("slot3", share.Offer(path, "slot3").Id);
        await share.UploadOf("slot3")!;

        // Nearly a day on, the hub would soon drop its copy: sent again.
        clock.Now += SentFileShare.ReuseFor + TimeSpan.FromMinutes(1);
        Assert.Equal("slot4", share.Offer(path, "slot4").Id);
        await share.UploadOf("slot4")!;
        Assert.Equal(["slot1", "slot3", "slot4"], hub.Uploads.Select(u => u.Slot));
    }

    [Fact]
    public async Task AFailedUploadIsTriedAgainOnTheNextAsk()
    {
        var path = Path.Combine(_dir, "notes.txt");
        await File.WriteAllTextAsync(path, "hello");
        var hub = new FakeHub { Fail = true };
        var share = new SentFileShare(hub.UploadAsync);

        share.Offer(path, "slot1");
        var failing = share.UploadOf("slot1")!;
        hub.Gate.SetResult();
        await Assert.ThrowsAsync<HttpRequestException>(() => failing);
        await Task.Delay(50); // The failure's clean-up runs just after.

        hub.Fail = false;
        Assert.Equal("slot2", share.Offer(path, "slot2").Id);
        await share.UploadOf("slot2")!;
        Assert.Equal("slot2", Assert.Single(hub.Uploads).Slot);
    }

    [Fact]
    public async Task AFileThatHasGoneOrIsTooLargeIsRefusedWithAReason()
    {
        var share = new SentFileShare(new FakeHub().UploadAsync);
        var gone = Assert.Throws<InvalidOperationException>(() => share.Offer(Path.Combine(_dir, "missing.png"), "slot1"));
        Assert.Contains("no longer on the PC", gone.Message);

        var big = Path.Combine(_dir, "big.bin");
        await using (var file = File.Create(big))
        {
            file.SetLength(SentFileShare.MaxSize + 1);
        }

        var tooLarge = Assert.Throws<InvalidOperationException>(() => share.Offer(big, "slot2"));
        Assert.Contains("too large", tooLarge.Message);

        // A folder is not a file.
        Assert.Throws<InvalidOperationException>(() => share.Offer(_dir, "slot3"));
    }

    [Fact]
    public async Task AFileLargerThanThePhoneAskedForIsNeverSent()
    {
        var path = Path.Combine(_dir, "large.png");
        await File.WriteAllBytesAsync(path, new byte[100]);
        var hub = new FakeHub();
        hub.Gate.SetResult();
        var share = new SentFileShare(hub.UploadAsync);

        var refused = Assert.Throws<InvalidOperationException>(() => share.Offer(path, "slot1", maxSize: 99));
        Assert.Contains("larger than the phone asked for", refused.Message);
        Assert.Null(share.UploadOf("slot1"));
        Assert.Equal("slot2", share.Offer(path, "slot2", maxSize: 100).Id);
        await share.UploadOf("slot2")!;
        Assert.Equal("slot2", Assert.Single(hub.Uploads).Slot);
    }

    [Fact]
    public async Task AFreshCopyIsSentWhenTheHubHasLostTheOneBefore()
    {
        var path = Path.Combine(_dir, "shot.png");
        await File.WriteAllBytesAsync(path, [1, 2]);
        var hub = new FakeHub();
        hub.Gate.SetResult();
        var share = new SentFileShare(hub.UploadAsync);

        share.Offer(path, "slot1");
        await share.UploadOf("slot1")!;
        Assert.Equal("slot1", share.Offer(path, "slot2").Id);
        Assert.Equal("slot3", share.Offer(path, "slot3", fresh: true).Id);
        await share.UploadOf("slot3")!;
        Assert.Equal("slot3", share.Offer(path, "slot4").Id); // And that one is named from then on.
        Assert.Equal(["slot1", "slot3"], hub.Uploads.Select(u => u.Slot));
    }
}
