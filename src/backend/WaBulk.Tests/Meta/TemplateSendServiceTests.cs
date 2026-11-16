using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WaBulk.Domain.Messaging;
using WaBulk.Infrastructure.Messaging;
using WaBulk.Infrastructure.Meta;
using WaBulk.Infrastructure.Persistence;

namespace WaBulk.Tests.Meta;

public sealed class TemplateSendServiceTests
{
    private readonly DbContextOptions<WaBulkDbContext> _dbOptions = new DbContextOptionsBuilder<WaBulkDbContext>()
        .UseInMemoryDatabase($"wabulk-{Guid.NewGuid()}")
        .Options;

    private async Task<DispatchJob> SeedJobAsync()
    {
        await using var db = new WaBulkDbContext(_dbOptions);
        var job = new DispatchJob
        {
            CampaignVersionId = 1,
            ContactId = 1,
            PhoneE164 = "+59170000000",
            ScheduledUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddDays(1),
        };
        db.DispatchJobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    [Fact]
    public async Task Attempt_IsCommittedAsSubmitting_BeforeThePost()
    {
        var job = await SeedJobAsync();
        SendAttempt? seenDuringPost = null;
        string? callbackData = null;

        var handler = new FakeHttpHandler(async (request, _) =>
        {
            // Durante el POST, otro contexto (otra "conexión") ya debe ver el intento confirmado.
            await using var other = new WaBulkDbContext(_dbOptions);
            seenDuringPost = await other.SendAttempts.AsNoTracking().SingleOrDefaultAsync();
            callbackData = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync())
                .RootElement.GetProperty("biz_opaque_callback_data").GetString();
            return MetaWhatsAppClientTests.Json(HttpStatusCode.OK, MetaWhatsAppClientTests.AcceptedJson);
        });

        await using var db = new WaBulkDbContext(_dbOptions);
        var service = new TemplateSendService(
            db, MetaWhatsAppClientTests.CreateClient(handler), TimeProvider.System, NullLogger<TemplateSendService>.Instance);

        var attempt = await service.SendAsync(job, new TemplateMessage("hello_world", "en_US"), CancellationToken.None);

        Assert.NotNull(seenDuringPost);
        Assert.Equal(SendAttemptStatus.Submitting, seenDuringPost.Status);
        Assert.Equal(attempt.AttemptId, seenDuringPost.AttemptId);
        Assert.Equal(attempt.AttemptId.ToString(), callbackData);

        await using var check = new WaBulkDbContext(_dbOptions);
        var saved = await check.SendAttempts.SingleAsync();
        Assert.Equal(SendAttemptStatus.Accepted, saved.Status);
        Assert.Equal("wamid.TEST123", saved.Wamid);
        Assert.NotNull(saved.FinishedUtc);
    }

    [Fact]
    public async Task Timeout_LeavesAttemptInSubmissionUnknown_WithoutResending()
    {
        var job = await SeedJobAsync();
        var handler = new FakeHttpHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return MetaWhatsAppClientTests.Json(HttpStatusCode.OK, MetaWhatsAppClientTests.AcceptedJson);
        });

        await using var db = new WaBulkDbContext(_dbOptions);
        var service = new TemplateSendService(
            db, MetaWhatsAppClientTests.CreateClient(handler, TimeSpan.FromMilliseconds(100)),
            TimeProvider.System, NullLogger<TemplateSendService>.Instance);

        await service.SendAsync(job, new TemplateMessage("hello_world", "en_US"), CancellationToken.None);

        Assert.Single(handler.Requests); // un solo POST: sin reintento automático
        await using var check = new WaBulkDbContext(_dbOptions);
        var saved = await check.SendAttempts.SingleAsync();
        Assert.Equal(SendAttemptStatus.SubmissionUnknown, saved.Status);
        Assert.Null(saved.Wamid);
    }
}
