using CartSmart.Core.Worker;
using CartSmart.API.Models;
using FluentAssertions;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using CartSmart.Scraping;

namespace CartSmart.Tests;

public class DealUpdateOrchestratorTests
{
    private readonly Mock<IDealRepository> _repo = new();
    private readonly List<IStoreClient> _clients = new();
    private readonly Mock<IHtmlScraper> _scraper = new();

    [Fact]
    public async Task ReturnsErrorWhenNoUrl()
    {
        var deal = new Deal { Id = 1, ExternalOfferUrl = null! };
        _repo.Setup(r => r.GetActiveDealsForRefreshAsync(It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Deal> { deal });
        var orch = new DealUpdateOrchestrator(_repo.Object, _clients, NullLogger<DealUpdateOrchestrator>.Instance, _scraper.Object);
        var res = await orch.RefreshDealsAsync(10, CancellationToken.None);
        res.Errors.Should().Be(1);
    }
}