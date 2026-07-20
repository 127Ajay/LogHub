using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LogViewer.Web.Pages;

// The app list itself is loaded/edited client-side via /api/apps - this
// page just needs to render the shell.
public class AdminModel : PageModel
{
    public void OnGet()
    {
    }
}
