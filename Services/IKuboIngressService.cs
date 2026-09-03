using P2FK.IO.Models;

namespace P2FK.IO.Services
{
    public interface IKuboIngressService
    {
        Task<KuboAddResult> AddAsync(Stream stream, string fileName, CancellationToken cancellationToken = default);
        Task FetchAsync(string cid, CancellationToken cancellationToken = default);
        Task PinAsync(string cid, CancellationToken cancellationToken = default);
        Task UnpinAsync(string cid, CancellationToken cancellationToken = default);
        Task<long> GetRepoSizeAsync(CancellationToken cancellationToken = default);
        Task RunGarbageCollectionAsync(CancellationToken cancellationToken = default);
        Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default);
    }
}
