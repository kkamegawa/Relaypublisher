using IntuneLobPublisher.Core.Exceptions;
using IntuneLobPublisher.Core.Sources;

namespace IntuneLobPublisher.Core.Tests;

[TestClass]
public sealed class AzureBlobDownloaderTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("ab")] // too short
    [DataRow("averyveryverylongaccountname")] // too long
    [DataRow("Contoso")] // uppercase
    [DataRow("evil.host/x")] // hostname injection
    [DataRow("conto-so")] // hyphen not allowed
    public async Task DownloadToAsync_InvalidAccountName_FailsBeforeAnyRequest(string accountName)
    {
        var downloader = new AzureBlobDownloader();

        var ex = await Assert.ThrowsExactlyAsync<SourceDownloadException>(
            () => downloader.DownloadToAsync(
                accountName, "container", "blob", Path.Combine(Path.GetTempPath(), "unused.bin"), CancellationToken.None));

        Assert.Contains("AccountName", ex.Message);
    }
}
