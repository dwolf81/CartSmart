using System;

namespace CartSmart.API.Exceptions
{
    public sealed class DealSubmissionLimitException : Exception
    {
        public int Limit { get; }
        public int Used { get; }

        public DealSubmissionLimitException(string message, int limit, int used) : base(message)
        {
            Limit = limit;
            Used = used;
        }
    }
}