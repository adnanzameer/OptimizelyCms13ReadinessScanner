using EPiServer.Find;

namespace Sample;

public class Legacy
{
    public void Run(dynamic area, System.Threading.Tasks.Task t)
    {
        var items = area.FilteredItems;
        var r = SearchClient.Instance.Search<object>().GetContentResult();
        var e = FilterAccess.QueryDistinctAccessEdit(items);
        t.GetAwaiter().GetResult();
    }
}
