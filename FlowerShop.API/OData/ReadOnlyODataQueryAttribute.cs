using Microsoft.AspNetCore.OData.Query;

namespace FlowerShop.API.OData;

public sealed class ReadOnlyODataQueryAttribute : EnableQueryAttribute
{
    public ReadOnlyODataQueryAttribute(string orderByProperties)
    {
        PageSize = 20;
        MaxTop = 100;
        MaxSkip = 10000;
        MaxNodeCount = 30;
        MaxOrderByNodeCount = 2;
        EnsureStableOrdering = true;

        AllowedOrderByProperties = orderByProperties;

        AllowedQueryOptions =
            AllowedQueryOptions.Filter |
            AllowedQueryOptions.OrderBy |
            AllowedQueryOptions.Select |
            AllowedQueryOptions.Top |
            AllowedQueryOptions.Skip |
            AllowedQueryOptions.Count;
    }
}
