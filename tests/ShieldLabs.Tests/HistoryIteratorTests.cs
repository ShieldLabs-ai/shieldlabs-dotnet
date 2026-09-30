using ShieldLabs.Tests.Support;

namespace ShieldLabs.Tests;

public class HistoryIteratorTests
{
    private const string UserHid = "9f86d081884c7d659a2feaa0c55ad015";

    private static async Task<List<string>> Collect(IAsyncEnumerable<Identification> items)
    {
        var ids = new List<string>();
        await foreach (var item in items)
        {
            ids.Add(item.RequestId);
        }

        return ids;
    }

    [Fact]
    public async Task Pages_until_total_and_deduplicates_on_request_id()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(200, TestClients.Page(new[] { TestClients.Row(TestClients.Id(1)), TestClients.Row(TestClients.Id(2)), TestClients.Row(TestClients.Id(3)) }, 6))
            // A new identification arrived: the next offset page repeats row 3.
            .Enqueue(200, TestClients.Page(new[] { TestClients.Row(TestClients.Id(3)), TestClients.Row(TestClients.Id(4)), TestClients.Row(TestClients.Id(5)) }, 6));
        var client = TestClients.History(handler);

        var ids = await Collect(client.History.IterateAsync(LookupType.UserHid, UserHid, new HistoryIterateOptions { PageSize = 3 }));

        Assert.Equal(new[] { TestClients.Id(1), TestClients.Id(2), TestClients.Id(3), TestClients.Id(4), TestClients.Id(5) }, ids);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("?limit=3&offset=0", handler.Requests[0].Uri.Query);
        Assert.Equal("?limit=3&offset=3", handler.Requests[1].Uri.Query);
    }

    [Fact]
    public async Task Stops_at_an_empty_page()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(200, TestClients.Page(new[] { TestClients.Row(TestClients.Id(1)), TestClients.Row(TestClients.Id(2)) }, 1000))
            .Enqueue(200, Fixtures.Text("history-empty.json"));

        var ids = await Collect(TestClients.History(handler).History.IterateAsync(LookupType.UserHid, UserHid, new HistoryIterateOptions { PageSize = 2 }));

        Assert.Equal(2, ids.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Stops_after_max_items()
    {
        var handler = new FakeHttpHandler().Always(200, Fixtures.Text("history-page.json"));

        var ids = await Collect(TestClients.History(handler).History.IterateAsync(LookupType.DeviceId, "ac7c303d-971b-41d1-8e25-cd5b46b46aed", new HistoryIterateOptions { MaxItems = 3 }));

        Assert.Equal(3, ids.Count);
        Assert.Equal("?limit=100&offset=0", Assert.Single(handler.Requests).Uri.Query);
    }

    [Fact]
    public async Task Max_items_zero_sends_nothing()
    {
        var handler = new FakeHttpHandler().Always(200, Fixtures.Text("history-page.json"));

        var ids = await Collect(TestClients.History(handler).History.IterateAsync(LookupType.UserHid, UserHid, new HistoryIterateOptions { MaxItems = 0 }));

        Assert.Empty(ids);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Stops_when_a_page_holds_only_repeated_rows_and_total_is_reached()
    {
        var page = TestClients.Page(new[] { TestClients.Row(TestClients.Id(1)), TestClients.Row(TestClients.Id(2)) }, 4);
        var handler = new FakeHttpHandler().Enqueue(200, page).Enqueue(200, page);

        var ids = await Collect(TestClients.History(handler).History.IterateAsync(LookupType.UserHid, UserHid, new HistoryIterateOptions { PageSize = 2 }));

        Assert.Equal(2, ids.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(101, null)]
    [InlineData(10, -1)]
    public void Invalid_options_throw_at_call_time(int pageSize, int? maxItems)
    {
        var handler = new FakeHttpHandler().Always(200, Fixtures.Text("history-page.json"));

        Assert.Throws<ValidationException>(() => TestClients.History(handler).History.IterateAsync(
            LookupType.UserHid, UserHid, new HistoryIterateOptions { PageSize = pageSize, MaxItems = maxItems }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Iteration_can_be_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHttpHandler().Always(200, TestClients.Page(new[] { TestClients.Row(TestClients.Id(1)), TestClients.Row(TestClients.Id(2)) }, 100));
        var client = TestClients.History(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in client.History.IterateAsync(LookupType.UserHid, UserHid).WithCancellation(cts.Token))
            {
                cts.Cancel();
            }
        });
        Assert.Single(handler.Requests);
    }
}
