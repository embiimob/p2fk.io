namespace P2FK.IO.Services
{
    public class DailyUploadQuotaExceededException : Exception
    {
        public DailyUploadQuotaExceededException(string message) : base(message) { }
    }

    public class TemporaryIngressCacheFullException : Exception
    {
        public TemporaryIngressCacheFullException(string message) : base(message) { }
    }
}
