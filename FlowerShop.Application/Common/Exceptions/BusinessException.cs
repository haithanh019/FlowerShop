namespace FlowerShop.Application.Common.Exceptions;

public enum BusinessErrorType
{
    NotFound,
    Conflict
}

public sealed class BusinessException(
    BusinessErrorType errorType,
    string message) : Exception(message)
{
    public BusinessErrorType ErrorType { get; } = errorType;
}
