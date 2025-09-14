using System.Threading.Tasks;

namespace twoSaaSCore.Services
{
    public interface ISmsSender
    {
        Task SendAsync(string toNumber, string message);
    }

    public sealed class NoopSmsSender : ISmsSender
    {
        public Task SendAsync(string toNumber, string message) => Task.CompletedTask;
    }
}