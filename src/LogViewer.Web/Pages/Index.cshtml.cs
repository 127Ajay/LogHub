using LogViewer.Web.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LogViewer.Web.Pages;

public class IndexModel : PageModel
{
    private readonly LogAppRegistry _registry;
    private readonly LogFolderScanner _scanner;

    public IndexModel(LogAppRegistry registry, LogFolderScanner scanner)
    {
        _registry = registry;
        _scanner = scanner;
    }

    public List<AppSummary> Apps { get; set; } = new();

    public class AppSummary
    {
        public string Name { get; set; } = "";
        public int TodayFileCount { get; set; }
        public int DatesAvailable { get; set; }
    }

    public void OnGet()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        foreach (var app in _registry.All)
        {
            var index = _scanner.BuildIndex(app);
            Apps.Add(new AppSummary
            {
                Name = app.Name,
                TodayFileCount = index.TryGetValue(today, out var files) ? files.Count : 0,
                DatesAvailable = index.Count
            });
        }
    }
}
