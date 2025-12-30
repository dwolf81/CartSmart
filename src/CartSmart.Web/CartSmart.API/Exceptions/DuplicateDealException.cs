using System;

namespace CartSmart.API.Exceptions
{
    public sealed class DuplicateDealException : Exception
    {
        public int ExistingDealId { get; }

        public DuplicateDealException(string message, int existingDealId)
            : base(message)
        {
            ExistingDealId = existingDealId;
        }
    }
}