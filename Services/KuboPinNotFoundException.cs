namespace P2FK.IO.Services
{
    public sealed class KuboPinNotFoundException : InvalidOperationException
    {
        public KuboPinNotFoundException(string message)
            : base(message)
        {
        }
    }
}
