using Microsoft.AspNetCore.Mvc;

namespace AssetTracking.Web.Controllers
{
    [Route("Dashboard")]
    public class DashboardController : Controller
    {
        public IActionResult Index()
        {
            return View();
        }
    }
}
