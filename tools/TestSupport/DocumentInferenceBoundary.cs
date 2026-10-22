// Boundary for existing non-inference suites. Real document inference is covered by DocumentOcrTest.
namespace Starshot.Features.DocumentOcr;
internal static class DocumentInference
{
    internal static Task UnloadAsync(CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.CompletedTask;}
}
