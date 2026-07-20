using LogViewer.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LogViewer.Web.Pages;

public class LiveTailModel : PageModel
{
    private readonly LogAppRegistry _registry;

    public LiveTailModel(LogAppRegistry registry)
    {
        _registry = registry;
    }

    [BindProperty(SupportsGet = true)]
    public string? App { get; set; }

    public List<string> AppNames { get; set; } = new();

    public void OnGet()
    {
        AppNames = _registry.All.Select(a => a.Name).ToList();
        if (string.IsNullOrEmpty(App) && AppNames.Count > 0)
        {
            App = AppNames[0];
        }
    }
}
