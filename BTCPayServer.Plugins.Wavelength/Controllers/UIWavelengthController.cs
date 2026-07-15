using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Wavelength.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Wavelength.Controllers;

[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewStoreSettings)]
[AutoValidateAntiforgeryToken]
[Route("stores/{storeId}/plugins/wavelength")]
public class UIWavelengthController(WavedMnemonicOnceCache mnemonicCache) : Controller
{
    // Deliberately a GET, not a POST: the notification's ActionLink navigates here directly.
    // TakeOnce still only ever succeeds once - a second visit (refresh, back button, someone
    // else clicking an old link) finds nothing left to show.
    [HttpGet("mnemonic")]
    public IActionResult Mnemonic(string storeId)
    {
        var mnemonic = mnemonicCache.TakeOnce(storeId);
        return View(mnemonic);
    }
}
