namespace P2FK.IO.Services
{
    public sealed class KuboPinStatusTimeoutException : InvalidOperationException
    {
        public KuboPinStatusTimeoutException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
